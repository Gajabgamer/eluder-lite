using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MyOverlayPOC
{
    public class AutoTypeProgressEventArgs : EventArgs
    {
        public int CharactersTyped { get; }
        public int TotalCharacters { get; }
        public AutoTypeProgressEventArgs(int typed, int total) { CharactersTyped = typed; TotalCharacters = total; }
    }

    public class AutoTyper
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL, wParamH;
        }

        private const uint INPUT_KEYBOARD    = 1;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint KEYEVENTF_KEYUP   = 0x0002;

        public bool IsTyping { get; private set; }
        public bool IsPaused { get; private set; }

        public event EventHandler<AutoTypeProgressEventArgs>? Progress;
        public event EventHandler? Started;
        public event EventHandler? Completed;

        private CancellationTokenSource? _cts;
        private const int DelayMs = 18;

        public async Task TypeAsync(string text, CancellationToken externalToken = default)
        {
            _cts?.Cancel();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            var token = _cts.Token;

            IsTyping = true;
            IsPaused = false;
            Started?.Invoke(this, EventArgs.Empty);

            // Normalize line endings to avoid double newlines in editors
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");

            try
            {
                for (int i = 0; i < text.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    while (IsPaused)
                        await Task.Delay(50, token);

                    char c = text[i];
                    if (c == '\r')
                        continue;
                    if (c == '\n')
                        SendKey(0x0D);
                    else
                        SendUnicode(c);

                    Progress?.Invoke(this, new AutoTypeProgressEventArgs(i + 1, text.Length));
                    await Task.Delay(DelayMs, token);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                IsTyping = false;
                IsPaused = false;
                Completed?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Pause()       => IsPaused = true;
        public void Resume()      => IsPaused = false;
        public void Stop()        => _cts?.Cancel();
        public void TogglePause() { if (IsPaused) Resume(); else Pause(); }

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private static void SendUnicode(char c)
        {
            var inputs = new INPUT[2];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].u.ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE };
            inputs[1].type = INPUT_KEYBOARD;
            inputs[1].u.ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP };
            SendInput(2, inputs, Marshal.SizeOf<INPUT>());
        }

        private static void SendKey(ushort vk)
        {
            var inputs = new INPUT[2];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].u.ki = new KEYBDINPUT { wVk = vk };
            inputs[1].type = INPUT_KEYBOARD;
            inputs[1].u.ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP };
            SendInput(2, inputs, Marshal.SizeOf<INPUT>());
        }

        public static void SendCtrlV()
        {
            // CRITICAL: Ensure physical Alt, Shift, and Windows keys are released before Ctrl+V!
            // When user presses Alt+V, Alt is physically held down. Sending Ctrl+V without releasing Alt
            // results in Ctrl+Alt+V which is ignored by code editors!
            keybd_event(0x12, 0, 0x0002, UIntPtr.Zero); // VK_MENU (Alt) UP
            keybd_event(0x10, 0, 0x0002, UIntPtr.Zero); // VK_SHIFT UP
            keybd_event(0x5B, 0, 0x0002, UIntPtr.Zero); // VK_LWIN UP

            // Send Ctrl+V
            keybd_event(0x11, 0, 0, UIntPtr.Zero);      // VK_CONTROL DOWN
            keybd_event(0x56, 0, 0, UIntPtr.Zero);      // 'V' DOWN
            keybd_event(0x56, 0, 0x0002, UIntPtr.Zero); // 'V' UP
            keybd_event(0x11, 0, 0x0002, UIntPtr.Zero); // VK_CONTROL UP
        }
    }
}