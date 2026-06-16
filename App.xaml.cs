using MyRoboticsInspector.Pages;
using MyRoboticsInspector.Services;

namespace MyRoboticsInspector;

public partial class App : Application
{
    private readonly IServiceProvider _services;
    private readonly AuthService _auth;
    private Window? _window;

    public App(IServiceProvider services, AuthService auth)
    {
        InitializeComponent();

        // Kayıtlı tema tercihini uygula (Sistem / Açık / Koyu) — kök sayfa kurulmadan önce.
        ThemeService.ApplyInitial();

        _services = services;
        _auth = auth;

        // Tüm yakalanmamış istisnaları yakalayıp dosyaya yaz — crash teşhis için.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { LogCrash("UnobservedTaskException", e.Exception); e.SetObserved(); };
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var path = Path.Combine(FileSystem.AppDataDirectory, "crash.log");
            var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{ex}\n---\n";
            File.AppendAllText(path, msg);
        }
        catch { }
    }

    /// <summary>GitHub release kaynağı — eski/boş ayar URL'i buna yönlendirilir.</summary>
    private const string DefaultUpdateRepo = "https://github.com/ademdagcayir/MyRoboticsInspector";

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // Gömülü Mosquitto broker'ını başlat (exe yanındaki 'mosquitto\' klasöründen).
        // Ayrı broker kurulumu/eski yazılım gerekmez. Port zaten doluysa (başka broker
        // çalışıyorsa) sessizce no-op'a düşer; uygulama mevcut broker'a bağlanır.
        _ = _services.GetRequiredService<BrokerService>().StartAsync();

        // Bulut yedek servisini ayağa kaldır + kayıtlı bulut oturumunu sessizce bağla.
        // Login ekranı kaldırıldı (kullanıcı tercihi: saha aracında sürtünmesiz açılış);
        // çoklu hesap artık YALNIZCA bulut tarafında (Ayarlar → Bulut Yedek e-posta/parola).
        // Yerel profil yok → sabit profil kimliği 0 ile bulut oturumu cihaz başına saklanır.
        _services.GetRequiredService<Services.Cloud.CloudBackupService>().Start();
        _ = _services.GetRequiredService<Services.Cloud.CloudAuthService>()
            .AttachProfileAsync(Services.Cloud.CloudAuthService.LocalProfileId);

        _window = new Window { Page = BuildRootPage() };
        // Açılışta sessiz otomatik güncelleme (varsa indir + uygula + yeniden başlat)
        _ = TryStartupAutoUpdateAsync();
        return _window;
    }

    /// <summary>
    /// Açılışta sessizce güncelleme kontrol eder; varsa indirip uygular ve uygulamayı
    /// yeni sürümle otomatik yeniden başlatır (kullanıcıya sormaz). ChequeApp benzeri akış.
    /// </summary>
    private async Task TryStartupAutoUpdateAsync()
    {
        try
        {
            var updates = _services.GetService<UpdateService>();
            var db      = _services.GetService<DatabaseService>();
            if (updates is null || db is null || !updates.IsSupported) return;

            var settings = await db.GetSettingsAsync();
            if (!settings.AutoCheckUpdates) return;

            // Eski (myrobotics.com.tr) veya boş URL → GitHub release kaynağına yönlendir
            var url = settings.UpdateServerUrl;
            if (string.IsNullOrWhiteSpace(url) || url.Contains("myrobotics.com.tr", StringComparison.OrdinalIgnoreCase))
                url = DefaultUpdateRepo;

            // Pencere görünür olsun diye kısa bekleme, sonra sessiz uygula+restart
            await Task.Delay(2500);
            await updates.ApplyAndRestartAsync(url);
            // Güncelleme yoksa buradan döner; varsa uygulama zaten yeniden başlamıştır.
        }
        catch (Exception ex)
        {
            LogCrash("StartupAutoUpdate", ex);
        }
    }

    private Page BuildRootPage()
    {
        // Login ekranı yok — uygulama doğrudan ana ekrana (AppShell) açılır.
        return _services.GetRequiredService<AppShell>();
    }

    /// <summary>Tema değişince kök sayfayı yeniden kurar — StaticResource renkler yeniden çözülür.</summary>
    public void RecreateRoot()
    {
        if (_window is null) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try { _window.Page = BuildRootPage(); }
            catch (Exception ex) { LogCrash("RecreateRoot", ex); }
        });
    }
}
