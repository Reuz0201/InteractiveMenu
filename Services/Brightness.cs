using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;

namespace InteractiveMenu.Services
{
    /// <summary>
    /// Яркость: встроенный экран ноутбука — через WMI, внешние мониторы — через DDC/CI (dxva2).
    /// Вызовы DDC медленные (десятки мс на монитор), поэтому дёргать только из фонового потока.
    /// </summary>
    public static class Brightness
    {
        /// <summary>Текущая яркость (первого найденного экрана) 0..100 или null.</summary>
        public static int? Get()
        {
            int? wmi = GetWmi();
            if (wmi != null) return wmi;

            int? result = null;
            ForEachPhysicalMonitor(h =>
            {
                if (result == null && GetMonitorBrightness(h, out uint min, out uint cur, out uint max) && max > min)
                    result = (int)Math.Round((cur - min) * 100.0 / (max - min));
            });
            return result;
        }

        /// <summary>«+10», «-10» — изменить; «70» — поставить. Возвращает новую яркость.</summary>
        public static int Apply(string argument)
        {
            string arg = (argument ?? "").Trim().Replace("%", "");
            bool relative = arg.StartsWith("+") || arg.StartsWith("-");
            if (!int.TryParse(arg, out int value)) throw new ArgumentException($"не понял яркость «{argument}» — нужно +10, -10 или 70");

            int? applied = null;

            // ноутбук
            int? wmi = GetWmi();
            if (wmi != null)
            {
                int target = Math.Clamp(relative ? wmi.Value + value : value, 0, 100);
                if (SetWmi(target)) applied = target;
            }

            // внешние мониторы
            ForEachPhysicalMonitor(h =>
            {
                if (!GetMonitorBrightness(h, out uint min, out uint cur, out uint max) || max <= min) return;
                int curPct = (int)Math.Round((cur - min) * 100.0 / (max - min));
                int target = Math.Clamp(relative ? curPct + value : value, 0, 100);
                uint raw = (uint)Math.Round(min + (max - min) * target / 100.0);
                if (SetMonitorBrightness(h, raw)) applied ??= target;
            });

            if (applied == null)
                throw new InvalidOperationException("ни один экран не дал поменять яркость (у монитора может быть выключен DDC/CI в его меню)");
            return applied.Value;
        }

        // ------------------------------------------------------------ WMI (ноутбуки)

        private static int? GetWmi()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                using var results = searcher.Get();
                foreach (ManagementObject obj in results)
                {
                    using (obj)
                        return Convert.ToInt32(obj["CurrentBrightness"]);
                }
            }
            catch
            {
                // на десктопе такого класса нет — это нормально
            }
            return null;
        }

        private static bool SetWmi(int level)
        {
            try
            {
                using var mclass = new ManagementClass(@"root\WMI", "WmiMonitorBrightnessMethods", null);
                using var instances = mclass.GetInstances();
                bool any = false;
                foreach (ManagementObject obj in instances)
                {
                    using (obj)
                    {
                        obj.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)level });
                        any = true;
                    }
                }
                return any;
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------ DDC/CI (внешние мониторы)

        private static void ForEachPhysicalMonitor(Action<IntPtr> action)
        {
            var monitors = new List<IntPtr>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data) =>
            {
                monitors.Add(hMon);
                return true;
            }, IntPtr.Zero);

            foreach (var hMon in monitors)
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) || count == 0) continue;
                var arr = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hMon, count, arr)) continue;
                try
                {
                    foreach (var pm in arr)
                    {
                        try { action(pm.hPhysicalMonitor); }
                        catch { }
                    }
                }
                finally
                {
                    DestroyPhysicalMonitors(count, arr);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint size, [Out] PHYSICAL_MONITOR[] monitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool DestroyPhysicalMonitors(uint size, [In] PHYSICAL_MONITOR[] monitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetMonitorBrightness(IntPtr monitor, out uint min, out uint current, out uint max);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool SetMonitorBrightness(IntPtr monitor, uint brightness);
    }
}
