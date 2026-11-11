using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        public const string Version = "6.1.0";

        internal static ManualLogSource Log;

        public enum ControlMode { Otomatik, VR, Masaustu }

        // Windows sanal tuş kodları
        private const int VK_F6 = 0x75, VK_F8 = 0x77;

        // Parmak ucu: bu kadar yakına gelince imleç parmağı takip eder; yüzeye önden değince tıklar
        private const float HoverDistance = 0.08f, PressDepth = 0.004f, ReleaseDistance = 0.02f;

        private ConfigEntry<ControlMode> mode;
        private ConfigEntry<int> monitorIndex;
        private ConfigEntry<float> screenWidthMeters;
        private ConfigEntry<int> captureFps;
        private ConfigEntry<bool> forceGdi;
        private ConfigEntry<int> keyToggle, keyMove;
        private ConfigEntry<bool> touchEnabled, showLogo, pressSound, locked;
        private ConfigEntry<string> audioSavedDefault, lockedPose;
        private ConfigEntry<string> addedApps;

        private IScreenSource source;
        private Material screenMat;
        private AudioRouter router;
        private SettingsUi settings;
        private MonitorModel model;
        private HomeMenu menu;
        private VrKeyboard keyboard;
        private Font font;

        private GameObject screenRoot;
        private Transform screen;
        private bool visible;
        private float screenWidth, screenAspect;

        // El: 0 = sağ, 1 = sol. Avuç (grip ile tutma) ve işaret parmağı ucu (basma)
        private Transform[] palms;
        private Transform[] fingertips;
        private float nextFingertipSearch;
        private int fingertipSearches;
        private readonly Transform[] fingerBuffer = new Transform[2]; // her karede dizi oluşturmamak için

        // Grip ile tutma: VR'da iki el, masaüstünde (WalkSim) kamera + F6
        private readonly Hand[] hands = { new Hand(), new Hand() };
        private int handCount;
        private bool prevToggle;

        // Parmakla basma
        private int touchHand = -1;
        private readonly float[] prevFingerFront = { 1f, 1f };
        private Vector2Int lastCursor = new Vector2Int(-1, -1);

        // Taşıma / iki elle büyütme
        private readonly List<int> grabbing = new List<int>(2);
        private int grabMode; // 0 yok, 1 tek el, 2 iki el
        private Vector3 grabLocalPos;
        private Quaternion grabLocalRot, lastHandsFrame = Quaternion.identity;
        private float twoHandStartDist, twoHandStartWidth;

        private sealed class Hand
        {
            public Transform T;
            public bool Grip, PrevGrip, Trigger, PrevTrigger;
            public float Stick;
        }

        private void Awake()
        {
            Log = Logger;
            mode = Config.Bind("Genel", "KontrolModu", ControlMode.Otomatik,
                "Otomatik: VR headset on = controllers, otherwise (WalkSim) keyboard");
            monitorIndex = Config.Bind("Genel", "Monitor", -1,
                "Monitor to show (0, 1, ...). -1 = auto (VR: primary monitor, desktop: the monitor the game is NOT on)");
            screenWidthMeters = Config.Bind("Ekran", "GenislikMetre", 1.6f, "Default screen width (meters)");
            captureFps = Config.Bind("Ekran", "FPS", 60, "Max capture frames per second");
            forceGdi = Config.Bind("Ekran", "EskiYontemiZorla", false, "true = use the old (slow) GDI capture instead of GPU");
            showLogo = Config.Bind("Ekran", "GTVLogosu", true, "Show the white GTV logo on the bottom of the frame");
            locked = Config.Bind("Ekran", "Kilitli", false,
                "Lock: Y does not hide/summon the screen and grip does not move it. The screen opens at its locked place");
            lockedPose = Config.Bind("Ekran", "KilitliKonum", "", "Used by the mod: where the screen was locked");

            const string keyHelp = "Windows virtual key code (decimal): learn.microsoft.com/windows/win32/inputdev/virtual-key-codes";
            keyToggle = Config.Bind("Klavye", "EkranAcKapat", VK_F8, "F8: show/hide screen. " + keyHelp);
            keyMove = Config.Bind("Klavye", "EkraniTasi", VK_F6, "F6 (hold): move the screen with the camera");

            // Eski sürümler Windows ses çıkışını değiştiriyordu; geri alınmamış kaldıysa düzeltmek için okunur
            audioSavedDefault = Config.Bind("Ses", "OncekiVarsayilanCihaz", "",
                "Used by the mod, do not edit. Old versions changed the Windows sound output; if set, it is restored on start");
            touchEnabled = Config.Bind("Dokunma", "Acik", true, "VR: click by touching the screen with your index fingertip");
            pressSound = Config.Bind("Dokunma", "TikSesi", true, "GT button click sound and vibration when pressing");
            addedApps = Config.Bind("Menu", "EklenenUygulamalar", "", "Used by the mod: apps added from the + page (comma separated)");

            Native.TrySetDpiAware();

            // Eski sürümden kalan ses yönlendirmesi varsa geri al (mod artık sese dokunmuyor)
            router = new AudioRouter(audioSavedDefault);
            if (router.IsRouted)
            {
                Log.LogWarning("Eski sürümden kalan ses yönlendirmesi geri alınıyor.");
                router.Restore();
            }
            Application.quitting += Cleanup;
        }

        private bool IsVR()
        {
            if (mode.Value == ControlMode.VR) return true;
            if (mode.Value == ControlMode.Masaustu) return false;
            try { return XRSettings.isDeviceActive; } catch { return false; }
        }

        // ---------------------------------------------------------------- giriş

        private bool ReadInputs(GorillaTagger tagger, bool vr)
        {
            var poller = ControllerInputPoller.instance;
            var toggle = Native.KeyDown(keyToggle.Value);

            if (vr && poller != null)
            {
                toggle |= poller.leftControllerSecondaryButton;
                FillHand(hands[0], tagger.rightHandTransform, poller.rightControllerGripFloat, poller.rightControllerIndexFloat, poller.rightControllerPrimary2DAxis.y);
                FillHand(hands[1], tagger.leftHandTransform, poller.leftControllerGripFloat, poller.leftControllerIndexFloat, poller.leftControllerPrimary2DAxis.y);
                handCount = 2;
            }
            else
            {
                // WalkSim: F6 basılıyken ekran kamerayla birlikte taşınır
                var cam = Camera.main;
                hands[0].T = cam != null ? cam.transform : tagger.headCollider.transform;
                hands[0].Grip = Native.KeyDown(keyMove.Value);
                hands[0].Stick = 0f;
                handCount = 1;
            }
            return toggle;
        }

        private static void FillHand(Hand h, Transform t, float grip, float trigger, float stick)
        {
            h.T = t;
            h.Grip = grip > 0.7f;
            h.Trigger = h.Trigger ? trigger > 0.25f : trigger > 0.5f;
            h.Stick = Mathf.Abs(stick) > 0.2f ? stick : 0f;
        }

        // GT'nin düğmelere basmak için kullandığı işaret parmağı ucu collider'ları
        private void FindFingertips(GorillaTagger tagger)
        {
            // FindObjectsOfType pahalı: bulunamazsa birkaç denemeden sonra vazgeç (kare takılmasın)
            if (fingertips != null || fingertipSearches >= 15 || Time.time < nextFingertipSearch) return;
            nextFingertipSearch = Time.time + 2f;
            fingertipSearches++;

            Transform right = null, left = null;
            foreach (var indicator in FindObjectsByType<GorillaTriggerColliderHandIndicator>(FindObjectsSortMode.None))
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

            var vr = IsVR();
            Native.TrySetDpiAware(); // ekran koordinatları ölçeklenmesin (Windows %125 vb.)
            var toggle = ReadInputs(tagger, vr);

            // Kilitliyken Y ekranı kapatmaz; kapalıysa kilitli yerinde açar
            if (toggle && !prevToggle && !(visible && locked.Value)) SetVisible(!visible, tagger);
            prevToggle = toggle;

            if (!visible)
            {
                EndFrame();
                return;
            }

            source.Update();
            if (screenMat.mainTexture != source.Texture) screenMat.mainTexture = source.Texture;

            UpdateGrab(vr);
            settings.FollowScreen(screen);
            model?.Follow(screen, screenWidth);
            menu.Follow(screen, screenWidth);
            menu.Tick();
            UpdateLaunch();

            fingerBuffer[0] = Fingertip(0);
            fingerBuffer[1] = Fingertip(1);

            // Klavye: parmakla ya da klavyeye doğrultulan ışın + tetik ile
            var onKeyboard = false;
            if (vr && keyboard.Visible && grabMode == 0)
            {
                keyboard.ClearHover();
                var pressed = keyboard.Touch(fingerBuffer, out var near);
                if (pressed >= 0) PressFeedback(pressed == 1);
                onKeyboard = near;
                for (var i = 0; i < handCount; i++)
                {
                    var h = hands[i];
                    if (!TryControllerAim(i == 0 ? XRNode.RightHand : XRNode.LeftHand, out var aim)) { keyboard.HideBeam(i); continue; }
                    var press = h.Trigger && !h.PrevTrigger;
                    if (keyboard.Beam(i, h.T.position, aim * Vector3.forward, press))
                    {
                        onKeyboard = true;
                        if (press) PressFeedback(i == 1);
                    }
                }
            }

            // Ayar tuşları: parmakla
            SettingsUi.Button hoverA = null, hoverB = null;
            if (vr && grabMode == 0)
            {
                var clicked = settings.Touch(fingerBuffer, out hoverA, out hoverB);
                if (clicked >= 0) PressFeedback(clicked == 1);
            }
            settings.SetHover(hoverA, hoverB);

            // Ekrana dokunma: menü açıkken kartlara, değilse bilgisayar ekranına
            if (vr && grabMode == 0 && !onKeyboard)
            {
                if (menu.Visible) UpdateMenuTouch(); else UpdateFingerTouch();
            }
            EndFrame();
        }

        // ---------------------------------------------------------------- ana menü

        private void UpdateMenuTouch()
        {
            if (!touchEnabled.Value) return;
            if (touchHand >= 0)
            {
                // Bastıktan sonra parmak çekilene kadar tekrar basmaz
                var on = FingerOnScreen(touchHand, out _, out var front);
                if (on && front < ReleaseDistance && front > -0.12f) return;
                touchHand = -1;
            }

            Vector2? hover = null;
            for (var i = 0; i < 2; i++)
            {
                var on = FingerOnScreen(i, out var uv, out var front);
                var cameFromFront = prevFingerFront[i] > PressDepth;
                prevFingerFront[i] = on ? front : 1f;
                if (!on || front > HoverDistance || front < -0.12f) continue;

                if (front <= PressDepth && cameFromFront)
                {
                    touchHand = i;
                    PressFeedback(i == 1);
                    Activate(menu.HitTest(uv));
                    menu.Hover(null);
                    return;
                }
                if (!hover.HasValue) hover = uv;
            }
            menu.Hover(hover);
        }

        // Menüde bir karta basıldı
        private void Activate(string id)
        {
            if (id == null) return;
            switch (id)
            {
                case HomeMenu.Settings: settings.SetOpen(!settings.Open); return;
                case HomeMenu.MyScreen: ShowDesktop(); return;
                case HomeMenu.Plus: menu.Show(HomeMenu.Page.AddApps); return;
                case HomeMenu.Back: menu.Show(HomeMenu.Page.Home); return;
            }

            if (menu.Current == HomeMenu.Page.AddApps)
            {
                menu.ToggleAdded(id);
                return;
            }

            var app = HomeMenu.HomeApps.Concat(HomeMenu.ExtraApps).FirstOrDefault(a => a.Id == id);
            if (app == null) return;
            var monitor = new Native.RECT
            {
                Left = source.SourceX, Top = source.SourceY,
                Right = source.SourceX + source.SourceWidth, Bottom = source.SourceY + source.SourceHeight,
            };
            // Pencere hazÄ±r olup sayfa yÃ¼klenene kadar yÃ¼kleme ekranÄ±; sonra uygulamaya geÃ§
            menu.ShowLoading(app.Id);
            launchId = app.Id;
            launchResult = 0;
            launchReadyAt = 0f;
            AppLauncher.Open(app.Id, app.Url, monitor, ok => launchResult = ok ? 1 : 2);
        }

        private string launchId;
        private volatile int launchResult; // 0 bekliyor, 1 aÃ§Ä±ldÄ±, 2 aÃ§Ä±lamadÄ±
        private float launchReadyAt;

        private void UpdateLaunch()
        {
            if (!menu.Visible || menu.Current != HomeMenu.Page.Loading || launchId == null) return;
            if (launchResult == 1)
            {
                // Pencere tam ekran; sayfanÄ±n Ã§izilmesi iÃ§in kÄ±sa bir sÃ¼re daha yÃ¼kleme ekranÄ±nda kal
                if (launchReadyAt == 0f) launchReadyAt = Time.time + 1.5f;
                else if (Time.time >= launchReadyAt) { launchId = null; ShowDesktop(); }
            }
            else if (launchResult == 2)
            {
                if (launchReadyAt == 0f) { menu.ShowError(launchId); launchReadyAt = Time.time + 2.5f; }
                else if (Time.time >= launchReadyAt) { launchId = null; GoHome(); }
            }
        }

        // Klavyeden yazmadan Ã¶nce: odak oyundaysa yazÄ± oyuna gitmesin, son aÃ§Ä±lan uygulamaya geÃ§ir
        private void EnsureAppFocus()
        {
            var app = AppLauncher.LastWindow;
            if (app == IntPtr.Zero || !Native.IsWindow(app)) return;
            var fg = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(fg, out var pid);
            if (pid == (uint)Process.GetCurrentProcess().Id) Native.ForceForeground(app);
        }

        private void ShowDesktop()
        {
            menu.SetVisible(false);
            touchHand = -1;
        }

        private void GoHome()
        {
            menu.SetVisible(true);
            menu.Show(HomeMenu.Page.Home);
            touchHand = -1;
        }

        private static readonly List<InputFeatureUsage> usageBuffer = new List<InputFeatureUsage>();
        private readonly InputFeatureUsage<Quaternion>?[] pointerUsage = new InputFeatureUsage<Quaternion>?[2];
        private readonly bool[] pointerUsageSearched = new bool[2];

        // Kumandanın dünya uzayındaki nişan dönüşü (parmak animasyonundan etkilenmez).
        // Takip uzayından dünyaya: kafanın takip dönüşü ile kameranın dünya dönüşü arasındaki fark
        private bool TryControllerAim(XRNode node, out Quaternion world)
        {
            world = Quaternion.identity;
            var cam = Camera.main;
            if (cam == null) return false;
            var device = InputDevices.GetDeviceAtXRNode(node);
            var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (!device.isValid || !head.isValid) return false;

            var i = node == XRNode.RightHand ? 0 : 1;
            if (!pointerUsageSearched[i] && device.TryGetFeatureUsages(usageBuffer))
            {
                pointerUsageSearched[i] = true;
                foreach (var u in usageBuffer)
                {
                    var name = u.name.ToLowerInvariant();
                    if (u.type == typeof(Quaternion) && name.Contains("pointer") && name.Contains("rot")) { pointerUsage[i] = u.As<Quaternion>(); break; }
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

        private void EndFrame()
        {
            foreach (var h in hands) { h.PrevGrip = h.Grip; h.PrevTrigger = h.Trigger; }
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

        // Parmak yaklaşınca imleç parmağı takip eder; yüzeye önden değince bir kez tıklar.
        // Tekrar tıklamak için parmak ekrandan çekilmeli
        private void UpdateFingerTouch()
        {
            if (!touchEnabled.Value) return;

            if (touchHand >= 0)
            {
                var on = FingerOnScreen(touchHand, out _, out var front);
                if (on && front < ReleaseDistance && front > -0.12f) return;
                touchHand = -1;
            }

            var hovering = false;
            for (var i = 0; i < 2; i++)
            {
                var on = FingerOnScreen(i, out var uv, out var front);
                var cameFromFront = prevFingerFront[i] > PressDepth;
                prevFingerFront[i] = on ? front : 1f;
                if (!on || front > HoverDistance || front < -0.12f) continue;

                var target = ToDesktop(uv);
                if (front <= PressDepth && cameFromFront)
                {
                    touchHand = i;
                    ClickAt(target);
                    PressFeedback(i == 1);
                    return;
                }

                // Basmadan önce imleç parmağın olduğu yeri göstersin
                if (!hovering) MoveCursor(target);
                hovering = true;
            }
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

        // Tek tık: basılı kalmaz, sürüklenmez
        private void ClickAt(Vector2Int target)
        {
            lastCursor = target;
            Native.MouseAt(target.x, target.y, Native.MOUSEEVENTF_LEFTDOWN);
            Native.MouseAt(target.x, target.y, Native.MOUSEEVENTF_LEFTUP);
        }

        // ---------------------------------------------------------------- taşıma / büyütme

        // Grip: ekranı tut (el ekranın yanındayken). Tek elle taşı, iki elle tutup büyüt/küçült.
        // Kilitliyken ekran yerinden oynamaz
        private void UpdateGrab(bool vr)
        {
            var changed = false;
            for (var i = 0; i < handCount; i++)
            {
                var h = hands[i];
                var held = grabbing.Contains(i);
                if (h.Grip && !h.PrevGrip && !held && !locked.Value && (!vr || PalmNearScreen(i))) { grabbing.Add(i); changed = true; }
                else if ((!h.Grip || locked.Value) && held) { grabbing.Remove(i); changed = true; }
            }
            for (var i = grabbing.Count - 1; i >= 0; i--)
                if (grabbing[i] >= handCount) { grabbing.RemoveAt(i); changed = true; }

            var mode = Math.Min(grabbing.Count, 2);
            if (changed || mode != grabMode) BeginGrab(mode);
            grabMode = mode;

            if (mode == 1)
            {
                var h = hands[grabbing[0]];
                // Tutarken joystick ileri/geri = ekranı uzaklaştır/yaklaştır
                var away = (screen.position - h.T.position).normalized;
                screen.position = h.T.TransformPoint(grabLocalPos) + away * (h.Stick * 1.5f * Time.deltaTime);
                screen.rotation = h.T.rotation * grabLocalRot;
                grabLocalPos = h.T.InverseTransformPoint(screen.position);
            }
            else if (mode == 2)
            {
                var a = hands[grabbing[0]].T.position;
                var b = hands[grabbing[1]].T.position;
                var frame = HandsFrame(a, b);
                screen.position = (a + b) * 0.5f + frame * grabLocalPos;
                screen.rotation = frame * grabLocalRot;
                var scale = Vector3.Distance(a, b) / Mathf.Max(0.01f, twoHandStartDist);
                SetScreenWidth(Mathf.Clamp(twoHandStartWidth * scale, 0.3f, 8f));
            }
        }

        // Avuç ekranın (ya da çerçevesinin) yakınında mı
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
                var t = hands[grabbing[0]].T;
                grabLocalPos = t.InverseTransformPoint(screen.position);
                grabLocalRot = Quaternion.Inverse(t.rotation) * screen.rotation;
            }
            else if (mode == 2)
            {
                var a = hands[grabbing[0]].T.position;
                var b = hands[grabbing[1]].T.position;
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

        // ---------------------------------------------------------------- kilit

        private void ToggleLock()
        {
            locked.Value = !locked.Value;
            if (locked.Value)
            {
                // Kilitlendiği yeri kaydet: sonraki açılışta ekran burada çıkar
                var p = screen.position;
                var r = screen.rotation;
                lockedPose.Value = string.Join(";", new[] { p.x, p.y, p.z, r.x, r.y, r.z, r.w, screenWidth }
                    .Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));
                grabbing.Clear();
                grabMode = 0;
            }
        }

        private bool TryGetLockedPose(out Vector3 position, out Quaternion rotation, out float width)
        {
            position = default;
            rotation = Quaternion.identity;
            width = screenWidthMeters.Value;
            var parts = (lockedPose.Value ?? "").Split(';');
            if (parts.Length < 8) return false;
            var v = new float[8];
            for (var i = 0; i < 8; i++)
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return false;
            position = new Vector3(v[0], v[1], v[2]);
            rotation = new Quaternion(v[3], v[4], v[5], v[6]);
            width = Mathf.Clamp(v[7], 0.3f, 8f);
            return true;
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
                if (locked.Value && TryGetLockedPose(out var pos, out var rot, out var width))
                {
                    screen.SetPositionAndRotation(pos, rot);
                    SetScreenWidth(width);
                }
                else PlaceInFront(tagger);
                // VR'da açılışta ana menü; WalkSim'de menüye dokunulamadığı için doğrudan bilgisayar ekranı
                if (vr) GoHome(); else ShowDesktop();
            }
            else
            {
                source.Stop();
                keyboard.SetVisible(false);
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

        // ---------------------------------------------------------------- kurulum

        private IntPtr GameWindow()
        {
            try { return Process.GetCurrentProcess().MainWindowHandle; } catch { return IntPtr.Zero; }
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
            screenAspect = source.SourceHeight / (float)source.SourceWidth;
            SetScreenWidth(screenWidth);
            screenMat.mainTexture = source.Texture;
            RebuildModel();
            menu.SetAspect(screenAspect);
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
            font = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Segoe UI Symbol", "Arial" }, 64); // sembol yazı tipi: ⌫ ⇧ ▼ için
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

            RebuildModel();
            BuildSettings();

            menu = new HomeMenu(screenRoot.transform, MakeMaterial, font, addedApps);
            menu.SetAspect(screenAspect);
            keyboard = new VrKeyboard(screenRoot.transform, MakeMaterial, font) { BeforeType = EnsureAppFocus };
            settings.AddSideButton("⌂", 1, GoHome);
            settings.AddSideButton("⌨", 2, () => keyboard.Toggle(Camera.main != null ? Camera.main.transform : GorillaTagger.Instance.headCollider.transform));
        }

        private void BuildSettings()
        {
            settings = new SettingsUi(screenRoot.transform, MakeMaterial, font);
            settings.AddHeader("GTV  Settings");
            settings.AddToggle("Lock screen", () => locked.Value, ToggleLock);
            settings.AddStepper("Monitor", MonitorLabel, () => CycleMonitor(-1), () => CycleMonitor(1));
            settings.AddStepper("FPS", () => captureFps.Value.ToString(), () => CycleFps(-1), () => CycleFps(1));
            settings.AddToggle("Touch", () => touchEnabled.Value, () => touchEnabled.Value = !touchEnabled.Value);
            settings.AddToggle("Click sound", () => pressSound.Value, () => pressSound.Value = !pressSound.Value);
            settings.AddToggle("GTV logo", () => showLogo.Value, () => { showLogo.Value = !showLogo.Value; RebuildModel(); });
            settings.AddAction("Bring screen to me", ResetScreenPose);
            settings.AddInfo("Capture", () => $"{(source is GpuCapture ? "GPU" : "Fallback")}  {source.Width}x{source.Height}");
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
            if (monitorIndex.Value < 0 || monitorIndex.Value >= monitors.Count) return "Auto";
            var r = monitors[monitorIndex.Value].rect;
            return $"{monitorIndex.Value + 1}/{monitors.Count}  {r.Width}x{r.Height}";
        }

        // Auto -> 1 -> 2 -> ... -> Auto
        private void CycleMonitor(int dir)
        {
            var count = Native.Monitors().Count;
            var next = monitorIndex.Value + dir;
            if (next < -1) next = count - 1;
            if (next >= count) next = -1;
            monitorIndex.Value = next;
            RebuildSource();
        }

        // Kilitliyken ekran yerinde kalır
        private void ResetScreenPose()
        {
            if (locked.Value) return;
            SetScreenWidth(screenWidthMeters.Value);
            var tagger = GorillaTagger.Instance;
            if (tagger != null) PlaceInFront(tagger);
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

        private void Cleanup()
        {
            source?.Dispose();
            source = null;
        }

        private void OnDestroy() => Cleanup();
    }
}
