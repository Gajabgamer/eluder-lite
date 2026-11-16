// Eluder 2.0 — CefSharp WinForms overlay
// Key stealth: WinForms window (NOT WPF — no "HwndWrapper" class name)
//              + subclassed HWND registered as "ApplicationFrameWindow"
//              + WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP
//              + WDA_EXCLUDEFROMCAPTURE on the CefSharp HWND (covers full CEF surface)
//              + 2s heartbeat, WinEvent Z-hook, desktop watcher
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CefSharp;
using CefSharp.WinForms;

namespace Eluder2
{
    public class OverlayForm : Form
    {
        // ── Win32 P/Invoke ────────────────────────────────────────────────────
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint a);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr i, int x, int y, int cx, int cy, uint f);
        [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr h, int n);
        [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr h, int n, IntPtr v);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint eMin, uint eMax, IntPtr hmod, WinEventDelegate fn, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr h);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);
        [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint dwThreadId);
        [DllImport("user32.dll")] static extern bool SetThreadDesktop(IntPtr hDesktop);
        [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr hDesktop);
        [DllImport("user32.dll")] static extern bool GetUserObjectInformation(IntPtr h, int i, [Out] char[] pvInfo, uint nLength, out uint lpnLengthNeeded);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? m);

        delegate void WinEventDelegate(IntPtr hHook, uint evt, IntPtr hwnd, int idObj, int idChild, uint thread, uint time);

        // Extended style constants
        const int    GWL_EXSTYLE            = -20;
        const uint   WS_EX_TOOLWINDOW       = 0x00000080;
        const uint   WS_EX_NOACTIVATE       = 0x08000000;
        const uint   WS_EX_LAYERED          = 0x00080000;
        const uint   WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
        const uint   WS_EX_APPWINDOW        = 0x00040000;
        const uint   WDA_EXCLUDEFROMCAPTURE = 0x11;
        const uint   SWP_NOMOVE             = 0x0002;
        const uint   SWP_NOSIZE             = 0x0001;
        const uint   SWP_NOACTIVATE         = 0x0010;
        const uint   SWP_NOSENDCHANGING     = 0x0400;
        static readonly IntPtr HWND_TOPMOST = new(-1);
        const int    SW_SHOWNOACTIVATE      = 4;
        const uint   EVENT_OBJECT_REORDER   = 0x8004;
        const uint   EVENT_SYSTEM_FOREGROUND = 0x0003;
        const uint   WINEVENT_OUTOFCONTEXT  = 0x0000;
        const uint   WINEVENT_SKIPOWNPROCESS = 0x0002;
        // SEB's whitelist class name (Windows Shell Frame)
        const string STEALTH_CLASS          = "ApplicationFrameWindow";

        // ── State ─────────────────────────────────────────────────────────────
        private ChromiumWebBrowser? _browser;
        private readonly AutoTyper   _typer   = new();
        private readonly GlobalKbHook _kbHook;
        private bool   _isHidden;
        private System.Threading.Timer? _heartbeat;
        private IntPtr _zHook, _fgHook;
        private WinEventDelegate? _winEventDelegate; // keep alive
        private Thread? _desktopWatcher;
        private volatile bool _running = true;

        // ── Constructor ───────────────────────────────────────────────────────
        public OverlayForm()
        {
            // WinForms window setup (no WPF — no HwndWrapper class name)
            FormBorderStyle = FormBorderStyle.None;
            Width  = 440;
            Height = 600;
            BackColor = Color.Black;
            TopMost   = true;
            ShowInTaskbar = false;
            Opacity   = 0.97;

            // Position on primary screen right side
            var screen = Screen.PrimaryScreen;
            if (screen != null) {
                Left = screen.WorkingArea.Right - Width - 20;
                Top  = screen.WorkingArea.Top  + 20;
            }

            // Keyboard hook
            _kbHook = new GlobalKbHook();
            _kbHook.ShortcutFired += OnShortcut;
        }

        // ── Form load ─────────────────────────────────────────────────────────
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ApplyStealthStyles();
            InitBrowser();
            StartHeartbeat();
            StartDesktopWatcher();
        }

        // ── The critical stealth method ───────────────────────────────────────
        private void ApplyStealthStyles()
        {
            IntPtr hwnd = Handle;

            // Apply stealth extended styles
            var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            ex |=  WS_EX_TOOLWINDOW;        // hide from shell EnumWindows / Alt+Tab
            ex |=  WS_EX_NOACTIVATE;        // never steal foreground focus
            ex |=  WS_EX_NOREDIRECTIONBITMAP; // exclude from DWM redirection / DirectX capture
            ex &= ~(long)WS_EX_APPWINDOW;   // no taskbar entry
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));

            // Capture exclusion — invisible to BitBlt/PrintWindow/DXGI
            // Covers the ENTIRE CefSharp render surface (not just the frame)
            SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

            // Assert topmost without activating or notifying shell
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING);

            // WinEvent hooks — intercept SEB's Z-order and foreground changes
            _winEventDelegate = OnWinEvent;
            _zHook  = SetWinEventHook(EVENT_OBJECT_REORDER, EVENT_OBJECT_REORDER,
                IntPtr.Zero, _winEventDelegate, 0, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            _fgHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _winEventDelegate, 0, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        }

        private void ReassertStealth()
        {
            if (!IsHandleCreated) return;
            try {
                IntPtr hwnd = Handle;
                var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                ex |=  WS_EX_TOOLWINDOW;
                ex |=  WS_EX_NOACTIVATE;
                ex |=  WS_EX_NOREDIRECTIONBITMAP;
                ex &= ~(long)WS_EX_APPWINDOW;
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
                SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
                if (!_isHidden)
                    SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
            } catch { }
        }

        private void OnWinEvent(IntPtr hHook, uint evt, IntPtr hwnd, int idObj, int idChild, uint thread, uint time)
        {
            // Another window changed Z-order or foreground → silently re-lock ours
            if (hwnd == Handle) return;
            try {
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
            } catch { }
        }

        // ── Heartbeat: re-apply stealth every 1500ms ──────────────────────────
        private void StartHeartbeat()
        {
            _heartbeat = new System.Threading.Timer(_ => {
                try { Invoke(ReassertStealth); } catch { }
            }, null, 1500, 1500);
        }

        // ── Desktop watcher: follow SEB to its secure desktop ─────────────────
        private void StartDesktopWatcher()
        {
            _desktopWatcher = new Thread(() =>
            {
                IntPtr lastDesk = GetThreadDesktop(GetCurrentThreadId());
                while (_running)
                {
                    Thread.Sleep(100);
                    IntPtr inputDesk = OpenInputDesktop(0, false, 0x00020000 | 0x00040000 | 0x10000000);
                    if (inputDesk == IntPtr.Zero) continue;

                    var curName = new char[256]; uint sz;
                    var newName = new char[256];
                    GetUserObjectInformation(lastDesk,  2, curName, 512, out sz);
                    GetUserObjectInformation(inputDesk, 2, newName, 512, out sz);

                    string cur = new string(curName).TrimEnd('\0');
                    string nxt = new string(newName).TrimEnd('\0');

                    if (!cur.Equals(nxt, StringComparison.OrdinalIgnoreCase))
                    {
                        SetThreadDesktop(inputDesk);
                        lastDesk = inputDesk;
                        try { Invoke(ReassertStealth); } catch { }
                    }
                    else CloseDesktop(inputDesk);
                }
            }) { IsBackground = true, Name = "DesktopWatcher" };
            _desktopWatcher.Start();
        }

        // ── Show without activating ───────────────────────────────────────────
        public void ShowSilent()
        {
            if (IsHandleCreated)
            {
                ShowWindow(Handle, SW_SHOWNOACTIVATE);
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
            }
        }

        // ── CefSharp browser init ─────────────────────────────────────────────
        private void InitBrowser()
        {
            _browser = new ChromiumWebBrowser("https://chatgpt.com/");
            _browser.Dock = DockStyle.Fill;

            _browser.LoadingStateChanged += (s, ev) =>
            {
                // FrameLoadEnd equivalent: fire when done loading
                if (!ev.IsLoading && ev.Browser != null)
                    InjectTopBar();
            };

            Controls.Add(_browser);
        }

        // ── Inject minimal top bar via JS ─────────────────────────────────────
        private void InjectTopBar()
        {
            const string js = @"
(function(){
    if(document.getElementById('__el_bar')) return;
    var bar=document.createElement('div');
    bar.id='__el_bar';
    bar.style.cssText='position:fixed;top:0;left:0;right:0;height:34px;background:rgba(8,8,8,0.95);'+
        'z-index:2147483647;display:flex;align-items:center;padding:0 8px;gap:5px;'+
        'font-family:system-ui,sans-serif;font-size:11px;color:#888;-webkit-app-region:drag;user-select:none;';
    var providers=[
        {id:'chatgpt', label:'ChatGPT', color:'#10a37f', url:'https://chatgpt.com/'},
        {id:'claude',  label:'Claude',  color:'#d97757', url:'https://claude.ai/new'},
        {id:'gemini',  label:'Gemini',  color:'#4285f4', url:'https://gemini.google.com/app'},
        {id:'deepseek',label:'DeepSeek',color:'#4d6bfe', url:'https://chat.deepseek.com/'},
    ];
    providers.forEach(function(p){
        var b=document.createElement('button');
        b.textContent=p.label;
        b.style.cssText='background:'+p.color+';color:#fff;border:none;border-radius:3px;'+
            'padding:2px 7px;cursor:pointer;font-size:10px;-webkit-app-region:no-drag;';
        b.onclick=function(){location.href=p.url;};
        bar.appendChild(b);
    });
    var op=[1,0.75,0.5,0.25]; var oi=0;
    var ob=document.createElement('button');
    ob.textContent='100%';
    ob.style.cssText='margin-left:auto;background:#1a1a1a;color:#888;border:1px solid #333;'+
        'border-radius:3px;padding:2px 7px;cursor:pointer;font-size:10px;-webkit-app-region:no-drag;';
    ob.onclick=function(){oi=(oi+1)%op.length;document.documentElement.style.opacity=op[oi];ob.textContent=Math.round(op[oi]*100)+'%';};
    bar.appendChild(ob);
    document.body.style.paddingTop='34px';
    document.body.prepend(bar);
    document.documentElement.style.overscrollBehavior='none';
})();";
            try
            {
                _browser?.GetBrowser()?.MainFrame?.ExecuteJavaScriptAsync(js);
            }
            catch { }
        }

        // ── Keyboard shortcuts ────────────────────────────────────────────────
        private void OnShortcut(ShortcutAction action)
        {
            if (InvokeRequired) { Invoke(() => OnShortcut(action)); return; }
            switch (action)
            {
                case ShortcutAction.ToggleVisible:
                    _isHidden = !_isHidden;
                    if (_isHidden) Hide();
                    else ShowSilent();
                    break;

                case ShortcutAction.TriggerAutoType:
                    if (_typer.IsTyping) break;
                    _ = StartAutoTypeAsync();
                    break;

                case ShortcutAction.TriggerPaste:
                    _ = PasteAsync();
                    break;

                case ShortcutAction.TogglePause:
                    _typer.TogglePause();
                    break;
            }
        }

        private async Task<string> GetCodeFromPageAsync()
        {
            if (_browser == null) return "";
            const string js = @"(function(){
                var sel=window.getSelection()?.toString()||'';
                if(sel.trim()) return sel.trim();
                var blocks=document.querySelectorAll('pre code,pre,.cm-content,[class*=""code-block""]');
                var valid=[];
                for(var i=0;i<blocks.length;i++){
                    var el=blocks[i];
                    if(el.closest('#prompt-textarea')||el.closest('form')||el.getAttribute('contenteditable')==='true') continue;
                    var t=(el.innerText||el.textContent||'').trim();
                    if(t) valid.push(t);
                }
                return valid.length?valid[valid.length-1]:'';
            })()";
            try
            {
                var frame = _browser.GetBrowser()?.MainFrame;
                if (frame == null) return "";
                var resp = await frame.EvaluateScriptAsync(js);
                if (resp.Success && resp.Result is string s2) return s2;
                return "";
            }
            catch { return ""; }
        }

        private async Task StartAutoTypeAsync()
        {
            string text = await GetCodeFromPageAsync();
            if (string.IsNullOrWhiteSpace(text)) return;
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");

            // 3-second countdown then hide
            Hide();
            await Task.Delay(3000);

            _typer.Start(text, () => Invoke(ShowSilent));
        }

        private async Task PasteAsync()
        {
            string text = await GetCodeFromPageAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                text = text.Replace("\r\n", "\n").Replace("\r", "\n");
                Clipboard.SetText(text);
            }
            Hide();
            await Task.Delay(200);
            AutoTyper.SendCtrlV();
            await Task.Delay(150);
            ShowSilent();
        }

        // ── Drag (title bar area) ─────────────────────────────────────────────
        protected override void WndProc(ref Message m)
        {
            // Allow dragging from top 34px
            if (m.Msg == 0x84) // WM_NCHITTEST
            {
                base.WndProc(ref m);
                var pt = PointToClient(new Point(m.LParam.ToInt32() & 0xffff, m.LParam.ToInt32() >> 16));
                if (pt.Y < 34) m.Result = (IntPtr)2; // HTCAPTION
                return;
            }
            base.WndProc(ref m);
        }

        // ── Override CreateParams: set class name before HWND is created ───────
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= (int)(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED);
                cp.ExStyle &= ~(int)WS_EX_APPWINDOW;
                // Set class name to our stealth name (pre-registered in Program.cs)
                cp.ClassName = STEALTH_CLASS;
                return cp;
            }
        }

        // ── Cleanup ───────────────────────────────────────────────────────────
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _running = false;
            _heartbeat?.Dispose();
            _kbHook?.Dispose();
            _typer?.Stop();
            if (_zHook  != IntPtr.Zero) UnhookWinEvent(_zHook);
            if (_fgHook != IntPtr.Zero) UnhookWinEvent(_fgHook);
            base.OnFormClosing(e);
        }
    }
}
