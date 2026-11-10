using System;
using System.Runtime.InteropServices;

namespace GorillaDesktop
{
    // Windows ses cihazlarını (MMDevice) listeleme yardımcıları. COM başlatılmış bir thread'de çağrılmalı.
    internal static class AudioDevices
    {
        public const int eRender = 0;
        public const int eConsole = 0, eMultimedia = 1;

        public static IntPtr CreateEnumerator()
        {
            var clsid = CLSID_MMDeviceEnumerator;
            var iid = IID_IMMDeviceEnumerator;
            Com.Check(Com.CoCreateInstance(ref clsid, IntPtr.Zero, Com.CLSCTX_ALL, ref iid, out var enumerator), "MMDeviceEnumerator");
            return enumerator;
        }

        public static IntPtr GetDefault(IntPtr enumerator, int role)
        {
            Com.Check(Com.Fn<GetDefaultEndpointFn>(enumerator, 4)(enumerator, eRender, role, out var device), "GetDefaultAudioEndpoint");
            return device;
        }

        // Adı filtreyi içeren ilk aktif çıkış cihazı (bulunamazsa Zero)
        public static IntPtr FindByName(IntPtr enumerator, string filter, bool log = false)
        {
            Com.Check(Com.Fn<EnumEndpointsFn>(enumerator, 3)(enumerator, eRender, DEVICE_STATE_ACTIVE, out var collection), "EnumAudioEndpoints");
            try
            {
                Com.Fn<Com.GetCountFn>(collection, 3)(collection, out var n);
                for (uint i = 0; i < n; i++)
                {
                    if (Com.Fn<Com.IndexOutPtrFn>(collection, 4)(collection, i, out var dev) < 0) continue;
                    var name = FriendlyName(dev);
                    if (log) Plugin.Log.LogInfo("Ses çıkışı: " + name);
                    if (name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return dev;
                    Com.Release(ref dev);
                }
            }
            finally { Com.Release(ref collection); }
            return IntPtr.Zero;
        }

        public static string Id(IntPtr device)
        {
            Com.Check(Com.Fn<Com.OutPtrFn>(device, 5)(device, out var id), "IMMDevice.GetId");
            return Com.PtrToStringAndFree(id);
        }

        public static string FriendlyName(IntPtr device)
        {
            if (Com.Fn<OpenPropertyStoreFn>(device, 4)(device, 0 /* STGM_READ */, out var store) < 0) return "?";
            var pv = Marshal.AllocHGlobal(24);
            try
            {
                for (var i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
                var key = PKEY_Device_FriendlyName;
                if (Com.Fn<GetValueFn>(store, 5)(store, ref key, pv) < 0) return "?";
                var name = Marshal.ReadInt16(pv) == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(pv, 8)) : "?";
                PropVariantClear(pv);
                return name ?? "?";
            }
            finally
            {
                Marshal.FreeHGlobal(pv);
                Com.Release(ref store);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTYKEY { public Guid fmtid; public int pid; }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumEndpointsFn(IntPtr self, int flow, int mask, out IntPtr collection);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDefaultEndpointFn(IntPtr self, int flow, int role, out IntPtr device);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OpenPropertyStoreFn(IntPtr self, int access, out IntPtr store);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetValueFn(IntPtr self, ref PROPERTYKEY key, IntPtr pv);

        private const int DEVICE_STATE_ACTIVE = 1;
        private static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid IID_IMMDeviceEnumerator = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
        private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new PROPERTYKEY { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };

        [DllImport("ole32.dll")] private static extern int PropVariantClear(IntPtr pv);
    }
}
