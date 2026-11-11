using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace GorillaDesktop
{
    // TV tarzÄ± ana menÃ¼ (GPT tasarÄ±mÄ±) ve "+" ile aÃ§Ä±lan uygulama ekleme sayfasÄ±.
    // Kartlar arka planÄ±n Ã¼stÃ¼ne iÅlemcide bir kez dizilip tek bir dokuya yazÄ±lÄ±r; sadece
    // uygulama eklenip Ã§Ä±karÄ±lÄ±nca yeniden dizilir, yani her karede bir maliyeti yoktur.
    internal sealed class HomeMenu
    {
        public const int W = 1672, H = 941; // tasarÄ±mÄ±n piksel boyutu

        public enum Page { Home, AddApps, Loading }

        // YÃ¼kleme ekranÄ±nda gÃ¶rÃ¼nen adlar
        public static readonly Dictionary<string, string> Names = new Dictionary<string, string>
        {
            { "youtube", "YouTube" }, { "netflix", "Netflix" }, { "disney", "Disney+" }, { "prime", "Prime Video" },
            { "discord", "Discord" }, { "hulu", "Hulu" }, { "twitch", "Twitch" }, { "spotify", "Spotify" },
            { "hbomax", "HBO Max" }, { "kick", "Kick" }, { "tiktok", "TikTok" }, { "crunchyroll", "Crunchyroll" },
            { "appletv", "Apple TV" }, { "paramountplus", "Paramount+" }, { "soundcloud", "SoundCloud" }, { "x", "X" },
            { "instagram", "Instagram" }, { "reddit", "Reddit" },
        };

        public sealed class App
        {
            public string Id, Url;
            public App(string id, string url) { Id = id; Url = url; }
        }

        // Ana ekrandaki sabit uygulamalar (My Screen hariÃ§) ve "+" listesindekiler
        public static readonly App[] HomeApps =
        {
            new App("youtube", "https://www.youtube.com/"),
            new App("netflix", "https://www.netflix.com/browse"),
            new App("disney", "https://www.disneyplus.com/"),
            new App("prime", "https://www.primevideo.com/"),
            new App("discord", "https://discord.com/app"),
            new App("hulu", "https://www.hulu.com/"),
            new App("twitch", "https://www.twitch.tv/"),
            new App("spotify", "https://open.spotify.com/"),
        };

        public static readonly App[] ExtraApps =
        {
            new App("hbomax", "https://www.hbomax.com/"),
            new App("kick", "https://kick.com/"),
            new App("tiktok", "https://www.tiktok.com/"),
            new App("crunchyroll", "https://www.crunchyroll.com/"),
            new App("appletv", "https://tv.apple.com/"),
            new App("paramountplus", "https://www.paramountplus.com/"),
            new App("soundcloud", "https://soundcloud.com/"),
            new App("x", "https://x.com/"),
            new App("instagram", "https://www.instagram.com/"),
            new App("reddit", "https://www.reddit.com/"),
        };

        public const string MyScreen = "myscreen", Plus = "plus", Back = "back", Settings = "settings";

        private struct Item { public string Id; public RectInt Rect; }

        private sealed class Card { public Color32[] Px; public int W, H; }

        private readonly ConfigEntry<string> added;
        private readonly Transform root, quad, outline;
        private readonly Material material;
        private readonly TextMesh title, subtitle, status;
        private readonly Transform spinner;
        private readonly Texture2D homeTex, addTex, loadingTex;
        private readonly Color32[] background;
        private readonly Dictionary<string, Card> cards = new Dictionary<string, Card>();
        private List<Item> homeItems, addItems;
        private float aspect = (float)H / W;
        private int shownMinute = -1;

        public Page Current { get; private set; } = Page.Home;

        public HomeMenu(Transform parent, Func<Color, Material> makeMaterial, Font font, ConfigEntry<string> addedApps)
        {
            added = addedApps;
            root = new GameObject("HomeMenu").transform;
            root.SetParent(parent, false);

            var bg = Load("menu_bg.png");
            background = bg.GetPixels32();
            UnityEngine.Object.Destroy(bg);
            foreach (var id in new[] { MyScreen, Plus, Back }.Concat(HomeApps.Select(a => a.Id)).Concat(ExtraApps.Select(a => a.Id)))
            {
                var t = Load("card_" + id + ".png");
                cards[id] = new Card { Px = t.GetPixels32(), W = t.width, H = t.height };
                UnityEngine.Object.Destroy(t);
            }

            homeTex = NewCanvas();
            addTex = NewCanvas();
            loadingTex = NewCanvas();

            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            UnityEngine.Object.Destroy(q.GetComponent<Collider>());
            q.name = "MenuQuad";
            quad = q.transform;
            quad.SetParent(root, false);
            quad.localPosition = new Vector3(0f, 0f, -0.0008f); // ekranÄ±n hemen Ã¶nÃ¼nde (Ã¶n yÃ¼z -Z)
            material = makeMaterial(Color.white);
            q.GetComponent<Renderer>().material = material;

            // Saat / baÅlÄ±k: tasarÄ±mdaki yerinde (sol Ã¼st)
            title = UiText.Create(root, font, "8:30", 0f, TextAnchor.UpperLeft, Color.white, Vector3.zero);
            subtitle = UiText.Create(root, font, "", 0f, TextAnchor.UpperLeft, new Color(0.75f, 0.77f, 0.82f), Vector3.zero);

            // YÃ¼kleme ekranÄ±: ortada yazÄ± ve altÄ±nda dÃ¶nen noktalar
            status = UiText.Create(root, font, "", 0f, TextAnchor.MiddleCenter, Color.white, Vector3.zero);
            spinner = new GameObject("Spinner").transform;
            spinner.SetParent(root, false);
            for (var i = 0; i < 8; i++)
            {
                var dot = GameObject.CreatePrimitive(PrimitiveType.Quad);
                UnityEngine.Object.Destroy(dot.GetComponent<Collider>());
                dot.transform.SetParent(spinner, false);
                dot.GetComponent<Renderer>().material = makeMaterial(Color.white);
            }

            // ParmaÄÄ±n Ã¼stÃ¼nde olduÄu kartÄ±n mavi Ã§erÃ§evesi
            outline = new GameObject("Hover").transform;
            outline.SetParent(root, false);
            var glow = makeMaterial(new Color(0.45f, 0.75f, 1f));
            for (var i = 0; i < 4; i++)
            {
                var edge = GameObject.CreatePrimitive(PrimitiveType.Quad);
                UnityEngine.Object.Destroy(edge.GetComponent<Collider>());
                edge.transform.SetParent(outline, false);
                edge.GetComponent<Renderer>().material = glow;
            }
            outline.gameObject.SetActive(false);

            SetAspect(aspect);
            Rebuild();
            Show(Page.Home);
        }

        // EkranÄ±n en-boy oranÄ±na uy (menÃ¼ ekranÄ± tam kaplasÄ±n)
        public void SetAspect(float a)
        {
            aspect = a;
            quad.localScale = new Vector3(1f, a, 1f);
            LayoutTexts();
        }

        public void Follow(Transform screen, float width)
        {
            root.SetPositionAndRotation(screen.position, screen.rotation);
            root.localScale = new Vector3(width, width, 1f);
        }

        public bool Visible => root.gameObject.activeSelf;
        public void SetVisible(bool on) => root.gameObject.SetActive(on);

        public void Show(Page page)
        {
            Current = page;
            material.mainTexture = page == Page.Home ? homeTex : page == Page.AddApps ? addTex : loadingTex;
            var loading = page == Page.Loading;
            title.gameObject.SetActive(!loading);
            subtitle.gameObject.SetActive(!loading);
            status.gameObject.SetActive(loading);
            spinner.gameObject.SetActive(loading);
            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", material.mainTexture);
            shownMinute = -1;
            outline.gameObject.SetActive(false);
            UpdateTexts();
        }

        // Her kare: saat dakikada bir gÃ¼ncellenir
        public void Tick()
        {
            if (Current == Page.Home && DateTime.Now.Minute != shownMinute) UpdateTexts();
            if (Current == Page.Loading && spinner.gameObject.activeSelf) AnimateSpinner();
        }

        // Uygulama aÃ§Ä±lÄ±rken: kartÄ± ortada bÃ¼yÃ¼k, altÄ±nda "Opening ..." ve dÃ¶nen noktalar
        public void ShowLoading(string id)
        {
            var name = Names.TryGetValue(id, out var n) ? n : id;
            var px = (Color32[])background.Clone();
            if (cards.TryGetValue(id, out var card)) Blit(px, card, new RectInt(W / 2 - 270, 260, 540, 324));
            loadingTex.SetPixels32(px);
            loadingTex.Apply(true);
            Show(Page.Loading);
            status.text = "Opening " + name + "…";
            spinner.gameObject.SetActive(true);
        }

        public void ShowError(string id)
        {
            status.text = "Couldn't open " + (Names.TryGetValue(id, out var n) ? n : id);
            spinner.gameObject.SetActive(false);
        }

        // 8 nokta Ã§ember Ã¼zerinde; parlaklÄ±k dÃ¶nerek ilerler
        private void AnimateSpinner()
        {
            var head = Time.unscaledTime * 10f;
            for (var i = 0; i < 8; i++)
            {
                var dot = spinner.GetChild(i);
                var angle = i * Mathf.PI / 4f;
                dot.localPosition = Unit(W / 2f + Mathf.Sin(angle) * 34f, 735f - Mathf.Cos(angle) * 34f, -0.0014f);
                var behind = Mathf.Repeat(head - i, 8f);
                var size = Mathf.Lerp(13f, 6f, behind / 8f);
                dot.localScale = new Vector3(size / W, size / H * aspect, 1f);
            }
        }

        // uv: (0,0) sol Ã¼st, (1,1) saÄ alt. Dokunulan kartÄ±n kimliÄi (yoksa null)
        public string HitTest(Vector2 uv)
        {
            var p = new Vector2Int((int)(uv.x * W), (int)(uv.y * H));
            if (p.x >= 1460 && p.x <= 1545 && p.y >= 105 && p.y <= 195) return Settings; // tasarÄ±mdaki diÅli
            foreach (var item in Current == Page.Home ? homeItems : addItems)
                if (item.Rect.Contains(p)) return item.Id;
            return null;
        }

        public void Hover(Vector2? uv)
        {
            Item? hit = null;
            if (uv.HasValue)
            {
                var p = new Vector2Int((int)(uv.Value.x * W), (int)(uv.Value.y * H));
                foreach (var item in Current == Page.Home ? homeItems : addItems)
                    if (item.Rect.Contains(p)) { hit = item; break; }
            }
            if (!hit.HasValue) { if (outline.gameObject.activeSelf) outline.gameObject.SetActive(false); return; }

            // KartÄ±n kenarlarÄ±na 4 ince Åerit
            var r = hit.Value.Rect;
            var t = 5f; // piksel
            SetEdge(0, r.x - t, r.y - t, r.width + 2 * t, t);
            SetEdge(1, r.x - t, r.yMax, r.width + 2 * t, t);
            SetEdge(2, r.x - t, r.y, t, r.height);
            SetEdge(3, r.xMax, r.y, t, r.height);
            if (!outline.gameObject.activeSelf) outline.gameObject.SetActive(true);
        }

        public bool IsAdded(string id) => AddedIds().Contains(id);

        public void ToggleAdded(string id)
        {
            var list = AddedIds();
            if (!list.Remove(id)) list.Add(id);
            added.Value = string.Join(",", list);
            Rebuild();
        }

        // ---------------------------------------------------------------- dizilim

        private List<string> AddedIds() =>
            new List<string>((added.Value ?? "").Split(',').Select(s => s.Trim()).Where(s => ExtraApps.Any(a => a.Id == s)).Distinct());

        private void Rebuild()
        {
            var home = new List<string> { MyScreen };
            home.AddRange(HomeApps.Select(a => a.Id));
            home.AddRange(AddedIds());
            home.Add(Plus);
            homeItems = Compose(homeTex, home, null);

            var add = new List<string>(ExtraApps.Select(a => a.Id));
            add.Add(Back);
            var addedSet = new HashSet<string>(AddedIds());
            addItems = Compose(addTex, add, addedSet);
        }

        // 5 sÃ¼tun; 2 sÄ±radan fazlaysa kartlar kÃ¼Ã§Ã¼lÃ¼p yukarÄ±dan baÅlar
        private static List<RectInt> Layout(int count)
        {
            var rows = (count + 4) / 5;
            float cw = 270, ch = 162, gx = 22, gy = 36, top = 386;
            if (rows > 2)
            {
                top = 300; gy = 24;
                ch = Mathf.Min(162f, (900f - top - (rows - 1) * gy) / rows);
                cw = ch * 270f / 162f;
            }
            var x0 = (W - (5 * cw + 4 * gx)) / 2f;
            var list = new List<RectInt>();
            for (var i = 0; i < count; i++)
                list.Add(new RectInt(Mathf.RoundToInt(x0 + (i % 5) * (cw + gx)), Mathf.RoundToInt(top + (i / 5) * (ch + gy)), Mathf.RoundToInt(cw), Mathf.RoundToInt(ch)));
            return list;
        }

        private List<Item> Compose(Texture2D tex, List<string> ids, HashSet<string> checkmarks)
        {
            var px = (Color32[])background.Clone();
            var rects = Layout(ids.Count);
            var items = new List<Item>();
            for (var i = 0; i < ids.Count; i++)
            {
                Blit(px, cards[ids[i]], rects[i]);
                if (checkmarks != null && checkmarks.Contains(ids[i])) DrawCheck(px, rects[i]);
                items.Add(new Item { Id = ids[i], Rect = rects[i] });
            }
            tex.SetPixels32(px);
            tex.Apply(true);
            return items;
        }

        // KartÄ± Ã¶lÃ§ekleyip (Ã§ift doÄrusal) saydamlÄ±ÄÄ±yla birlikte arka plana bas.
        // TasarÄ±m koordinatlarÄ± Ã¼stten alta; Unity dokularÄ± alttan Ã¼ste
        private static void Blit(Color32[] dst, Card card, RectInt r)
        {
            for (var dy = 0; dy < r.height; dy++)
            {
                var ty = H - 1 - (r.y + dy);
                if (ty < 0 || ty >= H) continue;
                var sy = (dy + 0.5f) * card.H / r.height - 0.5f;
                for (var dx = 0; dx < r.width; dx++)
                {
                    var tx = r.x + dx;
                    if (tx < 0 || tx >= W) continue;
                    var sx = (dx + 0.5f) * card.W / r.width - 0.5f;
                    var c = Sample(card, sx, sy);
                    if (c.a == 0) continue;
                    var o = ty * W + tx;
                    var d = dst[o];
                    var a = c.a / 255f;
                    dst[o] = new Color32((byte)(c.r * a + d.r * (1 - a)), (byte)(c.g * a + d.g * (1 - a)), (byte)(c.b * a + d.b * (1 - a)), 255);
                }
            }
        }

        private static Color32 Sample(Card card, float x, float yTop)
        {
            x = Mathf.Clamp(x, 0, card.W - 1.001f);
            yTop = Mathf.Clamp(yTop, 0, card.H - 1.001f);
            int x0 = (int)x, y0 = (int)yTop;
            float fx = x - x0, fy = yTop - y0;
            Color32 P(int xx, int yy) => card.Px[(card.H - 1 - yy) * card.W + xx];
            var a = P(x0, y0); var b = P(x0 + 1, y0); var c = P(x0, y0 + 1); var d = P(x0 + 1, y0 + 1);
            byte L(byte p, byte q, byte r, byte s) => (byte)((p * (1 - fx) + q * fx) * (1 - fy) + (r * (1 - fx) + s * fx) * fy);
            return new Color32(L(a.r, b.r, c.r, d.r), L(a.g, b.g, c.g, d.g), L(a.b, b.b, c.b, d.b), L(a.a, b.a, c.a, d.a));
        }

        // EklenmiÅ uygulamalarda kartÄ±n saÄ Ã¼stÃ¼ne yeÅil daire iÃ§inde tik
        private static void DrawCheck(Color32[] dst, RectInt r)
        {
            var cx = r.xMax - 26f; var cy = r.y + 26f; const float rad = 17f;
            Vector2 a = new Vector2(cx - 8, cy + 1), b = new Vector2(cx - 2, cy + 7), c = new Vector2(cx + 9, cy - 6);
            for (var y = (int)(cy - rad - 1); y <= cy + rad + 1; y++)
                for (var x = (int)(cx - rad - 1); x <= cx + rad + 1; x++)
                {
                    var dist = Vector2.Distance(new Vector2(x, y), new Vector2(cx, cy));
                    if (dist > rad + 0.5f) continue;
                    var p = new Vector2(x, y);
                    var onTick = SegDist(p, a, b) < 2.6f || SegDist(p, b, c) < 2.6f;
                    var col = onTick ? new Color32(255, 255, 255, 255) : new Color32(34, 170, 84, 255);
                    var ty = H - 1 - y;
                    if (ty >= 0 && ty < H && x >= 0 && x < W) dst[ty * W + x] = col;
                }
        }

        private static float SegDist(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + ab * t);
        }

        // ---------------------------------------------------------------- yardÄ±mcÄ±lar

        // TasarÄ±m pikseli -> menÃ¼ kÃ¶kÃ¼nÃ¼n birimi (geniÅlik 1, merkez 0)
        private Vector3 Unit(float px, float py, float z = -0.0016f) => new Vector3(px / W - 0.5f, (0.5f - py / H) * aspect, z);

        private void SetEdge(int i, float x, float y, float w, float h)
        {
            var e = outline.GetChild(i);
            e.localPosition = Unit(x + w / 2f, y + h / 2f, -0.0012f);
            e.localScale = new Vector3(w / W, h / H * aspect, 1f);
        }

        private void LayoutTexts()
        {
            title.transform.localPosition = Unit(150, 112);
            subtitle.transform.localPosition = Unit(154, 214);
            status.transform.localPosition = Unit(W / 2f, 650);
            status.characterSize = 46f / H * aspect / (status.fontSize * 0.1f);
            title.characterSize = 92f / H * aspect / (title.fontSize * 0.1f);
            subtitle.characterSize = 34f / H * aspect / (subtitle.fontSize * 0.1f);
        }

        private void UpdateTexts()
        {
            if (Current == Page.Home)
            {
                var now = DateTime.Now;
                shownMinute = now.Minute;
                title.text = now.ToString("H:mm", CultureInfo.InvariantCulture);
                subtitle.text = now.ToString("dddd, MMM d", new CultureInfo("en-US"));
            }
            else
            {
                title.text = "Add apps";
                subtitle.text = "Tap an app to add or remove it";
            }
        }

        private static Texture2D NewCanvas() => new Texture2D(W, H, TextureFormat.RGBA32, true)
        {
            filterMode = FilterMode.Trilinear,
            anisoLevel = 8,
            wrapMode = TextureWrapMode.Clamp,
        };

        // Modun iÃ§ine gÃ¶mÃ¼lÃ¼ PNG
        private static Texture2D Load(string name)
        {
            using (var s = typeof(HomeMenu).Assembly.GetManifestResourceStream(name))
            {
                if (s == null) throw new FileNotFoundException("GÃ¶mÃ¼lÃ¼ gÃ¶rsel yok: " + name);
                var bytes = new byte[s.Length];
                s.Read(bytes, 0, bytes.Length);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.LoadImage(bytes);
                return tex;
            }
        }
    }
}
