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

            _autoTyper.Progress  += (_, e) => Dispatcher.InvokeAsync(() => CountdownText.Text = $"Typing...\n{e.CharactersTyped}/{e.TotalCharacters}");
            _autoTyper.Completed += async (_, _) => await Dispatcher.InvokeAsync(() => { Show(); Activate(); });

            Loaded += async (_, _) => await InitWebViewAsync();
        }

        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);

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
                // Low-end device optimisations via Chromium flags
                var opts = new CoreWebView2EnvironmentOptions(
                    "--disable-gpu-compositing " +           // fall back to software compositing
                    "--disable-background-timer-throttling " + // don't slow down when hidden
                    "--disable-renderer-backgrounding " +    // keep renderer active when hidden
                    "--memory-pressure-off " +               // don't aggressively free memory
                    "--js-flags=--lite-mode " +              // V8 lite mode (less RAM)
                    "--enable-low-end-device-mode"           // Chromium low-end device mode
                );

                var env = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: Path.Combine(_userDataFolder, "WebView2Cache"),
                    options: opts);
                await MainWebView.EnsureCoreWebView2Async(env);
                MainWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                MainWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                MainWebView.CoreWebView2.PermissionRequested += (s, ev) =>
                {
                    if (ev.PermissionKind == CoreWebView2PermissionKind.ClipboardRead)
                        ev.State = CoreWebView2PermissionState.Allow;
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
        private void DragArea_MouseLeftButtonDown(object s, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }

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
            try { await MainWebView.CoreWebView2.ExecuteScriptAsync("document.execCommand('copy')"); }
            catch { }
        }

        private async void PasteBtn_Click(object s, RoutedEventArgs e) => await PasteClipboardToWebView();

        private async Task PasteClipboardToWebView()
        {
            try
            {
                if (Clipboard.ContainsImage())
                {
                    // For images: focus webview and send native Ctrl+V
                    MainWebView.Focus();
                    await MainWebView.CoreWebView2.ExecuteScriptAsync(
                        "document.querySelector('[contenteditable=\"true\"]," +
                        "textarea,input[type=\"text\"]')?.focus()");
                    await Task.Delay(80);
                    AutoTyper.SendCtrlV();
                    return;
                }

                if (!Clipboard.ContainsText()) return;
                var text = Clipboard.GetText();

                // FIX: execCommand('insertText') is blocked in modern Chromium.
                // Strategy: write to a hidden textarea via JS, then read it back
                // using the native input event chain that ChatGPT/Claude listen to.
                var escaped = System.Text.Json.JsonSerializer.Serialize(text);
                var js = $@"(function(){{
                    var text = {escaped};
                    var el = document.activeElement;
                    var isEditable = el && (el.isContentEditable ||
                        el.tagName === 'TEXTAREA' || el.tagName === 'INPUT');
                    if (!isEditable) {{
                        el = document.querySelector('[contenteditable=""true""]') ||
                             document.querySelector('textarea') ||
                             document.querySelector('input[type=""text""]');
                        if (el) el.focus();
                    }}
                    if (!el) return 'no-element';

                    if (el.tagName === 'TEXTAREA' || el.tagName === 'INPUT') {{
                        var nativeSetter = Object.getOwnPropertyDescriptor(
                            el.tagName === 'TEXTAREA'
                                ? window.HTMLTextAreaElement.prototype
                                : window.HTMLInputElement.prototype, 'value');
                        var start = el.selectionStart || el.value.length;
                        var end   = el.selectionEnd   || el.value.length;
                        var newVal = el.value.substring(0, start) + text + el.value.substring(end);
                        if (nativeSetter && nativeSetter.set) {{
                            nativeSetter.set.call(el, newVal);
                        }} else {{
                            el.value = newVal;
                        }}
                        el.selectionStart = el.selectionEnd = start + text.length;
                        el.dispatchEvent(new Event('input', {{bubbles:true}}));
                        el.dispatchEvent(new Event('change', {{bubbles:true}}));
                    }} else {{
                        var sel = window.getSelection();
                        if (sel && sel.rangeCount > 0) {{
                            var range = sel.getRangeAt(0);
                            range.deleteContents();
                            range.insertNode(document.createTextNode(text));
                            range.collapse(false);
                            sel.removeAllRanges();
                            sel.addRange(range);
                        }} else {{
                            el.textContent += text;
                        }}
                        el.dispatchEvent(new InputEvent('input', {{bubbles:true, inputType:'insertText', data:text}}));
                    }}
                    return 'ok';
                }})()";
                var result = await MainWebView.CoreWebView2.ExecuteScriptAsync(js);
                Debug.WriteLine($"Paste result: {result}");
            }
            catch (Exception ex) { Debug.WriteLine($"Paste error: {ex}"); }
        }

        private void StopBtn_Click(object s, RoutedEventArgs e) => _autoTyper.TogglePause();
        private void AutoTypeBtn_Click(object s, RoutedEventArgs e) => _ = StartAutoTypeFlow();

        // ── AUTO-TYPE ─────────────────────────────────────────────────────────────
        private async Task StartAutoTypeFlow()
        {
            string text = string.Empty;
            try
            {
                var sel = await MainWebView.CoreWebView2.ExecuteScriptAsync("window.getSelection().toString()");
                sel = sel?.Trim('"') ?? "";
                if (!string.IsNullOrWhiteSpace(sel) && sel != "null")
                    text = System.Text.RegularExpressions.Regex.Unescape(sel);
                else if (Clipboard.ContainsText())
                    text = Clipboard.GetText();
            }
            catch { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }

            if (string.IsNullOrWhiteSpace(text))
            {
                MessageBox.Show("No text to type. Copy something first.", "Auto-Type", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

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
                return;
            }

            CountdownOverlay.Visibility = Visibility.Collapsed;
            Hide();
            await Task.Delay(200);
            try { await _autoTyper.TypeAsync(text, token); }
            catch (OperationCanceledException) { }
            finally { Show(); Activate(); }
        }

        private void CancelAutoType_Click(object s, RoutedEventArgs e)
        {
            _countdownCts?.Cancel();
            _autoTyper.Stop();
            CountdownOverlay.Visibility = Visibility.Collapsed;
        }

        // ── Global Keys ───────────────────────────────────────────────────────────
        // FIX: mark Handled immediately, then post to UI thread — never block hook callback
        private void OnGlobalKeyDown(object? s, GlobalKeyEventArgs e)
        {
            if (e.Key == Key.LeftAlt || e.Key == Key.RightAlt || e.Key == Key.System) _altPressed = true;
            if (e.Key == Key.LeftShift || e.Key == Key.RightShift) _shiftPressed = true;

            if (_altPressed && (e.Key == Key.OemTilde || (_shiftPressed && e.Key == Key.Z)))
            { e.Handled = true; Dispatcher.BeginInvoke(ToggleWindowVisibility); return; }
            if (_altPressed && e.Key == Key.S)
            { e.Handled = true; Dispatcher.BeginInvoke(() => _ = CaptureScreenAsync()); return; }
            if (_altPressed && e.Key == Key.V)
            { e.Handled = true; Dispatcher.BeginInvoke(() => _ = PasteClipboardToWebView()); return; }
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