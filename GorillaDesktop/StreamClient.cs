using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace GorillaDesktop
{
    // Aktarma sunucusuyla konuşan ortak kısım. Mesaj türleri (ilk bayt):
    //   1 = görüntü: [1][genişlik u16][yükseklik u16][JPEG]
    //   2 = ses:     [2][int16 stereo 24 kHz örnekler]
    //   4 = izleyici sayısı (sunucudan yayıncıya): [4][sayı]
    internal abstract class StreamConnection : IDisposable
    {
        public const int AudioRate = 24000;

        public volatile string Status = "Bağlanıyor";
        protected volatile bool running = true;
        private readonly string url;
        private readonly Thread thread;
        protected ClientWebSocket socket;

        protected StreamConnection(string server, string key, string channel, string role, string threadName)
        {
            url = server.TrimEnd('/') + "/s/" + channel + "?role=" + role + "&key=" + Uri.EscapeDataString(key ?? "");
            thread = new Thread(Run) { IsBackground = true, Name = threadName };
            thread.Start();
        }

        // Lobi + yayıncı + anahtardan kanal kimliği: aynı lobideki modlu oyuncular aynı kanalı bulur
        public static string ChannelId(string room, int ownerActor, string key)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(room + "|" + ownerActor + "|" + key));
                var sb = new StringBuilder();
                for (var i = 0; i < 12; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private void Run()
        {
            Com.CoInitializeEx(IntPtr.Zero, Com.COINIT_MULTITHREADED); // WIC için
            var backoff = 1000;
            try
            {
                OnThreadStart();
                while (running)
                {
                    try
                    {
                        Status = "Bağlanıyor";
                        socket = new ClientWebSocket();
                        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                        if (!socket.ConnectAsync(new Uri(url), CancellationToken.None).Wait(15000))
                            throw new Exception("zaman aşımı");
                        backoff = 1000;
                        OnConnected();
                        Session();
                    }
                    catch (Exception e)
                    {
                        if (running) Status = "Bağlantı yok: " + Short(e);
                    }
                    finally
                    {
                        try { socket?.Abort(); } catch { }
                        try { socket?.Dispose(); } catch { }
                        socket = null;
                    }

                    // Yeniden dene (sunucu uykudan uyanıyor olabilir)
                    for (var waited = 0; running && waited < backoff; waited += 100) Thread.Sleep(100);
                    backoff = Math.Min(backoff * 2, 10000);
                }
            }
            finally
            {
                OnThreadEnd();
                Com.CoUninitialize();
            }
        }

        protected virtual void OnThreadStart() { }
        protected virtual void OnThreadEnd() { }
        protected virtual void OnConnected() { }
        protected abstract void Session();

        protected void Send(byte[] data, int length)
        {
            socket.SendAsync(new ArraySegment<byte>(data, 0, length), WebSocketMessageType.Binary, true, CancellationToken.None).Wait();
        }

        // Bir mesajın tamamını oku; bağlantı kapanırsa false
        protected bool Receive(MemoryStream message, byte[] buffer)
        {
            message.SetLength(0);
            while (true)
            {
                var task = socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                task.Wait();
                var result = task.Result;
                if (result.MessageType == WebSocketMessageType.Close) return false;
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) return true;
            }
        }

        private static string Short(Exception e)
        {
            while (e is AggregateException && e.InnerException != null) e = e.InnerException;
            return e.Message.Length > 40 ? e.Message.Substring(0, 40) : e.Message;
        }

        public virtual void Dispose()
        {
            running = false;
            try { socket?.Abort(); } catch { }
            thread.Join(2000);
        }
    }

    // Yayıncı: ekran karelerini JPEG yapıp, sesi 24 kHz'e indirip sunucuya yollar
    internal sealed class StreamPublisher : StreamConnection
    {
        public volatile int Viewers;

        private readonly float quality;
        private readonly int frameIntervalMs;

        // Kare: yakalama thread'i yazar, gönderim thread'i alır (sadece en son kare)
        private readonly object frameLock = new object();
        private byte[] pendingFrame = new byte[0], workFrame = new byte[0];
        private int pendingW, pendingH;
        private bool hasPending;
        private int lastSubmit;

        // Ses: 20 ms'lik parçalar halinde kuyruğa
        private readonly ConcurrentQueue<byte[]> audioQueue = new ConcurrentQueue<byte[]>();
        private byte[] audioChunk = NewAudioChunk();
        private int audioFill;
        private double audioPhase;
        private float accL, accR;
        private int accN;

        private WicJpeg jpeg;
        private byte[] lastJpegMessage;
        private int lastJpegLength, lastSent;

        public StreamPublisher(string server, string key, string channel, float quality, int fps)
            : base(server, key, channel, "pub", "GorillaDesktop.Publisher")
        {
            this.quality = quality;
            frameIntervalMs = 1000 / Math.Max(1, fps);
        }

        public bool WantsFrame => Environment.TickCount - lastSubmit >= frameIntervalMs && Status == "Yayında";

        // Yakalama thread'inden: BGRA üstten alta, satır aralığı 'stride'
        public void SubmitFrame(IntPtr pixels, int width, int height, int stride)
        {
            lastSubmit = Environment.TickCount;
            lock (frameLock)
            {
                var size = width * height * 4;
                if (pendingFrame.Length != size) pendingFrame = new byte[size];
                for (var y = 0; y < height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(pixels + y * stride, pendingFrame, y * width * 4, width * 4);
                pendingW = width;
                pendingH = height;
                hasPending = true;
            }
        }

        public void SubmitFrame(byte[] bgra, int width, int height)
        {
            lastSubmit = Environment.TickCount;
            lock (frameLock)
            {
                if (pendingFrame.Length != bgra.Length) pendingFrame = new byte[bgra.Length];
                Buffer.BlockCopy(bgra, 0, pendingFrame, 0, bgra.Length);
                pendingW = width;
                pendingH = height;
                hasPending = true;
            }
        }

        // Ses yakalama thread'inden, her örnek için. Ortalama alarak 24 kHz'e indirir
        public void PushAudio(float left, float right, int rate)
        {
            accL += left;
            accR += right;
            accN++;
            audioPhase += (double)AudioRate / rate;
            if (audioPhase < 1.0) return;
            audioPhase -= 1.0;

            var l = (short)(Clamp(accL / accN) * 32767f);
            var r = (short)(Clamp(accR / accN) * 32767f);
            accL = accR = 0f;
            accN = 0;

            var o = 1 + audioFill * 4;
            audioChunk[o] = (byte)l;
            audioChunk[o + 1] = (byte)(l >> 8);
            audioChunk[o + 2] = (byte)r;
            audioChunk[o + 3] = (byte)(r >> 8);
            if (++audioFill < AudioRate / 50) return;

            if (Status == "Yayında" && audioQueue.Count < 50) audioQueue.Enqueue(audioChunk);
            audioChunk = NewAudioChunk();
            audioFill = 0;
        }

        private static byte[] NewAudioChunk()
        {
            var chunk = new byte[1 + AudioRate / 50 * 4];
            chunk[0] = 2;
            return chunk;
        }

        private static float Clamp(float v) => v < -1f ? -1f : v > 1f ? 1f : v;

        protected override void OnThreadStart() => jpeg = new WicJpeg();
        protected override void OnThreadEnd() => jpeg?.Dispose();

        protected override void OnConnected()
        {
            while (audioQueue.TryDequeue(out _)) { }
            lastJpegMessage = null;
            Status = "Yayında";

            // İzleyici sayısını dinle
            var reader = new Thread(() =>
            {
                var buffer = new byte[256];
                var message = new MemoryStream();
                var mySocket = socket;
                try
                {
                    while (running && socket == mySocket && Receive(message, buffer))
                        if (message.Length >= 2 && message.GetBuffer()[0] == 4) Viewers = message.GetBuffer()[1];
                }
                catch { }
            }) { IsBackground = true, Name = "GorillaDesktop.PublisherRx" };
            reader.Start();
        }

        protected override void Session()
        {
            while (running && socket.State == WebSocketState.Open)
            {
                var busy = false;

                while (audioQueue.TryDequeue(out var chunk))
                {
                    Send(chunk, chunk.Length);
                    busy = true;
                }

                int w = 0, h = 0;
                lock (frameLock)
                {
                    if (hasPending)
                    {
                        var t = workFrame; workFrame = pendingFrame; pendingFrame = t;
                        w = pendingW; h = pendingH;
                        hasPending = false;
                    }
                }
                if (w > 0)
                {
                    var data = jpeg.Encode(workFrame, w, h, quality);
                    var size = 5 + data.Length;
                    if (lastJpegMessage == null || lastJpegMessage.Length < size) lastJpegMessage = new byte[size + 16 * 1024];
                    lastJpegMessage[0] = 1;
                    lastJpegMessage[1] = (byte)w; lastJpegMessage[2] = (byte)(w >> 8);
                    lastJpegMessage[3] = (byte)h; lastJpegMessage[4] = (byte)(h >> 8);
                    Buffer.BlockCopy(data, 0, lastJpegMessage, 5, data.Length);
                    lastJpegLength = size;
                    Send(lastJpegMessage, size);
                    lastSent = Environment.TickCount;
                    busy = true;
                }
                // Ekran değişmiyorsa son kareyi arada bir tekrar yolla: sonradan gelen izleyici de görsün
                else if (lastJpegMessage != null && Environment.TickCount - lastSent > 1500)
                {
                    Send(lastJpegMessage, lastJpegLength);
                    lastSent = Environment.TickCount;
                }

                if (!busy) Thread.Sleep(5);
            }
        }
    }

    // İzleyici: kareleri açar (ana thread dokuya yükler), sesi halka tampona yazar
    internal sealed class StreamViewer : StreamConnection
    {
        public readonly AudioRing Audio = new AudioRing(AudioRate, 150); // ağ dalgalanmasına karşı daha büyük tampon

        private readonly object frameLock = new object();
        private byte[] latest = new byte[0], decodeBuffer = new byte[0];
        private int latestW, latestH;
        private bool hasFrame;
        private WicJpeg jpeg;

        public StreamViewer(string server, string key, string channel)
            : base(server, key, channel, "sub", "GorillaDesktop.Viewer") { }

        // Ana thread: yeni kare varsa tamponları takas eder
        public bool TryGetFrame(ref byte[] target, out int width, out int height)
        {
            lock (frameLock)
            {
                width = latestW;
                height = latestH;
                if (!hasFrame) return false;
                var t = target; target = latest; latest = t ?? new byte[0];
                hasFrame = false;
                return true;
            }
        }

        protected override void OnThreadStart() => jpeg = new WicJpeg();
        protected override void OnThreadEnd() => jpeg?.Dispose();
        protected override void OnConnected() => Status = "Bağlandı";

        protected override void Session()
        {
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            while (running && Receive(message, buffer))
            {
                var data = message.GetBuffer();
                var length = (int)message.Length;
                if (length < 1) continue;

                if (data[0] == 1 && length > 5)
                {
                    jpeg.Decode(data, 5, length - 5, ref decodeBuffer, out var w, out var h);
                    lock (frameLock)
                    {
                        var t = latest; latest = decodeBuffer; decodeBuffer = t;
                        latestW = w;
                        latestH = h;
                        hasFrame = true;
                    }
                    Status = "İzleniyor";
                }
                else if (data[0] == 2)
                {
                    lock (Audio.WriteLock)
                    {
                        for (var o = 1; o + 3 < length; o += 4)
                        {
                            var l = (short)(data[o] | (data[o + 1] << 8));
                            var r = (short)(data[o + 2] | (data[o + 3] << 8));
                            Audio.WriteUnlocked(l / 32768f, r / 32768f);
                        }
                    }
                }
            }
        }
    }
}
