using MyRoboticsInspector.Pages;
using MyRoboticsInspector.Services;
using MyRoboticsInspector.Services.Cloud;

namespace MyRoboticsInspector;

public partial class AppShell : Shell
{
    private readonly AuthService _auth;
    private readonly CloudBackupService _cloud;
    private readonly CloudAuthService _cloudAuth;
    private EventHandler? _cloudHandler;
    private EventHandler? _profileHandler;

    public AppShell(AuthService auth, CloudBackupService cloud, CloudAuthService cloudAuth)
    {
        InitializeComponent();
        _auth = auth;
        _cloud = cloud;
        _cloudAuth = cloudAuth;

        Routing.RegisterRoute("inspectiondetail", typeof(InspectionDetailPage));
        Routing.RegisterRoute("inspectionreview", typeof(InspectionReviewPage));
        Routing.RegisterRoute("projectform", typeof(ProjectFormPage));
        Routing.RegisterRoute("projectchannels", typeof(ProjectChannelsPage));
        Routing.RegisterRoute("channeledit", typeof(ChannelEditPage));

        VersionLabel.Text = "v" + AppInfo.VersionString;
        RefreshOperatorRow();

        // Shell, oturum değişiminde yeniden kurulur (App.OnAuthChanged) — eski örnek
        // singleton eventinde asılı kalmasın diye Loaded/Unloaded ile abone olunur.
        _cloudHandler = (_, _) => MainThread.BeginInvokeOnMainThread(RefreshCloudBadge);
        // Sessiz otomatik girişte AppShell, CurrentProfile HENÜZ null iken kurulur ve
        // App.OnAuthChanged koruması kökü yeniden kurmaz → constructor'daki tek seferlik
        // RefreshOperatorRow bir daha çalışmaz. Bu yüzden profil değişimine reaktif abone ol.
        _profileHandler = (_, _) => MainThread.BeginInvokeOnMainThread(RefreshOperatorRow);
        Loaded += (_, _) =>
        {
            _cloud.StatusChanged += _cloudHandler;
            _cloudAuth.SessionChanged += _cloudHandler;
            _auth.CurrentProfileChanged += _profileHandler;
            RefreshCloudBadge();
            RefreshOperatorRow(); // sessiz giriş Loaded'dan önce tamamlanmış olabilir
        };
        Unloaded += (_, _) =>
        {
            _cloud.StatusChanged -= _cloudHandler;
            _cloudAuth.SessionChanged -= _cloudHandler;
            _auth.CurrentProfileChanged -= _profileHandler;
        };
    }

    private void RefreshOperatorRow()
    {
        var p = _auth.CurrentProfile;
        OperatorRow.IsVisible = p is not null;
        SwitchProfileButton.IsVisible = p is not null;
        if (p is null) return;
        OperatorAvatar.Text = p.Initials;
        OperatorNameLabel.Text = p.Name;
    }

    private void RefreshCloudBadge()
    {
        try
        {
            var (text, color) = _cloud.State switch
            {
                CloudBackupState.Syncing => ("Buluta yedekleniyor…", "#6AB7FF"),
                CloudBackupState.Idle when _cloud.PendingCount > 0
                    => ($"Bulut: {_cloud.PendingCount} dosya sırada", "#FF9F0A"),
                CloudBackupState.Idle => ("Bulut yedek güncel", "#30D158"),
                CloudBackupState.Offline => ("Bulut: çevrimdışı", "#FF9F0A"),
                CloudBackupState.Error => ("Bulut: hata", "#FF453A"),
                _ when _cloudAuth.IsSignedIn => ("Bulut hesabı bağlı", "#30D158"),
                _ => ("Bulut yedek kapalı", "#7E828F"),
            };
            CloudStatusLabel.Text = text;
            CloudDot.Fill = new SolidColorBrush(Color.FromArgb(color));
        }
        catch { /* footer süsleme — düşürme */ }
    }

    private void OnSwitchProfileClicked(object sender, EventArgs e)
    {
        // Oturumu kapat → kök sayfa LoginPage'e döner (App.OnAuthChanged).
        _auth.Logout();
    }

    private void OnExitClicked(object sender, EventArgs e)
    {
        Application.Current?.Quit();
    }
}
