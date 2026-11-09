using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace InteractiveMenu.Services
{
    /// <summary>
    /// Глобальный низкоуровневый хук клавиатуры: ловит нажатие и ОТПУСКАНИЕ хоткея
    /// (обычный RegisterHotKey не умеет отпускание, а нам надо «зажал — отпустил»).
    /// </summary>
    public sealed class KeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        private const uint LLKHF_INJECTED = 0x10;
        private const int VK_ESCAPE = 0x1B;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private readonly LowLevelKeyboardProc _proc; // держим ссылку, чтобы GC не съел делегат
        private readonly Dispatcher _dispatcher;
        private IntPtr _hook;
        private bool _active;

        public int HotkeyVk { get; set; }
        public ModifierKeys HotkeyModifiers { get; set; }

        /// <summary>Пока true — хук ничего не перехватывает (например, когда вводим новый хоткей в настройках).</summary>
        public bool Suspended { get; set; }

        public Func<bool> IsMenuOpen { get; set; } = () => false;

        public event Action Pressed;
        public event Action Released;
        public event Action EscapePressed;

        public KeyboardHook(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _proc = HookProc;
            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module?.ModuleName), 0);
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !Suspended)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if ((data.flags & LLKHF_INJECTED) == 0)
                {
                    int msg = (int)wParam;
                    bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                    bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                    int vk = (int)data.vkCode;

                    if (vk == HotkeyVk && HotkeyVk != 0)
                    {
                        if (down)
                        {
                            if (_active) return (IntPtr)1; // автоповтор, пока держим
                            if (ModifiersMatch())
                            {
                                _active = true;
                                _dispatcher.BeginInvoke(new Action(() => Pressed?.Invoke()));
                                return (IntPtr)1;
                            }
                        }
                        else if (up && _active)
                        {
                            _active = false;
                            _dispatcher.BeginInvoke(new Action(() => Released?.Invoke()));
                            return (IntPtr)1;
                        }
                    }
                    else if (down && vk == VK_ESCAPE && IsMenuOpen())
                    {
                        _dispatcher.BeginInvoke(new Action(() => EscapePressed?.Invoke()));
                        return (IntPtr)1;
                    }
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        private bool ModifiersMatch()
        {
            bool ctrl = IsDown(0x11), alt = IsDown(0x12), shift = IsDown(0x10), win = IsDown(0x5B) || IsDown(0x5C);
            return ctrl == HotkeyModifiers.HasFlag(ModifierKeys.Control)
                && alt == HotkeyModifiers.HasFlag(ModifierKeys.Alt)
                && shift == HotkeyModifiers.HasFlag(ModifierKeys.Shift)
                && win == HotkeyModifiers.HasFlag(ModifierKeys.Windows);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }
}
