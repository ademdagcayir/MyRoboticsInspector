using System.Buffers;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MQTTnet;
using MQTTnet.Protocol;
using MyRoboticsInspector.Models;
using MyRoboticsInspector.Services;
using MyRoboticsInspector.Services.Cloud;

namespace MyRoboticsInspector.ViewModels;

/// <summary>Bulut dosya listesi satırı (Ayarlar → Bulut Yedek kartı).</summary>
public sealed class CloudFileDisplay
{
    public required string RelPath { get; init; }
    public required string StoragePath { get; init; }
    public required string Detail { get; init; }
    public string FileName => Path.GetFileName(RelPath);
}

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

    /// <summary>Kalibrasyon ekranı anlık ham ADC göstergesi için telemetri servisi.</summary>
    public TelemetryService Telemetry => _telemetry;

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
    private readonly TelemetryService _telemetry;
    private readonly CloudAuthService _cloudAuth;
    private readonly CloudBackupService _cloudBackup;
    private EventHandler? _cloudStatusHandler;

    public SettingsViewModel(DatabaseService db, BackupService backup, AuthService auth, UpdateService updates,
                             SyncedVideoPipeline pipeline, TelemetryService telemetry,
                             CloudAuthService cloudAuth, CloudBackupService cloudBackup)
    {
        _db = db;
        _backup = backup;
        _auth = auth;
        _updates = updates;
        _pipeline = pipeline;
        _telemetry = telemetry;
        _cloudAuth = cloudAuth;
        _cloudBackup = cloudBackup;
        Title = "Ayarlar";

        OneDriveAvailable = _backup.OneDriveAvailable;
        BackupRecommendedFolder = _backup.RecommendedBackupFolder;
        CurrentProfileName = _auth.CurrentProfile?.Name;
        CurrentVersion = _updates.CurrentVersion;

        // VM transient — sayfa kapanınca Cleanup() ile çözülür (sızıntı yok).
        _cloudStatusHandler = (_, _) => MainThread.BeginInvokeOnMainThread(RefreshCloudStatus);
        _cloudBackup.StatusChanged += _cloudStatusHandler;
        _cloudAuth.SessionChanged += _cloudStatusHandler;
    }

    /// <summary>Sayfa kapanınca singleton eventlerinden ayrıl (transient VM sızıntı önlemi).</summary>
    public void Cleanup()
    {
        if (_cloudStatusHandler is null) return;
        _cloudBackup.StatusChanged -= _cloudStatusHandler;
        _cloudAuth.SessionChanged -= _cloudStatusHandler;
        _cloudStatusHandler = null;
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

        // Bulut: bu profilin kayıtlı oturumu varsa sessizce bağlan ve listeyi getir.
        if (!_cloudAuth.IsSignedIn)
            await _cloudAuth.AttachProfileAsync(_auth.CurrentProfile?.Id ?? CloudAuthService.LocalProfileId);
        RefreshCloudStatus();
        if (IsCloudSignedIn) _ = LoadCloudFilesAsync();

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
        _telemetry.ApplyCalibration(Settings);   // eğim/basınç kalibrasyonu anında etkili
        RefreshBackupStatus();
        StatusMessage = "Ayarlar kaydedildi";
    }

    // ============ KALİBRASYON — "Şimdi Yakala" (anlık ham ADC'yi referans olarak al) ============
    [RelayCommand] private void CaptureTilt0()      { Settings.TiltRaw0Deg     = _telemetry.TiltRaw;     OnPropertyChanged(nameof(Settings)); StatusMessage = $"0° referansı: {_telemetry.TiltRaw?.ToString() ?? "—"}"; }
    [RelayCommand] private void CaptureTiltMinus45() { Settings.TiltRawMinus45  = _telemetry.TiltRaw;     OnPropertyChanged(nameof(Settings)); StatusMessage = $"-45° referansı: {_telemetry.TiltRaw?.ToString() ?? "—"}"; }
    [RelayCommand] private void CaptureTiltPlus45()  { Settings.TiltRawPlus45   = _telemetry.TiltRaw;     OnPropertyChanged(nameof(Settings)); StatusMessage = $"+45° referansı: {_telemetry.TiltRaw?.ToString() ?? "—"}"; }
    [RelayCommand] private void CapturePressureMin() { Settings.PressureRawMin  = _telemetry.PressureRaw; OnPropertyChanged(nameof(Settings)); StatusMessage = $"Basınç min: {_telemetry.PressureRaw?.ToString() ?? "—"}"; }
    [RelayCommand] private void CapturePressureMax() { Settings.PressureRawMax  = _telemetry.PressureRaw; OnPropertyChanged(nameof(Settings)); StatusMessage = $"Basınç max: {_telemetry.PressureRaw?.ToString() ?? "—"}"; }

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

    // ============ BULUT YEDEK (Supabase) ============

    [ObservableProperty] private string cloudEmailInput = "";
    [ObservableProperty] private string cloudPasswordInput = "";
    [ObservableProperty] private bool isCloudBusy;
    [ObservableProperty] private string cloudStatusText = "";
    [ObservableProperty] private Color cloudStatusColor = Colors.Gray;
    [ObservableProperty] private string? cloudSignedInEmail;
    [ObservableProperty] private bool isCloudSignedIn;
    [ObservableProperty] private string cloudQueueText = "";
    [ObservableProperty] private string? cloudCurrentItem;
    [ObservableProperty] private string cloudTotalsText = "";
    [ObservableProperty] private bool isCloudDeleteUnlocked;
    [ObservableProperty] private bool isCloudFilesLoading;

    public ObservableCollection<CloudFileDisplay> CloudFiles { get; } = new();

    private void RefreshCloudStatus()
    {
        IsCloudSignedIn = _cloudAuth.IsSignedIn;
        CloudSignedInEmail = _cloudAuth.Session?.Email;
        CloudCurrentItem = _cloudBackup.CurrentItem;
        IsCloudDeleteUnlocked = _cloudBackup.DeleteUnlockedUntil is { } t && t > DateTime.Now;

        CloudQueueText = _cloudBackup.PendingCount > 0
            ? $"{_cloudBackup.UploadedCount} dosya bulutta · {_cloudBackup.PendingCount} sırada"
            : _cloudBackup.UploadedCount > 0
                ? $"{_cloudBackup.UploadedCount} dosya bulutta · sıra boş"
                : "";

        (CloudStatusText, CloudStatusColor) = _cloudBackup.State switch
        {
            CloudBackupState.Syncing => (
                "⟳ Senkronlanıyor…", Color.FromArgb("#6ab7ff")),
            CloudBackupState.Idle when _cloudBackup.LastSyncAt is { } at => (
                $"✓ Yedek güncel — son senkron {at:HH:mm}", Color.FromArgb("#5fc97e")),
            CloudBackupState.Idle => (
                "✓ Hazır", Color.FromArgb("#5fc97e")),
            CloudBackupState.Offline => (
                "⚠ Çevrimdışı — bağlantı gelince devam edilecek", Color.FromArgb("#ffcc66")),
            CloudBackupState.Error => (
                $"✗ {_cloudBackup.LastError}", Color.FromArgb("#e66")),
            CloudBackupState.NotSignedIn when Settings.CloudBackupEnabled => (
                "Bulut hesabına giriş yapın", Color.FromArgb("#ffcc66")),
            CloudBackupState.Disabled or CloudBackupState.NotSignedIn => (
                "Bulut yedekleme kapalı", Color.FromArgb("#9ba3af")),
            _ => ("", Colors.Gray),
        };
    }

    [RelayCommand]
    private async Task CloudSignInAsync()
    {
        if (string.IsNullOrWhiteSpace(CloudEmailInput) || string.IsNullOrWhiteSpace(CloudPasswordInput))
        {
            StatusMessage = "E-posta ve parola gerekli";
            return;
        }
        IsCloudBusy = true;
        try
        {
            await _cloudAuth.AttachProfileAsync(_auth.CurrentProfile?.Id ?? CloudAuthService.LocalProfileId);
            await _cloudAuth.SignInAsync(CloudEmailInput, CloudPasswordInput);
            CloudPasswordInput = "";
            Settings.CloudBackupEnabled = true;
            await _db.SaveSettingsAsync(Settings);
            OnPropertyChanged(nameof(Settings));
            StatusMessage = "Bulut hesabına giriş yapıldı — yedekleme başlıyor";
            _ = _cloudBackup.SyncNowAsync();
        }
        catch (CloudAuthException ex) { StatusMessage = ex.Message; }
        catch (Exception ex) { StatusMessage = "Giriş başarısız: " + ex.Message; }
        finally { IsCloudBusy = false; RefreshCloudStatus(); }
    }

    [RelayCommand]
    private async Task CloudSignUpAsync()
    {
        if (string.IsNullOrWhiteSpace(CloudEmailInput) || CloudPasswordInput.Length < 6)
        {
            StatusMessage = "E-posta ve en az 6 karakter parola gerekli";
            return;
        }
        IsCloudBusy = true;
        try
        {
            await _cloudAuth.AttachProfileAsync(_auth.CurrentProfile?.Id ?? CloudAuthService.LocalProfileId);
            await _cloudAuth.SignUpAsync(CloudEmailInput, CloudPasswordInput);
            CloudPasswordInput = "";
            Settings.CloudBackupEnabled = true;
            await _db.SaveSettingsAsync(Settings);
            OnPropertyChanged(nameof(Settings));
            StatusMessage = "Hesap oluşturuldu — yedekleme başlıyor";
            _ = _cloudBackup.SyncNowAsync();
        }
        catch (CloudAuthException ex) { StatusMessage = ex.Message; }
        catch (Exception ex) { StatusMessage = "Kayıt başarısız: " + ex.Message; }
        finally { IsCloudBusy = false; RefreshCloudStatus(); }
    }

    [RelayCommand]
    private async Task CloudSignOutAsync()
    {
        await _cloudAuth.SignOutAsync();
        CloudFiles.Clear();
        CloudTotalsText = "";
        StatusMessage = "Bulut hesabından çıkıldı (yedekler bulutta korunur)";
        RefreshCloudStatus();
    }

    [RelayCommand]
    private async Task CloudSyncNowAsync()
    {
        await _db.SaveSettingsAsync(Settings); // toggle değişiklikleri senkrondan önce kalıcı olsun
        await _cloudBackup.SyncNowAsync();
        await LoadCloudFilesAsync();
    }

    [RelayCommand]
    private async Task LoadCloudFilesAsync()
    {
        if (!_cloudAuth.IsSignedIn) return;
        IsCloudFilesLoading = true;
        try
        {
            var (count, bytes) = await _cloudBackup.GetCloudTotalsAsync();
            CloudTotalsText = count == 0
                ? "Bulutta henüz dosya yok"
                : $"Bulutta toplam {count} dosya · {CloudBackupService.FormatBytes(bytes)}";

            var files = await _cloudBackup.GetRecentCloudFilesAsync();
            CloudFiles.Clear();
            foreach (var f in files)
                CloudFiles.Add(new CloudFileDisplay
                {
                    RelPath = f.RelPath,
                    StoragePath = f.StoragePath,
                    Detail = $"{CloudBackupService.FormatBytes(f.ByteSize)} · {f.UploadedAt.ToLocalTime():dd.MM.yyyy HH:mm}",
                });
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsCloudFilesLoading = false; }
    }

    [RelayCommand]
    private async Task SetCloudDeletePinAsync()
    {
        if (!_cloudAuth.IsSignedIn) { StatusMessage = "Önce bulut hesabına giriş yapın"; return; }
        try
        {
            var current = await Shell.Current.DisplayPromptAsync("Silme PIN'i",
                "Mevcut PIN (ilk kez belirliyorsanız boş bırakın):", "Devam", "Vazgeç",
                maxLength: 12, keyboard: Keyboard.Numeric);
            if (current is null) return;

            var fresh = await Shell.Current.DisplayPromptAsync("Silme PIN'i",
                "Yeni PIN (en az 4 hane). Buluttan silme YALNIZ bu PIN ile mümkün olur:",
                "Kaydet", "Vazgeç", maxLength: 12, keyboard: Keyboard.Numeric);
            if (string.IsNullOrWhiteSpace(fresh)) return;

            await _cloudBackup.SetDeletePinAsync(current, fresh);
            StatusMessage = "✓ Silme PIN'i sunucuda güncellendi (bcrypt)";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task DeleteCloudFileAsync(CloudFileDisplay? file)
    {
        if (file is null) return;
        try
        {
            // Pencere kapalıysa PIN iste → sunucuda 5 dakikalık silme izni aç.
            if (_cloudBackup.DeleteUnlockedUntil is not { } until || until <= DateTime.Now)
            {
                var pin = await Shell.Current.DisplayPromptAsync("🔒 Silme Kilidi",
                    $"\"{file.FileName}\" buluttan silinecek.\nSilme PIN'inizi girin (5 dk geçerli pencere açılır):",
                    "Kilidi Aç", "Vazgeç", maxLength: 12, keyboard: Keyboard.Numeric);
                if (string.IsNullOrWhiteSpace(pin)) return;
                await _cloudBackup.UnlockDeleteAsync(pin);
            }

            var sure = await Shell.Current.DisplayAlert("Buluttan Sil",
                $"\"{file.FileName}\" kalıcı olarak buluttan silinsin mi?\n(Yerel kopya etkilenmez.)",
                "Sil", "Vazgeç");
            if (!sure) return;

            await _cloudBackup.DeleteCloudFileAsync(file.StoragePath);
            CloudFiles.Remove(file);
            StatusMessage = $"✓ Buluttan silindi: {file.FileName}";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { RefreshCloudStatus(); }
    }

    [RelayCommand]
    private async Task LockCloudDeleteAsync()
    {
        await _cloudBackup.LockDeleteAsync();
        StatusMessage = "Silme kilidi kapatıldı";
        RefreshCloudStatus();
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
                .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V311) // Arduino/eski Mosquitto uyumu
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
