using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace GorillaDesktop
{
    // WASAPI loopback: bir çıkış cihazına giden sesi yakalayıp halka tampona yazar.
    // Unity ses thread'i ReadInto ile okur; oyun ana döngüsü takılsa bile ses akmaya devam eder.
    // Yayın açıksa aynı ses Publisher'a da verilir.
    internal sealed class AudioLoopback : IAudioFeed, IDisposable
    {
        public string DeviceName { get; private set; }
        public volatile StreamPublisher Publisher;

        private readonly string deviceFilter;
        private readonly AudioRing ring = new AudioRing(48000);
        private Thread thread;
        private volatile bool running;

        public AudioLoopback(string deviceFilter)
        {
            this.deviceFilter = deviceFilter ?? "";
        }

        public void Start()
        {
            if (running) return;
            running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "GorillaDesktop.Audio" };
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            thread?.Join(500);
            thread = null;
            ring.Clear();
        }

        public void ReadInto(float[] data, int channels, int outputRate) => ring.ReadInto(data, channels, outputRate);

        private void Loop()
        {
            IntPtr enumerator = IntPtr.Zero, device = IntPtr.Zero, client = IntPtr.Zero, capture = IntPtr.Zero, format = IntPtr.Zero;
            Com.CoInitializeEx(IntPtr.Zero, Com.COINIT_MULTITHREADED);
            try
            {
                enumerator = AudioDevices.CreateEnumerator();
                device = string.IsNullOrWhiteSpace(deviceFilter)
                    ? AudioDevices.GetDefault(enumerator, AudioDevices.eMultimedia)
                    : AudioDevices.FindByName(enumerator, deviceFilter, log: true);
                if (device == IntPtr.Zero)
                {
                    Plugin.Log.LogWarning($"Ses cihazı bulunamadı: \"{deviceFilter}\". PC sesi oyuna aktarılmayacak.");
                    return;
                }
                DeviceName = AudioDevices.FriendlyName(device);

                var iidClient = IID_IAudioClient;
                Com.Check(Com.Fn<ActivateFn>(device, 3)(device, ref iidClient, Com.CLSCTX_ALL, IntPtr.Zero, out client), "Activate");
                Com.Check(Com.Fn<Com.OutPtrFn>(client, 8)(client, out format), "GetMixFormat");

                var channels = (ushort)Marshal.ReadInt16(format, 2);
                var rate = Marshal.ReadInt32(format, 4);
                var bits = (ushort)Marshal.ReadInt16(format, 14);
                ring.Reset(rate);

                Com.Check(Com.Fn<InitializeFn>(client, 3)(client, 0 /* shared */, AUDCLNT_STREAMFLAGS_LOOPBACK, 2000000, 0, format, IntPtr.Zero), "Initialize");
                var iidCapture = IID_IAudioCaptureClient;
                Com.Check(Com.Fn<GetServiceFn>(client, 14)(client, ref iidCapture, out capture), "GetService");
                Com.Check(Com.Fn<Com.NoArgFn>(client, 10)(client), "Start");
                Plugin.Log.LogInfo($"Ses yakalanıyor: {DeviceName} ({rate} Hz, {channels} kanal, {bits} bit)");

                var getNext = Com.Fn<GetNextPacketSizeFn>(capture, 5);
                var getBuffer = Com.Fn<GetBufferFn>(capture, 3);
                var releaseBuffer = Com.Fn<ReleaseBufferFn>(capture, 4);

                while (running)
                {
                    Thread.Sleep(3);
                    while (running && getNext(capture, out var packet) == 0 && packet > 0)
                    {
                        if (getBuffer(capture, out var data, out var frames, out var flags, IntPtr.Zero, IntPtr.Zero) < 0) break;
                        var silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                        var publisher = Publisher;

                        lock (ring.WriteLock)
                        {
                            for (var f = 0; f < frames; f++)
                            {
                                float l = 0f, r = 0f;
                                if (!silent)
                                {
                                    l = Sample(data, f * channels, bits);
                                    r = channels > 1 ? Sample(data, f * channels + 1, bits) : l;
                                }
                                ring.WriteUnlocked(l, r);
                                publisher?.PushAudio(l, r, rate);
                            }
                        }
                        releaseBuffer(capture, frames);
                    }
                }

                Com.Fn<Com.NoArgFn>(client, 11)(client); // Stop
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ses yakalama durdu: " + e.Message);
            }
            finally
            {
                if (format != IntPtr.Zero) Com.CoTaskMemFree(format);
                Com.Release(ref capture);
                Com.Release(ref client);
                Com.Release(ref device);
                Com.Release(ref enumerator);
                Com.CoUninitialize();
            }
        }

        private static unsafe float Sample(IntPtr data, int index, int bits)
        {
            if (bits == 16) return ((short*)data)[index] / 32768f;
            return ((float*)data)[index];
        }

        public void Dispose() => Stop();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ActivateFn(IntPtr self, ref Guid iid, int ctx, IntPtr p, out IntPtr iface);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InitializeFn(IntPtr self, int share, uint flags, long buffer, long period, IntPtr format, IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetServiceFn(IntPtr self, ref Guid iid, out IntPtr service);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetNextPacketSizeFn(IntPtr self, out uint frames);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetBufferFn(IntPtr self, out IntPtr data, out uint frames, out uint flags, IntPtr devicePos, IntPtr qpcPos);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReleaseBufferFn(IntPtr self, uint frames);

        private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        private static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        private static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    }
}
