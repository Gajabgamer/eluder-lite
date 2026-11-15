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
        public event EventHandler? Completed;

        private CancellationTokenSource? _cts;
        private const int DelayMs = 32;

        public async Task TypeAsync(string text, CancellationToken externalToken = default)
        {
            _cts?.Cancel();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            var token = _cts.Token;

            IsTyping = true;
            IsPaused = false;

            try
            {
                for (int i = 0; i < text.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    while (IsPaused)
                        await Task.Delay(50, token);

                    char c = text[i];
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
            var inputs = new INPUT[4];
            inputs[0].type = INPUT_KEYBOARD; inputs[0].u.ki = new KEYBDINPUT { wVk = 0x11 };
            inputs[1].type = INPUT_KEYBOARD; inputs[1].u.ki = new KEYBDINPUT { wVk = 0x56 };
            inputs[2].type = INPUT_KEYBOARD; inputs[2].u.ki = new KEYBDINPUT { wVk = 0x56, dwFlags = KEYEVENTF_KEYUP };
            inputs[3].type = INPUT_KEYBOARD; inputs[3].u.ki = new KEYBDINPUT { wVk = 0x11, dwFlags = KEYEVENTF_KEYUP };
            SendInput(4, inputs, Marshal.SizeOf<INPUT>());
        }
    }
}