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

        // Koyu tema sabit — saha kullanımı + Windows 11 Mica koyu malzemesiyle uyumlu.
        UserAppTheme = AppTheme.Dark;

        _services = services;
        _auth = auth;
        _auth.CurrentProfileChanged += OnAuthChanged;

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

    protected override Window CreateWindow(IActivationState? activationState)
    {
        _window = new Window { Page = BuildRootPage() };
        return _window;
    }

    private Page BuildRootPage()
    {
        // Giriş ekranı devre dışı — uygulama doğrudan ana sayfayla açılır.
        return _services.GetRequiredService<AppShell>();
    }

    private void OnAuthChanged(object? sender, EventArgs e)
    {
        if (_window is null) return;
        // Login or logout — swap the root page to keep the navigation graph clean.
        try
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    var newPage = BuildRootPage();
                    if (newPage is not null)
                    {
                        _window.Page = newPage;
                    }
                }
                catch (Exception ex)
                {
                    LogCrash("OnAuthChanged.PageSwap.Inner", ex);
                    try
                    {
                        Application.Current?.Windows[0].Page?.DisplayAlert("Hata", $"Geçiş başarısız: {ex.Message}", "Tamam");
                    }
                    catch { }
                }
            });
        }
        catch (Exception ex)
        {
            LogCrash("OnAuthChanged.Outer", ex);
        }
    }
}
