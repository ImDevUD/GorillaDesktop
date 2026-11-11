using System;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

namespace GorillaDesktop
{
    // Ekranı tamamen ekran kartında, tam çözünürlükte yakalar (DXGI Desktop Duplication).
    //
    // Üretici (kendi thread'imiz + kendi D3D11 cihazımız): kareyi özel dokuya kopyalar, mipmap üretir,
    //   anahtarlı kilit (keyed mutex) boştaysa paylaşılan dokuya aktarır.
    // Tüketici (Unity'nin çizim thread'i, GL.IssuePluginEvent ile): kilit hazırsa paylaşılan dokuyu
    //   Unity'nin kendi dokusuna kopyalar. Kilit 0 ms beklemeyle alınır; oyun ASLA beklemez.
    internal sealed class GpuCapture : IScreenSource
    {
        public string Name => "GPU (Desktop Duplication)";
        public int SourceX { get; private set; }
        public int SourceY { get; private set; }
        public int SourceWidth { get; private set; }
        public int SourceHeight { get; private set; }
        public int Width => SourceWidth;
        public int Height => SourceHeight;
        public bool DrawsCursor => false;
        public Texture Texture => unityTexture;

        private const uint FORMAT_BGRA = 87, FORMAT_BGRA_SRGB = 91;
        private const int WAIT_TIMEOUT = unchecked((int)0x887A0027), ACCESS_LOST = unchecked((int)0x887A0026);
        private const int KEY_WAIT_TIMEOUT = 0x102;

        private readonly int frameIntervalMs;
        private uint format;
        private int mips;

        // Üretici tarafı
        private IntPtr output1, device, context, duplication, privateTexture, privateView, sharedTexture, producerMutex;
        // Tüketici (Unity) tarafı
        private IntPtr unityContext, openedShared, consumerMutex, unityNative;
        private Texture2D unityTexture;

        private Thread thread;
        private volatile bool running;
        private readonly object consumeLock = new object();
        private bool disposed;

        // Unity çizim thread'inden çağrılan geri çağırma; GC toplamasın diye statik
        private static readonly RenderEventFn renderEvent = OnRenderEvent;
        private static readonly IntPtr renderEventPtr = Marshal.GetFunctionPointerForDelegate(renderEvent);
        private static volatile GpuCapture current;

        private AcquireSyncFn consumerAcquire;
        private ReleaseSyncFn consumerRelease;
        private CopySubresourceFn consumerCopy;

        private GpuCapture(int fps)
        {
            frameIntervalMs = 1000 / Math.Max(1, fps);
        }

        // Başarısız olursa null döner ve nedeni loglar; çağıran GDI'a düşer
        public static GpuCapture TryCreate(Native.RECT monitor, int fps)
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
            {
                Plugin.Log.LogWarning($"GPU yakalama sadece Direct3D 11'de çalışır (oyun: {SystemInfo.graphicsDeviceType}). Yedek yönteme geçiliyor.");
                return null;
            }

            var capture = new GpuCapture(fps);
            try
            {
                capture.Init(monitor);
                return capture;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("GPU yakalama başlatılamadı, yedek yönteme geçiliyor: " + e.Message);
                capture.Dispose();
                return null;
            }
        }

        private void Init(Native.RECT monitor)
        {
            // 1) Monitöre bağlı DXGI çıkışını bul
            var iidFactory = IID_IDXGIFactory1;
            Com.Check(CreateDXGIFactory1(ref iidFactory, out var factory), "CreateDXGIFactory1");
            IntPtr adapter = IntPtr.Zero, output = IntPtr.Zero;
            try
            {
                for (uint a = 0; output == IntPtr.Zero && Com.Fn<Com.IndexOutPtrFn>(factory, 12)(factory, a, out var ad) >= 0; a++)
                {
                    for (uint o = 0; Com.Fn<Com.IndexOutPtrFn>(ad, 7)(ad, o, out var op) >= 0; o++)
                    {
                        var desc = Marshal.AllocHGlobal(96);
                        try
                        {
                            Com.Check(Com.Fn<GetDescFn>(op, 7)(op, desc), "IDXGIOutput.GetDesc");
                            var left = Marshal.ReadInt32(desc, 64);
                            var top = Marshal.ReadInt32(desc, 68);
                            var right = Marshal.ReadInt32(desc, 72);
                            var bottom = Marshal.ReadInt32(desc, 76);
                            var rotation = Marshal.ReadInt32(desc, 84);
                            if (left == monitor.Left && top == monitor.Top && right == monitor.Right && bottom == monitor.Bottom)
                            {
                                if (rotation > 1) throw new Exception("döndürülmüş (dikey) monitör desteklenmiyor");
                                output = op;
                                adapter = ad;
                                break;
                            }
                        }
                        finally { Marshal.FreeHGlobal(desc); }
                        Com.Release(ref op);
                    }
                    if (output == IntPtr.Zero) Com.Release(ref ad);
                }
            }
            finally { Com.Release(ref factory); }

            if (output == IntPtr.Zero) throw new Exception("monitör ekran kartı çıkışlarında bulunamadı");

            try
            {
                // 2) O ekran kartında kendi cihazımız
                Com.Check(D3D11CreateDevice(adapter, 0 /* UNKNOWN */, IntPtr.Zero, 0x20 /* BGRA_SUPPORT */, IntPtr.Zero, 0, 7,
                    out device, out _, out context), "D3D11CreateDevice");
                output1 = Com.QueryInterface(output, IID_IDXGIOutput1);
            }
            finally
            {
                Com.Release(ref output);
                Com.Release(ref adapter);
            }

            CreateDuplication();

            SourceX = monitor.Left;
            SourceY = monitor.Top;
            SourceWidth = monitor.Width;
            SourceHeight = monitor.Height;

            // 3) Unity'nin dokusu: tam çözünürlük + mipmap (uzaktan titremesin), üç doğrusal + anizotropik süzme
            var linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            format = linear ? FORMAT_BGRA_SRGB : FORMAT_BGRA;
            unityTexture = new Texture2D(SourceWidth, SourceHeight, TextureFormat.BGRA32, true, false)
            {
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
                wrapMode = TextureWrapMode.Clamp,
                name = "GorillaDesktop.Screen",
            };
            unityTexture.Apply(false, true); // CPU kopyasını bırak
            mips = unityTexture.mipmapCount;
            unityNative = unityTexture.GetNativeTexturePtr();

            // 4) Üretici dokuları
            privateTexture = CreateTexture(device, 0x20 | 0x8 /* RENDER_TARGET | SHADER_RESOURCE */, 0x1 /* GENERATE_MIPS */);
            Com.Check(Com.Fn<CreateViewFn>(device, 7)(device, privateTexture, IntPtr.Zero, out privateView), "CreateShaderResourceView");
            sharedTexture = CreateTexture(device, 0x8, 0x100 /* SHARED_KEYEDMUTEX */);
            producerMutex = Com.QueryInterface(sharedTexture, IID_IDXGIKeyedMutex);

            var resource = Com.QueryInterface(sharedTexture, IID_IDXGIResource);
            IntPtr handle;
            try { Com.Check(Com.Fn<Com.OutPtrFn>(resource, 8)(resource, out handle), "GetSharedHandle"); }
            finally { Com.Release(ref resource); }

            // 5) Unity'nin cihazında paylaşılan dokuyu aç
            Com.Fn<GetDeviceFn>(unityNative, 3)(unityNative, out var unityDevice);
            if (unityDevice == IntPtr.Zero) throw new Exception("Unity'nin ekran kartı cihazı alınamadı");
            try
            {
                var iidTex = IID_ID3D11Texture2D;
                Com.Check(Com.Fn<OpenSharedFn>(unityDevice, 28)(unityDevice, handle, ref iidTex, out openedShared),
                    "OpenSharedResource (oyun ve monitör farklı ekran kartlarında olabilir)");
                Com.Fn<GetDeviceFn>(unityDevice, 40)(unityDevice, out unityContext); // GetImmediateContext
            }
            finally { Com.Release(ref unityDevice); }

            consumerMutex = Com.QueryInterface(openedShared, IID_IDXGIKeyedMutex);
            consumerAcquire = Com.Fn<AcquireSyncFn>(consumerMutex, 8);
            consumerRelease = Com.Fn<ReleaseSyncFn>(consumerMutex, 9);
            consumerCopy = Com.Fn<CopySubresourceFn>(unityContext, 46);

            current = this;
            Plugin.Log.LogInfo($"GPU yakalama hazır: {SourceWidth}x{SourceHeight}, {mips} mip seviyesi");
        }

        private IntPtr CreateTexture(IntPtr dev, uint bind, uint misc)
        {
            // D3D11_TEXTURE2D_DESC: Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality,
            // Usage (DEFAULT), BindFlags, CPUAccessFlags, MiscFlags
            var desc = new[] { SourceWidth, SourceHeight, mips, 1, (int)format, 1, 0, 0, (int)bind, 0, (int)misc };
            var ptr = Marshal.AllocHGlobal(desc.Length * 4);
            try
            {
                Marshal.Copy(desc, 0, ptr, desc.Length);
                Com.Check(Com.Fn<CreateTextureFn>(dev, 5)(dev, ptr, IntPtr.Zero, out var tex), "CreateTexture2D");
                return tex;
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        private void CreateDuplication()
        {
            Com.Release(ref duplication);
            Com.Check(Com.Fn<DuplicateOutputFn>(output1, 22)(output1, device, out duplication), "DuplicateOutput");

            // HDR açıksa görüntü 16-bit float gelir; o durumda desteklemiyoruz
            var desc = Marshal.AllocHGlobal(36);
            try
            {
                Com.Fn<GetDescFn>(duplication, 7)(duplication, desc);
                var fmt = (uint)Marshal.ReadInt32(desc, 16);
                if (fmt != FORMAT_BGRA) throw new Exception($"desteklenmeyen masaüstü formatı {fmt} (Windows HDR açık olabilir)");
            }
            finally { Marshal.FreeHGlobal(desc); }
        }

        public void Start()
        {
            if (running) return;
            running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "GorillaDesktop.GpuCapture" };
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            thread?.Join(1000);
            thread = null;
        }

        // Ana thread: Unity'nin çizim thread'ine "hazır kare varsa kopyala" komutu gönder
        public void Update()
        {
            GL.IssuePluginEvent(renderEventPtr, 0);
        }

        // Unity çizim thread'i
        private static void OnRenderEvent(int eventId)
        {
            var self = current;
            if (self == null) return;
            lock (self.consumeLock)
            {
                if (self.disposed) return;
                try
                {
                    // 0 ms: yeni kare hazır değilse hiç bekleme, bir sonraki kareye kalsın
                    if (self.consumerAcquire(self.consumerMutex, 1, 0) != 0) return;
                    for (uint m = 0; m < self.mips; m++)
                        self.consumerCopy(self.unityContext, self.unityNative, m, 0, 0, 0, self.openedShared, m, IntPtr.Zero);
                    self.consumerRelease(self.consumerMutex, 0);
                }
                catch { }
            }
        }

        private void Loop()
        {
            var info = Marshal.AllocHGlobal(64);
            var acquire = Com.Fn<AcquireFn>(duplication, 8);
            var releaseFrame = Com.Fn<Com.NoArgFn>(duplication, 14);
            var copy = Com.Fn<CopySubresourceFn>(context, 46);
            var generateMips = Com.Fn<GenerateMipsFn>(context, 54);
            var flush = Com.Fn<FlushFn>(context, 111);
            var producerAcquire = Com.Fn<AcquireSyncFn>(producerMutex, 8);
            var producerRelease = Com.Fn<ReleaseSyncFn>(producerMutex, 9);
            var dirty = false;

            try
            {
                while (running)
                {
                    var start = Environment.TickCount;
                    var hr = acquire(duplication, (uint)frameIntervalMs, info, out var resource);

                    if (hr == ACCESS_LOST || (hr < 0 && hr != WAIT_TIMEOUT))
                    {
                        // Çözünürlük değişti, UAC ekranı vb. Yeniden bağlan
                        if (hr != ACCESS_LOST) Plugin.Log.LogWarning($"AcquireNextFrame 0x{hr:X8}, yeniden bağlanılıyor");
                        Thread.Sleep(250);
                        try
                        {
                            CreateDuplication();
                            acquire = Com.Fn<AcquireFn>(duplication, 8);
                            releaseFrame = Com.Fn<Com.NoArgFn>(duplication, 14);
                        }
                        catch (Exception e) { Plugin.Log.LogWarning("Yeniden bağlanamadı: " + e.Message); Thread.Sleep(1000); }
                        continue;
                    }

                    if (hr >= 0)
                    {
                        try
                        {
                            // LastPresentTime 0 ise sadece fare oynamış; görüntü aynı
                            if (Marshal.ReadInt64(info, 0) != 0)
                            {
                                var frameTex = Com.QueryInterface(resource, IID_ID3D11Texture2D);
                                try { copy(context, privateTexture, 0, 0, 0, 0, frameTex, 0, IntPtr.Zero); }
                                finally { Com.Release(ref frameTex); }
                                generateMips(context, privateView);
                                dirty = true;
                            }
                        }
                        finally
                        {
                            Com.Release(ref resource);
                            releaseFrame(duplication);
                        }
                    }

                    // Unity önceki kareyi aldıysa (kilit bizde) yenisini ver; almadıysa sonra dene
                    if (dirty && producerAcquire(producerMutex, 0, 0) == 0)
                    {
                        for (uint m = 0; m < mips; m++)
                            copy(context, sharedTexture, m, 0, 0, 0, privateTexture, m, IntPtr.Zero);
                        producerRelease(producerMutex, 1);
                        flush(context);
                        dirty = false;
                    }

                    var wait = frameIntervalMs - (Environment.TickCount - start);
                    if (wait > 0) Thread.Sleep(wait);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("GPU yakalama durdu: " + e);
            }
            finally { Marshal.FreeHGlobal(info); }
        }

        public void Dispose()
        {
            Stop();
            if (current == this) current = null;
            lock (consumeLock)
            {
                disposed = true;
                Com.Release(ref consumerMutex);
                Com.Release(ref openedShared);
                Com.Release(ref unityContext);
            }
            if (unityTexture != null) UnityEngine.Object.Destroy(unityTexture);
            unityTexture = null;
            Com.Release(ref producerMutex);
            Com.Release(ref sharedTexture);
            Com.Release(ref privateView);
            Com.Release(ref privateTexture);
            Com.Release(ref duplication);
            Com.Release(ref output1);
            Com.Release(ref context);
            Com.Release(ref device);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void RenderEventFn(int eventId);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDescFn(IntPtr self, IntPtr desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DuplicateOutputFn(IntPtr self, IntPtr device, out IntPtr duplication);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AcquireFn(IntPtr self, uint timeoutMs, IntPtr info, out IntPtr resource);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTextureFn(IntPtr self, IntPtr desc, IntPtr initial, out IntPtr texture);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateViewFn(IntPtr self, IntPtr resource, IntPtr desc, out IntPtr view);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OpenSharedFn(IntPtr self, IntPtr handle, ref Guid iid, out IntPtr resource);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetDeviceFn(IntPtr self, out IntPtr device);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopySubresourceFn(IntPtr self, IntPtr dst, uint dstSub, uint x, uint y, uint z, IntPtr src, uint srcSub, IntPtr box);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GenerateMipsFn(IntPtr self, IntPtr view);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FlushFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AcquireSyncFn(IntPtr self, ulong key, uint ms);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReleaseSyncFn(IntPtr self, ulong key);

        private static readonly Guid IID_IDXGIFactory1 = new Guid("770AAE78-F26F-4DBA-A829-253C83D1B387");
        private static readonly Guid IID_IDXGIOutput1 = new Guid("00CDDEA8-939B-4B83-A340-A685226666CC");
        private static readonly Guid IID_IDXGIResource = new Guid("035F3AB4-482E-4E50-B41F-8A7F8BD8960B");
        private static readonly Guid IID_IDXGIKeyedMutex = new Guid("9D8E1289-D7B3-465F-8126-250E349AF85D");
        private static readonly Guid IID_ID3D11Texture2D = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

        [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels,
            uint levelCount, uint sdkVersion, out IntPtr device, out int level, out IntPtr context);
    }
}
