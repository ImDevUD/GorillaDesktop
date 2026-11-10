using ExitGames.Client.Photon;
using Photon.Pun;
using UnityEngine;

namespace GorillaDesktop
{
    // Lobide "ortak ekran kimde ve nerede" bilgisi. Sadece küçük oda ayarları (Photon room properties)
    // kullanılır; görüntü/ses GT sunucularından değil, ayrı aktarma sunucusundan gider.
    internal sealed class ShareSession
    {
        private const string OwnerKey = "gdShareOwner", PoseKey = "gdSharePose";

        private readonly bool moddedOnly;
        private float[] lastSentPose;

        public ShareSession(bool moddedOnly)
        {
            this.moddedOnly = moddedOnly;
        }

        public bool InRoom => PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null;

        // Utilla modlu lobileri oyun modunda "MODDED" geçer
        public bool InAllowedRoom
        {
            get
            {
                if (!InRoom) return false;
                if (!moddedOnly) return true;
                var props = PhotonNetwork.CurrentRoom.CustomProperties;
                return props != null && props.TryGetValue("gameMode", out var mode) && mode != null &&
                       mode.ToString().ToUpperInvariant().Contains("MODDED");
            }
        }

        public int MyActor => PhotonNetwork.LocalPlayer?.ActorNumber ?? -1;
        public string RoomName => PhotonNetwork.CurrentRoom?.Name ?? "";

        // Ekranı alan oyuncu; yoksa ya da lobiden çıktıysa 0
        public int Owner
        {
            get
            {
                if (!InRoom) return 0;
                var room = PhotonNetwork.CurrentRoom;
                if (!room.CustomProperties.TryGetValue(OwnerKey, out var value) || !(value is int actor) || actor == 0) return 0;
                return room.Players.ContainsKey(actor) ? actor : 0;
            }
        }

        public string OwnerName
        {
            get
            {
                var owner = Owner;
                if (owner == 0) return "";
                return PhotonNetwork.CurrentRoom.Players.TryGetValue(owner, out var p) ? p.NickName : "?";
            }
        }

        // Ekranı al. Aynı anda biri daha denerse Photon'un "beklenen değer" kontrolü sadece birini kabul eder
        public bool TryClaim(Transform screen, float width)
        {
            if (!InAllowedRoom) return false;
            var room = PhotonNetwork.CurrentRoom;
            var current = room.CustomProperties.TryGetValue(OwnerKey, out var value) ? value : null;
            if (Owner != 0 && Owner != MyActor) return false;

            var pose = Pose(screen, width);
            var set = new Hashtable { { OwnerKey, MyActor }, { PoseKey, pose } };
            var expected = current == null ? null : new Hashtable { { OwnerKey, current } };
            lastSentPose = pose;
            return room.SetCustomProperties(set, expected);
        }

        public void Release()
        {
            if (!InRoom || Owner != MyActor) return;
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { OwnerKey, 0 } }, new Hashtable { { OwnerKey, MyActor } });
        }

        // Yayıncı ekranı taşıdıysa yeni yerini herkese bildir (sadece değiştiğinde)
        public void SyncPose(Transform screen, float width)
        {
            if (!InRoom || Owner != MyActor) return;
            var pose = Pose(screen, width);
            if (lastSentPose != null && Same(pose, lastSentPose)) return;
            lastSentPose = pose;
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { PoseKey, pose } });
        }

        public bool TryGetPose(out Vector3 position, out Quaternion rotation, out float width)
        {
            position = default;
            rotation = Quaternion.identity;
            width = 1.6f;
            if (!InRoom || !PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(PoseKey, out var value) ||
                !(value is float[] p) || p.Length < 8) return false;
            position = new Vector3(p[0], p[1], p[2]);
            rotation = new Quaternion(p[3], p[4], p[5], p[6]);
            width = p[7];
            return true;
        }

        private static float[] Pose(Transform t, float width)
        {
            var p = t.position;
            var r = t.rotation;
            return new[] { p.x, p.y, p.z, r.x, r.y, r.z, r.w, width };
        }

        private static bool Same(float[] a, float[] b)
        {
            for (var i = 0; i < a.Length; i++)
                if (Mathf.Abs(a[i] - b[i]) > 0.005f) return false;
            return true;
        }
    }
}
