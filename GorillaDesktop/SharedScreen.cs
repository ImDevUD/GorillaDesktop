using System;
using UnityEngine;

namespace GorillaDesktop
{
    // İzleyenlerin gördüğü ortak ekran: yayıncının koyduğu yerde, aynı çerçeve ve logoyla.
    // Sadece izlenir; tıklanamaz.
    internal sealed class SharedScreen
    {
        private readonly GameObject root;
        private readonly Transform screen;
        private readonly Material material;
        private readonly TextMesh label;
        private readonly AudioSource audioSource;
        private readonly Func<Color, Material> makeMaterial;
        private readonly bool showLogo;
        private MonitorModel frame;
        private Texture2D texture;
        private byte[] pixels = new byte[0];
        private float aspect = 9f / 16f;

        public SharedScreen(Func<Color, Material> makeMaterial, Font font, IAudioFeed audio, float volume, float maxDistance, bool showLogo)
        {
            this.makeMaterial = makeMaterial;
            this.showLogo = showLogo;
            root = new GameObject("GorillaDesktop.Shared");
            UnityEngine.Object.DontDestroyOnLoad(root);

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            UnityEngine.Object.Destroy(quad.GetComponent<Collider>());
            quad.name = "SharedScreen";
            screen = quad.transform;
            screen.SetParent(root.transform, false);

            // Görüntü üstten alta geliyor: UV'leri dikeyde çevir
            var mesh = quad.GetComponent<MeshFilter>().mesh;
            var uvs = mesh.uv;
            for (var i = 0; i < uvs.Length; i++) uvs[i].y = 1f - uvs[i].y;
            mesh.uv = uvs;
            material = makeMaterial(Color.black);
            quad.GetComponent<Renderer>().material = material;

            // Üstte "X yayında" yazısı
            var labelObj = new GameObject("Label");
            labelObj.transform.SetParent(root.transform, false);
            label = labelObj.AddComponent<TextMesh>();
            label.font = font;
            label.fontSize = 64;
            label.characterSize = 0.004f;
            label.anchor = TextAnchor.LowerCenter;
            label.alignment = TextAlignment.Center;
            label.color = Color.white;
            labelObj.GetComponent<MeshRenderer>().material = font.material;

            player = quad.AddComponent<AudioPlayer>();
            audioSource = player.Setup(volume, maxDistance);
            player.Source = audio;

            RebuildFrame();
            root.SetActive(false);
        }

        private readonly AudioPlayer player;
        private readonly float[] prevFingerFront = { 1f, 1f };

        public void SetVolume(float volume) => audioSource.volume = Mathf.Clamp01(volume);

        // Kimse yayında değilken: boş ekran, dokunan alır
        public void ShowIdle(Vector3 position, Quaternion rotation, float width)
        {
            if (!root.activeSelf) root.SetActive(true);
            if (audioSource.isPlaying) audioSource.Stop();
            if (material.mainTexture != null) material.mainTexture = null;
            SetColor(new Color(0.03f, 0.03f, 0.04f));
            Layout(position, rotation, width, "Ekran boş · almak için dokun");
        }

        // Parmak ucu ekrana önden değdi mi (GT düğmesi gibi, bir kez)
        public bool Touched(Transform[] fingers)
        {
            if (!root.activeSelf) return false;
            var hit = false;
            for (var i = 0; i < fingers.Length && i < prevFingerFront.Length; i++)
            {
                var p = fingers[i].position;
                var local = screen.InverseTransformPoint(p);
                var front = Vector3.Dot(p - screen.position, -screen.forward);
                var inside = Mathf.Abs(local.x) <= 0.5f && Mathf.Abs(local.y) <= 0.5f;
                if (inside && front <= 0.01f && front > -0.1f && prevFingerFront[i] > 0.01f) hit = true;
                prevFingerFront[i] = inside ? front : 1f;
            }
            return hit;
        }

        public void Show(Vector3 position, Quaternion rotation, float width, string owner, StreamViewer viewer)
        {
            if (!root.activeSelf) root.SetActive(true);
            if (player.Source != viewer.Audio) player.Source = viewer.Audio;
            if (!audioSource.isPlaying) audioSource.Play();
            if (texture != null && material.mainTexture != texture)
            {
                material.mainTexture = texture;
                SetColor(Color.white);
            }

            // Yeni kare geldiyse dokuya yükle
            if (viewer.TryGetFrame(ref pixels, out var w, out var h) && w > 0 && pixels.Length == w * h * 4)
            {
                if (texture == null || texture.width != w || texture.height != h)
                {
                    if (texture != null) UnityEngine.Object.Destroy(texture);
                    texture = new Texture2D(w, h, TextureFormat.BGRA32, false)
                    {
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp,
                    };
                    material.mainTexture = texture;
                    SetColor(Color.white);
                    var newAspect = h / (float)w;
                    if (Mathf.Abs(newAspect - aspect) > 0.001f) { aspect = newAspect; RebuildFrame(); }
                }
                texture.LoadRawTextureData(pixels);
                texture.Apply(false);
            }

            Layout(position, rotation, width, owner + " yayında" + (viewer.Status == "İzleniyor" ? "" : "  (" + viewer.Status + ")"));
        }

        private void Layout(Vector3 position, Quaternion rotation, float width, string labelText)
        {
            screen.SetPositionAndRotation(position, rotation);
            screen.localScale = new Vector3(width, width * aspect, 1f);
            frame.Follow(screen, width);
            if (label.text != labelText) label.text = labelText;
            label.transform.SetPositionAndRotation(screen.TransformPoint(new Vector3(0f, 0.5f, 0f)) + screen.up * 0.04f, rotation);
        }

        public void Hide()
        {
            if (!root.activeSelf) return;
            audioSource.Stop();
            root.SetActive(false);
        }

        public void Destroy()
        {
            if (texture != null) UnityEngine.Object.Destroy(texture);
            UnityEngine.Object.Destroy(root);
        }

        private void RebuildFrame()
        {
            frame?.Destroy();
            frame = new MonitorModel(root.transform, aspect, makeMaterial, showLogo);
        }

        private void SetColor(Color c)
        {
            material.color = c;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", c);
        }
    }
}
