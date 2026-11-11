using System;
using System.Collections.Generic;
using UnityEngine;

namespace GorillaDesktop
{
    // Ekranın sağ üst köşesindeki ⚙ tuşu ve yanında açılan ayarlar paneli.
    // Satırlar: solda etiket, sağda kontrol (< değer >, aç/kapa anahtarı ya da tuş).
    // Tuşlara parmakla basılır (GT düğmeleri gibi).
    internal sealed class SettingsUi
    {
        public sealed class Button
        {
            public Transform T;
            public Action OnClick;
            public Material Mat;
            public Color Normal;
            public bool Hovered;
            public Func<bool> State; // aç/kapa anahtarıysa
            public TextMesh Text;
            public bool Visible => T.gameObject.activeInHierarchy;
        }

        private const float Width = 0.5f, RowHeight = 0.066f, Pad = 0.025f, HeaderHeight = 0.07f, ControlHeight = 0.048f;
        private static readonly Color PanelColor = new Color(0.07f, 0.075f, 0.09f);
        private static readonly Color HeaderColor = new Color(0.12f, 0.36f, 0.75f);
        private static readonly Color RowLine = new Color(0.12f, 0.125f, 0.15f);
        private static readonly Color ButtonColor = new Color(0.19f, 0.2f, 0.25f);
        private static readonly Color HoverColor = new Color(0.3f, 0.55f, 0.95f);
        private static readonly Color OnColor = new Color(0.2f, 0.65f, 0.35f);
        private static readonly Color OffColor = new Color(0.3f, 0.3f, 0.34f);
        private static readonly Color SubText = new Color(0.7f, 0.72f, 0.78f);

        private readonly Func<Color, Material> makeMaterial;
        private readonly Transform root, panel;
        private readonly List<Button> buttons = new List<Button>();
        private readonly List<(TextMesh text, Func<string> value)> labels = new List<(TextMesh, Func<string>)>();
        private readonly Font font;
        private int rows;
        private readonly bool[] handWasTouching = new bool[2];
        private readonly float[] handPrevFront = { 1f, 1f };

        private readonly Font symbolFont;

        public bool Open { get; private set; }

        public SettingsUi(Transform parent, Func<Color, Material> makeMaterial, Font font)
        {
            this.makeMaterial = makeMaterial;
            this.font = font;

            root = new GameObject("Settings").transform;
            root.SetParent(parent, false);

            // ⚙ tuşu: ekranın sağ üst köşesinin hemen sağında
            var gear = AddButton(root, new Vector2(0.065f, -0.035f), new Vector2(0.055f, 0.055f), "⚙", 1.5f, () => SetOpen(!Open));
            gear.Normal = new Color(0.12f, 0.12f, 0.15f);
            SetColor(gear.Mat, gear.Normal);
            symbolFont = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI Symbol", "Segoe UI Emoji", "Arial" }, 64);
            gear.Text.font = symbolFont;
            gear.Text.GetComponent<MeshRenderer>().material = symbolFont.material;

            panel = new GameObject("Panel").transform;
            panel.SetParent(root, false);
            panel.localPosition = new Vector3(0.11f, 0f, 0f);
            panel.gameObject.SetActive(false);
        }

        // Dişlinin altına sıralanan küçük kare tuşlar (Home, klavye)
        public void AddSideButton(string glyph, int slot, Action onClick)
        {
            var b = AddButton(root, new Vector2(0.065f, -0.035f - slot * 0.07f), new Vector2(0.055f, 0.055f), glyph, 1.5f, onClick);
            b.Normal = new Color(0.12f, 0.12f, 0.15f);
            SetColor(b.Mat, b.Normal);
            b.Text.font = symbolFont;
            b.Text.GetComponent<MeshRenderer>().material = symbolFont.material;
        }

        public void AddHeader(string title)
        {
            Quad(panel, "Header", new Vector2(Width / 2, -HeaderHeight / 2), new Vector2(Width, HeaderHeight), -0.001f)
                .GetComponent<Renderer>().material = makeMaterial(HeaderColor);
            AddText(panel, new Vector2(Pad, -HeaderHeight / 2), title, 1.15f, TextAnchor.MiddleLeft, Color.white);
        }

        // Etiket .......... [<] değer [>]
        public void AddStepper(string label, Func<string> value, Action prev, Action next)
        {
            var y = BeginRow(label);
            var right = Width - Pad;
            AddButton(panel, new Vector2(right - ControlHeight / 2, y), new Vector2(ControlHeight, ControlHeight), ">", 1f, next);
            var leftButtonX = right - 0.23f + ControlHeight / 2;
            AddButton(panel, new Vector2(leftButtonX, y), new Vector2(ControlHeight, ControlHeight), "<", 1f, prev);
            var mid = (leftButtonX + right - ControlHeight / 2) / 2;
            labels.Add((AddText(panel, new Vector2(mid, y), "", 0.8f, TextAnchor.MiddleCenter, Color.white), value));
        }

        // Label .......... [ ON ]
        public void AddToggle(string label, Func<bool> state, Action toggle)
        {
            var y = BeginRow(label);
            var b = AddButton(panel, new Vector2(Width - Pad - 0.055f, y), new Vector2(0.11f, ControlHeight), "", 0.8f, toggle);
            b.State = state;
        }

        // Tam genişlikte tuş
        public void AddAction(string text, Action onClick)
        {
            var y = BeginRow(null);
            AddButton(panel, new Vector2(Width / 2, y), new Vector2(Width - Pad * 2, ControlHeight), text, 0.85f, onClick);
        }

        // Etiket .......... bilgi yazısı
        public void AddInfo(string label, Func<string> value)
        {
            var y = BeginRow(label);
            labels.Add((AddText(panel, new Vector2(Width - Pad, y), "", 0.7f, TextAnchor.MiddleRight, SubText), value));
        }

        // Paneli bitir: arka planı satır sayısına göre çiz
        public void Finish()
        {
            var height = HeaderHeight + rows * RowHeight + Pad * 0.6f;
            Quad(panel, "Background", new Vector2(Width / 2, -height / 2), new Vector2(Width, height), 0f)
                .GetComponent<Renderer>().material = makeMaterial(PanelColor);
            Refresh();
        }

        public void SetOpen(bool open)
        {
            Open = open;
            panel.gameObject.SetActive(open);
            if (open) Refresh();
        }

        public void Refresh()
        {
            foreach (var (text, value) in labels) text.text = value();
            foreach (var b in buttons)
            {
                if (b.State == null) continue;
                var on = b.State();
                b.Text.text = on ? "ON" : "OFF";
                b.Normal = on ? OnColor : OffColor;
                if (!b.Hovered) SetColor(b.Mat, b.Normal);
            }
        }

        // Ekranın sağ üst köşesine yapıştır (ekran ölçeklense de tuşlar aynı boyutta kalır)
        public void FollowScreen(Transform screen)
        {
            root.SetPositionAndRotation(screen.TransformPoint(new Vector3(0.5f, 0.5f, 0f)), screen.rotation);
        }


        // Parmak ucuyla basma (GT düğmeleri gibi): parmak tuşa önden girdiği anda bir kez tıklar.
        // Tıklayan elin sırasını döner (yoksa -1); parmak bir tuşun önündeyse 'hovered' dolar
        public int Touch(Transform[] fingers, out Button hoveredA, out Button hoveredB)
        {
            hoveredA = hoveredB = null;
            var clicked = -1;
            for (var h = 0; h < fingers.Length && h < 2; h++)
            {
                Button touched = null, near = null;
                var pos = fingers[h].position;
                var front = 1f;
                foreach (var b in buttons)
                {
                    if (!b.Visible) continue;
                    var local = b.T.InverseTransformPoint(pos);
                    if (Mathf.Abs(local.x) > 0.5f || Mathf.Abs(local.y) > 0.5f) continue;
                    var f = Vector3.Dot(pos - b.T.position, -b.T.forward);
                    if (f < 0.06f && f > -0.05f) { near = b; front = f; }
                    if (f < 0.012f && f > -0.05f) { touched = b; break; }
                }

                if (h == 0) hoveredA = near; else hoveredB = near;
                var cameFromFront = handPrevFront[h] > 0.012f;
                if (touched != null && !handWasTouching[h] && cameFromFront)
                {
                    Click(touched);
                    clicked = h;
                }
                handWasTouching[h] = touched != null;
                handPrevFront[h] = near != null ? front : 1f;
            }
            return clicked;
        }

        public void Click(Button b)
        {
            b.OnClick?.Invoke();
            Refresh();
        }

        // Parmağın önünde olduğu tuşları renklendir
        public void SetHover(Button a, Button b)
        {
            foreach (var btn in buttons)
            {
                var hovered = btn == a || btn == b;
                if (hovered == btn.Hovered) continue;
                btn.Hovered = hovered;
                SetColor(btn.Mat, hovered ? HoverColor : btn.Normal);
            }
        }

        // Satırın etiketini yazar ve kontrolün dikey merkezini döner
        private float BeginRow(string label)
        {
            var top = -HeaderHeight - rows * RowHeight;
            var y = top - RowHeight / 2 - Pad * 0.3f;
            if (rows > 0)
                Quad(panel, "Line", new Vector2(Width / 2, top - Pad * 0.3f), new Vector2(Width - Pad * 2, 0.002f), -0.0015f)
                    .GetComponent<Renderer>().material = makeMaterial(RowLine);
            if (label != null) AddText(panel, new Vector2(Pad, y), label, 0.85f, TextAnchor.MiddleLeft, Color.white);
            rows++;
            return y;
        }

        private Button AddButton(Transform parent, Vector2 center, Vector2 size, string text, float textSize, Action onClick)
        {
            var quad = Quad(parent, "Button", center, size, -0.003f);
            var b = new Button { T = quad.transform, OnClick = onClick, Mat = makeMaterial(ButtonColor), Normal = ButtonColor };
            quad.GetComponent<Renderer>().material = b.Mat;

            b.Text = AddText(quad.transform, Vector2.zero, text, textSize, TextAnchor.MiddleCenter, Color.white);
            // Yazı tuşun ölçeğinden etkilenmesin
            b.Text.transform.localScale = new Vector3(1f / size.x, 1f / size.y, 1f);
            b.Text.transform.localPosition = new Vector3(0f, 0f, -0.01f);
            buttons.Add(b);
            return b;
        }

        private TextMesh AddText(Transform parent, Vector2 pos, string text, float size, TextAnchor anchor, Color color)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(pos.x, pos.y, -0.005f);
            var tm = go.AddComponent<TextMesh>();
            tm.font = font;
            tm.fontSize = 64;
            tm.characterSize = 0.0036f * size;
            tm.anchor = anchor;
            tm.alignment = anchor == TextAnchor.MiddleLeft ? TextAlignment.Left : anchor == TextAnchor.MiddleRight ? TextAlignment.Right : TextAlignment.Center;
            tm.color = color;
            tm.text = text;
            go.GetComponent<MeshRenderer>().material = font.material;
            return tm;
        }

        private static GameObject Quad(Transform parent, string name, Vector2 center, Vector2 size, float z)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            UnityEngine.Object.Destroy(quad.GetComponent<Collider>());
            quad.name = name;
            quad.transform.SetParent(parent, false);
            quad.transform.localPosition = new Vector3(center.x, center.y, z);
            quad.transform.localScale = new Vector3(size.x, size.y, 1f);
            return quad;
        }

        private static void SetColor(Material m, Color c)
        {
            m.color = c;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        }
    }
}
