using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.XR;

namespace GorillaDesktop
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.gorilladesktop.gtag";
        public const string Name = "GorillaDesktop";
        public const string Version = "4.1.0";

        internal static ManualLogSource Log;

        public enum ControlMode { Otomatik, VR, Masaustu }

        // Windows sanal tuş kodları
        private const int VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_F6 = 0x75, VK_F7 = 0x76, VK_F8 = 0x77, VK_F9 = 0x78;

        // Parmak ucu: bu kadar yakına gelince imleç parmağı takip eder; yüzeye değince basar
        private const float HoverDistance = 0.08f, PressDepth = 0.004f, ReleaseDistance = 0.02f;

        private ConfigEntry<ControlMode> mode;
        private ConfigEntry<int> monitorIndex;
        private ConfigEntry<float> screenWidthMeters;
        private ConfigEntry<int> captureFps;
        private ConfigEntry<bool> forceGdi;
        private ConfigEntry<Vector3> pointerRotationOffset, aimOffset;
        private ConfigEntry<float> scrollSpeed;
        private ConfigEntry<bool> refocusGame;
        private ConfigEntry<int> keyToggle, keyClick, keyRightClick, keyMove, keyScrollUp, keyScrollDown;
        private ConfigEntry<bool> audioEnabled, audioAutoRoute, touchEnabled, laserEnabled, showLogo, pressSound;
        private ConfigEntry<string> audioDevice, audioSavedDefault;
        private ConfigEntry<float> audioVolume, audioMaxDistance, cursorSize;
        private ConfigEntry<string> shareServer, shareKey, sharePlace;
        private ConfigEntry<float> shareQuality;
        private ConfigEntry<int> shareFps;
        private ConfigEntry<bool> shareModdedOnly;

        // Arkadaşlarla paylaşım
        private ShareSession share;
        private StreamPublisher publisher;
        private StreamViewer viewer;
        private SharedScreen sharedView;
        private int viewerOwner;
        private float nextPoseSync;
        private string shareNotice = "";

        private IScreenSource source;
        private Material screenMat;
        private AudioLoopback audio;
        private AudioSource audioSource;
        private AudioRouter router;
        private SettingsUi settings;
        private MonitorModel model;
        private Font font;

        private GameObject screenRoot;
        private Transform screen;
        private Transform cursor;
        private readonly LineRenderer[] lasers = new LineRenderer[2];
        private readonly Transform[] laserDots = new Transform[2];
        private bool visible;
        private float screenWidth, screenAspect;

        // El: 0 = sağ, 1 = sol. Avuç (grip ile tutma) ve işaret parmağı ucu (basma)
        private Transform[] palms;
        private Transform[] fingertips;
        private float nextFingertipSearch;
        // Parmak düzken ölçülen parmak ucu konumu (el yerel uzayında); lazer bunu kullanır
        private readonly Vector3[] straightTipLocal = new Vector3[2];
        private readonly bool[] straightCalibrated = new bool[2];

        // İşaretçiler: VR'da 0 = sağ el, 1 = sol el; masaüstünde sadece 0 = kamera
        private readonly Pointer[] pointers = { new Pointer(), new Pointer() };
        private int pointerCount;
        private int activePointer, clickOwner = -1;

        private bool prevToggle, prevKeyboard, leftDown;
        private Vector2Int lastCursor = new Vector2Int(-1, -1);
        private float scrollAccum;
        private IntPtr gameWindow;

        // Parmakla basma
        private int touchHand = -1;
        private readonly float[] prevFingerFront = { 1f, 1f };
        private SettingsUi.Button fingerHoverA, fingerHoverB;
        private readonly Transform[] fingerBuffer = new Transform[2]; // her karede dizi oluşturmamak için

        // Taşıma / iki elle büyütme
        private readonly List<int> grabbing = new List<int>(2);
        private int grabMode; // 0 yok, 1 tek el, 2 iki el
        private Vector3 grabLocalPos;
        private Quaternion grabLocalRot, lastHandsFrame = Quaternion.identity;
        private float twoHandStartDist, twoHandStartWidth;

        private sealed class Pointer
        {
            public Transform T;    // lazerin çıktığı yer (VR: el, masaüstü: kamera)
            public Vector3 Origin, Dir, HitPoint, UiPoint;
            public Vector2 Uv;
            public SettingsUi.Button UiButton;
            public bool Trigger, Grip, Secondary, Hit;
            public bool PrevTrigger, PrevGrip, PrevSecondary;
            public float Stick;

            // Son karelerin ekran noktaları: tetiğe basma hareketi eli oynatır, tık basmadan hemen önceki noktaya gider
            private readonly Vector2[] uvHistory = new Vector2[32];
            private readonly float[] timeHistory = new float[32];
            private readonly bool[] hitHistory = new bool[32];
            private int historyPos;

            public void Record()
            {
                historyPos = (historyPos + 1) % uvHistory.Length;
                uvHistory[historyPos] = Uv;
                timeHistory[historyPos] = Time.unscaledTime;
                hitHistory[historyPos] = Hit;
            }

            public Vector2 UvAgo(float seconds)
            {
                var target = Time.unscaledTime - seconds;
                for (var k = 0; k < uvHistory.Length; k++)
                {
                    var idx = (historyPos - k + uvHistory.Length) % uvHistory.Length;
                    if (!hitHistory[idx]) break;
                    if (timeHistory[idx] <= target) return uvHistory[idx];
                }
                return Uv;
            }
        }

        private void Awake()
        {
            Log = Logger;
            mode = Config.Bind("Genel", "KontrolModu", ControlMode.Otomatik,
                "Otomatik: VR başlık takılıysa kumandalar, değilse (WalkSim) klavye + ekran ortası nişan");
            monitorIndex = Config.Bind("Genel", "Monitor", -1,
                "Yansıtılacak monitör sırası (0, 1, ...). -1 = otomatik (VR: ana monitör, masaüstü: oyunun açık OLMADIĞI monitör)");
            refocusGame = Config.Bind("Genel", "TiklamadanSonraOyunaDon", true,
                "Masaüstü modunda tıkladıktan sonra odağı oyuna geri ver. Diğer uygulamaya yazı yazmak istiyorsan false yap");
            screenWidthMeters = Config.Bind("Ekran", "GenislikMetre", 1.6f, "Oyun içi ekranın başlangıç genişliği (metre)");
            captureFps = Config.Bind("Ekran", "FPS", 60, "Saniyede en fazla kaç kare");
            forceGdi = Config.Bind("Ekran", "EskiYontemiZorla", false, "true = GPU yerine eski (yavaş) GDI yakalamayı kullan");
            cursorSize = Config.Bind("Ekran", "ImlecBoyutu", 1f, "Oyun içi fare imlecinin boyut çarpanı");
            showLogo = Config.Bind("Ekran", "GTVLogosu", true, "Çerçevenin alt kenarında beyaz GTV logosu göster");
            laserEnabled = Config.Bind("Lazer", "Acik", false, "VR: ellerden uzaktan tıklama lazeri (kapalıyken sadece parmakla basılır)");
            pointerRotationOffset = Config.Bind("Lazer", "YonOfseti", new Vector3(90f, 0f, 0f),
                "Yedek: kumanda nişan yönü okunamazsa lazerin el dönüşüne göre açısı (derece)");
            aimOffset = Config.Bind("Lazer", "NisanOfseti", Vector3.zero,
                "Lazerin kumanda nişan yönüne göre açısı (derece). Ör. biraz aşağı bakıyorsa x'i eksi yap");
            scrollSpeed = Config.Bind("Lazer", "KaydirmaHizi", 6f, "Kaydırma hızı");

            const string keyHelp = "Windows sanal tuş kodu (onluk). Liste: learn.microsoft.com/windows/win32/inputdev/virtual-key-codes";
            keyToggle = Config.Bind("Klavye", "EkranAcKapat", VK_F8, "F8. " + keyHelp);
            keyClick = Config.Bind("Klavye", "SolTik", VK_F9, "F9 (basılı tut = sürükle)");
            keyRightClick = Config.Bind("Klavye", "SagTik", VK_F7, "F7");
            keyMove = Config.Bind("Klavye", "EkraniTasi", VK_F6, "F6 (basılı tut, kamerayla birlikte taşınır)");
            keyScrollUp = Config.Bind("Klavye", "YukariKaydir", VK_PRIOR, "Page Up");
            keyScrollDown = Config.Bind("Klavye", "AsagiKaydir", VK_NEXT, "Page Down");

            audioEnabled = Config.Bind("Ses", "Acik", true, "PC sesini oyun içi ekrandan çal");
            audioDevice = Config.Bind("Ses", "Cihaz", "Steam Streaming Speakers",
                "PC sesinin gideceği sanal çıkış cihazının adı (bir kısmı yeterli). Mod sesi buradan yakalar");
            audioAutoRoute = Config.Bind("Ses", "OtomatikYonlendir", true,
                "Ekran açıkken Windows varsayılan çıkışını otomatik olarak yukarıdaki cihaza çevir, oyunun sesini kulaklıkta tut. " +
                "Ekran kapanınca/oyundan çıkınca eski haline döner. false = Windows ses ayarlarını elle yap");
            audioSavedDefault = Config.Bind("Ses", "OncekiVarsayilanCihaz", "",
                "Mod tarafından kullanılır, elle değiştirme. Doluysa ses yönlendirmesi geri alınmamış demektir");
            audioVolume = Config.Bind("Ses", "Seviye", 1f, "Ses seviyesi (0-1)");
            audioMaxDistance = Config.Bind("Ses", "MaksMesafe", 20f, "Bu mesafeden (metre) sonra ses duyulmaz");
            touchEnabled = Config.Bind("Dokunma", "Acik", true, "VR: işaret parmağı ucuyla ekrana basarak tıkla (GT düğmeleri gibi)");
            pressSound = Config.Bind("Dokunma", "TikSesi", true, "Basınca GT düğmesi sesi ve titreşim");

            shareServer = Config.Bind("Paylasim", "Sunucu", "",
                "Aktarma sunucusunun adresi, ör. wss://gorilladesktop.onrender.com (boşsa paylaşım kapalı)");
            shareKey = Config.Bind("Paylasim", "Anahtar", "", "Sunucudaki SECRET ile aynı olmalı. Arkadaşlarında da aynısı olmalı");
            shareQuality = Config.Bind("Paylasim", "Kalite", 0.6f, "Yayın görüntü kalitesi (0.3 - 0.9). Yüksek = net ama daha çok internet");
            shareFps = Config.Bind("Paylasim", "FPS", 15, "Yayının saniyedeki kare sayısı");
            shareModdedOnly = Config.Bind("Paylasim", "SadeceModluLobi", true, "Ortak ekran sadece Utilla modlu lobilerde çalışsın");
            sharePlace = Config.Bind("Paylasim", "SabitKonum", "",
                "Ortak ekranın sabit yeri (ayarlardaki 'Ortak ekranın yeri burası' tuşu doldurur). " +
                "Arkadaşlarında da aynısı olmalı. Boşsa ekran, yayıncının koyduğu yerde çıkar");
            share = new ShareSession(shareModdedOnly.Value);

            Native.TrySetDpiAware();

            // Önceki oturum çöktüyse ses ayarlarını geri al
            router = new AudioRouter(audioSavedDefault);
            if (router.IsRouted)
            {
                Log.LogWarning("Önceki oturumdan kalan ses yönlendirmesi geri alınıyor.");
                router.Restore();
            }
            Application.quitting += Cleanup;
            AppDomain.CurrentDomain.ProcessExit += (_, __) => router.Restore();
        }

        private bool IsVR()
        {
            if (mode.Value == ControlMode.VR) return true;
            if (mode.Value == ControlMode.Masaustu) return false;
            try { return XRSettings.isDeviceActive; } catch { return false; }
        }

        // ---------------------------------------------------------------- giriş

        // Kumandaları/klavyeyi okuyup işaretçileri doldurur
        private void ReadInputs(GorillaTagger tagger, bool vr, out bool toggle, out bool keyboard)
        {
            var poller = ControllerInputPoller.instance;
            toggle = Native.KeyDown(keyToggle.Value);
            keyboard = false;

            if (vr && poller != null)
            {
                toggle |= poller.leftControllerSecondaryButton;
                keyboard = poller.rightControllerSecondaryButton;
                FillHand(pointers[0], tagger.rightHandTransform, poller.rightControllerIndexFloat, poller.rightControllerGripFloat,
                    poller.rightControllerPrimaryButton, poller.rightControllerPrimary2DAxis.y);
                FillHand(pointers[1], tagger.leftHandTransform, poller.leftControllerIndexFloat, poller.leftControllerGripFloat,
                    poller.leftControllerPrimaryButton, poller.leftControllerPrimary2DAxis.y);
                pointerCount = 2;
            }
            else
            {
                // WalkSim: ekranın ortasından nişan al
                var p = pointers[0];
                var cam = Camera.main;
                p.T = cam != null ? cam.transform : tagger.headCollider.transform;
                p.Origin = p.T.position;
                p.Dir = p.T.forward;
                p.Trigger = p.Grip = p.Secondary = false;
                p.Stick = 0f;
                pointerCount = 1;
            }

            // Klavye her iki modda da birinci işaretçiyi kontrol eder (odak başka penceredeyken bile)
            var k = pointers[0];
            k.Trigger |= Native.KeyDown(keyClick.Value);
            k.Secondary |= Native.KeyDown(keyRightClick.Value);
            k.Grip |= Native.KeyDown(keyMove.Value);
            if (Native.KeyDown(keyScrollUp.Value)) k.Stick += 1f;
            if (Native.KeyDown(keyScrollDown.Value)) k.Stick -= 1f;
        }

        private void FillHand(Pointer p, Transform hand, float trigger, float grip, bool secondary, float stick)
        {
            p.T = hand;
            p.Origin = hand.position;
            p.Dir = hand.rotation * Quaternion.Euler(pointerRotationOffset.Value) * Vector3.forward;
            // Tetiği hafif çekince tıklar (fare tuşu gibi anında), bırakmaya başlayınca bırakır
            p.Trigger = p.Trigger ? trigger > 0.2f : trigger > 0.35f;
            p.Grip = grip > 0.7f;
            p.Secondary = secondary;
            p.Stick = Mathf.Abs(stick) > 0.2f ? stick : 0f;

            var i = p == pointers[0] ? 0 : 1;

            // Lazer, gorilla elinin işaret parmağının devamı olarak dümdüz çıkar (f_index.01 -> f_index.03).
            // Parmağın EN DÜZ olduğu an (iki kemik arası mesafe en uzun) kumandaya göre bir kez öğrenilip kilitlenir.
            // Sonra lazer sadece kumandayla döner; parmak/başparmak animasyonu lazeri hiç oynatmaz.
            if (indexBase[i] != null && indexTip[i] != null)
            {
                var along = indexTip[i].position - indexBase[i].position;
                var chord = along.magnitude;
                if (chord > 0.001f && chord > straightChord[i] + 0.002f)
                {
                    straightChord[i] = chord;
                    straightLocalDir[i] = hand.InverseTransformDirection(along / chord);
                    // Kemik son boğumun başında; ucu biraz ileride
                    straightTipLocal[i] = hand.InverseTransformPoint(indexTip[i].position + along * 0.6f);
                    straightCalibrated[i] = true;
                }

                if (straightCalibrated[i])
                {
                    p.Origin = hand.TransformPoint(straightTipLocal[i]);
                    p.Dir = hand.TransformDirection(straightLocalDir[i]).normalized;
                }
            }
            // Parmak kemikleri bulunamazsa: kumandanın nişan yönü
            else if (TryControllerAim(i == 0 ? XRNode.RightHand : XRNode.LeftHand, out var aim))
            {
                p.Dir = aim * Quaternion.Euler(aimOffset.Value) * Vector3.forward;
            }

            // Titreme filtresi: küçük titremeleri yumuşat, hızlı hareketi anında takip et
            var s = smoothedDir[i];
            if (s.sqrMagnitude < 0.5f) s = p.Dir;
            var angle = Vector3.Angle(s, p.Dir);
            var follow = 0.12f + 0.88f * Mathf.Clamp01(angle / 4f) * Mathf.Clamp01(angle / 4f);
            s = Vector3.Slerp(s, p.Dir, follow).normalized;
            smoothedDir[i] = s;
            p.Dir = s;
        }

        // Gorilla elinin işaret parmağı kemikleri (lazerin yönü bunlardan çıkar)
        private readonly Transform[] indexBase = new Transform[2], indexTip = new Transform[2];
        private readonly Vector3[] straightLocalDir = new Vector3[2], smoothedDir = new Vector3[2];
        private readonly float[] straightChord = new float[2];
        private float nextBoneSearch;

        private void FindIndexBones(GorillaTagger tagger)
        {
            if ((indexBase[0] != null && indexBase[1] != null) || Time.time < nextBoneSearch) return;
            nextBoneSearch = Time.time + 2f;
            if (tagger.offlineVRRig == null) return;

            foreach (var t in tagger.offlineVRRig.GetComponentsInChildren<Transform>(true))
            {
                var name = t.name.ToLowerInvariant();
                var side = name.EndsWith(".r") ? 0 : name.EndsWith(".l") ? 1 : -1;
                if (side < 0) continue;
                if (name.StartsWith("f_index.01")) indexBase[side] = t;
                else if (name.StartsWith("f_index.03")) indexTip[side] = t;
            }

            if (indexBase[0] != null && indexTip[0] != null)
                Log.LogInfo($"İşaret parmağı kemikleri bulundu: {indexBase[0].name} -> {indexTip[0].name}");
            else
                Log.LogWarning("İşaret parmağı kemikleri bulunamadı; lazer kumanda yönünü kullanacak.");
        }

        private static readonly List<InputFeatureUsage> usageBuffer = new List<InputFeatureUsage>();
        private readonly InputFeatureUsage<Quaternion>?[] pointerUsage = new InputFeatureUsage<Quaternion>?[2];
        private readonly bool[] pointerUsageSearched = new bool[2];

        // Kumandanın dünya uzayındaki nişan dönüşü. Takip uzayından dünyaya çevirmek için
        // kafanın takip dönüşü ile kameranın dünya dönüşü arasındaki farkı kullanır (hiyerarşiden bağımsız)
        private bool TryControllerAim(XRNode node, out Quaternion world)
        {
            world = Quaternion.identity;
            var cam = Camera.main;
            if (cam == null) return false;

            var device = InputDevices.GetDeviceAtXRNode(node);
            var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (!device.isValid || !head.isValid) return false;

            // OpenXR "pointer" (nişan) pozu varsa onu kullan, yoksa kumandanın kendi dönüşü
            var i = node == XRNode.RightHand ? 0 : 1;
            if (!pointerUsageSearched[i] && device.TryGetFeatureUsages(usageBuffer))
            {
                pointerUsageSearched[i] = true;
                foreach (var u in usageBuffer)
                {
                    var name = u.name.ToLowerInvariant();
                    if (u.type == typeof(Quaternion) && name.Contains("pointer") && name.Contains("rot"))
                    {
                        pointerUsage[i] = u.As<Quaternion>();
                        Log.LogInfo($"Lazer yönü için '{u.name}' kullanılıyor ({node})");
                        break;
                    }
                }
            }

            Quaternion rot;
            if (!(pointerUsage[i].HasValue && device.TryGetFeatureValue(pointerUsage[i].Value, out rot)) &&
                !device.TryGetFeatureValue(CommonUsages.deviceRotation, out rot)) return false;
            if (!head.TryGetFeatureValue(CommonUsages.deviceRotation, out var headRot) &&
                !head.TryGetFeatureValue(CommonUsages.centerEyeRotation, out headRot)) return false;

            world = cam.transform.rotation * Quaternion.Inverse(headRot) * rot;
            return true;
        }

        // GT'nin düğmelere basmak için kullandığı işaret parmağı ucu collider'ları
        private void FindFingertips(GorillaTagger tagger)
        {
            if (fingertips != null || Time.time < nextFingertipSearch) return;
            nextFingertipSearch = Time.time + 2f;

            Transform right = null, left = null;
            foreach (var indicator in FindObjectsOfType<GorillaTriggerColliderHandIndicator>())
            {
                // Yerel oyuncunun ellerine en yakın olanları al (diğer oyuncularınki değil)
                var hand = indicator.isLeftHand ? tagger.leftHandTransform : tagger.rightHandTransform;
                if (Vector3.Distance(indicator.transform.position, hand.position) > 0.35f) continue;
                if (indicator.isLeftHand) left = indicator.transform; else right = indicator.transform;
            }

            if (right != null && left != null)
            {
                fingertips = new[] { right, left };
                Log.LogInfo("Parmak uçları bulundu: " + right.name + ", " + left.name);
            }
        }

        private Transform Fingertip(int hand) => fingertips != null ? fingertips[hand] : (hand == 0 ? GorillaTagger.Instance.rightHandTriggerCollider.transform : GorillaTagger.Instance.leftHandTriggerCollider.transform);

        // ---------------------------------------------------------------- kare döngüsü

        private void Update()
        {
            var tagger = GorillaTagger.Instance;
            if (tagger == null) return;
            if (palms == null) palms = new[] { tagger.rightHandTransform, tagger.leftHandTransform };
            FindFingertips(tagger);
            FindIndexBones(tagger);

            var vr = IsVR();
            Native.TrySetDpiAware(); // ekran koordinatları ölçeklenmesin (Windows %125 vb.)
            ReadInputs(tagger, vr, out var toggle, out var keyboard);

            if (toggle && !prevToggle) SetVisible(!visible, tagger);
            prevToggle = toggle;
            UpdateSharing(vr);

            if (!visible)
            {
                EndFrame(keyboard);
                return;
            }

            source.Update();
            if (screenMat.mainTexture != source.Texture) screenMat.mainTexture = source.Texture;
            UpdateCursorOverlay();
            if (audioSource != null) audioSource.volume = Mathf.Clamp01(audioVolume.Value);

            // Lazerler: VR'da sadece ayarlardan açıksa; masaüstünde ekran ortası nişan her zaman
            var lasersOn = !vr || laserEnabled.Value;
            settings.FollowScreen(screen);
            for (var i = 0; i < pointerCount; i++)
            {
                var p = pointers[i];
                var fingerBusy = vr && (touchHand == i || FingerNear(i));
                var aims = lasersOn && !fingerBusy;
                p.UiButton = aims ? settings.Raycast(p.Origin, p.Dir, out p.UiPoint) : null;
                p.Hit = aims && p.UiButton == null && RaycastScreen(p.Origin, p.Dir, out p.HitPoint, out p.Uv);
                p.Record();
            }
            for (var i = pointerCount; i < pointers.Length; i++) { pointers[i].UiButton = null; pointers[i].Hit = false; }

            UpdateGrab(vr);
            settings.FollowScreen(screen);
            model?.Follow(screen, screenWidth);

            // Ayar tuşları: parmakla ya da lazerle tetik
            fingerHoverA = fingerHoverB = null;
            if (grabMode == 0)
            {
                for (var i = 0; i < pointerCount; i++)
                {
                    var p = pointers[i];
                    if (p.UiButton != null && p.Trigger && !p.PrevTrigger) settings.Click(p.UiButton);
                }
                if (vr)
                {
                    fingerBuffer[0] = Fingertip(0);
                    fingerBuffer[1] = Fingertip(1);
                    var clicked = settings.Touch(fingerBuffer, out fingerHoverA, out fingerHoverB);
                    if (clicked >= 0) PressFeedback(clicked == 1);
                }
            }
            settings.SetHover(pointers[0].UiButton, pointers[1].UiButton, fingerHoverA, fingerHoverB);

            DrawLasers(vr, lasersOn);
            if (grabMode != 0)
            {
                ReleaseLeft(vr);
                EndFrame(keyboard);
                return;
            }

            // Parmakla basma, lazerden önceliklidir
            if (!(vr && UpdateFingerTouch())) UpdatePointers(vr, keyboard);
            EndFrame(keyboard);
        }

        private void EndFrame(bool keyboard)
        {
            prevKeyboard = keyboard;
            foreach (var p in pointers)
            {
                p.PrevTrigger = p.Trigger;
                p.PrevGrip = p.Grip;
                p.PrevSecondary = p.Secondary;
            }
        }

        // ---------------------------------------------------------------- parmakla basma (GT düğmeleri gibi)

        // Parmak ucunun ekrana göre konumu; front > 0 = ekranın önünde (metre)
        private bool FingerOnScreen(int hand, out Vector2 uv, out float front)
        {
            uv = default;
            var p = Fingertip(hand).position;
            var local = screen.InverseTransformPoint(p);
            front = Vector3.Dot(p - screen.position, -screen.forward);
            if (Mathf.Abs(local.x) > 0.5f || Mathf.Abs(local.y) > 0.5f) return false;
            uv = new Vector2(local.x + 0.5f, 0.5f - local.y);
            return true;
        }

        private bool FingerNear(int hand)
        {
            if (!touchEnabled.Value) return false;
            return FingerOnScreen(hand, out _, out var front) && front < HoverDistance * 2f && front > -0.1f;
        }

        // Parmak yaklaşınca imleç parmağı takip eder; yüzeye önden değince sol tık, değdiği sürece sürükler.
        // Parmak kontrolü elindeyse true döner
        private bool UpdateFingerTouch()
        {
            if (!touchEnabled.Value) return false;

            if (touchHand >= 0)
            {
                var on = FingerOnScreen(touchHand, out var uv, out var front);
                // Tıklandı; parmak ekrandan çekilene kadar tekrar tıklamaz
                if (on && front < ReleaseDistance && front > -0.12f) return true;
                touchHand = -1;
                return false;
            }

            var hovering = false;
            for (var i = 0; i < 2; i++)
            {
                var on = FingerOnScreen(i, out var uv, out var front);
                var cameFromFront = prevFingerFront[i] > PressDepth;
                prevFingerFront[i] = on ? front : 1f;
                if (!on || front > HoverDistance || front < -0.12f) continue;

                var target = ToDesktop(uv);
                if (front <= PressDepth && cameFromFront && !leftDown)
                {
                    touchHand = i;
                    ClickAt(target, true);
                    PressFeedback(i == 1);
                    return true;
                }

                // Basmadan önce imleç parmağın olduğu yeri göstersin
                if (!hovering && !leftDown) MoveCursor(target);
                hovering = true;
            }
            return hovering;
        }

        // GT düğmesine basmış gibi: titreşim + tık sesi
        private void PressFeedback(bool left)
        {
            if (!pressSound.Value) return;
            try
            {
                var tagger = GorillaTagger.Instance;
                tagger.StartVibration(left, tagger.tapHapticStrength / 2f, tagger.tapHapticDuration);
                tagger.offlineVRRig.PlayHandTapLocal(67, left, 0.05f);
            }
            catch { }
        }

        // ---------------------------------------------------------------- lazer / klavye ile tıklama

        private const float PrePressSeconds = 0.06f;

        // Her basış tek bir tık: ne kadar basılı tutulursa tutulsun basılı kalmaz, sürüklenmez
        private void ClickAt(Vector2Int target, bool vr)
        {
            lastCursor = target;
            Native.MouseAt(target.x, target.y, Native.MOUSEEVENTF_LEFTDOWN);
            Native.MouseAt(target.x, target.y, Native.MOUSEEVENTF_LEFTUP);
            if (!vr && refocusGame.Value && GameWindow() != IntPtr.Zero)
                Native.SetForegroundWindow(GameWindow());
        }

        private void UpdatePointers(bool vr, bool keyboard)
        {
            for (var i = 0; i < pointerCount; i++)
            {
                var p = pointers[i];
                if (!p.Hit) continue;
                // Basma hareketi eli oynatır: basmadan hemen önce nişan alınan nokta
                var target = ToDesktop(p.UvAgo(PrePressSeconds));

                // Tetiğe basılan el aktif olur ve bir kez tıklar
                if (p.Trigger && !p.PrevTrigger)
                {
                    activePointer = i;
                    ClickAt(target, vr);
                    if (vr) PressFeedback(i == 1);
                }

                if (p.Secondary && !p.PrevSecondary)
                {
                    activePointer = i;
                    if (vr) PressFeedback(i == 1);
                    Native.MouseAt(target.x, target.y, Native.MOUSEEVENTF_RIGHTDOWN);
                    Native.MouseAt(target.x, target.y, Native.MOUSEEVENTF_RIGHTUP);
                }
            }

            // İmleç aktif eli takip eder; aktif el ekranda değilse ekrana bakan diğer el
            if (activePointer >= pointerCount || !pointers[activePointer].Hit)
                for (var i = 0; i < pointerCount; i++)
                    if (pointers[i].Hit) { activePointer = i; break; }

            var a = pointers[activePointer < pointerCount ? activePointer : 0];
            if (!a.Hit) return;
            var aim = ToDesktop(a.Uv);

            // Masaüstünde sürekli taşımıyoruz, yoksa WalkSim fare hareketini kamera dönüşü sanar
            if (vr && !leftDown) MoveCursor(aim);

            if (keyboard && !prevKeyboard) OpenOnScreenKeyboard();

            if (Mathf.Abs(a.Stick) > 0.01f)
            {
                scrollAccum += a.Stick * scrollSpeed.Value * 120f * Time.deltaTime;
                var delta = (int)scrollAccum;
                if (delta != 0)
                {
                    Native.MouseAt(aim.x, aim.y, Native.MOUSEEVENTF_WHEEL, delta);
                    scrollAccum -= delta;
                }
            }
            else scrollAccum = 0f;
        }

        // ---------------------------------------------------------------- taşıma / büyütme

        // Grip: ekranı tut. Eli ekranın yakınındayken (ya da lazer ekrandayken) basınca yakalar.
        // Tek elle taşı, iki elle tutunca (Meta'daki gibi) taşı + büyüt/küçült
        private void UpdateGrab(bool vr)
        {
            var changed = false;
            for (var i = 0; i < pointerCount; i++)
            {
                var p = pointers[i];
                var held = grabbing.Contains(i);
                if (p.Grip && !p.PrevGrip && !held && (!vr || p.Hit || PalmNearScreen(i))) { grabbing.Add(i); changed = true; }
                else if (!p.Grip && held) { grabbing.Remove(i); changed = true; }
            }
            for (var i = grabbing.Count - 1; i >= 0; i--)
                if (grabbing[i] >= pointerCount) { grabbing.RemoveAt(i); changed = true; }

            var mode = Math.Min(grabbing.Count, 2);
            if (changed || mode != grabMode) BeginGrab(mode);
            grabMode = mode;

            if (mode == 1)
            {
                var p = pointers[grabbing[0]];
                // Tutarken joystick ileri/geri = ekranı uzaklaştır/yaklaştır
                screen.position = p.T.TransformPoint(grabLocalPos) + p.Dir * (p.Stick * 1.5f * Time.deltaTime);
                screen.rotation = p.T.rotation * grabLocalRot;
                grabLocalPos = p.T.InverseTransformPoint(screen.position);
            }
            else if (mode == 2)
            {
                var a = pointers[grabbing[0]].T.position;
                var b = pointers[grabbing[1]].T.position;
                var frame = HandsFrame(a, b);
                screen.position = (a + b) * 0.5f + frame * grabLocalPos;
                screen.rotation = frame * grabLocalRot;
                var scale = Vector3.Distance(a, b) / Mathf.Max(0.01f, twoHandStartDist);
                SetScreenWidth(Mathf.Clamp(twoHandStartWidth * scale, 0.3f, 8f));
            }
        }

        // Avuç ekranın (ya da kasasının) yakınında mı
        private bool PalmNearScreen(int hand)
        {
            var p = palms[hand].position;
            var local = screen.InverseTransformPoint(p);
            var front = Vector3.Dot(p - screen.position, -screen.forward);
            var marginX = 0.12f / screenWidth;
            var marginY = 0.15f / (screenWidth * screenAspect);
            return Mathf.Abs(local.x) < 0.5f + marginX && Mathf.Abs(local.y) < 0.5f + marginY && front < 0.2f && front > -0.15f;
        }

        private void BeginGrab(int mode)
        {
            if (mode == 1)
            {
                var t = pointers[grabbing[0]].T;
                grabLocalPos = t.InverseTransformPoint(screen.position);
                grabLocalRot = Quaternion.Inverse(t.rotation) * screen.rotation;
            }
            else if (mode == 2)
            {
                var a = pointers[grabbing[0]].T.position;
                var b = pointers[grabbing[1]].T.position;
                var frame = HandsFrame(a, b);
                grabLocalPos = Quaternion.Inverse(frame) * (screen.position - (a + b) * 0.5f);
                grabLocalRot = Quaternion.Inverse(frame) * screen.rotation;
                twoHandStartDist = Vector3.Distance(a, b);
                twoHandStartWidth = screenWidth;
            }
        }

        // İki elin oluşturduğu yatay yön; eller döndükçe ekran da döner
        private Quaternion HandsFrame(Vector3 a, Vector3 b)
        {
            var across = b - a;
            across.y = 0f;
            if (across.sqrMagnitude < 0.0004f) return lastHandsFrame;
            lastHandsFrame = Quaternion.LookRotation(Vector3.Cross(across.normalized, Vector3.up), Vector3.up);
            return lastHandsFrame;
        }

        private void SetScreenWidth(float width)
        {
            screenWidth = width;
            screen.localScale = new Vector3(width, width * screenAspect, 1f);
        }

        // ---------------------------------------------------------------- aç / kapa

        private void SetVisible(bool show, GorillaTagger tagger)
        {
            var vr = IsVR();
            if (show && screenRoot == null) BuildScreen(vr);
            visible = show;
            screenRoot.SetActive(show);

            if (show)
            {
                Application.runInBackground = true;
                source.Start();
                StartAudio();
                PlaceInFront(tagger);
            }
            else
            {
                // Ekranını kapatan yayıncı yayını da bırakır
                share.Release();
                StopPublisher();
                source.Stop();
                StopAudio();
                ReleaseLeft(true);
                grabbing.Clear();
                grabMode = 0;
            }
        }

        private void PlaceInFront(GorillaTagger tagger)
        {
            var cam = Camera.main;
            var view = cam != null ? cam.transform : tagger.headCollider.transform;
            var flatForward = Vector3.ProjectOnPlane(view.forward, Vector3.up).normalized;
            if (flatForward.sqrMagnitude < 0.01f) flatForward = Vector3.forward;
            screen.position = tagger.headCollider.transform.position + flatForward * 1.4f;
            screen.rotation = Quaternion.LookRotation(flatForward, Vector3.up);
        }

        private void StartAudio()
        {
            if (!audioEnabled.Value) return;
            if (audioAutoRoute.Value) router.Route(audioDevice.Value);

            if (audio == null) audio = new AudioLoopback(audioDevice.Value);
            audio.Publisher = publisher;
            audio.Start();

            if (audioSource == null)
            {
                var player = screen.gameObject.AddComponent<AudioPlayer>();
                audioSource = player.Setup(audioVolume.Value, audioMaxDistance.Value);
                player.Source = audio;
            }
            audioSource.Play();
        }

        private void StopAudio()
        {
            if (audioSource != null) audioSource.Stop();
            audio?.Stop();
            router.RestoreAsync();
        }

        // ---------------------------------------------------------------- kurulum

        private IntPtr GameWindow()
        {
            if (gameWindow == IntPtr.Zero)
            {
                try { gameWindow = Process.GetCurrentProcess().MainWindowHandle; } catch { }
            }
            return gameWindow;
        }

        private Native.RECT PickMonitor(bool vr)
        {
            var monitors = Native.Monitors();
            for (var m = 0; m < monitors.Count; m++)
                Log.LogInfo($"Monitör {m}: {monitors[m].rect.Left},{monitors[m].rect.Top} {monitors[m].rect.Width}x{monitors[m].rect.Height}");

            if (monitorIndex.Value >= 0 && monitorIndex.Value < monitors.Count)
                return monitors[monitorIndex.Value].rect;

            // VR: oyun penceresine bakmıyoruz, ana monitörü (0,0'daki) göster
            if (vr)
                foreach (var m in monitors)
                    if (m.rect.Left == 0 && m.rect.Top == 0) return m.rect;

            // Masaüstü: oyunun açık olmadığı ilk monitör (sonsuz ayna olmasın)
            var gameMonitor = Native.MonitorFromWindow(GameWindow(), Native.MONITOR_DEFAULTTONEAREST);
            foreach (var m in monitors)
                if (m.handle != gameMonitor) return m.rect;
            return monitors.Count > 0 ? monitors[0].rect : new Native.RECT
            {
                Right = Native.GetSystemMetrics(Native.SM_CXSCREEN),
                Bottom = Native.GetSystemMetrics(Native.SM_CYSCREEN),
            };
        }

        private IScreenSource CreateSource(bool vr)
        {
            var monitor = PickMonitor(vr);
            IScreenSource s = forceGdi.Value ? null : GpuCapture.TryCreate(monitor, captureFps.Value);
            if (s == null) s = new GdiSource(monitor, 1280, Math.Min(30, captureFps.Value));
            Log.LogInfo("Ekran yakalama yöntemi: " + s.Name);
            return s;
        }

        // Monitör/FPS değişince yakalamayı yeniden kur
        private void RebuildSource()
        {
            source.Stop();
            source.Dispose();
            source = CreateSource(IsVR());
            source.Publisher = publisher;
            screenAspect = source.SourceHeight / (float)source.SourceWidth;
            SetScreenWidth(screenWidth);
            screenMat.mainTexture = source.Texture;

            if (cursor != null) Destroy(cursor.gameObject);
            cursor = null;
            if (!source.DrawsCursor) BuildCursor();
            RebuildModel();

            if (visible) source.Start();
        }

        private void RebuildModel()
        {
            model?.Destroy();
            model = new MonitorModel(screenRoot.transform, screenAspect, MakeMaterial, showLogo.Value);
            model.Follow(screen, screenWidth);
        }

        private void BuildScreen(bool vr)
        {
            font = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Arial" }, 64);
            source = CreateSource(vr);

            screenAspect = source.SourceHeight / (float)source.SourceWidth;
            screenWidth = screenWidthMeters.Value;

            screenRoot = new GameObject("GorillaDesktop");
            DontDestroyOnLoad(screenRoot);

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(quad.GetComponent<Collider>()); // oyuncu ekrana çarpmasın
            quad.name = "Screen";
            screen = quad.transform;
            screen.SetParent(screenRoot.transform, false);
            SetScreenWidth(screenWidth);

            // Görüntü üstten alta geliyor: UV'leri dikeyde çevir
            var mesh = quad.GetComponent<MeshFilter>().mesh;
            var uvs = mesh.uv;
            for (var i = 0; i < uvs.Length; i++) uvs[i].y = 1f - uvs[i].y;
            mesh.uv = uvs;

            screenMat = MakeMaterial(Color.white);
            screenMat.mainTexture = source.Texture;
            quad.GetComponent<Renderer>().material = screenMat;

            // GPU yakalama imleci içermez; oyun içinde ok şeklinde çiziyoruz
            if (!source.DrawsCursor) BuildCursor();
            RebuildModel();
            BuildSettings();

            // Her el için bir lazer ve uç noktası
            var laserMat = MakeMaterial(new Color(0.2f, 0.8f, 1f));
            var dotMat = MakeMaterial(Color.white);
            for (var i = 0; i < lasers.Length; i++)
            {
                var laserObj = new GameObject("Laser" + i);
                laserObj.transform.SetParent(screenRoot.transform, false);
                var laser = laserObj.AddComponent<LineRenderer>();
                laser.positionCount = 2;
                laser.startWidth = 0.004f;
                laser.endWidth = 0.002f;
                laser.useWorldSpace = true;
                laser.material = laserMat;
                laser.enabled = false;
                lasers[i] = laser;

                var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(dot.GetComponent<Collider>());
                dot.name = "LaserDot" + i;
                laserDots[i] = dot.transform;
                laserDots[i].SetParent(screenRoot.transform, false);
                laserDots[i].localScale = Vector3.one * 0.012f;
                dot.GetComponent<Renderer>().material = dotMat;
                dot.SetActive(false);
            }
        }

        private void BuildSettings()
        {
            settings = new SettingsUi(screenRoot.transform, MakeMaterial, font);
            settings.AddHeader("GTV  Ayarlar");
            settings.AddStepper("Monitör", MonitorLabel, () => CycleMonitor(-1), () => CycleMonitor(1));
            settings.AddStepper("FPS", () => captureFps.Value.ToString(),
                () => CycleFps(-1), () => CycleFps(1));
            settings.AddToggle("Ses", () => audioEnabled.Value, () =>
            {
                audioEnabled.Value = !audioEnabled.Value;
                if (audioEnabled.Value) StartAudio(); else StopAudio();
            });
            settings.AddStepper("Ses seviyesi", () => $"%{Mathf.RoundToInt(audioVolume.Value * 100)}",
                () => audioVolume.Value = Mathf.Clamp01(audioVolume.Value - 0.1f),
                () => audioVolume.Value = Mathf.Clamp01(audioVolume.Value + 0.1f));
            settings.AddToggle("Parmakla dokunma", () => touchEnabled.Value, () => touchEnabled.Value = !touchEnabled.Value);
            settings.AddToggle("Uzaktan lazer", () => laserEnabled.Value, () => laserEnabled.Value = !laserEnabled.Value);
            settings.AddToggle("Tık sesi", () => pressSound.Value, () => pressSound.Value = !pressSound.Value);
            settings.AddToggle("GTV logosu", () => showLogo.Value, () => { showLogo.Value = !showLogo.Value; RebuildModel(); });
            settings.AddToggle("Arkadaşlarla paylaş", () => publisher != null, ToggleShare);
            settings.AddInfo("Paylaşım", ShareStatus);
            settings.AddAction("Ortak ekranın yeri burası olsun", SaveSharePlace);
            settings.AddAction("Ekranı önüme getir", ResetScreenPose);
            settings.AddInfo("Yakalama", () => $"{(source is GpuCapture ? "GPU" : "Yedek")}  {source.Width}x{source.Height}");
            settings.Finish();
        }

        private static readonly int[] FpsSteps = { 30, 45, 60, 90, 120 };

        private void CycleFps(int dir)
        {
            var index = Array.IndexOf(FpsSteps, captureFps.Value);
            if (index < 0) index = Array.FindLastIndex(FpsSteps, s => s <= captureFps.Value);
            index = Mathf.Clamp(index + dir, 0, FpsSteps.Length - 1);
            if (FpsSteps[index] == captureFps.Value) return;
            captureFps.Value = FpsSteps[index];
            RebuildSource();
        }

        private string MonitorLabel()
        {
            var monitors = Native.Monitors();
            if (monitorIndex.Value < 0 || monitorIndex.Value >= monitors.Count) return "Otomatik";
            var r = monitors[monitorIndex.Value].rect;
            return $"{monitorIndex.Value + 1}/{monitors.Count}  {r.Width}x{r.Height}";
        }

        // Otomatik -> 1 -> 2 -> ... -> Otomatik
        private void CycleMonitor(int dir)
        {
            var count = Native.Monitors().Count;
            var next = monitorIndex.Value + dir;
            if (next < -1) next = count - 1;
            if (next >= count) next = -1;
            monitorIndex.Value = next;
            RebuildSource();
        }

        private void ResetScreenPose()
        {
            SetScreenWidth(screenWidthMeters.Value);
            var tagger = GorillaTagger.Instance;
            if (tagger != null) PlaceInFront(tagger);
        }

        private void BuildCursor()
        {
            cursor = new GameObject("Cursor").transform;
            cursor.SetParent(screen, false);
            // Piksel biriminde çizilen oku monitör pikseline ölçekle
            var k = Mathf.Max(1.5f, source.SourceWidth / 1000f) * cursorSize.Value;
            cursor.localScale = new Vector3(k / source.SourceWidth, k / source.SourceHeight, 1f);

            AddArrow("Outline", 1.35f, 0.0015f, Color.black);
            AddArrow("Fill", 1f, 0.0025f, Color.white);
        }

        // Klasik fare oku; (0,0) uç noktası, y aşağı doğru piksel
        private void AddArrow(string name, float scale, float zOffset, Color color)
        {
            Vector2[] pts = { new Vector2(0, 0), new Vector2(0, 17), new Vector2(4, 13), new Vector2(7, 20), new Vector2(9, 19), new Vector2(6, 12), new Vector2(12, 12) };
            var center = new Vector2(4f, 10f);
            var verts = new Vector3[pts.Length];
            for (var i = 0; i < pts.Length; i++)
            {
                var p = center + (pts[i] - center) * scale - new Vector2(0f, (scale - 1f) * 3f);
                verts[i] = new Vector3(p.x, -p.y, -zOffset); // ekranın önü -Z
            }
            int[] tris = { 0, 1, 2, 0, 2, 5, 0, 5, 6, 2, 3, 4, 2, 4, 5 };
            var both = new int[tris.Length * 2];
            for (var i = 0; i < tris.Length; i += 3)
            {
                both[i] = tris[i]; both[i + 1] = tris[i + 1]; both[i + 2] = tris[i + 2];
                both[tris.Length + i] = tris[i]; both[tris.Length + i + 1] = tris[i + 2]; both[tris.Length + i + 2] = tris[i + 1];
            }

            var go = new GameObject(name);
            go.transform.SetParent(cursor, false);
            go.AddComponent<MeshFilter>().mesh = new Mesh { vertices = verts, triangles = both };
            go.AddComponent<MeshRenderer>().material = MakeMaterial(color);
        }

        private void UpdateCursorOverlay()
        {
            if (cursor == null) return;
            if (!Native.GetCursorPos(out var p)) return;
            var x = (p.X - source.SourceX) / (float)source.SourceWidth;
            var y = (p.Y - source.SourceY) / (float)source.SourceHeight;
            var inside = x >= 0f && x <= 1f && y >= 0f && y <= 1f;
            if (cursor.gameObject.activeSelf != inside) cursor.gameObject.SetActive(inside);
            if (inside) cursor.localPosition = new Vector3(x - 0.5f, 0.5f - y, 0f);
        }

        private static Material MakeMaterial(Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Texture")
                         ?? Shader.Find("Sprites/Default");
            var mat = new Material(shader) { color = color };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            return mat;
        }

        // ---------------------------------------------------------------- yardımcılar

        // Ekran düzlemiyle ışın kesişimi; uv: (0,0) sol üst, (1,1) sağ alt
        private bool RaycastScreen(Vector3 origin, Vector3 dir, out Vector3 point, out Vector2 uv)
        {
            point = default;
            uv = default;
            // Ekranın arkasından tıklanmasın
            if (Vector3.Dot(origin - screen.position, -screen.forward) <= 0f) return false;
            var plane = new Plane(-screen.forward, screen.position);
            if (!plane.Raycast(new Ray(origin, dir), out var dist) || dist > 20f) return false;

            point = origin + dir * dist;
            var local = screen.InverseTransformPoint(point);
            if (Mathf.Abs(local.x) > 0.5f || Mathf.Abs(local.y) > 0.5f) return false;

            uv = new Vector2(local.x + 0.5f, 0.5f - local.y);
            return true;
        }

        // Lazer sadece ucu ekrana/ayar tuşuna değerken (ya da ekranı tutarken) görünür
        private void DrawLasers(bool vr, bool lasersOn)
        {
            for (var i = 0; i < lasers.Length; i++)
            {
                var p = pointers[i];
                var active = lasersOn && i < pointerCount;
                var held = active && grabbing.Contains(i) && p.Hit;
                var hit = active && (p.Hit || p.UiButton != null);
                var end = p.Hit ? p.HitPoint : p.UiPoint;

                // Masaüstünde lazer kameranın içinden çıkar; sadece nokta göster
                var showLine = vr && (hit || held);
                if (lasers[i].enabled != showLine) lasers[i].enabled = showLine;
                if (showLine)
                {
                    lasers[i].SetPosition(0, p.Origin);
                    lasers[i].SetPosition(1, end);
                }

                var dot = laserDots[i].gameObject;
                if (dot.activeSelf != hit) dot.SetActive(hit);
                if (hit) laserDots[i].position = end - screen.forward * 0.006f;
            }
        }

        private Vector2Int ToDesktop(Vector2 uv)
        {
            var x = Mathf.Clamp((int)(uv.x * source.SourceWidth), 0, source.SourceWidth - 1);
            var y = Mathf.Clamp((int)(uv.y * source.SourceHeight), 0, source.SourceHeight - 1);
            return new Vector2Int(source.SourceX + x, source.SourceY + y);
        }

        private void MoveCursor(Vector2Int target)
        {
            if (target == lastCursor) return;
            lastCursor = target;
            Native.SetCursorPos(target.x, target.y);
        }

        private void ReleaseLeft(bool vr)
        {
            if (!leftDown) return;
            Native.Mouse(Native.MOUSEEVENTF_LEFTUP);
            leftDown = false;
            clickOwner = -1;
            touchHand = -1;

            if (!vr && refocusGame.Value && GameWindow() != IntPtr.Zero)
                Native.SetForegroundWindow(GameWindow());
        }

        private static void OpenOnScreenKeyboard()
        {
            try { Process.Start(new ProcessStartInfo("osk.exe") { UseShellExecute = true }); }
            catch (Exception e) { Log.LogWarning("Ekran klavyesi açılamadı: " + e.Message); }
        }

        // ---------------------------------------------------------------- arkadaşlarla paylaşım

        private bool ShareConfigured => !string.IsNullOrWhiteSpace(shareServer.Value);

        private void ToggleShare()
        {
            shareNotice = "";
            if (publisher != null || share.Owner == share.MyActor && share.Owner != 0)
            {
                share.Release();
                StopPublisher();
                return;
            }
            if (!ShareConfigured) { shareNotice = "Sunucu ayarlanmamış"; return; }
            if (!share.InAllowedRoom) { shareNotice = "Modlu lobide değilsin"; return; }
            if (share.Owner != 0) { shareNotice = share.OwnerName + " zaten yayında"; return; }
            MoveToSharePlace(); // sabit yer kaydedildiyse ekranım oraya geçer
            if (!share.TryClaim(screen, screenWidth)) shareNotice = "Alınamadı";
            else shareNotice = "Alınıyor...";
        }

        private string ShareStatus()
        {
            if (!ShareConfigured) return "Sunucu ayarlanmamış";
            if (!share.InRoom) return "Lobide değilsin";
            if (!share.InAllowedRoom) return "Modlu lobide değilsin";
            if (publisher != null) return publisher.Status == "Yayında" ? $"Yayındasın · {publisher.Viewers} izleyici" : publisher.Status;
            if (shareNotice.Length > 0) return shareNotice;
            var owner = share.Owner;
            return owner == 0 ? "Ekran boş" : share.OwnerName + " yayında";
        }

        // Her kare: lobideki ortak ekran durumuna göre yayıncı/izleyici bağlantılarını aç/kapat
        private void UpdateSharing(bool vr)
        {
            var allowed = ShareConfigured && share.InAllowedRoom;
            var owner = allowed ? share.Owner : 0;
            var me = share.MyActor;

            // Yayıncı: ekran bende ve kendi ekranım açık
            if (owner != 0 && owner == me && visible)
            {
                if (publisher == null) StartPublisher();
                shareNotice = "";
                if (Time.time >= nextPoseSync)
                {
                    nextPoseSync = Time.time + 0.3f;
                    share.SyncPose(screen, screenWidth);
                }
            }
            else if (publisher != null)
            {
                StopPublisher();
                if (owner == me) share.Release(); // ekranımı kapattım
            }

            // İzleyici: ekran başkasında
            Vector3 pos = default;
            Quaternion rot = Quaternion.identity;
            float width = 0f;
            var watching = owner != 0 && owner != me && share.TryGetPose(out pos, out rot, out width);
            if (watching)
            {
                if (viewer == null || viewerOwner != owner)
                {
                    viewer?.Dispose();
                    viewerOwner = owner;
                    viewer = new StreamViewer(shareServer.Value, shareKey.Value, StreamConnection.ChannelId(share.RoomName, owner, shareKey.Value));
                    Log.LogInfo($"Ortak ekran izleniyor: {share.OwnerName}");
                }
                EnsureSharedView().SetVolume(audioVolume.Value);
                sharedView.Show(pos, rot, width, share.OwnerName, viewer);
            }
            else if (viewer != null)
            {
                viewer.Dispose();
                viewer = null;
                viewerOwner = 0;
            }

            // Kimse yayında değil ve sabit bir yer kaydedilmiş: orada boş ekran, dokunan alır
            var idle = allowed && owner == 0 && publisher == null && TryGetSharePlace(out pos, out rot, out width);
            if (idle)
            {
                EnsureSharedView().ShowIdle(pos, rot, width);
                if (vr && fingertips != null && sharedView.Touched(fingertips))
                {
                    PressFeedback(false);
                    ClaimAtPlace();
                }
            }

            if (!watching && !idle) sharedView?.Hide();

            // Panel açıksa durum yazısını güncel tut
            if (settings != null && settings.Open && Time.time >= nextStatusRefresh)
            {
                nextStatusRefresh = Time.time + 1f;
                settings.Refresh();
            }
        }

        private float nextStatusRefresh;

        private SharedScreen EnsureSharedView()
        {
            if (sharedView != null) return sharedView;
            if (font == null) font = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Arial" }, 64);
            sharedView = new SharedScreen(MakeMaterial, font, null, audioVolume.Value, audioMaxDistance.Value, showLogo.Value);
            return sharedView;
        }

        // Kendi ekranımın şu anki yerini ortak ekranın sabit yeri olarak kaydet
        private void SaveSharePlace()
        {
            var p = screen.position;
            var r = screen.rotation;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            sharePlace.Value = string.Join(";", new[] { p.x, p.y, p.z, r.x, r.y, r.z, r.w, screenWidth }
                .Select(v => v.ToString("0.###", ci)));
            shareNotice = "Yer kaydedildi";
            Log.LogInfo("Ortak ekranın yeri kaydedildi: " + sharePlace.Value);
        }

        private bool TryGetSharePlace(out Vector3 position, out Quaternion rotation, out float width)
        {
            position = default;
            rotation = Quaternion.identity;
            width = 1.6f;
            var parts = (sharePlace.Value ?? "").Split(';');
            if (parts.Length < 8) return false;
            var v = new float[8];
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            for (var i = 0; i < 8; i++)
                if (!float.TryParse(parts[i], System.Globalization.NumberStyles.Float, ci, out v[i])) return false;
            position = new Vector3(v[0], v[1], v[2]);
            rotation = new Quaternion(v[3], v[4], v[5], v[6]);
            width = Mathf.Clamp(v[7], 0.3f, 8f);
            return true;
        }

        // Sabit yerdeki boş ekrana dokunuldu: kendi ekranımı orada aç ve yayını al
        private void ClaimAtPlace()
        {
            var tagger = GorillaTagger.Instance;
            if (tagger == null) return;
            if (!visible) SetVisible(true, tagger);
            MoveToSharePlace();
            shareNotice = share.TryClaim(screen, screenWidth) ? "Alınıyor..." : "Alınamadı";
        }

        private void MoveToSharePlace()
        {
            if (!TryGetSharePlace(out var pos, out var rot, out var width)) return;
            screen.SetPositionAndRotation(pos, rot);
            SetScreenWidth(width);
        }

        private void StartPublisher()
        {
            var channel = StreamConnection.ChannelId(share.RoomName, share.MyActor, shareKey.Value);
            publisher = new StreamPublisher(shareServer.Value, shareKey.Value, channel,
                Mathf.Clamp(shareQuality.Value, 0.2f, 0.95f), Mathf.Clamp(shareFps.Value, 5, 30));
            source.Publisher = publisher;
            if (audio != null) audio.Publisher = publisher;
            Log.LogInfo("Arkadaşlarla paylaşım başladı");
            settings?.Refresh();
        }

        private void StopPublisher()
        {
            if (publisher == null) return;
            if (source != null) source.Publisher = null;
            if (audio != null) audio.Publisher = null;
            publisher.Dispose();
            publisher = null;
            Log.LogInfo("Arkadaşlarla paylaşım bitti");
            settings?.Refresh();
        }

        private void Cleanup()
        {
            try { share?.Release(); } catch { }
            StopPublisher();
            viewer?.Dispose();
            viewer = null;
            ReleaseLeft(true);
            source?.Dispose();
            source = null;
            audio?.Dispose();
            router?.Restore();
        }

        private void OnDestroy() => Cleanup();
    }
}
