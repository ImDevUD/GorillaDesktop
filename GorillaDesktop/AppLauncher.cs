using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace GorillaDesktop
{
    // Uygulamaları seçili monitörde tam ekran açar:
    //  - Web uygulamaları: Edge'in uygulama modunda (adres çubuğu ve sekme yok).
    //  - Discord / Spotify: bilgisayarda kuruluysa programın kendisi, kenarlıksız tam ekran.
    // Aynı uygulama tekrar istenirse yeni pencere açmaz, açık olanı öne getirir.
    //
    // Netflix, Disney+ gibi siteler kopya korumalı videoyu ekran kartıyla çözer ve Windows bunu ekran
    // yakalamaya siyah verir. Bu yüzden Edge'i donanım hızlandırması kapalı başlatıyoruz (kullanıcının
    // kendi profiliyle, girişler kalır). Edge zaten başka ayarla çalışıyorsa ayar işlemez; önce kapatılır.
    internal static class AppLauncher
    {
        private sealed class DesktopApp
        {
            public string Exe, Args, Process;
        }

        private static readonly Dictionary<string, IntPtr> windows = new Dictionary<string, IntPtr>();
        private static readonly object gate = new object();
        private static string browser;
        private static int browserPid; // bizim hızlandırmasız başlattığımız Edge'in ana işlemi

        public static IntPtr LastWindow { get; private set; }

        private const string BrowserFlags =
            "--disable-gpu " +
            // Oyun öndeyken Edge arka planda kısılmasın (ses/görüntü donmasın)
            "--disable-backgrounding-occluded-windows --disable-renderer-backgrounding --disable-background-timer-throttling " +
            "--disable-features=HardwareSecureDecryption,HardwareSecureDecryptionExperiment,msPlayReadyHardwareDRM," +
            "CalculateNativeWinOcclusion,UseEcoQoSForBackgroundProcess,IntensiveWakeUpThrottling";

        public static string Browser
        {
            get
            {
                if (browser != null) return browser;
                // Edge (Windows'ta hep kurulu), yoksa Chrome
                var x86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";
                var x64 = Environment.GetEnvironmentVariable("ProgramW6432") ?? @"C:\Program Files";
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                foreach (var path in new[]
                {
                    x86 + @"\Microsoft\Edge\Application\msedge.exe", x64 + @"\Microsoft\Edge\Application\msedge.exe",
                    x64 + @"\Google\Chrome\Application\chrome.exe", x86 + @"\Google\Chrome\Application\chrome.exe",
                    local + @"\Google\Chrome\Application\chrome.exe",
                })
                    if (File.Exists(path)) return browser = path;
                return browser = "";
            }
        }

        // Programı kuruluysa programı açılacak uygulamalar
        private static DesktopApp Desktop(string id)
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            switch (id)
            {
                case "discord":
                    var update = Path.Combine(local, @"Discord\Update.exe");
                    return File.Exists(update) ? new DesktopApp { Exe = update, Args = "--processStart Discord.exe", Process = "Discord" } : null;
                case "spotify":
                    foreach (var p in new[] { Path.Combine(roaming, @"Spotify\Spotify.exe"), Path.Combine(local, @"Microsoft\WindowsApps\Spotify.exe") })
                        if (File.Exists(p)) return new DesktopApp { Exe = p, Args = "", Process = "Spotify" };
                    return null;
            }
            return null;
        }

        // Arka planda açar (oyunu bekletmez). done(true) = pencere tam ekran ve önde
        public static void Open(string id, string url, Native.RECT monitor, Action<bool> done = null)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var ok = false;
                try { ok = OpenNow(id, url, monitor); }
                catch (Exception e) { Plugin.Log?.LogWarning($"{id} açılamadı: {e.Message}"); }
                done?.Invoke(ok);
            });
        }

        private static bool OpenNow(string id, string url, Native.RECT monitor)
        {
            Native.TrySetDpiAware();
            IntPtr existing;
            lock (gate) windows.TryGetValue(id, out existing);
            if (existing != IntPtr.Zero && Native.IsWindow(existing) && Native.IsWindowVisible(existing))
            {
                var app = Desktop(id);
                Fullscreen(existing, monitor, borderless: app != null);
                Boost(app != null ? app.Process : Path.GetFileNameWithoutExtension(Browser));
                return true;
            }

            var desktop = Desktop(id);
            var hwnd = desktop != null ? StartDesktop(desktop) : StartWeb(url, monitor);
            if (hwnd == IntPtr.Zero)
            {
                Plugin.Log?.LogWarning($"{id} penceresi bulunamadı");
                return false;
            }
            lock (gate) windows[id] = hwnd;
            Thread.Sleep(400); // pencerenin yerleşmesini bekle
            Fullscreen(hwnd, monitor, borderless: desktop != null);
            Boost(desktop != null ? desktop.Process : Path.GetFileNameWithoutExtension(Browser));
            return true;
        }

        // Oyun işlemciyi yoğun kullanırken uygulamanın sesi takılmasın: önceliğini biraz yükselt
        private static void Boost(string processName)
        {
            foreach (var p in Process.GetProcessesByName(processName))
            {
                try { if (p.PriorityClass == ProcessPriorityClass.Normal) p.PriorityClass = ProcessPriorityClass.AboveNormal; }
                catch { }
                finally { p.Dispose(); }
            }
        }

        // ---------------------------------------------------------------- web (Edge uygulama modu)

        private static IntPtr StartWeb(string url, Native.RECT monitor)
        {
            if (Browser.Length == 0)
            {
                Plugin.Log?.LogWarning("Edge ya da Chrome bulunamadı");
                return IntPtr.Zero;
            }

            var exeName = Path.GetFileNameWithoutExtension(Browser);
            var ours = browserPid != 0 && IsAlive(browserPid, exeName);
            if (!ours && Process.GetProcessesByName(exeName).Length > 0)
            {
                // Edge başka ayarla çalışıyor: hızlandırmasız ayarın işlemesi için kapatıp yeniden başlat
                Plugin.Log?.LogInfo("Edge hızlandırma kapalı olarak yeniden başlatılıyor");
                CloseAll(exeName);
            }

            var before = new HashSet<IntPtr>(WindowsOf(exeName));
            var args = $"{BrowserFlags} --app={url} --start-fullscreen --window-position={monitor.Left},{monitor.Top} --window-size={monitor.Width},{monitor.Height}";
            var started = Process.Start(new ProcessStartInfo(Browser, args) { UseShellExecute = false });
            if (!ours && started != null) browserPid = started.Id;

            // Yeni açılan pencereyi bul (tarayıcı zaten açıksa işlem kimliği işe yaramaz; yeni pencereye bakıyoruz)
            for (var waited = 0; waited < 15000; waited += 150)
            {
                Thread.Sleep(150);
                foreach (var h in WindowsOf(exeName))
                    if (!before.Contains(h)) return h;
            }
            return IntPtr.Zero;
        }

        // Pencereleri düzgünce kapat (Edge sekmeleri sonra geri getirir), kapanmayanları sonlandır
        private static void CloseAll(string exeName)
        {
            foreach (var h in WindowsOf(exeName)) Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            for (var waited = 0; waited < 4000 && Process.GetProcessesByName(exeName).Length > 0; waited += 200) Thread.Sleep(200);
            foreach (var p in Process.GetProcessesByName(exeName))
            {
                try { p.Kill(); } catch { }
            }
            Thread.Sleep(500);
        }

        private static bool IsAlive(int pid, string exeName)
        {
            try { return !Process.GetProcessById(pid).HasExited && Process.GetProcessById(pid).ProcessName.Equals(exeName, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        // ---------------------------------------------------------------- masaüstü programları

        private static IntPtr StartDesktop(DesktopApp app)
        {
            Process.Start(new ProcessStartInfo(app.Exe, app.Args) { UseShellExecute = true });
            // Program zaten açıksa öne gelir, değilse açılmasını bekle; en büyük ana penceresini al
            for (var waited = 0; waited < 20000; waited += 250)
            {
                Thread.Sleep(250);
                var h = WindowsOf(app.Process, anyClass: true).OrderByDescending(Area).FirstOrDefault();
                if (h != IntPtr.Zero && Area(h) > 200 * 200) return h;
            }
            return IntPtr.Zero;
        }

        private static long Area(IntPtr h) => Native.GetWindowRect(h, out var r) ? (long)r.Width * r.Height : 0;

        // ---------------------------------------------------------------- tam ekran

        // Pencereyi monitöre taşı ve öne getir. Web: hâlâ başlık varsa F11. Program: kenarlıkları kaldır
        private static void Fullscreen(IntPtr hwnd, Native.RECT monitor, bool borderless)
        {
            LastWindow = hwnd;
            Native.ForceForeground(hwnd);
            // Öne alma izni verilmese bile pencereyi diğerlerinin üstüne çıkar (önce en üste sabitle, sonra bırak)
            Native.SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x3 | Native.SWP_SHOWWINDOW);  // HWND_TOPMOST, NOSIZE|NOMOVE
            Native.SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, 0x3 | Native.SWP_SHOWWINDOW);  // HWND_NOTOPMOST
            if (IsFullscreen(hwnd, monitor)) return;

            if (borderless)
            {
                Native.ShowWindow(hwnd, Native.SW_RESTORE); // ekranı kaplamış (maximized) ise önce normale al
                var style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
                Native.SetWindowLongPtr(hwnd, Native.GWL_STYLE, new IntPtr(style & ~(Native.WS_CAPTION | Native.WS_THICKFRAME)));
                Native.SetWindowPos(hwnd, IntPtr.Zero, monitor.Left, monitor.Top, monitor.Width, monitor.Height, Native.SWP_SHOWWINDOW | Native.SWP_FRAMECHANGED);
                Native.ForceForeground(hwnd);
                return;
            }

            Native.SetWindowPos(hwnd, IntPtr.Zero, monitor.Left, monitor.Top, monitor.Width, monitor.Height, Native.SWP_SHOWWINDOW);
            Thread.Sleep(150);
            Native.ForceForeground(hwnd);
            if (HasCaption(hwnd)) Native.KeyTap(Native.VK_F11);
        }

        public static bool IsFullscreen(IntPtr hwnd, Native.RECT monitor)
        {
            if (!Native.GetWindowRect(hwnd, out var r)) return false;
            return !HasCaption(hwnd) && r.Left <= monitor.Left && r.Top <= monitor.Top && r.Right >= monitor.Right && r.Bottom >= monitor.Bottom;
        }

        private static bool HasCaption(IntPtr hwnd) => (Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64() & Native.WS_CAPTION) == Native.WS_CAPTION;

        // Verilen işlem adına ait görünür, başlıklı üst pencereler
        private static List<IntPtr> WindowsOf(string processName, bool anyClass = false)
        {
            var pids = new HashSet<uint>(Process.GetProcessesByName(processName).Select(p => (uint)p.Id));
            var list = new List<IntPtr>();
            if (pids.Count == 0) return list;
            var cls = new StringBuilder(64);
            Native.EnumWindows((hwnd, _) =>
            {
                if (!Native.IsWindowVisible(hwnd) || Native.GetWindowTextLength(hwnd) == 0) return true;
                Native.GetWindowThreadProcessId(hwnd, out var pid);
                if (!pids.Contains(pid)) return true;
                cls.Length = 0;
                Native.GetClassName(hwnd, cls, cls.Capacity);
                if (anyClass || cls.ToString() == "Chrome_WidgetWin_1") list.Add(hwnd); // Edge'in ana pencere sınıfı; programlarda sınıfa bakılmaz
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static IntPtr WindowOf(string id)
        {
            lock (gate) return windows.TryGetValue(id, out var h) ? h : IntPtr.Zero;
        }
    }
}
