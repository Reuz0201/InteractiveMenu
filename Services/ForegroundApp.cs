using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace InteractiveMenu.Services
{
    /// <summary>Какое приложение сейчас активно — для контекстных веток.</summary>
    public static class ForegroundApp
    {
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        /// <summary>Имя процесса окна без .exe (например «chrome», «Code») или null.</summary>
        public static string ProcessNameOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            try
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0 || pid == Environment.ProcessId) return null;
                using var p = Process.GetProcessById((int)pid);
                return p.ProcessName;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Запущенные приложения с окнами — для выбора в настройках.</summary>
        public static List<(string Name, string Title)> ListWindowedApps()
        {
            var result = new List<(string Name, string Title)>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle) && p.Id != Environment.ProcessId)
                        result.Add((p.ProcessName, p.MainWindowTitle));
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
            return result
                .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
