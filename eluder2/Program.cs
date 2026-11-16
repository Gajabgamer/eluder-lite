using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Eluder2
{
    static class Program
    {
        // Pre-register "ApplicationFrameWindow" as a Win32 class BEFORE WinForms creates
        // the window, so CreateParams.ClassName = "ApplicationFrameWindow" works at HWND creation time.
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern ushort RegisterClassEx(ref WNDCLASSEX c);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? m);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX {
            public uint   cbSize, style;
            public IntPtr lpfnWndProc;
            public int    cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }
        delegate IntPtr WndProcDelegate(IntPtr h, uint m, IntPtr w, IntPtr l);

        [STAThread]
        static void Main()
        {
            // ── 1. Pre-register the stealth window class ──────────────────────────
            WndProcDelegate wndProc = DefWindowProc;
            var wc = new WNDCLASSEX {
                cbSize        = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(wndProc),
                hInstance     = GetModuleHandle(null),
                lpszClassName = "ApplicationFrameWindow",
                hbrBackground = (IntPtr)1,
            };
            RegisterClassEx(ref wc);  // ignore return — may already be registered by OS (fine)
            GC.KeepAlive(wndProc);

            // ── 2. Initialize CEF (via reflection — CefSharp.Core is C++/CLI) ─────
            try { CefInit.Initialize(); }
            catch (Exception ex)
            {
                MessageBox.Show($"CEF init failed:\n{ex.Message}", "Eluder 2.0",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // ── 3. License check ──────────────────────────────────────────────────
            var licResult = LicenseManager.CheckOnStartup();
            if (!licResult.Valid)
            {
                CefInit.Shutdown();
                return;
            }

            // ── 4. Run overlay ────────────────────────────────────────────────────
            ApplicationConfiguration.Initialize();
            Application.Run(new OverlayForm());

            CefInit.Shutdown();
        }
    }
}
