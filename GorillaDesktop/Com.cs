using System;
using System.Runtime.InteropServices;

namespace GorillaDesktop
{
    // COM/DirectX arayüzlerini vtable üzerinden elle çağırmak için yardımcılar.
    // Mono'nun COM desteğine güvenmek yerine her metodu sıra numarasıyla çağırıyoruz.
    internal static class Com
    {
        // Delegeler fonksiyon adresine göre önbelleklenir: her çağrıda yeni nesne oluşmasın
        // (arka plan thread'lerindeki çöp bile GC duraklamasıyla oyunu takılttırır)
        private static readonly System.Collections.Generic.Dictionary<(IntPtr, Type), Delegate> cache =
            new System.Collections.Generic.Dictionary<(IntPtr, Type), Delegate>();

        public static T Fn<T>(IntPtr obj, int slot) where T : class
        {
            var fn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size);
            var key = (fn, typeof(T));
            lock (cache)
            {
                if (!cache.TryGetValue(key, out var d))
                {
                    d = Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
                    cache[key] = d;
                }
                return (T)(object)d;
            }
        }

        public static void Release(ref IntPtr obj)
        {
            if (obj == IntPtr.Zero) return;
            Fn<ReleaseFn>(obj, 2)(obj);
            obj = IntPtr.Zero;
        }

        public static IntPtr QueryInterface(IntPtr obj, Guid iid)
        {
            Check(Fn<QueryInterfaceFn>(obj, 0)(obj, ref iid, out var result), "QueryInterface " + iid);
            return result;
        }

        public static void Check(int hr, string what)
        {
            if (hr < 0) throw new ComException(what, hr);
        }

        public static string PtrToStringAndFree(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            var s = Marshal.PtrToStringUni(p);
            CoTaskMemFree(p);
            return s;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int QueryInterfaceFn(IntPtr self, ref Guid iid, out IntPtr obj);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate uint ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int NoArgFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int OutPtrFn(IntPtr self, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int IndexOutPtrFn(IntPtr self, uint index, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GetCountFn(IntPtr self, out uint count);

        public const int COINIT_MULTITHREADED = 0;
        public const int CLSCTX_ALL = 0x17;

        [DllImport("ole32.dll")] public static extern int CoInitializeEx(IntPtr reserved, int flags);
        [DllImport("ole32.dll")] public static extern void CoUninitialize();
        [DllImport("ole32.dll")] public static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int ctx, ref Guid iid, out IntPtr obj);
        [DllImport("ole32.dll")] public static extern void CoTaskMemFree(IntPtr p);
    }

    internal sealed class ComException : Exception
    {
        public int HResult32 { get; }

        public ComException(string what, int hr) : base($"{what} başarısız (0x{hr:X8})")
        {
            HResult32 = hr;
        }
    }
}
