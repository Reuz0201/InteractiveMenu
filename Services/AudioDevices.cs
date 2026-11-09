using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace InteractiveMenu.Services
{
    /// <summary>
    /// Устройства вывода звука через Windows Core Audio (COM):
    /// список, текущее по умолчанию, громкость/mute и смена устройства по умолчанию.
    /// </summary>
    public static class AudioDevices
    {
        public sealed record Device(string Id, string Name);

        // ------------------------------------------------------------ публичное API

        public static List<Device> ListOutputs()
        {
            var result = new List<Device>();
            var enumerator = CreateEnumerator();
            try
            {
                Check(enumerator.EnumAudioEndpoints(EDataFlow.Render, DEVICE_STATE_ACTIVE, out var collection));
                Check(collection.GetCount(out uint count));
                for (uint i = 0; i < count; i++)
                {
                    Check(collection.Item(i, out var device));
                    result.Add(new Device(GetId(device), GetFriendlyName(device)));
                    Marshal.ReleaseComObject(device);
                }
                Marshal.ReleaseComObject(collection);
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
            return result;
        }

        public static Device GetDefaultOutput()
        {
            var enumerator = CreateEnumerator();
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device) != 0 || device == null)
                    return null;
                var d = new Device(GetId(device), GetFriendlyName(device));
                Marshal.ReleaseComObject(device);
                return d;
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }

        public static void SetDefaultOutput(string deviceId)
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            try
            {
                Check(policy.SetDefaultEndpoint(deviceId, ERole.Console));
                Check(policy.SetDefaultEndpoint(deviceId, ERole.Multimedia));
                Check(policy.SetDefaultEndpoint(deviceId, ERole.Communications));
            }
            finally
            {
                Marshal.ReleaseComObject(policy);
            }
        }

        /// <summary>
        /// Переключает устройство вывода. Пустой аргумент — следующее по кругу.
        /// «Наушники» — первое, в названии которого есть это слово.
        /// «Наушники | Динамики» — чередует перечисленные. Возвращает имя нового устройства.
        /// </summary>
        public static string Switch(string argument)
        {
            var all = ListOutputs();
            if (all.Count == 0) throw new InvalidOperationException("не нашёл ни одного устройства вывода");

            var current = GetDefaultOutput();
            List<Device> pool;

            var wanted = (argument ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (wanted.Length == 0)
            {
                pool = all;
            }
            else
            {
                pool = wanted
                    .Select(w => all.FirstOrDefault(d => d.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                    .Where(d => d != null)
                    .Distinct()
                    .ToList();
                if (pool.Count == 0) throw new InvalidOperationException($"нет устройства с названием «{argument}»");
            }

            int idx = current == null ? -1 : pool.FindIndex(d => d.Id == current.Id);
            var next = pool[(idx + 1) % pool.Count];
            SetDefaultOutput(next.Id);
            return next.Name;
        }

        /// <summary>Громкость 0..100 и mute устройства по умолчанию; null — если не получилось.</summary>
        public static (int Volume, bool Muted)? GetVolume()
        {
            var enumerator = CreateEnumerator();
            IMMDevice device = null;
            object iface = null;
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device) != 0 || device == null)
                    return null;

                var iid = typeof(IAudioEndpointVolume).GUID;
                Check(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out iface));
                var volume = (IAudioEndpointVolume)iface;
                Check(volume.GetMasterVolumeLevelScalar(out float level));
                Check(volume.GetMute(out bool muted));
                return ((int)Math.Round(level * 100), muted);
            }
            catch (Exception ex)
            {
                Log.Warn("Громкость: " + ex.Message);
                return null;
            }
            finally
            {
                if (iface != null) Marshal.ReleaseComObject(iface);
                if (device != null) Marshal.ReleaseComObject(device);
                Marshal.ReleaseComObject(enumerator);
            }
        }

        // ------------------------------------------------------------ helpers

        private static IMMDeviceEnumerator CreateEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

        private static void Check(int hr)
        {
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        }

        private static string GetId(IMMDevice device)
        {
            Check(device.GetId(out string id));
            return id;
        }

        private static string GetFriendlyName(IMMDevice device)
        {
            if (device.OpenPropertyStore(STGM_READ, out var store) != 0 || store == null) return "Устройство";
            try
            {
                var key = PKEY_Device_FriendlyName;
                if (store.GetValue(ref key, out var value) != 0) return "Устройство";
                try
                {
                    return value.vt == VT_LPWSTR && value.pointer != IntPtr.Zero
                        ? Marshal.PtrToStringUni(value.pointer) ?? "Устройство"
                        : "Устройство";
                }
                finally
                {
                    PropVariantClear(ref value);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }

        // ------------------------------------------------------------ COM

        private const uint DEVICE_STATE_ACTIVE = 0x1;
        private const int CLSCTX_ALL = 0x17;
        private const int STGM_READ = 0;
        private const short VT_LPWSTR = 31;

        private static readonly PROPERTYKEY PKEY_Device_FriendlyName =
            new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };

        private enum EDataFlow { Render = 0, Capture = 1, All = 2 }

        private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTYKEY
        {
            public Guid fmtid;
            public int pid;
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PROPVARIANT
        {
            [FieldOffset(0)] public short vt;
            [FieldOffset(8)] public IntPtr pointer;
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PROPVARIANT pvar);

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        private class PolicyConfigClient { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
            [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
            [PreserveSig] int Commit();
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
            [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        /// <summary>Недокументированный, но стабильный с Windows 7 интерфейс смены устройства по умолчанию.</summary>
        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);
            [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool isDefault, IntPtr format);
            [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
            [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
            [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
            [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
            [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
            [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
            [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool isFx, IntPtr key, IntPtr value);
            [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool isFx, IntPtr key, IntPtr value);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool visible);
        }
    }
}
