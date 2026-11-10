using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Configuration;

namespace GorillaDesktop
{
    // Ekran açıkken PC sesini otomatik yönlendirir:
    //  1) Gorilla Tag'i o anki varsayılan cihaza (kulaklık) sabitler,
    //  2) Windows varsayılan çıkışını sanal cihaza (Steam Streaming Speakers) çevirir.
    // Kapatınca eski hale döner. Önceki cihaz config'e yazılır; oyun çökerse bir sonraki açılışta geri yüklenir.
    // Not: Windows'un belgelenmemiş (EarTrumpet'in de kullandığı) ses ilkesi arayüzlerini kullanır.
    internal sealed class AudioRouter
    {
        private readonly ConfigEntry<string> savedDefault;

        public AudioRouter(ConfigEntry<string> savedDefault)
        {
            this.savedDefault = savedDefault;
        }

        public bool IsRouted => !string.IsNullOrEmpty(savedDefault.Value);

        // Arka planda yapar; oyun ana döngüsünü bekletmez
        public void Route(string targetFilter) => RunAsync(() => RouteNow(targetFilter));

        public void RestoreAsync() => RunAsync(() => Restore());

        private Thread worker;

        // İşlemleri sırayla (bir öncekinin bitmesini bekleyerek) arka planda çalıştır
        private void RunAsync(Action action)
        {
            var previous = worker;
            worker = new Thread(() =>
            {
                previous?.Join(5000);
                action();
            }) { IsBackground = true, Name = "GorillaDesktop.AudioRouterQueue" };
            worker.Start();
        }

        private void RouteNow(string targetFilter)
        {
            RunOnComThread(() =>
            {
                var enumerator = AudioDevices.CreateEnumerator();
                IntPtr target = IntPtr.Zero, current = IntPtr.Zero;
                try
                {
                    target = AudioDevices.FindByName(enumerator, targetFilter);
                    if (target == IntPtr.Zero)
                    {
                        Plugin.Log.LogWarning($"Otomatik ses yönlendirme yapılamadı: \"{targetFilter}\" cihazı yok.");
                        return;
                    }
                    var targetId = AudioDevices.Id(target);

                    current = AudioDevices.GetDefault(enumerator, AudioDevices.eConsole);
                    var currentId = AudioDevices.Id(current);

                    var original = currentId;
                    if (currentId == targetId)
                    {
                        // Zaten sanal cihazdayız: ya önceki oturum yarıda kaldı, ya da kullanıcı elle ayarladı.
                        // Elle ayarladıysa oyunun sabitlendiği cihaz (kulaklık) asıl cihazdır.
                        original = IsRouted ? savedDefault.Value : GetGamePinnedDevice();
                        if (string.IsNullOrEmpty(original) || original == targetId)
                        {
                            Plugin.Log.LogWarning("Varsayılan ses cihazı zaten sanal cihaz ve asıl cihaz bilinmiyor; yönlendirme atlandı. " +
                                                  "Windows ses çıkışını kulaklığına alırsan mod otomatik yönetir.");
                            return;
                        }
                    }

                    // Önce kaydet: çökme olursa geri dönebilelim
                    savedDefault.Value = original;
                    PinGame(original);
                    SetDefault(targetId);
                    Plugin.Log.LogInfo($"Ses yönlendirildi: PC sesi -> {AudioDevices.FriendlyName(target)}, oyun -> {AudioDevices.FriendlyName(current)}");
                }
                finally
                {
                    Com.Release(ref current);
                    Com.Release(ref target);
                    Com.Release(ref enumerator);
                }
            });
        }

        public void Restore()
        {
            if (!IsRouted) return;
            var original = savedDefault.Value;
            var ok = RunOnComThread(() =>
            {
                SetDefault(original);
                try { PinGame(null); }
                catch (Exception e) { Plugin.Log.LogWarning("Oyun ses sabitlemesi kaldırılamadı: " + e.Message); }
                Plugin.Log.LogInfo("Ses ayarları eski haline döndürüldü.");
            });
            // Başarısızsa kayıt kalsın; bir sonraki açılışta tekrar denenir
            if (ok) savedDefault.Value = "";
        }

        // İşlemi MTA bir thread'de yapıp bitmesini bekler (Unity ana thread'i STA'dır)
        private static bool RunOnComThread(Action action)
        {
            var ok = false;
            var thread = new Thread(() =>
            {
                RoInitialize(1 /* MULTITHREADED */);
                try { action(); ok = true; }
                catch (Exception e) { Plugin.Log.LogWarning("Ses yönlendirme hatası: " + e.Message); }
                finally { RoUninitialize(); }
            }) { IsBackground = true, Name = "GorillaDesktop.AudioRouter" };
            thread.Start();
            thread.Join(5000);
            return ok;
        }

        private static void SetDefault(string deviceId)
        {
            var clsid = CLSID_PolicyConfigClient;
            var iid = IID_IPolicyConfig;
            Com.Check(Com.CoCreateInstance(ref clsid, IntPtr.Zero, Com.CLSCTX_ALL, ref iid, out var policy), "PolicyConfig");
            try
            {
                var set = Com.Fn<SetDefaultEndpointFn>(policy, 13);
                Com.Check(set(policy, deviceId, AudioDevices.eConsole), "SetDefaultEndpoint");
                Com.Check(set(policy, deviceId, AudioDevices.eMultimedia), "SetDefaultEndpoint");
            }
            finally { Com.Release(ref policy); }
        }

        // Oyunun kendi sesini belirli bir cihaza sabitle (null = sabitlemeyi kaldır)
        private static void PinGame(string deviceId)
        {
            var factory = GetPolicyFactory();
            var hstring = IntPtr.Zero;
            try
            {
                if (deviceId != null)
                {
                    var full = MMDEVAPI_TOKEN + deviceId + DEVINTERFACE_AUDIO_RENDER;
                    Com.Check(WindowsCreateString(full, (uint)full.Length, out hstring), "WindowsCreateString");
                }

                var pid = (uint)Process.GetCurrentProcess().Id;
                var set = Com.Fn<SetPersistedFn>(factory, 25);
                Com.Check(set(factory, pid, AudioDevices.eRender, AudioDevices.eConsole, hstring), "SetPersistedDefaultAudioEndpoint");
                Com.Check(set(factory, pid, AudioDevices.eRender, AudioDevices.eMultimedia, hstring), "SetPersistedDefaultAudioEndpoint");
            }
            finally
            {
                if (hstring != IntPtr.Zero) WindowsDeleteString(hstring);
                Com.Release(ref factory);
            }
        }

        // Windows ayarlarında oyuna elle seçilmiş çıkış cihazı (yoksa null)
        private static string GetGamePinnedDevice()
        {
            var factory = GetPolicyFactory();
            try
            {
                var pid = (uint)Process.GetCurrentProcess().Id;
                if (Com.Fn<GetPersistedFn>(factory, 26)(factory, pid, AudioDevices.eRender, AudioDevices.eMultimedia, out var hstring) < 0 ||
                    hstring == IntPtr.Zero) return null;
                try
                {
                    // Biçim: \\?\SWD#MMDEVAPI#{0.0.0.00000000}.{guid}#{arayüz}
                    var full = Marshal.PtrToStringUni(WindowsGetStringRawBuffer(hstring, out _)) ?? "";
                    var start = full.IndexOf("MMDEVAPI#", StringComparison.Ordinal);
                    var end = full.LastIndexOf('#');
                    if (start < 0 || end <= start) return null;
                    start += "MMDEVAPI#".Length;
                    return full.Substring(start, end - start);
                }
                finally { WindowsDeleteString(hstring); }
            }
            finally { Com.Release(ref factory); }
        }

        private static IntPtr GetPolicyFactory()
        {
            Com.Check(WindowsCreateString(AudioPolicyClass, (uint)AudioPolicyClass.Length, out var className), "WindowsCreateString");
            try
            {
                // Arayüz kimliği Windows sürümüne göre değişir (21H2 öncesi/sonrası)
                foreach (var iid in new[] { IID_AudioPolicyFactory_21H2, IID_AudioPolicyFactory_Old })
                {
                    var id = iid;
                    if (RoGetActivationFactory(className, ref id, out var factory) >= 0) return factory;
                }
                throw new Exception("AudioPolicyConfig bu Windows sürümünde yok");
            }
            finally { WindowsDeleteString(className); }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetDefaultEndpointFn(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetPersistedFn(IntPtr self, uint processId, int flow, int role, IntPtr deviceId);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetPersistedFn(IntPtr self, uint processId, int flow, int role, out IntPtr deviceId);

        private const string AudioPolicyClass = "Windows.Media.Internal.AudioPolicyConfig";
        private const string MMDEVAPI_TOKEN = @"\\?\SWD#MMDEVAPI#";
        private const string DEVINTERFACE_AUDIO_RENDER = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

        private static readonly Guid CLSID_PolicyConfigClient = new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
        private static readonly Guid IID_IPolicyConfig = new Guid("F8679F50-850A-41CF-9C72-430F290290C8");
        private static readonly Guid IID_AudioPolicyFactory_21H2 = new Guid("AB3D4648-E242-459F-B02F-541C70306324");
        private static readonly Guid IID_AudioPolicyFactory_Old = new Guid("2A59116D-6C4F-45E0-A74F-707E3FEF9258");

        [DllImport("combase.dll")] private static extern int RoInitialize(int type);
        [DllImport("combase.dll")] private static extern void RoUninitialize();
        [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
        [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string s, uint length, out IntPtr hstring);
        [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
        [DllImport("combase.dll")] private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
    }
}
