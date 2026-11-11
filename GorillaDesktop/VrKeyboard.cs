using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace GorillaDesktop
{
    // SteamVR tarzı sanal klavye: ekranın altında, kullanıcıya doğru eğik durur.
    // Üstteki kutuda yazılan metin görünür; Done (ya da Enter) metni o an açık olan uygulamaya yazar.
    // Tuşlara parmakla dokunarak ya da elden çıkan ışınla nişan alıp tetikle basılır.
    internal sealed class VrKeyboard
    {
        private enum Kind { Char, Back, Enter, Shift, Symbols, Space, Mic, Hide, Done }

        private sealed class Key
        {
            public Transform T;
            public Kind Kind;
            public string Char;
            public TextMesh Label;
            public Material Mat;
            public Color Normal;
            public bool Hovered;
        }

        private const float K = 0.056f, Gap = 0.008f; // tuş boyu ve arası (metre)
        private static readonly Color KeyColor = new Color(0.17f, 0.2f, 0.25f);
        private static readonly Color SpecialColor = new Color(0.11f, 0.13f, 0.17f);
        private static readonly Color HoverColor = new Color(0.18f, 0.44f, 0.84f);
        private static readonly CultureInfo Tr = new CultureInfo("tr-TR");

        private static readonly string[][] Letters =
        {
            new[] { "q", "w", "e", "r", "t", "y", "u", "ı", "o", "p", "ğ", "ü" },
            new[] { "a", "s", "d", "f", "g", "h", "j", "k", "l", "ş", "i" },
            new[] { "z", "x", "c", "v", "b", "n", "m", "ö", "ç", "." },
        };

        private static readonly string[][] Symbols =
        {
            new[] { "-", "/", ":", ";", "(", ")", "₺", "&", "\"", "!", "?", "'" },
            new[] { "_", "#", "%", "*", "+", "=", "[", "]", "{", "}", "~" },
            new[] { "<", ">", "$", "€", "^", "|", "\\", "`", ",", "." },
        };

        private readonly Transform root;
        private readonly List<Key> keys = new List<Key>();
        private readonly List<Key> letterKeys = new List<Key>();
        private readonly TextMesh textBox;
        private readonly Func<Color, Material> makeMaterial;
        private readonly Font font;
        private readonly float width, height;
        private readonly LineRenderer[] beams = new LineRenderer[2];
        private readonly bool[] fingerWasTouching = new bool[2];
        private readonly float[] fingerPrevFront = { 1f, 1f };

        private string buffer = "";
        private bool shift, symbols;

        public bool Visible => root.gameObject.activeSelf;

        public VrKeyboard(Transform parent, Func<Color, Material> makeMaterial, Font font)
        {
            this.makeMaterial = makeMaterial;
            this.font = font;
            root = new GameObject("Keyboard").transform;
            root.SetParent(parent, false);

            width = 12 * K + 11 * Gap + 0.04f;
            height = 6 * (K + Gap) + 0.05f;

            // Panel
            Quad(root, new Vector2(0f, 0f), new Vector2(width, height), 0f, makeMaterial(new Color(0.07f, 0.08f, 0.1f)));

            // Satır yerleşimi: üstten alta. y = satır merkezi
            var top = height / 2f - 0.025f - K / 2f;
            var rowY = new float[6];
            for (var r = 0; r < 6; r++) rowY[r] = top - r * (K + Gap);

            // 0: yazı kutusu + Done
            var boxW = width - 0.04f - (K * 1.6f + Gap);
            Quad(root, new Vector2(-width / 2f + 0.02f + boxW / 2f, rowY[0]), new Vector2(boxW, K), -0.002f, makeMaterial(new Color(0.03f, 0.04f, 0.05f)));
            textBox = UiText.Create(root, font, "", K * 0.42f, TextAnchor.MiddleLeft, Color.white, new Vector3(-width / 2f + 0.035f, rowY[0], -0.004f));
            AddKey(Kind.Done, "Done", new Vector2(width / 2f - 0.02f - K * 0.8f, rowY[0]), K * 1.6f, HoverColor);

            // 1: rakamlar + sil
            var nums = new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" };
            Row(rowY[1], nums, out var x);
            AddKey(Kind.Back, "⌫", new Vector2(x + K * 0.75f, rowY[1]), K * 1.5f, SpecialColor);

            // 2-4: harfler (sembol sayfasında semboller)
            Row(rowY[2], Letters[0], out _, true);
            Row(rowY[3], Letters[1], out x, true);
            AddKey(Kind.Enter, "Enter", new Vector2(x + K * 0.9f, rowY[3]), K * 1.8f, SpecialColor);
            var shiftX = RowStart(Letters[2].Length + 1.5f);
            AddKey(Kind.Shift, "⇧", new Vector2(shiftX + K * 0.75f, rowY[4]), K * 1.5f, SpecialColor);
            RowFrom(shiftX + K * 1.5f + Gap, rowY[4], Letters[2], true);

            // 5: ?123, mikrofon, boşluk, @, kapat
            var bottom = new[] { (Kind.Symbols, "?123", 1.6f), (Kind.Mic, "Mic", 1.2f), (Kind.Space, "", 6f), (Kind.Char, "@", 1f), (Kind.Hide, "▼", 1.4f) };
            var total = 0f;
            foreach (var b in bottom) total += b.Item3 * K;
            total += (bottom.Length - 1) * Gap;
            var bx = -total / 2f;
            foreach (var b in bottom)
            {
                var w = b.Item3 * K;
                AddKey(b.Item1, b.Item2, new Vector2(bx + w / 2f, rowY[5]), w, b.Item1 == Kind.Char || b.Item1 == Kind.Space ? KeyColor : SpecialColor);
                bx += w + Gap;
            }

            // Elden çıkan ışınlar (sadece klavyeye bakarken görünür)
            var beamMat = makeMaterial(new Color(0.55f, 0.8f, 1f));
            for (var i = 0; i < 2; i++)
            {
                var go = new GameObject("KeyboardBeam" + i);
                go.transform.SetParent(parent, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.positionCount = 2;
                lr.startWidth = 0.003f;
                lr.endWidth = 0.0015f;
                lr.useWorldSpace = true;
                lr.material = beamMat;
                lr.enabled = false;
                beams[i] = lr;
            }

            RefreshText();
            SetVisible(false);
        }

        public void SetVisible(bool on)
        {
            root.gameObject.SetActive(on);
            if (!on) foreach (var b in beams) b.enabled = false;
        }

        // Yazmadan önce çağrılır (yazının oyuna değil uygulamaya gitmesi için odağı ayarlar)
        public Action BeforeType;

        // SteamVR gibi: açıldığı an baktığın yönde, göğüs hizanda, sana doğru eğik çıkar ve orada kalır
        public void Toggle(Transform head)
        {
            if (!Visible) PlaceInFront(head);
            SetVisible(!Visible);
        }

        private void PlaceInFront(Transform head)
        {
            var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            var pos = head.position + forward * 0.45f - Vector3.up * 0.42f;
            root.SetPositionAndRotation(pos, Quaternion.LookRotation(forward, Vector3.up) * Quaternion.Euler(35f, 0f, 0f));
        }

        // ---------------------------------------------------------------- giriş

        // Parmakla basma: önden değince bir kez. Basılan elin sırasını döner (yoksa -1)
        public int Touch(Transform[] fingers, out bool anyNear)
        {
            anyNear = false;
            var pressed = -1;
            for (var h = 0; h < fingers.Length && h < 2; h++)
            {
                Key touched = null, near = null;
                var pos = fingers[h].position;
                var front = 1f;
                foreach (var k in keys)
                {
                    var local = k.T.InverseTransformPoint(pos);
                    if (Mathf.Abs(local.x) > 0.5f || Mathf.Abs(local.y) > 0.5f) continue;
                    var f = Vector3.Dot(pos - k.T.position, -k.T.forward);
                    if (f < 0.05f && f > -0.05f) { near = k; front = f; }
                    if (f < 0.01f && f > -0.05f) { touched = k; break; }
                }
                if (near != null) { anyNear = true; SetHover(near); }
                var cameFromFront = fingerPrevFront[h] > 0.01f;
                if (touched != null && !fingerWasTouching[h] && cameFromFront)
                {
                    Press(touched);
                    pressed = h;
                }
                fingerWasTouching[h] = touched != null;
                fingerPrevFront[h] = near != null ? front : 1f;
            }
            return pressed;
        }

        // Işınla nişan: klavyeye değiyorsa ışını çizer, üstündeki tuşu vurgular; tetik basıldıysa tuşa basar
        public bool Beam(int hand, Vector3 origin, Vector3 dir, bool triggerPressedNow)
        {
            var beam = beams[hand];
            var normal = -root.forward;
            if (Vector3.Dot(origin - root.position, normal) <= 0f || !new Plane(normal, root.position).Raycast(new Ray(origin, dir), out var dist) || dist > 4f)
            {
                beam.enabled = false;
                return false;
            }
            var hit = origin + dir * dist;
            var local = root.InverseTransformPoint(hit);
            if (Mathf.Abs(local.x) > width / 2f || Mathf.Abs(local.y) > height / 2f)
            {
                beam.enabled = false;
                return false;
            }

            beam.enabled = true;
            beam.SetPosition(0, origin);
            beam.SetPosition(1, hit);

            Key over = null;
            foreach (var k in keys)
            {
                var l = k.T.InverseTransformPoint(hit);
                if (Mathf.Abs(l.x) <= 0.5f && Mathf.Abs(l.y) <= 0.5f) { over = k; break; }
            }
            if (over != null)
            {
                SetHover(over);
                if (triggerPressedNow) Press(over);
            }
            return true;
        }

        public void HideBeam(int hand) => beams[hand].enabled = false;

        // Kare başında çağrılır: vurgular sonra Touch/Beam ile yeniden işaretlenir
        public void ClearHover()
        {
            foreach (var k in keys)
                if (k.Hovered) { k.Hovered = false; SetColor(k.Mat, k.Normal); }
        }

        // ---------------------------------------------------------------- tuşlar

        // Her tuş anında uygulamaya gider; üstteki kutu sadece ne yazdığını gösterir
        private void Press(Key k)
        {
            switch (k.Kind)
            {
                case Kind.Char:
                    var c = k.Char;
                    if (shift && !symbols) { c = c.ToUpper(Tr); shift = false; RefreshLetters(); }
                    Type(c);
                    break;
                case Kind.Space: Type(" "); break;
                case Kind.Back:
                    BeforeType?.Invoke();
                    Native.KeyTap(Native.VK_BACK);
                    if (buffer.Length > 0) buffer = buffer.Substring(0, buffer.Length - 1);
                    break;
                case Kind.Shift: shift = !shift; RefreshLetters(); break;
                case Kind.Symbols: symbols = !symbols; shift = false; RefreshLetters(); break;
                case Kind.Enter:
                    BeforeType?.Invoke();
                    Native.KeyTap(Native.VK_RETURN);
                    buffer = "";
                    break;
                case Kind.Done:
                case Kind.Hide:
                    buffer = "";
                    SetVisible(false);
                    break;
                case Kind.Mic:
                    BeforeType?.Invoke();
                    Native.KeyCombo(Native.VK_LWIN, (ushort)'H'); // Windows sesle yazma
                    break;
            }
            RefreshText();
        }

        private void Type(string s)
        {
            BeforeType?.Invoke();
            Native.TypeText(s);
            buffer += s;
        }

        private void RefreshText() => textBox.text = (buffer.Length > 40 ? "…" + buffer.Substring(buffer.Length - 40) : buffer) + "|";

        private void RefreshLetters()
        {
            var page = symbols ? Symbols : Letters;
            var i = 0;
            foreach (var row in page)
                foreach (var s in row)
                {
                    var k = letterKeys[i++];
                    k.Char = s;
                    k.Label.text = shift && !symbols ? s.ToUpper(Tr) : s;
                }
        }

        private float RowStart(float units) => -(units * K + (units - 1) * Gap) / 2f;

        private void Row(float y, string[] chars, out float endX, bool letters = false)
        {
            var units = chars.Length + (chars.Length <= 10 ? 1.5f : chars.Length == 11 ? 1.8f : 0f);
            endX = RowFrom(RowStart(units), y, chars, letters);
        }

        private float RowFrom(float x, float y, string[] chars, bool letters)
        {
            foreach (var c in chars)
            {
                var k = AddKey(Kind.Char, c, new Vector2(x + K / 2f, y), K, KeyColor);
                k.Char = c;
                if (letters) letterKeys.Add(k);
                x += K + Gap;
            }
            return x;
        }

        private Key AddKey(Kind kind, string label, Vector2 center, float w, Color color)
        {
            var mat = makeMaterial(color);
            var t = Quad(root, center, new Vector2(w, K), -0.003f, mat);
            var text = UiText.Create(t, font, label, 0f, TextAnchor.MiddleCenter, Color.white, new Vector3(0f, 0f, -0.01f));
            // Yazı tuşun ölçeğinden etkilenmesin
            text.transform.localScale = new Vector3(1f / w, 1f / K, 1f);
            text.characterSize = (label.Length > 2 ? K * 0.3f : K * 0.45f) / (text.fontSize * 0.1f);
            var k = new Key { T = t, Kind = kind, Char = label, Label = text, Mat = mat, Normal = color };
            keys.Add(k);
            return k;
        }

        private void SetHover(Key k)
        {
            if (k.Hovered) return;
            k.Hovered = true;
            SetColor(k.Mat, HoverColor);
        }

        private static Transform Quad(Transform parent, Vector2 center, Vector2 size, float z, Material mat)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            UnityEngine.Object.Destroy(q.GetComponent<Collider>());
            q.transform.SetParent(parent, false);
            q.transform.localPosition = new Vector3(center.x, center.y, z);
            q.transform.localScale = new Vector3(size.x, size.y, 1f);
            q.GetComponent<Renderer>().material = mat;
            return q.transform;
        }

        private static void SetColor(Material m, Color c)
        {
            m.color = c;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        }
    }
}
