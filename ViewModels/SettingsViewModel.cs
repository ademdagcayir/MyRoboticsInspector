using System.Buffers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MQTTnet;
using MQTTnet.Protocol;
using MyRoboticsInspector.Models;
using MyRoboticsInspector.Services;

namespace MyRoboticsInspector.ViewModels;

public partial class SettingsViewModel : BaseViewModel
{
    private readonly DatabaseService _db;
    private readonly BackupService _backup;
    private readonly AuthService _auth;
    private readonly UpdateService _updates;

    [ObservableProperty] private AppSettings settings = new();
    [ObservableProperty] private string? testResult;
    [ObservableProperty] private bool isTesting;

    // Backup section bindings
    [ObservableProperty] private string backupStatusText = "";
    [ObservableProperty] private Color backupStatusColor = Colors.Gray;
    [ObservableProperty] private string? backupRecommendedFolder;
    [ObservableProperty] private bool oneDriveAvailable;
    [ObservableProperty] private bool isMigrating;
    [ObservableProperty] private string? migrationLog;
    [ObservableProperty] private string? currentProfileName;

    // Update section bindings (Velopack)
    [ObservableProperty] private string currentVersion = "—";
    [ObservableProperty] private string updateStatus = "";
    [ObservableProperty] private bool updateAvailable;
    [ObservableProperty] private string? newVersion;
    [ObservableProperty] private bool isCheckingUpdate;
    [ObservableProperty] private bool isApplyingUpdate;
    [ObservableProperty] private int updateProgress;

    public bool IsUpdateSupported => _updates.IsSupported;

    /// <summary>Overlay için seçilebilir sistem fontları (Picker kaynağı).</summary>
    public string[] OverlayFonts { get; } =
    {
        "Arial", "Segoe UI", "Tahoma", "Verdana", "Calibri",
        "Times New Roman", "Consolas", "Courier New", "Impact"
    };

    /// <summary>Kayıt encoder seçenekleri (Picker kaynağı).</summary>
    public string[] RecordingEncoders { get; } = { "auto", "nvenc", "qsv", "libx264" };

    /// <summary>Tema seçenekleri (Picker kaynağı).</summary>
    public string[] ThemeOptions { get; } = { "Koyu", "Açık", "Sistem" };

    [ObservableProperty] private string selectedTheme = "Koyu";
    private bool _suppressThemeApply;

    private static string ThemePrefToDisplay(string p) => p switch
    {
        ThemeService.Light  => "Açık",
        ThemeService.System => "Sistem",
        _                   => "Koyu"
    };

    private static string ThemeDisplayToPref(string d) => d switch
    {
        "Açık"   => ThemeService.Light,
        "Sistem" => ThemeService.System,
        _        => ThemeService.Dark
    };

    partial void OnSelectedThemeChanged(string value)
    {
        if (_suppressThemeApply || string.IsNullOrEmpty(value)) return;
        var pref = ThemeDisplayToPref(value);
        if (pref == ThemeService.CurrentPref) return;
        ThemeService.Apply(pref); // paleti değiştirir + kök sayfayı yeniden kurar
    }

    private readonly SyncedVideoPipeline _pipeline;

    public SettingsViewModel(DatabaseService db, BackupService backup, AuthService auth, UpdateService updates, SyncedVideoPipeline pipeline)
    {
        _db = db;
        _backup = backup;
        _auth = auth;
        _updates = updates;
        _pipeline = pipeline;
        Title = "Ayarlar";

        OneDriveAvailable = _backup.OneDriveAvailable;
        BackupRecommendedFolder = _backup.RecommendedBackupFolder;
        CurrentProfileName = _auth.CurrentProfile?.Name;
        CurrentVersion = _updates.CurrentVersion;
    }

    partial void OnSettingsChanged(AppSettings value) => RefreshBackupStatus();

    [RelayCommand]
    public async Task LoadAsync()
    {
        Settings = await _db.GetSettingsAsync();

        // Tema picker'ını mevcut tercihe ayarla (uygulamayı yeniden tetiklemeden)
        _suppressThemeApply = true;
        SelectedTheme = ThemePrefToDisplay(ThemeService.CurrentPref);
        _suppressThemeApply = false;

        RefreshBackupStatus();

        // Açılışta sessiz background kontrolü (kullanıcı istemişse)
        if (Settings.AutoCheckUpdates && IsUpdateSupported)
            _ = CheckUpdateAsync();
    }

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.UpdateServerUrl))
        {
            UpdateStatus = "Güncelleme sunucusu ayarlanmamış";
            return;
        }
        IsCheckingUpdate = true;
        UpdateStatus = "Kontrol ediliyor...";
        UpdateAvailable = false;
        var result = await _updates.CheckAsync(Settings.UpdateServerUrl);
        IsCheckingUpdate = false;

        (UpdateStatus, UpdateAvailable, NewVersion) = result.State switch
        {
            UpdateState.UpdateAvailable => ($"Yeni sürüm hazır: {result.NewVersion}", true, result.NewVersion),
            UpdateState.UpToDate        => ("✓ En son sürümdesiniz",                  false, null),
            UpdateState.NotInstalled    => ("⚠ Geliştirici yapı (Setup.exe ile kurulmadı) — güncelleme uygulanamaz", false, null),
            UpdateState.NotSupported    => ("Otomatik güncelleme bu platformda yok", false, null),
            UpdateState.Error           => ($"Hata: {result.ErrorMessage}",           false, null),
            _ => (UpdateStatus, false, null)
        };
    }

    [RelayCommand]
    private async Task ApplyUpdateAsync()
    {
        if (!UpdateAvailable || string.IsNullOrWhiteSpace(Settings.UpdateServerUrl)) return;

        var confirmed = await Shell.Current.DisplayAlert(
            "Güncellemeyi uygula",
            $"Sürüm {NewVersion} indirilip kurulacak. Uygulama yeniden başlayacak.\n\nDevam edilsin mi?",
            "Evet, güncelle", "Vazgeç");
        if (!confirmed) return;

        IsApplyingUpdate = true;
        UpdateProgress = 0;
        UpdateStatus = "İndiriliyor... %0";
        var progress = new Progress<int>(p =>
        {
            UpdateProgress = p;
            UpdateStatus = $"İndiriliyor... %{p}";
        });

        var result = await _updates.ApplyAndRestartAsync(Settings.UpdateServerUrl, progress);
        IsApplyingUpdate = false;

        // Başarılıysa burada kod çalışmaz — uygulama zaten kapanıyor restart için.
        if (result.State == UpdateState.Error)
            UpdateStatus = $"Güncelleme başarısız: {result.ErrorMessage}";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        await _db.SaveSettingsAsync(Settings);
        // Senkron gecikmesi + overlay font'unu canlı uygula (pipeline singleton — anında etkili)
        _pipeline.OffsetSeconds = Math.Clamp(Settings.MeterSyncOffsetMs, 0, 3000) / 1000.0;
        _pipeline.ConfigureOverlayFont(Settings.OverlayFontFamily, Settings.OverlayFontScale);
        RefreshBackupStatus();
        StatusMessage = "Ayarlar kaydedildi";
    }

    [RelayCommand]
    private async Task UseOneDriveAsync()
    {
        try
        {
            var folder = _backup.EnsureBackupFolder();
            Settings.StoragePath = folder;
            await _db.SaveSettingsAsync(Settings);
            OnPropertyChanged(nameof(Settings));
            RefreshBackupStatus();
            StatusMessage = $"Yeni kayıtlar artık OneDrive'a yazılacak: {folder}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"OneDrive klasörü ayarlanamadı: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task MigrateExistingDataAsync()
    {
        var source = FileSystem.AppDataDirectory;
        var target = _backup.RecommendedBackupFolder;
        if (target is null)
        {
            MigrationLog = "OneDrive bulunamadı.";
            return;
        }

        IsMigrating = true;
        MigrationLog = "Taşıma başladı...";
        var progress = new Progress<string>(line => MainThread.BeginInvokeOnMainThread(() =>
        {
            MigrationLog = line;
        }));

        try
        {
            var (copied, failed, bytes) = await _backup.MigrateAsync(source, target, progress);
            Settings.StoragePath = target;
            await _db.SaveSettingsAsync(Settings);
            OnPropertyChanged(nameof(Settings));
            RefreshBackupStatus();
            var mb = bytes / 1024.0 / 1024.0;
            MigrationLog = failed == 0
                ? $"Tamam: {copied} dosya, {mb:0.0} MB taşındı. OneDrive otomatik yedekleyecek."
                : $"Bitti: {copied} kopyalandı, {failed} HATA. Detaylar için log.";
        }
        catch (Exception ex)
        {
            MigrationLog = $"Taşıma başarısız: {ex.Message}";
        }
        finally
        {
            IsMigrating = false;
        }
    }

    [RelayCommand]
    private void Logout()
    {
        _auth.Logout();
    }

    private void RefreshBackupStatus()
    {
        var status = _backup.GetStatus(Settings.StoragePath);
        (BackupStatusText, BackupStatusColor) = status switch
        {
            BackupStatus.Active => (
                "✓ Yedek aktif — dosyalar OneDrive ile otomatik senkronlanıyor",
                Color.FromArgb("#5fc97e")),
            BackupStatus.NotConfigured when OneDriveAvailable => (
                "⚠ OneDrive bulundu ama uygulama yerel diske yazıyor (yedeksiz)",
                Color.FromArgb("#ffcc66")),
            BackupStatus.NotConfigured => (
                "ℹ Yerel disk kullanılıyor",
                Color.FromArgb("#9ba3af")),
            BackupStatus.NotAvailable => (
                "✗ OneDrive yok — Windows OneDrive istemcisini kur veya farklı yedek kullan",
                Color.FromArgb("#e66")),
            _ => ("", Colors.Gray)
        };
    }

    // ============ MQTT TEST (unchanged) ============

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsTesting = true;
        TestResult = "Bağlanılıyor...";
        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();
        var telemetryTopic = $"{Settings.TopicPrefix}/{Settings.RobotId}/telemetry";
        int telemetryCount = 0;
        string? lastSample = null;

        client.ApplicationMessageReceivedAsync += e =>
        {
            if (e.ApplicationMessage.Topic == telemetryTopic)
            {
                telemetryCount++;
                if (telemetryCount == 1)
                {
                    try
                    {
                        var bytes = e.ApplicationMessage.Payload.ToArray();
                        lastSample = System.Text.Encoding.UTF8.GetString(bytes);
                        if (lastSample.Length > 100) lastSample = lastSample[..100] + "...";
                    }
                    catch { }
                }
            }
            return Task.CompletedTask;
        };

        try
        {
            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(Settings.BrokerHost, Settings.BrokerPort)
                .WithClientId($"test-{Guid.NewGuid().ToString("N")[..8]}")
                .WithCleanSession()
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(10));
            if (!string.IsNullOrWhiteSpace(Settings.BrokerUsername))
                builder = builder.WithCredentials(Settings.BrokerUsername, Settings.BrokerPassword);

            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(builder.Build(), connectCts.Token);
            await client.SubscribeAsync(telemetryTopic, MqttQualityOfServiceLevel.AtMostOnce);

            TestResult = $"Bağlandı. Telemetri bekleniyor (topic: {telemetryTopic})...";
            await Task.Delay(2000);

            if (telemetryCount > 0)
            {
                TestResult = $"✓ Bağlandı, 2 sn'de {telemetryCount} telemetri mesajı. Örnek: {lastSample}";
            }
            else
            {
                TestResult = $"⚠ Bağlandı, ama telemetri yok. Robot/simulator çalışıyor mu? Topic: {telemetryTopic}";
            }

            try { await client.DisconnectAsync(); } catch { }
        }
        catch (OperationCanceledException)
        {
            TestResult = $"✗ Bağlantı zaman aşımı ({Settings.BrokerHost}:{Settings.BrokerPort})";
        }
        catch (Exception ex)
        {
            TestResult = $"✗ Bağlantı başarısız: {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }
}
