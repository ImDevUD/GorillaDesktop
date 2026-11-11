using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace GorillaDesktop
{
    // Ana monitörü arka plan thread'inde GDI ile yakalar, küçültür ve imleci üstüne çizer.
    // Ana thread TryGetFrame ile en son kareyi (BGRA, üstten alta) alır.
    internal sealed class DesktopCapture : IDisposable
    {
        // Yakalanan monitörün sanal masaüstündeki konumu
        public int SourceX { get; }
        public int SourceY { get; }
        public int SourceWidth { get; }
        public int SourceHeight { get; }
        public int Width { get; }
        public int Height { get; }

        private readonly int frameIntervalMs;
        private readonly object frameLock = new object();
        private byte[] latest;
        private bool hasNewFrame;
        private Thread thread;
        private volatile bool running;

        public DesktopCapture(Native.RECT monitor, int maxWidth, int fps)
        {
            SourceX = monitor.Left;
            SourceY = monitor.Top;
            SourceWidth = monitor.Width;
            SourceHeight = monitor.Height;
            Width = Math.Min(maxWidth, SourceWidth);
            Height = (int)Math.Round(SourceHeight * (Width / (double)SourceWidth));
            frameIntervalMs = 1000 / Math.Max(1, fps);
            latest = new byte[Width * Height * 4];
        }

        public void Start()
        {
            if (running) return;
            running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "GorillaDesktop.Capture" };
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            thread?.Join(500);
            thread = null;
        }

        public bool TryGetFrame(ref byte[] target)
        {
            lock (frameLock)
            {
                if (!hasNewFrame) return false;
                // Tamponları takas et; kopyalama yok
                var tmp = target;
                target = latest;
                latest = tmp ?? new byte[Width * Height * 4];
                hasNewFrame = false;
                return true;
            }
        }

        private void Loop()
        {
            Native.TrySetDpiAware();
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            var bmi = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = Width,
                biHeight = -Height, // negatif = üstten alta
                biPlanes = 1,
                biBitCount = 32,
            };
            IntPtr dib = Native.CreateDIBSection(screenDc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            IntPtr old = Native.SelectObject(memDc, dib);
            Native.SetStretchBltMode(memDc, Native.HALFTONE);
            Native.SetBrushOrgEx(memDc, 0, 0, IntPtr.Zero);
            var size = Width * Height * 4;
            var scratch = new byte[size];

            try
            {
                while (running)
                {
                    var start = Environment.TickCount;

                    Native.StretchBlt(memDc, 0, 0, Width, Height, screenDc, SourceX, SourceY, SourceWidth, SourceHeight, Native.SRCCOPY);
                    DrawCursor(memDc);
                    Marshal.Copy(bits, scratch, 0, size);

                    // Görüntü değişmediyse ana thread'e yükleme yaptırma
                    var sum = Checksum(scratch);
                    if (sum != lastChecksum)
                    {
                        lastChecksum = sum;
                        lock (frameLock)
                        {
                            var tmp = latest;
                            latest = scratch;
                            scratch = tmp;
                            hasNewFrame = true;
                        }
                    }

                    var remaining = frameIntervalMs - (Environment.TickCount - start);
                    if (remaining > 0) Thread.Sleep(remaining);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ekran yakalama durdu: " + e);
            }
            finally
            {
                Native.SelectObject(memDc, old);
                Native.DeleteObject(dib);
                Native.DeleteDC(memDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private long lastChecksum = -1;

        // Her 16. pikseli toplayan ucuz değişim kontrolü
        private static unsafe long Checksum(byte[] data)
        {
            long sum = 0;
            fixed (byte* p = data)
            {
                var ints = (int*)p;
                var n = data.Length / 4;
                for (var i = 0; i < n; i += 16) sum = sum * 31 + ints[i];
            }
            return sum;
        }

        // GDI yakalaması imleci içermez; ölçekli olarak elle çiziyoruz
        private void DrawCursor(IntPtr dc)
        {
            var ci = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
            if (!Native.GetCursorInfo(ref ci) || (ci.flags & Native.CURSOR_SHOWING) == 0) return;

            int hx = 0, hy = 0;
            if (Native.GetIconInfo(ci.hCursor, out var info))
            {
                hx = info.xHotspot;
                hy = info.yHotspot;
                if (info.hbmMask != IntPtr.Zero) Native.DeleteObject(info.hbmMask);
                if (info.hbmColor != IntPtr.Zero) Native.DeleteObject(info.hbmColor);
            }

            var scale = Width / (double)SourceWidth;
            // Küçültülmüş görüntüde kaybolmasın diye imleci biraz büyük tut
            var cursorSize = Math.Max(16, (int)(32 * scale * 1.5));
            var x = (int)((ci.ptScreenPos.X - SourceX - hx) * scale);
            var y = (int)((ci.ptScreenPos.Y - SourceY - hy) * scale);
            Native.DrawIconEx(dc, x, y, ci.hCursor, cursorSize, cursorSize, 0, IntPtr.Zero, Native.DI_NORMAL);
        }

        public void Dispose() => Stop();
    }
}
