using System;
using UnityEngine;

namespace GorillaDesktop
{
    // Oyun içi ekrana görüntü veren kaynak (GPU ya da GDI yedek)
    internal interface IScreenSource : IDisposable
    {
        string Name { get; }
        int SourceX { get; }
        int SourceY { get; }
        int SourceWidth { get; }
        int SourceHeight { get; }
        int Width { get; }
        int Height { get; }
        // false ise imleci oyun içinde biz çizeriz
        bool DrawsCursor { get; }
        Texture Texture { get; }
        void Start();
        void Stop();
        // Ana thread'de her kare çağrılır
        void Update();
    }

    // Yedek yöntem: GDI ile işlemcide yakalama + dokuya yükleme
    internal sealed class GdiSource : IScreenSource
    {
        private readonly DesktopCapture capture;
        private readonly Texture2D texture;
        private byte[] frame;

        public GdiSource(Native.RECT monitor, int maxWidth, int fps)
        {
            capture = new DesktopCapture(monitor, maxWidth, fps);
            texture = new Texture2D(capture.Width, capture.Height, TextureFormat.BGRA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            Plugin.Log.LogInfo($"GDI yakalama: {capture.SourceWidth}x{capture.SourceHeight} -> {capture.Width}x{capture.Height}");
        }

        public string Name => "GDI (yedek)";
        public int SourceX => capture.SourceX;
        public int SourceY => capture.SourceY;
        public int SourceWidth => capture.SourceWidth;
        public int SourceHeight => capture.SourceHeight;
        public int Width => capture.Width;
        public int Height => capture.Height;
        public bool DrawsCursor => true;
        public Texture Texture => texture;

        public void Start() => capture.Start();
        public void Stop() => capture.Stop();

        public void Update()
        {
            // Sadece görüntü değiştiyse yükle
            if (!capture.TryGetFrame(ref frame)) return;
            texture.LoadRawTextureData(frame);
            texture.Apply(false);
        }

        public void Dispose()
        {
            capture.Dispose();
            UnityEngine.Object.Destroy(texture);
        }
    }
}
