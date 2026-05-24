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
        _services = services;
        _auth = auth;
        _auth.CurrentProfileChanged += OnAuthChanged;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        _window = new Window { Page = BuildRootPage() };
        return _window;
    }

    private Page BuildRootPage()
    {
        return _auth.CurrentProfile is null
            ? _services.GetRequiredService<LoginPage>()
            : new AppShell();
    }

    private void OnAuthChanged(object? sender, EventArgs e)
    {
        if (_window is null) return;
        // Login or logout — swap the root page to keep the navigation graph clean.
        MainThread.BeginInvokeOnMainThread(() => _window.Page = BuildRootPage());
    }
}
