using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace MyOverlayPOC
{
    public class GlobalKeyEventArgs : EventArgs
    {
        public Key Key { get; }
        public KBDLLHOOKSTRUCT Kbdllhookstruct { get; }
        public bool Handled { get; set; }
        public GlobalKeyEventArgs(KBDLLHOOKSTRUCT k) { Key = KeyInterop.KeyFromVirtualKey((int)k.vkCode); Kbdllhookstruct = k; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode; public KBDLLHOOKSTRUCTFlags flags; public uint time; public UIntPtr dwExtraInfo; }

    [Flags]
    public enum KBDLLHOOKSTRUCTFlags : uint { LLKHF_EXTENDED=0x01, LLKHF_LOWER_IL_INJECTED=0x02, LLKHF_INJECTED=0x10, LLKHF_ALTDOWN=0x20, LLKHF_UP=0x80 }

    public sealed class GlobalKeyboardHook : IDisposable
    {
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc fn, IntPtr mod, uint tid);
        [DllImport("user32.dll", SetLastError = true)][return:MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetModuleHandle(string m);

        private readonly LowLevelKeyboardProc _proc;
        private IntPtr _hookId;
        private bool _disposed;
        public event EventHandler<GlobalKeyEventArgs>? KeyDown, KeyUp;

        public GlobalKeyboardHook()
        {
            _proc = HookCallback;
            using var p = Process.GetCurrentProcess();
            var m = p.MainModule ?? throw new Exception("No MainModule");
            _hookId = SetWindowsHookEx(13, _proc, GetModuleHandle(m.ModuleName!), 0);
        }

        private IntPtr HookCallback(int n, IntPtr w, IntPtr l)
        {
            if (n >= 0)
            {
                try
                {
                    var s = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(KBDLLHOOKSTRUCT))!;
                    var a = new GlobalKeyEventArgs(s);
                    if (w == (IntPtr)0x100 || w == (IntPtr)0x104) KeyDown?.Invoke(this, a);
                    else if (w == (IntPtr)0x101 || w == (IntPtr)0x105) KeyUp?.Invoke(this, a);
                    if (a.Handled) return (IntPtr)1;
                }
                catch { }
            }
            // MUST call next hook immediately — never do any slow work before this
            return CallNextHookEx(_hookId, n, w, l);
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (_hookId != IntPtr.Zero) { UnhookWindowsHookEx(_hookId); _hookId = IntPtr.Zero; }
            _disposed = true;
            GC.SuppressFinalize(this);
        }
        ~GlobalKeyboardHook() => Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    public partial class MainWindow : Window
    {
        // Win32
        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr h, uint a);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr i, int x, int y, int cx, int cy, uint f);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        // Providers
        private static readonly Dictionary<string,(string Name,string Url,string NewUrl,string Color)> Providers = new()
        {
            ["chatgpt"]  = ("ChatGPT",  "https://chatgpt.com",           "https://chatgpt.com/",          "#10a37f"),
            ["claude"]   = ("Claude",   "https://claude.ai",             "https://claude.ai/new",         "#d97757"),
            ["gemini"]   = ("Gemini",   "https://gemini.google.com/app", "https://gemini.google.com/app", "#4285f4"),
            ["deepseek"] = ("DeepSeek", "https://chat.deepseek.com",     "https://chat.deepseek.com/",    "#4d6bfe"),
        };

        // State
        private string _currentProvider = "chatgpt";
        private readonly AutoTyper _autoTyper = new();
        private readonly GlobalKeyboardHook _keyboardHook;
        private bool _isHidden;
        private CancellationTokenSource? _countdownCts;
        private bool _altPressed, _shiftPressed;
        private readonly string _userDataFolder;
        private readonly string _tempFolder;
        private IntPtr _hwnd;

        // Opacity: levels and current index
        private static readonly double[] OpacityLevels = { 1.0, 0.75, 0.5, 0.25 };
        private int _opacityIndex = 0;

        // Screenshots sidebar
        private bool _sidebarVisible = false;
        private readonly List<(BitmapImage Image, string FilePath)> _shots = new();

        public MainWindow()
        {
            InitializeComponent();

            _userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Eluder");
            _tempFolder = Path.Combine(_userDataFolder, "Screenshots");
            Directory.CreateDirectory(_userDataFolder);
            Directory.CreateDirectory(_tempFolder);

            _keyboardHook = new GlobalKeyboardHook();
            _keyboardHook.KeyDown += OnGlobalKeyDown;
            _keyboardHook.KeyUp  += OnGlobalKeyUp;

            _autoTyper.Started   += (_, _) => Dispatcher.InvokeAsync(SetAutoTypeButtonsActive);
            _autoTyper.Progress  += (_, e) => Dispatcher.InvokeAsync(() => CountdownText.Text = $"Typing...\n{e.CharactersTyped}/{e.TotalCharacters}");
            _autoTyper.Completed += async (_, _) => await Dispatcher.InvokeAsync(() => {
                ResetAutoTypeButtons();
                Show();
                Activate();
            });

            Loaded += async (_, _) => await InitWebViewAsync();
        }

        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);

            // Position overlay on the primary screen top-left corner area (not off-screen / second monitor)
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen != null)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                double scaleX = dpi.DpiScaleX;
                double scaleY = dpi.DpiScaleY;
                // Place at top-left with a margin, anchored on primary screen
                Left = (screen.WorkingArea.Left / scaleX) + 20;
                Top  = (screen.WorkingArea.Top  / scaleY) + 20;
            }
        }

        // ── Opacity: WPF Opacity for bars + JS brightness for WebView2 ──────────
        private void SetOpacityLevel(double level)
        {
            // WPF Opacity dims the bars/chrome
            this.Opacity = level;
            int pct = (int)(level * 100);
            OpacityBtn.Content = $"{pct}%";

            // JS brightness dims the WebView2 content (fixes HwndHost airspace)
            if (MainWebView.CoreWebView2 != null)
                _ = MainWebView.CoreWebView2.ExecuteScriptAsync(
                    $"document.documentElement.style.filter='brightness({pct}%)'");
        }

        // ── WebView2 Init ────────────────────────────────────────────────────────
        private async Task InitWebViewAsync()
        {
            try
            {
                // Performance flags: disable GPU compositing (avoid "second screen" HwndHost layer),
                // cap raster tile size, disable unnecessary bg tasks, reduce memory pressure.
                var opts = new CoreWebView2EnvironmentOptions(
                    "--disable-gpu-compositing " +               // software compositing — no separate GPU surface
                    "--disable-gpu-vsync " +                     // remove vsync overhead on overlay
                    "--disable-backgrounding-occluded-windows " + // keep rendering even when overlapped
                    "--disable-background-timer-throttling " +   // don't slow timers when hidden
                    "--disable-renderer-backgrounding " +        // keep renderer alive when hidden
                    "--max-decoded-image-size-mb=64 " +          // cap image decode RAM
                    "--tile-width=256 --tile-height=256 " +      // smaller tiles = less VRAM per frame
                    "--num-raster-threads=1 " +                  // limit raster threads (lighter)
                    "--enable-low-end-device-mode " +            // Chromium low-end mode
                    "--js-flags=--lite-mode"                     // V8 lite — less JIT overhead
                );

                var env = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: Path.Combine(_userDataFolder, "WebView2Cache"),
                    options: opts);
                await MainWebView.EnsureCoreWebView2Async(env);

                // Disable unnecessary chrome
                MainWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                MainWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                MainWebView.CoreWebView2.Settings.IsZoomControlEnabled = false;

                // Allow clipboard read (for copy feature)
                MainWebView.CoreWebView2.PermissionRequested += (s, ev) =>
                {
                    if (ev.PermissionKind == CoreWebView2PermissionKind.ClipboardRead)
                        ev.State = CoreWebView2PermissionState.Allow;
                };

                // Inject CSS to prevent the page from causing horizontal scrollbar / overflow
                MainWebView.CoreWebView2.DOMContentLoaded += async (s, ev) =>
                {
                    await MainWebView.CoreWebView2.ExecuteScriptAsync(
                        "document.documentElement.style.overscrollBehavior='none';" +
                        "document.body && (document.body.style.overscrollBehavior='none');");
                };

                MainWebView.CoreWebView2.Navigate(Providers[_currentProvider].Url);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WebView2 init error: {ex}");
                MessageBox.Show("WebView2 Runtime not found.\nhttps://go.microsoft.com/fwlink/p/?LinkId=2124703",
                    "WebView2 Required", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Provider ─────────────────────────────────────────────────────────────
        private void SwitchProvider(string key)
        {
            if (!Providers.ContainsKey(key)) return;
            _currentProvider = key;
            var p = Providers[key];
            ProviderNameText.Text = p.Name;
            ProviderBtn.Background = new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(p.Color));
            ProviderPopup.IsOpen = false;
            MainWebView.CoreWebView2?.Navigate(p.Url);
        }

        private void ProviderBtn_Click(object s, RoutedEventArgs e) => ProviderPopup.IsOpen = !ProviderPopup.IsOpen;
        private void ProviderPopupClose_Click(object s, RoutedEventArgs e) => ProviderPopup.IsOpen = false;
        private void Provider_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string key) SwitchProvider(key);
        }

        // ── Nav ───────────────────────────────────────────────────────────────────
        private void BackBtn_Click(object s, RoutedEventArgs e) { if (MainWebView.CoreWebView2?.CanGoBack == true) MainWebView.GoBack(); }
        private void ForwardBtn_Click(object s, RoutedEventArgs e) { if (MainWebView.CoreWebView2?.CanGoForward == true) MainWebView.GoForward(); }
        private void RefreshBtn_Click(object s, RoutedEventArgs e) => MainWebView.CoreWebView2?.Reload();
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                ReleaseCapture();
                SendMessage(_hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
        }
        private void DragArea_MouseLeftButtonDown(object s, MouseButtonEventArgs e) => Window_MouseLeftButtonDown(s, e);

        // ── Opacity ───────────────────────────────────────────────────────────────
        private void OpacityBtn_Click(object s, RoutedEventArgs e)
        {
            _opacityIndex = (_opacityIndex + 1) % OpacityLevels.Length;
            SetOpacityLevel(OpacityLevels[_opacityIndex]);
        }

        // ── Hide / Clear / Close ──────────────────────────────────────────────────
        private void HideBtn_Click(object s, RoutedEventArgs e) => ToggleWindowVisibility();
        private void ToggleWindowVisibility()
        {
            _isHidden = !_isHidden;
            if (_isHidden) Hide();
            else { Show(); Activate(); Topmost = true; }
        }
        private void ClearBtn_Click(object s, RoutedEventArgs e) => MainWebView.CoreWebView2?.Navigate(Providers[_currentProvider].NewUrl);
        private void CloseBtn_Click(object s, RoutedEventArgs e) => Application.Current.Shutdown();

        // ── Resize grip ──────────────────────────────────────────────────────────
        private void ResizeGrip_MouseDown(object s, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                SendMessage(_hwnd, 0x112, (IntPtr)(61440 + 8), IntPtr.Zero);
        }

        // ── Screenshot sidebar toggle ─────────────────────────────────────────────
        private void ToggleShotsBtn_Click(object s, RoutedEventArgs e)
        {
            _sidebarVisible = !_sidebarVisible;
            ScreenshotSidebar.Visibility = _sidebarVisible ? Visibility.Visible : Visibility.Collapsed;
            ToggleShotsBtn.Foreground = new SolidColorBrush(
                _sidebarVisible ? (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#00cccc")
                                : (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#555555"));
        }

        private void ClearShots_Click(object s, RoutedEventArgs e)
        {
            _shots.Clear();
            ThumbnailsPanel.Children.Clear();
        }

        // ── Capture ───────────────────────────────────────────────────────────────
        private void CaptureBtn_Click(object s, RoutedEventArgs e) => _ = CaptureScreenAsync();

        private async Task CaptureScreenAsync()
        {
            try
            {
                GetCursorPos(out POINT cursor);
                var screenBounds = System.Windows.Forms.Screen
                    .FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)).Bounds;

                // FIX: Run GDI capture on background thread — prevents WebView2 blank/freeze
                var (bitmapImage, filePath) = await Task.Run(() =>
                {
                    var bounds = screenBounds;
                    using var bmp = new Bitmap(bounds.Width, bounds.Height,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using var g = Graphics.FromImage(bmp);
                    g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);

                    string path = Path.Combine(_tempFolder, $"shot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                    bmp.Save(path, ImageFormat.Png);

                    var bi = new BitmapImage();
                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        ms.Position = 0;
                        bi.BeginInit();
                        bi.StreamSource = ms;
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.EndInit();
                    }
                    bi.Freeze();
                    return (bi, path);
                });

                // Back on UI thread: copy to clipboard and add thumbnail
                Clipboard.SetImage(bitmapImage);
                var thumb = CreateThumbnail(bitmapImage, 134, 80);
                _shots.Add((thumb, filePath));
                AddThumbnailToPanel(thumb, filePath);

                if (!_sidebarVisible)
                {
                    _sidebarVisible = true;
                    ScreenshotSidebar.Visibility = Visibility.Visible;
                    ToggleShotsBtn.Foreground = new SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#00cccc"));
                }
            }
            catch (Exception ex) { Debug.WriteLine($"Capture error: {ex}"); }
        }

        private BitmapImage CreateThumbnail(BitmapImage source, int maxW, int maxH)
        {
            double ratio = Math.Min((double)maxW / source.PixelWidth, (double)maxH / source.PixelHeight);
            int w = Math.Max(1, (int)(source.PixelWidth * ratio));
            int h = Math.Max(1, (int)(source.PixelHeight * ratio));
            var tb = new TransformedBitmap(source, new ScaleTransform(ratio, ratio));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(tb));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            ms.Position = 0;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = ms;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }

        private void AddThumbnailToPanel(BitmapImage thumb, string filePath)
        {
            int idx = _shots.Count - 1;

            // Container border
            var container = new Border
            {
                Margin = new Thickness(0, 0, 0, 6),
                CornerRadius = new CornerRadius(4),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2a, 0x2a, 0x2a)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1a, 0x1a, 0x1a)),
                Cursor = System.Windows.Input.Cursors.Hand,
                ClipToBounds = true
            };

            var stack = new StackPanel();

            // Image
            var img = new System.Windows.Controls.Image
            {
                Source = thumb,
                Stretch = Stretch.UniformToFill,
                Height = 76,
                SnapsToDevicePixels = true
            };

            // Label strip
            var lbl = new TextBlock
            {
                Text = $"#{idx + 1}  Click=copy  Drag=drop",
                FontSize = 7.5,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55)),
                Margin = new Thickness(4, 2, 4, 2),
                TextWrapping = TextWrapping.NoWrap
            };

            stack.Children.Add(img);
            stack.Children.Add(lbl);
            container.Child = stack;

            // Click = copy to clipboard
            container.MouseLeftButtonUp += (s, e) =>
            {
                try
                {
                    var full = new BitmapImage();
                    full.BeginInit();
                    full.UriSource = new Uri(filePath);
                    full.CacheOption = BitmapCacheOption.OnLoad;
                    full.EndInit();
                    full.Freeze();
                    Clipboard.SetImage(full);
                }
                catch { }
            };

            // Drag = OLE file drop (WebView2 accepts FileDrop as Chromium)
            container.MouseMove += (s, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    var data = new DataObject(DataFormats.FileDrop, new string[] { filePath });
                    DragDrop.DoDragDrop(container, data, DragDropEffects.Copy);
                }
            };

            // Hover highlight
            container.MouseEnter += (s, e) =>
                container.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xcc, 0xcc));
            container.MouseLeave += (s, e) =>
                container.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2a, 0x2a, 0x2a));

            ThumbnailsPanel.Children.Insert(0, container); // newest on top
        }

        // ── Copy / Paste / Stop / Auto-Type ──────────────────────────────────────
        private async void CopyBtn_Click(object s, RoutedEventArgs e)
        {
            try
            {
                string text = await GetCodeToOutputAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    Clipboard.SetText(text);
                }
                else if (MainWebView.CoreWebView2 != null)
                {
                    await MainWebView.CoreWebView2.ExecuteScriptAsync("document.execCommand('copy')");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Copy error: {ex}");
            }
        }

        private void PasteBtn_Click(object s, RoutedEventArgs e) => _ = PasteToCodeEditorAsync();

        /// <summary>
        /// Intelligently extracts the latest code block or highlighted selection from the AI response,
        /// strictly ignoring ChatGPT's chat input / command textarea.
        /// </summary>
        private async Task<string> GetCodeToOutputAsync()
        {
            string text = string.Empty;

            // 1. Check if user highlighted text anywhere in the WebView
            try
            {
                if (MainWebView.CoreWebView2 != null)
                {
                    var selJson = await MainWebView.CoreWebView2.ExecuteScriptAsync("window.getSelection().toString()");
                    if (!string.IsNullOrWhiteSpace(selJson) && selJson != "\"\"" && selJson != "null")
                    {
                        var sel = System.Text.Json.JsonSerializer.Deserialize<string>(selJson);
                        if (!string.IsNullOrWhiteSpace(sel)) return sel.Trim();
                    }
                }
            }
            catch { }

            // 2. Intelligently extract the latest code block from AI response, ignoring ChatGPT prompt textarea
            try
            {
                if (MainWebView.CoreWebView2 != null)
                {
                    const string extractJs = @"(function() {
                        try {
                            var blocks = document.querySelectorAll('pre code, pre, .cm-content, [class*=""code-block""]');
                            var valid = [];
                            for (var i = 0; i < blocks.length; i++) {
                                var el = blocks[i];
                                if (el.closest('#prompt-textarea') || el.closest('form') || el.id === 'prompt-textarea' || el.getAttribute('contenteditable') === 'true') {
                                    continue;
                                }
                                var t = (el.innerText || el.textContent || '').trim();
                                if (t.length > 0) {
                                    valid.push(t);
                                }
                            }
                            if (valid.length > 0) {
                                return valid[valid.length - 1];
                            }
                        } catch(e) {}

                        try {
                            var msgs = document.querySelectorAll('[data-message-author-role=""assistant""], article, .agent-turn');
                            for (var j = msgs.length - 1; j >= 0; j--) {
                                var pre = msgs[j].querySelector('pre');
                                if (pre) {
                                    var pt = (pre.innerText || pre.textContent || '').trim();
                                    if (pt.length > 0) return pt;
                                }
                            }
                        } catch(e) {}

                        return '';
                    })()";

                    var codeJson = await MainWebView.CoreWebView2.ExecuteScriptAsync(extractJs);
                    if (!string.IsNullOrWhiteSpace(codeJson) && codeJson != "\"\"" && codeJson != "null")
                    {
                        var code = System.Text.Json.JsonSerializer.Deserialize<string>(codeJson);
                        if (!string.IsNullOrWhiteSpace(code)) return code.Trim();
                    }
                }
            }
            catch { }

            // 3. Fallback to clipboard if available
            try
            {
                if (Clipboard.ContainsText())
                {
                    text = Clipboard.GetText();
                }
            }
            catch { }

            return text.Trim();
        }

        private static void SafeSetClipboardText(string text)
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true);
                    return;
                }
                catch
                {
                    Thread.Sleep(30);
                }
            }
        }

        /// <summary>
        /// Pastes directly into the user's active code editor via Alt+V or the PASTE button.
        /// Temporarily hides Eluder so the code editor receives native focus,
        /// releases physical Alt/modifier keys, sends Ctrl+V, then restores Eluder.
        /// </summary>
        private async Task PasteToCodeEditorAsync()
        {
            try
            {
                string code = await GetCodeToOutputAsync();
                if (!string.IsNullOrWhiteSpace(code))
                {
                    code = code.Replace("\r\n", "\n").Replace("\r", "\n");
                    SafeSetClipboardText(code);
                }

                if (!Clipboard.ContainsText() && !Clipboard.ContainsImage())
                {
                    return;
                }

                bool wasVisible = !_isHidden && IsVisible;
                if (wasVisible)
                {
                    Hide();
                    await Task.Delay(200); // Give Windows time to return focus to the code editor
                }

                // Send Ctrl+V directly to the code editor (AutoTyper.SendCtrlV releases Alt first!)
                AutoTyper.SendCtrlV();

                if (wasVisible)
                {
                    await Task.Delay(150);
                    Show();
                    Topmost = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PasteToCodeEditor error: {ex}");
            }
        }

        // ── AutoType Button Color States ──────────────────────────────────────────
        private void SetAutoTypeButtonsActive()
        {
            AutoTypeBtn.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1a4a1a"));
            AutoTypeBtnText.Text = "TYPING...";
            AutoTypeBtnText.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#32CD32"));

            StopBtn.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4a1a1a"));
            StopBtnText.Text = "STOP";
            StopBtnText.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#ff4444"));
            StopBtnSub.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#ff8888"));
        }

        private void ResetAutoTypeButtons()
        {
            AutoTypeBtn.Background = System.Windows.Media.Brushes.Transparent;
            AutoTypeBtnText.Text = "AUTO-TYPE";
            AutoTypeBtnText.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#aaaaaa"));

            StopBtn.Background = System.Windows.Media.Brushes.Transparent;
            StopBtnText.Text = "STOP";
            StopBtnText.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#aaaaaa"));
            StopBtnSub.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#555555"));
        }

        private void StopBtn_Click(object s, RoutedEventArgs e)
        {
            _countdownCts?.Cancel();
            _autoTyper.Stop();
            CountdownOverlay.Visibility = Visibility.Collapsed;
            ResetAutoTypeButtons();
        }

        private void AutoTypeBtn_Click(object s, RoutedEventArgs e) => _ = StartAutoTypeFlow();

        // ── AUTO-TYPE ─────────────────────────────────────────────────────────────
        private async Task StartAutoTypeFlow()
        {
            string text = await GetCodeToOutputAsync();

            if (string.IsNullOrWhiteSpace(text))
            {
                MessageBox.Show("No code found to type. Wait for the AI to generate code or highlight it first.", "Auto-Type", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Normalize newlines to avoid double-spacing in code editors
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");

            try { SafeSetClipboardText(text); } catch { }

            CountdownOverlay.Visibility = Visibility.Visible;
            _countdownCts = new CancellationTokenSource();
            var token = _countdownCts.Token;

            try
            {
                for (int i = 3; i >= 1; i--)
                {
                    CountdownText.Text = i.ToString();
                    await Task.Delay(1000, token);
                }
            }
            catch (OperationCanceledException)
            {
                CountdownOverlay.Visibility = Visibility.Collapsed;
                ResetAutoTypeButtons();
                return;
            }

            CountdownOverlay.Visibility = Visibility.Collapsed;
            Hide();
            await Task.Delay(250); // Give code editor active focus
            try { await _autoTyper.TypeAsync(text, token); }
            catch (OperationCanceledException) { }
            finally {
                ResetAutoTypeButtons();
                Show();
                Activate();
                Topmost = true;
            }
        }

        private void CancelAutoType_Click(object s, RoutedEventArgs e)
        {
            _countdownCts?.Cancel();
            _autoTyper.Stop();
            CountdownOverlay.Visibility = Visibility.Collapsed;
            ResetAutoTypeButtons();
        }

        // ── Keyboard Shortcuts Guide Popup ────────────────────────────────────────
        private void InfoBtn_Click(object s, RoutedEventArgs e)
        {
            ShortcutsPopup.IsOpen = !ShortcutsPopup.IsOpen;
        }

        private void CloseShortcuts_Click(object s, RoutedEventArgs e)
        {
            ShortcutsPopup.IsOpen = false;
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (ShortcutsPopup.IsOpen)
                {
                    e.Handled = true;
                    ShortcutsPopup.IsOpen = false;
                    return;
                }

                if (_autoTyper.IsTyping || CountdownOverlay.Visibility == Visibility.Visible)
                {
                    e.Handled = true;
                    _countdownCts?.Cancel();
                    _autoTyper.Stop();
                    CountdownOverlay.Visibility = Visibility.Collapsed;
                    ResetAutoTypeButtons();
                    return;
                }
            }
            base.OnPreviewKeyDown(e);
        }

        // ── Global Keys ───────────────────────────────────────────────────────────
        // FIX: mark Handled immediately, then post to UI thread — never block hook callback
        private void OnGlobalKeyDown(object? s, GlobalKeyEventArgs e)
        {
            if (e.Key == Key.LeftAlt || e.Key == Key.RightAlt || e.Key == Key.System) _altPressed = true;
            if (e.Key == Key.LeftShift || e.Key == Key.RightShift) _shiftPressed = true;

            // Global ESC: terminate autotype or close popup immediately from anywhere
            if (e.Key == Key.Escape)
            {
                if (_autoTyper.IsTyping || CountdownOverlay.Visibility == Visibility.Visible || ShortcutsPopup.IsOpen)
                {
                    e.Handled = true;
                    Dispatcher.BeginInvoke(() =>
                    {
                        ShortcutsPopup.IsOpen = false;
                        _countdownCts?.Cancel();
                        _autoTyper.Stop();
                        CountdownOverlay.Visibility = Visibility.Collapsed;
                        ResetAutoTypeButtons();
                    });
                    return;
                }
            }

            if (_altPressed && (e.Key == Key.OemTilde || (_shiftPressed && e.Key == Key.Z)))
            { e.Handled = true; Dispatcher.BeginInvoke(ToggleWindowVisibility); return; }
            if (_altPressed && e.Key == Key.S)
            { e.Handled = true; Dispatcher.BeginInvoke(() => _ = CaptureScreenAsync()); return; }
            if (_altPressed && e.Key == Key.V)
            { e.Handled = true; Dispatcher.BeginInvoke(() => _ = PasteToCodeEditorAsync()); return; }
            if (_altPressed && e.Key == Key.P)
            { e.Handled = true; Dispatcher.BeginInvoke(() => _autoTyper.TogglePause()); return; }
            if (e.Key == Key.F8)
            { e.Handled = true; Dispatcher.BeginInvoke(() => _ = StartAutoTypeFlow()); return; }
        }

        private void OnGlobalKeyUp(object? s, GlobalKeyEventArgs e)
        {
            if (e.Key == Key.LeftAlt || e.Key == Key.RightAlt || e.Key == Key.System) _altPressed = false;
            if (e.Key == Key.LeftShift || e.Key == Key.RightShift) _shiftPressed = false;
        }

        private void MainWindow_Closing(object s, System.ComponentModel.CancelEventArgs e)
        {
            _keyboardHook?.Dispose();
            _autoTyper?.Stop();
            _countdownCts?.Cancel();
        }
    }
}