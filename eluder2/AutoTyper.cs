using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Eluder2
{
    public class AutoTyper
    {
        [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);

        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION u; }
        [StructLayout(LayoutKind.Explicit)]   struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }

        const uint INPUT_KEYBOARD    = 1;
        const uint KEYEVENTF_UNICODE = 4;
        const uint KEYEVENTF_KEYUP   = 2;

        private volatile bool _typing, _paused, _stop;
        private Thread? _thread;

        public bool IsTyping => _typing;

        public void Start(string text, Action? onDone = null)
        {
            Stop();
            _stop = _paused = false;
            _typing = true;
            _thread = new Thread(() =>
            {
                foreach (char ch in text)
                {
                    while (_paused && !_stop) Thread.Sleep(50);
                    if (_stop) break;

                    var inp = new INPUT[2];
                    inp[0].type = inp[1].type = INPUT_KEYBOARD;
                    inp[0].u.ki.wScan   = inp[1].u.ki.wScan = ch;
                    inp[0].u.ki.dwFlags = KEYEVENTF_UNICODE;
                    inp[1].u.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
                    SendInput(2, inp, Marshal.SizeOf<INPUT>());
                    Thread.Sleep(ch == '\n' ? 20 : 8);
                }
                _typing = false;
                if (!_stop) onDone?.Invoke();
            }) { IsBackground = true };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            _thread?.Join(2000);
            _typing = _paused = false;
        }

        public void TogglePause() => _paused = !_paused;

        public static void SendCtrlV()
        {
            var inp = new INPUT[4];
            // Release Alt
            inp[0].type = INPUT_KEYBOARD; inp[0].u.ki.wVk = 0x12; inp[0].u.ki.dwFlags = KEYEVENTF_KEYUP;
            // Ctrl down
            inp[1].type = INPUT_KEYBOARD; inp[1].u.ki.wVk = 0x11;
            // V down
            inp[2].type = INPUT_KEYBOARD; inp[2].u.ki.wVk = (ushort)'V';
            // V up
            inp[3].type = INPUT_KEYBOARD; inp[3].u.ki.wVk = (ushort)'V'; inp[3].u.ki.dwFlags = KEYEVENTF_KEYUP;
            SendInput(4, inp, Marshal.SizeOf<INPUT>());
            var ctrlUp = new INPUT[1];
            ctrlUp[0].type = INPUT_KEYBOARD; ctrlUp[0].u.ki.wVk = 0x11; ctrlUp[0].u.ki.dwFlags = KEYEVENTF_KEYUP;
            SendInput(1, ctrlUp, Marshal.SizeOf<INPUT>());
        }
    }
}
