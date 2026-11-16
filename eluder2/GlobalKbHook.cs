using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Eluder2
{
    public enum ShortcutAction { ToggleVisible, TriggerAutoType, TriggerPaste, TogglePause }

    public sealed class GlobalKbHook : IDisposable
    {
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc fn, IntPtr mod, uint tid);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? m);

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr extra; }

        public event Action<ShortcutAction>? ShortcutFired;

        private readonly LowLevelKeyboardProc _proc;
        private IntPtr _hook;
        private bool _alt, _shift;

        public GlobalKbHook()
        {
            _proc = HookCallback;
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            _hook = SetWindowsHookEx(13, _proc, GetModuleHandle(proc.MainModule!.ModuleName), 0);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    var kb = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT))!;
                    bool down = wParam == 0x100 || wParam == 0x104; // WM_KEYDOWN / WM_SYSKEYDOWN
                    bool up   = wParam == 0x101 || wParam == 0x105;

                    if (kb.vkCode is 0xA4 or 0xA5) { _alt   = down; }  // VK_LMENU / VK_RMENU
                    if (kb.vkCode is 0xA0 or 0xA1) { _shift = down; }  // VK_LSHIFT / VK_RSHIFT

                    if (down)
                    {
                        // Alt+` or Alt+Shift+Z → Toggle
                        if (_alt && (kb.vkCode == 0xC0 || (_shift && kb.vkCode == 'Z')))
                        { Fire(ShortcutAction.ToggleVisible); return (IntPtr)1; }

                        // F8 → AutoType
                        if (kb.vkCode == 0x77)
                        { Fire(ShortcutAction.TriggerAutoType); return (IntPtr)1; }

                        // Alt+V → Paste
                        if (_alt && kb.vkCode == 'V')
                        { Fire(ShortcutAction.TriggerPaste); return (IntPtr)1; }

                        // Alt+P → Pause
                        if (_alt && kb.vkCode == 'P')
                        { Fire(ShortcutAction.TogglePause); return (IntPtr)1; }
                    }
                }
                catch { }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private void Fire(ShortcutAction action) =>
            ThreadPool.QueueUserWorkItem(_ => ShortcutFired?.Invoke(action));

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        }
    }
}
