using UnityEngine;

namespace GorillaDesktop
{
    // TextMesh'i istenen satır yüksekliğinde (üst nesnenin biriminde) oluşturur
    internal static class UiText
    {
        public static TextMesh Create(Transform parent, Font font, string sample, float height, TextAnchor anchor, Color color, Vector3 localPos, FontStyle style = FontStyle.Normal)
        {
            var go = new GameObject("Text");
            var tm = go.AddComponent<TextMesh>();
            tm.font = font;
            tm.fontSize = 96;
            tm.fontStyle = style;
            tm.characterSize = 1f;
            tm.anchor = anchor;
            tm.alignment = anchor == TextAnchor.UpperRight || anchor == TextAnchor.MiddleRight || anchor == TextAnchor.LowerRight
                ? TextAlignment.Right
                : anchor == TextAnchor.UpperCenter || anchor == TextAnchor.MiddleCenter || anchor == TextAnchor.LowerCenter ? TextAlignment.Center : TextAlignment.Left;
            tm.color = color;
            go.GetComponent<MeshRenderer>().material = font.material;

            // TextMesh satır yüksekliği ≈ fontSize * characterSize * 0.1 (ayarlar panelindeki yazılarla doğrulandı)
            tm.characterSize = height / (tm.fontSize * 0.1f);
            tm.text = sample;

            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            return tm;
        }
    }
}
