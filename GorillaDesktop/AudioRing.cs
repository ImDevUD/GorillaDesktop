using System;

namespace GorillaDesktop
{
    // Ses kaynağı: Unity ses thread'i ReadInto ile çeker (AudioPlayer)
    internal interface IAudioFeed
    {
        void ReadInto(float[] data, int channels, int outputRate);
    }

    // Stereo halka tampon. Bir thread yazar (yakalama ya da ağ), Unity ses thread'i okur.
    // Okurken örnekleme hızını dönüştürür ve gecikmeyi hızı hafifçe ayarlayarak sabit tutar.
    internal sealed class AudioRing : IAudioFeed
    {
        private readonly object ringLock = new object();
        private float[] ring;
        private int readPos, count;
        private double phase;
        private bool primed;
        private readonly int targetMs;

        public int SampleRate { get; private set; }

        public AudioRing(int sampleRate, int targetMs = 40)
        {
            this.targetMs = targetMs;
            Reset(sampleRate);
        }

        public void Reset(int sampleRate)
        {
            lock (ringLock)
            {
                SampleRate = sampleRate;
                ring = new float[sampleRate * 2]; // 1 saniye stereo
                readPos = 0; count = 0; phase = 0; primed = false;
            }
        }

        public void Clear()
        {
            lock (ringLock) { readPos = 0; count = 0; phase = 0; primed = false; }
        }

        // Yazıcı thread kilidi bir paket boyunca tutar (her örnekte kilitlenmesin)
        public object WriteLock => ringLock;

        // WriteLock altında çağrılmalı
        public void WriteUnlocked(float left, float right)
        {
            if (count + 2 > ring.Length) return; // dolu: ReadInto fazlasını zaten atar
            var w = (readPos + count) % ring.Length;
            ring[w] = left;
            ring[w + 1] = right;
            count += 2;
        }

        public void ReadInto(float[] data, int channels, int outputRate)
        {
            var frames = data.Length / channels;
            lock (ringLock)
            {
                var buffered = count / 2;
                var target = SampleRate * targetMs / 1000;

                // Tampon dolana kadar sessiz kal (kopuk kopuk çalmasın)
                if (!primed)
                {
                    if (buffered < target) { Array.Clear(data, 0, data.Length); return; }
                    primed = true;
                }

                // Çok birikmişse (ör. oyun takıldı) bir kerede hedefe in
                if (buffered > SampleRate / 2) Skip(buffered - target);
                buffered = count / 2;

                var step = (double)SampleRate / outputRate;
                if (buffered > target * 3) step *= 1.02;      // geride kaldık: hafif hızlan
                else if (buffered < target / 2) step *= 0.98; // tampon azaldı: hafif yavaşla

                var f = 0;
                for (; f < frames; f++)
                {
                    var i0 = (int)phase;
                    if (i0 + 1 >= buffered) break;
                    var t = (float)(phase - i0);
                    var a = (readPos + i0 * 2) % ring.Length;
                    var b = (a + 2) % ring.Length;
                    var l = ring[a] + (ring[b] - ring[a]) * t;
                    var r = ring[a + 1] + (ring[b + 1] - ring[a + 1]) * t;

                    var o = f * channels;
                    data[o] *= l;
                    if (channels > 1) data[o + 1] *= r;
                    for (var c = 2; c < channels; c++) data[o + c] *= (l + r) * 0.5f;
                    phase += step;
                }

                // Tükendi: kalanı sustur ve yeniden dolmasını bekle
                if (f < frames)
                {
                    Array.Clear(data, f * channels, (frames - f) * channels);
                    primed = false;
                }

                var consumed = Math.Min((int)phase, buffered);
                Skip(consumed);
                phase -= consumed;
            }
        }

        private void Skip(int frames)
        {
            readPos = (readPos + frames * 2) % ring.Length;
            count -= frames * 2;
        }
    }
}
