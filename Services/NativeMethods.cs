using System;
using System.Runtime.InteropServices;

namespace InteractiveMenu.Services
{
    internal static class NativeMethods
    {
        public const uint KEYEVENTF_EXTENDEDKEY = 0x1;
        public const uint KEYEVENTF_KEYUP = 0x2;

        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        public static extern bool LockWorkStation();

        [DllImport("powrprof.dll", SetLastError = true)]
        public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        public static bool AnyModifierDown() =>
            IsDown(0x10) || IsDown(0x11) || IsDown(0x12) || IsDown(0x5B) || IsDown(0x5C);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Тёмный системный заголовок окна (Windows 10 20H1+ / 11).</summary>
        public static void UseDarkTitleBar(IntPtr hwnd)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0) // DWMWA_USE_IMMERSIVE_DARK_MODE
                    DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));     // старые сборки Win10
                int caption = 0x00140F0F; // COLORREF 0x00BBGGRR → #0F0F14
                DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));    // DWMWA_CAPTION_COLOR (Win11)
            }
            catch
            {
                // не поддерживается — останется обычный заголовок
            }
        }

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x80;

        public static void KeyDown(byte vk, bool extended = false) =>
            keybd_event(vk, 0, extended ? KEYEVENTF_EXTENDEDKEY : 0, UIntPtr.Zero);

        public static void KeyUp(byte vk, bool extended = false) =>
            keybd_event(vk, 0, KEYEVENTF_KEYUP | (extended ? KEYEVENTF_EXTENDEDKEY : 0), UIntPtr.Zero);

        public static void Tap(byte vk, bool extended = false)
        {
            KeyDown(vk, extended);
            KeyUp(vk, extended);
        }
    }
}
