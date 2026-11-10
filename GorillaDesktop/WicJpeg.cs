using System;
using System.Runtime.InteropServices;

namespace GorillaDesktop
{
    // Windows Imaging Component ile JPEG sıkıştırma/açma. Unity'ye dokunmaz, her thread'de çalışır
    // (Unity'nin JPEG fonksiyonları ana thread'e bağlı ve oyunu takılttırırdı).
    // Her thread kendi örneğini kullanmalı; COM o thread'de başlatılmış olmalı.
    internal sealed class WicJpeg : IDisposable
    {
        private IntPtr factory;
        private byte[] bgr = new byte[0];

        public WicJpeg()
        {
            var clsid = CLSID_WICImagingFactory;
            var iid = IID_IWICImagingFactory;
            Com.Check(Com.CoCreateInstance(ref clsid, IntPtr.Zero, Com.CLSCTX_ALL, ref iid, out factory), "WICImagingFactory");
        }

        // BGRA (üstten alta) -> JPEG
        public byte[] Encode(byte[] bgra, int width, int height, float quality)
        {
            // JPEG kodlayıcı 24 bit BGR ister
            var bgrStride = width * 3;
            if (bgr.Length < bgrStride * height) bgr = new byte[bgrStride * height];
            unsafe
            {
                fixed (byte* src = bgra)
                fixed (byte* dst = bgr)
                {
                    var n = width * height;
                    for (int i = 0, s = 0, d = 0; i < n; i++, s += 4, d += 3)
                    {
                        dst[d] = src[s];
                        dst[d + 1] = src[s + 1];
                        dst[d + 2] = src[s + 2];
                    }
                }
            }

            IntPtr stream = IntPtr.Zero, encoder = IntPtr.Zero, frame = IntPtr.Zero, props = IntPtr.Zero;
            try
            {
                Com.Check(CreateStreamOnHGlobal(IntPtr.Zero, true, out stream), "CreateStreamOnHGlobal");
                var container = GUID_ContainerFormatJpeg;
                Com.Check(Com.Fn<CreateEncoderFn>(factory, 8)(factory, ref container, IntPtr.Zero, out encoder), "CreateEncoder");
                Com.Check(Com.Fn<EncoderInitFn>(encoder, 3)(encoder, stream, 2 /* NoCache */), "Encoder.Initialize");
                Com.Check(Com.Fn<CreateFrameFn>(encoder, 10)(encoder, out frame, out props), "CreateNewFrame");
                SetQuality(props, quality);
                Com.Check(Com.Fn<FrameInitFn>(frame, 3)(frame, props), "Frame.Initialize");
                Com.Check(Com.Fn<SetSizeFn>(frame, 4)(frame, (uint)width, (uint)height), "SetSize");
                var format = GUID_WICPixelFormat24bppBGR;
                Com.Check(Com.Fn<SetPixelFormatFn>(frame, 6)(frame, ref format), "SetPixelFormat");
                unsafe
                {
                    fixed (byte* p = bgr)
                        Com.Check(Com.Fn<WritePixelsFn>(frame, 10)(frame, (uint)height, (uint)bgrStride, (uint)(bgrStride * height), (IntPtr)p), "WritePixels");
                }
                Com.Check(Com.Fn<Com.NoArgFn>(frame, 12)(frame), "Frame.Commit");
                Com.Check(Com.Fn<Com.NoArgFn>(encoder, 11)(encoder), "Encoder.Commit");

                // Akıştaki baytları al
                Com.Check(Com.Fn<SeekFn>(stream, 5)(stream, 0, 1 /* CUR */, out var size), "Seek");
                Com.Check(GetHGlobalFromStream(stream, out var hglobal), "GetHGlobalFromStream");
                var ptr = GlobalLock(hglobal);
                try
                {
                    var result = new byte[size];
                    Marshal.Copy(ptr, result, 0, (int)size);
                    return result;
                }
                finally { GlobalUnlock(hglobal); }
            }
            finally
            {
                Com.Release(ref props);
                Com.Release(ref frame);
                Com.Release(ref encoder);
                Com.Release(ref stream);
            }
        }

        // JPEG -> BGRA (üstten alta). 'bgra' gerekirse büyütülür
        public void Decode(byte[] jpeg, int offset, int length, ref byte[] bgra, out int width, out int height)
        {
            IntPtr memory = Marshal.AllocHGlobal(length), stream = IntPtr.Zero, decoder = IntPtr.Zero, frame = IntPtr.Zero, converter = IntPtr.Zero;
            try
            {
                Marshal.Copy(jpeg, offset, memory, length);
                Com.Check(Com.Fn<Com.OutPtrFn>(factory, 14)(factory, out stream), "CreateStream");
                Com.Check(Com.Fn<InitFromMemoryFn>(stream, 16)(stream, memory, (uint)length), "InitializeFromMemory");
                Com.Check(Com.Fn<CreateDecoderFromStreamFn>(factory, 4)(factory, stream, IntPtr.Zero, 0 /* OnDemand */, out decoder), "CreateDecoderFromStream");
                Com.Check(Com.Fn<Com.IndexOutPtrFn>(decoder, 13)(decoder, 0, out frame), "GetFrame");
                Com.Check(Com.Fn<Com.OutPtrFn>(factory, 10)(factory, out converter), "CreateFormatConverter");
                var bgraFormat = GUID_WICPixelFormat32bppBGRA;
                Com.Check(Com.Fn<ConverterInitFn>(converter, 8)(converter, frame, ref bgraFormat, 0, IntPtr.Zero, 0.0, 0), "FormatConverter.Initialize");
                Com.Check(Com.Fn<GetSizeFn>(converter, 3)(converter, out var w, out var h), "GetSize");
                width = (int)w;
                height = (int)h;
                var size = width * height * 4;
                if (bgra == null || bgra.Length != size) bgra = new byte[size];
                unsafe
                {
                    fixed (byte* p = bgra)
                        Com.Check(Com.Fn<CopyPixelsFn>(converter, 7)(converter, IntPtr.Zero, (uint)(width * 4), (uint)size, (IntPtr)p), "CopyPixels");
                }
            }
            finally
            {
                Com.Release(ref converter);
                Com.Release(ref frame);
                Com.Release(ref decoder);
                Com.Release(ref stream);
                Marshal.FreeHGlobal(memory);
            }
        }

        // IPropertyBag2.Write("ImageQuality", VT_R4)
        private static void SetQuality(IntPtr props, float quality)
        {
            var name = Marshal.StringToCoTaskMemUni("ImageQuality");
            var bag = Marshal.AllocHGlobal(40);
            var variant = Marshal.AllocHGlobal(24);
            try
            {
                for (var i = 0; i < 40; i++) Marshal.WriteByte(bag, i, 0);
                for (var i = 0; i < 24; i++) Marshal.WriteByte(variant, i, 0);
                Marshal.WriteIntPtr(bag, 16, name);          // PROPBAG2.pstrName
                Marshal.WriteInt16(variant, 0, 4);            // VT_R4
                Marshal.Copy(new[] { Mathf01(quality) }, 0, variant + 8, 1);
                Com.Fn<PropBagWriteFn>(props, 4)(props, 1, bag, variant);
            }
            finally
            {
                Marshal.FreeHGlobal(variant);
                Marshal.FreeHGlobal(bag);
                Marshal.FreeCoTaskMem(name);
            }
        }

        private static float Mathf01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        public void Dispose() => Com.Release(ref factory);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateEncoderFn(IntPtr self, ref Guid container, IntPtr vendor, out IntPtr encoder);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EncoderInitFn(IntPtr self, IntPtr stream, int cacheOption);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateFrameFn(IntPtr self, out IntPtr frame, out IntPtr props);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FrameInitFn(IntPtr self, IntPtr props);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetSizeFn(IntPtr self, uint width, uint height);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetPixelFormatFn(IntPtr self, ref Guid format);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int WritePixelsFn(IntPtr self, uint lines, uint stride, uint size, IntPtr pixels);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SeekFn(IntPtr self, long move, int origin, out long newPos);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InitFromMemoryFn(IntPtr self, IntPtr buffer, uint size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateDecoderFromStreamFn(IntPtr self, IntPtr stream, IntPtr vendor, int options, out IntPtr decoder);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ConverterInitFn(IntPtr self, IntPtr source, ref Guid format, int dither, IntPtr palette, double alpha, int paletteType);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetSizeFn(IntPtr self, out uint width, out uint height);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CopyPixelsFn(IntPtr self, IntPtr rect, uint stride, uint size, IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PropBagWriteFn(IntPtr self, uint count, IntPtr bags, IntPtr values);

        private static readonly Guid CLSID_WICImagingFactory = new Guid("CACAF262-9370-4615-A13B-9F5539DA4C0A");
        private static readonly Guid IID_IWICImagingFactory = new Guid("EC5EC8A9-C395-4314-9C77-54D7A935FF70");
        private static readonly Guid GUID_ContainerFormatJpeg = new Guid("19E4A5AA-5662-4FC5-A0C0-1758028E1057");
        private static readonly Guid GUID_WICPixelFormat24bppBGR = new Guid("6FDDC324-4E03-4BFE-B185-3D77768DC90C");
        private static readonly Guid GUID_WICPixelFormat32bppBGRA = new Guid("6FDDC324-4E03-4BFE-B185-3D77768DC90F");

        [DllImport("ole32.dll")] private static extern int CreateStreamOnHGlobal(IntPtr hglobal, bool deleteOnRelease, out IntPtr stream);
        [DllImport("ole32.dll")] private static extern int GetHGlobalFromStream(IntPtr stream, out IntPtr hglobal);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);
    }
}
