using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GorillaDesktop
{
    // Ekranın arkasındaki ince siyah çerçeve ve alt kenarın ortasındaki beyaz GTV logosu.
    // Çerçeve kalınlığı ve logo boyutu metre cinsinden sabit; Follow her karede ekrana uydurur.
    internal sealed class MonitorModel
    {
        private const float Border = 0.03f, FrameDepth = 0.02f, LogoHeight = 0.02f;

        private readonly Transform root, frame, logo;
        private readonly float aspect;

        public MonitorModel(Transform parent, float aspect, Func<Color, Material> makeMaterial, bool showLogo)
        {
            this.aspect = aspect;
            root = new GameObject("Frame").transform;
            root.SetParent(parent, false);

            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.Destroy(cube.GetComponent<Collider>()); // oyuncu çarpmasın
            cube.GetComponent<Renderer>().material = makeMaterial(new Color(0.05f, 0.05f, 0.05f));
            frame = cube.transform;
            frame.SetParent(root, false);

            if (!showLogo) return;
            var go = new GameObject("GTV");
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().mesh = LogoMesh(out var logoAspect);
            go.AddComponent<MeshRenderer>().material = makeMaterial(Color.white);
            logo = go.transform;
            logo.localScale = new Vector3(LogoHeight * logoAspect, LogoHeight, 1f);
        }

        public void Follow(Transform screen, float width)
        {
            root.SetPositionAndRotation(screen.position, screen.rotation);
            var height = width * aspect;
            frame.localPosition = new Vector3(0f, 0f, 0.005f + FrameDepth / 2);
            frame.localScale = new Vector3(width + Border * 2, height + Border * 2, FrameDepth);
            // Alt kenarın ortası; çerçevenin ön yüzünün hemen önünde (ekranın önü -Z)
            if (logo != null) logo.localPosition = new Vector3(0f, -height / 2 - Border / 2, 0.003f);
        }

        public void Destroy() => UnityEngine.Object.Destroy(root.gameObject);

        // GTV logosu, resimden çıkarılmış dikdörtgenler halinde (genişlik 1, ortalanmış)
        private static Mesh LogoMesh(out float logoAspect)
        {
            var reader = new BinaryReader(new MemoryStream(Convert.FromBase64String(LogoData)));
            float w = reader.ReadUInt16(), h = reader.ReadUInt16();
            logoAspect = w / h;

            var verts = new List<Vector3>();
            var tris = new List<int>();
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                float x = reader.ReadUInt16(), y = reader.ReadUInt16(), rw = reader.ReadUInt16(), rh = reader.ReadUInt16();
                // Piksel (y aşağı) -> yerel (genişlik 1, yükseklik 1, y yukarı, merkez 0)
                var x0 = x / w - 0.5f;
                var x1 = (x + rw) / w - 0.5f;
                var y0 = 0.5f - y / h;
                var y1 = 0.5f - (y + rh) / h;
                var i = verts.Count;
                verts.Add(new Vector3(x0, y0, 0f));
                verts.Add(new Vector3(x1, y0, 0f));
                verts.Add(new Vector3(x1, y1, 0f));
                verts.Add(new Vector3(x0, y1, 0f));
                // Her iki yüz: hangi taraftan bakılırsa görünsün
                tris.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3, i, i + 2, i + 1, i, i + 3, i + 2 });
            }

            var mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            return mesh;
        }

        // Kullanıcının verdiği GTV logo resminden üretildi: [genişlik, yükseklik] + (x, y, w, h) ushort dikdörtgenler
        private const string LogoData =
            "MgJvAFcAAAB9AAEA2QAAAJwAHQB5AQAAJgAfAO8BAAAqAAEAIgIAAAgAAQBSAAEAggABAO4BAQAqAAEAHwIBAA0AAQBPAAIAhQABAO0BAgAqAAEAHQICAAUAAQAqAgIAAwABAE0AAwCHAAEA7AEDACoAAQAcAgMABAABACsCAwADAAEASwAEAIkAAQDsAQQAKQABABwCBAADAAEALAIEAAMAAQBJAAUAiwABAOsBBQApAAEAGwIFAAMAAQAhAgUACAABAC0CBQADAAEASAAGAIwAAQDqAQYAKgABABsCBgACAAEAIQIGAAkAAQAuAgYAAwACAEYABwCOAAEA6QEHACoAAQAaAgcAAgAEACECBwACAAMAKAIHAAMAAwBFAAgAjwABAOkBCAApAAEALwIIAAIAAgBDAAkAkQABAOgBCQApAAEAQgAKAJIAAQDnAQoAKQABACECCgAJAAEALwIKAAMAAwBBAAsAkwABAOYBCwAqAAEAGgILAAEAAQAhAgsACAABAEAADACUAAEA5QEMACoAAQAaAgwAAgAEACECDAACAAYAJQIMAAQAAQA/AA0AlQABAOQBDQAqAAEAJgINAAMAAQAvAg0AAgACAD4ADgCWAAEA4wEOACoAAQAnAg4AAwABAD0ADwCXAAEA4gEPACoAAQAoAg8AAgABAC4CDwADAAEAPAAQAJgAAQDiARAAKQABABsCEAACAAEAKAIQAAMAAQAuAhAAAgABADsAEQCZAAIA4QERACoAAQAbAhEAAwABACkCEQACAAEALQIRAAMAAQDgARIAKgABABwCEgADAAEALAISAAMAAQA6ABMAmgACAN8BEwAqAAEAHQITAAQAAQAqAhMABAABAN4BFAAqAAEAHgIUAAUAAQAoAhQABQABADkAFQCbAAEA3QEVACoAAQAgAhUACwABADgAFgCcAAIA3AEWACsAAQAiAhYABwABANsBFwArAAEANwAYAJ0AAgDbARgAKgABANoBGQAqAAEANgAaAJ4AAQDZARoAKgABADUAGwCfAAIA2AEbACoAAQDXARwAKwABADQAHQCgAAEA2QAdAAoAAQA+AR0ANwABANYBHQArAAEAMwAeAIIAAQDWAR4AKgABADMAHwAtAAEAfwEfACAAAQDVAR8AKgABADIAIAAsAAEAjwEgABAAAQDUASAAKgABADIAIQAkAAEA0wEhACoAAQAxACIAGQABANIBIgArAAEAMAAjABAAAQAUASMAKgBMANEBIwArAAEAMAAkAAYAAQDRASQAKgABANABJQAqAAEAegEmAAEAAQDWASYAJAABAHkBJwAYAAEA3gEnABsAAQBOACgACgABAHkBKAAlAAEA5wEoABEAAQAsACkAKwABAH0AKQBmAAIAeQEpACYANQDwASkABwABACwAKgAqAAEAKwArACsAAQB8ACsAZwABAMoBKwANAAEAKwAsACoAAQB7ACwAaAADAMoBLAAaAAEAKgAtACoAAQDJAS0AKQABACkALgArAAEAyAEuACsAAQApAC8AKgABAHoALwBpAAIAxwEvACsAAQAoADAAKgABAMYBMAArAAEAJwAxACsAAQB5ADEAagACAMUBMQArAAEAJwAyACoAAQDEATIAKwABACYAMwAqAAEAeAAzAGsAAgDDATMALAABACUANAArAAEAwwE0ACsAAQAlADUAKgABAHcANQBsAAEAwgE1ACsAAQAkADYAKwABAHYANgBtAAIAwQE2ACsAAQAjADcAKwABAMABNwArAAEAIwA4ACoAAQB1ADgAbgACAL8BOAAsAAEAIgA5ACoAAgC+ATkALAABAHQAOgBvAAIAvQE6ACwAAQAhADsAKgABAL0BOwArAAEAIAA8ACsAAQBzADwAcAACALwBPAArAAEAIAA9ACoAAQC7AT0AKwABAB8APgAqAAEAcgA+AHEAAgC6AT4ALAABAB4APwArAAEAuQE/ACwAAQAeAEAAKgABAHEAQAByAAIAuQFAACsAAQAdAEEAKgABALgBQQArAAEAHABCACsAAQBwAEIAcwACALcBQgArAAEAGwBDACsAAQC2AUMAKwABABsARAAqAAEAvABEACcABQC1AUQALAABABoARQArAAEAtAFFACwAAQAaAEYAKgABALMBRgAsAAEAGQBHACoAAgCyAUcALAABALIBSAArAAEAGABJACoAAQC7AEkAKAADALEBSQArAAEAFwBKACoAAQCwAUoAKwABABYASwArAAEArwFLACwAAQAWAEwAKgABALoATAApAAEArgFMACwAAQAVAE0AKgABALkATQAqAAEArQFNACwAAQAUAE4AKwABALgATgArAAEArAFOAC0AAQAUAE8AKgABALcATwAsAAEArAFPACwAAQATAFAAKgABALUAUAAuAAEAqwFQACwAAQASAFEA0QACAKoBUQAsAAEAqQFSACwAAQARAFMA0gACAKgBUwAsAAEApwFUACwAAgAQAFUA0gABAA8AVgDTAAIApgFWACwAAQClAVcALAABAA4AWADUAAEApAFYACwAAQANAFkA1AABAKMBWQAsAAEADABaANUAAQCiAVoALAABAAwAWwDUAAEAoQFbAC0AAQALAFwA1QABAKEBXAAsAAEACwBdANQAAQCgAV0ALAABAAoAXgDVAAEAeQFeAFIAAQAJAF8A1QACAHkBXwBRAAIACABhANUAAQB5AWEAUAABAAcAYgDVAAEAeQFiAE8AAQAHAGMA1AABAHkBYwBOAAEABgBkANQAAQB5AWQATQABAAUAZQDUAAEAeQFlAEwAAQAFAGYA0wABAHkBZgBLAAIABABnANMAAQADAGgA0wABAHkBaABKAAEAAwBpANEAAQB5AWkASQABAAIAagDQAAEAeQFqAEgAAQACAGsAzwABAHkBawBHAAIAAQBsAM0AAQAAAG0AywABAHkBbQBGAAEAAABuAMcAAQB5AW4ARQABAA==";
    }
}
