using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using MyRoboticsInspector.Models;
using MyRoboticsInspector.Services;

// NOT: Apple platformlarında global `MediaPlayer` namespace'i var; LibVLCSharp tipi için
// tam-nitelikli ad kullanılmalı (using alias namespace ile çakışır).

namespace MyRoboticsInspector.ViewModels;

public partial class LiveViewModel : BaseViewModel
{
    private readonly VideoService _video;
    private readonly FfmpegRecorder _ffmpeg;
    private readonly IRobotProtocol _robot;
    private readonly MqttRobotClient? _mqtt;
    private readonly TelemetryService _telemetry;
    private readonly DatabaseService _db;
    private readonly IGamepadInput _gamepad;
    private readonly GamepadCommandMapper _gamepadMapper;
    private AppSettings? _settings;
    private DateTime? _streamStartedAt;
    private IDispatcherTimer? _elapsedTimer;
    private CancellationTokenSource? _lightAckCts;

    public LibVLCSharp.Shared.MediaPlayer? MediaPlayer => _video.MediaPlayer;
    public TelemetryService Telemetry => _telemetry;

    [ObservableProperty] private bool isStreaming;
    [ObservableProperty] private bool isRecording;
    [ObservableProperty] private bool isRobotConnected;
    [ObservableProperty] private bool isLightOn;
    [ObservableProperty] private bool isLightPending;
    [ObservableProperty] private float moveSpeed = 0.5f;

    // Gamepad
    [ObservableProperty] private bool isGamepadConnected;
    [ObservableProperty] private bool isGamepadActive;
    [ObservableProperty] private string gamepadStatusText = "🎮 Joystick: Yok";

    // Overlay-bound fields (XAML side)
    [ObservableProperty] private string? projectName;
    [ObservableProperty] private string? neighborhood;
    [ObservableProperty] private string? street;
    [ObservableProperty] private string? operatorName;
    [ObservableProperty] private string? companyName;
    [ObservableProperty] private string elapsedDisplay = "00:00:00";
    [ObservableProperty] private string nowDisplay = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");

    // Inspection workflow
    public ObservableCollection<Customer> Customers { get; } = new();
    public ObservableCollection<Defect> ActiveDefects { get; } = new();
    [ObservableProperty] private Customer? selectedCustomer;
    [ObservableProperty] private Inspection? activeInspection;
    public bool IsInspecting => ActiveInspection is not null;
    public bool CanStartInspection => SelectedCustomer is not null && ActiveInspection is null;
    public int DefectCount => ActiveDefects.Count;

    // Defect modal state — adding new
    [ObservableProperty] private bool isMarkingDefect;
    [ObservableProperty] private Defect editingDefect = new();
    [ObservableProperty] private string? editingDefectSnapshotPath;

    // Defect preview overlay — reviewing existing
    [ObservableProperty] private Defect? previewDefect;
    public bool IsPreviewingDefect => PreviewDefect is not null;

    public List<string> DefectTypes { get; } = new()
    {
        "Çatlak", "Kırık", "Kök Girişi", "Tıkanma", "Yağ / Tortu",
        "Çökme", "Sızıntı", "Birleşim Hatası", "Bağlantı (lateral)",
        "Deformasyon", "Korozyon", "Yabancı Cisim", "Diğer"
    };
    public List<DefectSeverity> SeverityOptions { get; } = new()
    {
        DefectSeverity.Info, DefectSeverity.Low, DefectSeverity.Medium,
        DefectSeverity.High, DefectSeverity.Critical
    };

    /// <summary>İSKİ standart kodları — defect modal'da chip seçim için.</summary>
    public IReadOnlyList<IskiDefectCode> IskiCodes => IskiDefectCodes.QuickPick;

    [RelayCommand]
    private void ApplyIskiCode(IskiDefectCode? code)
    {
        if (code is null) return;
        EditingDefect.IskiCode = code.Code;
        EditingDefect.Type = code.Title;
        EditingDefect.Severity = code.DefaultSeverity;
        if (string.IsNullOrWhiteSpace(EditingDefect.Description))
            EditingDefect.Description = code.Description;
        OnPropertyChanged(nameof(EditingDefect));
    }

    partial void OnSelectedCustomerChanged(Customer? value)
        => OnPropertyChanged(nameof(CanStartInspection));

    partial void OnActiveInspectionChanged(Inspection? value)
    {
        OnPropertyChanged(nameof(IsInspecting));
        OnPropertyChanged(nameof(CanStartInspection));
    }

    partial void OnPreviewDefectChanged(Defect? value)
        => OnPropertyChanged(nameof(IsPreviewingDefect));

    public LiveViewModel(VideoService video, FfmpegRecorder ffmpeg, IRobotProtocol robot,
                        TelemetryService telemetry, DatabaseService db,
                        IGamepadInput gamepad, GamepadCommandMapper gamepadMapper)
    {
        _video = video;
        _ffmpeg = ffmpeg;
        _robot = robot;
        _mqtt = robot as MqttRobotClient;
        _telemetry = telemetry;
        _db = db;
        _gamepad = gamepad;
        _gamepadMapper = gamepadMapper;
        Title = "Canlı Görüntü";

        _robot.ConnectionChanged += (_, c) => MainThread.BeginInvokeOnMainThread(() => IsRobotConnected = c);
        _robot.ErrorOccurred += (_, e) => MainThread.BeginInvokeOnMainThread(() => StatusMessage = e);
        _video.Error += (_, e) => MainThread.BeginInvokeOnMainThread(() => StatusMessage = e);
        _ffmpeg.Error += (_, e) => MainThread.BeginInvokeOnMainThread(() => StatusMessage = e);

        // Confirm robot-side state via telemetry (handshake).
        _telemetry.PropertyChanged += OnTelemetryPropertyChanged;

        ActiveDefects.CollectionChanged += (_, _) => OnPropertyChanged(nameof(DefectCount));

        // Gamepad bağlantı durumu UI'a yansısın
        _gamepad.ConnectionChanged += (_, connected) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsGamepadConnected = connected;
                GamepadStatusText = connected
                    ? (IsGamepadActive ? "🎮 Joystick: Aktif" : "🎮 Joystick: Bağlı (kapalı)")
                    : "🎮 Joystick: Yok";
            });

        // Gamepad → uygulama içi aksiyonlar (snapshot, defect, kayıt, vb.)
        _gamepadMapper.StatusMessage          += (_, msg) => MainThread.BeginInvokeOnMainThread(() => StatusMessage = msg);
        _gamepadMapper.SnapshotRequested      += (_, _) => MainThread.BeginInvokeOnMainThread(async () => await SnapshotAsync());
        _gamepadMapper.EmergencyStopRequested += (_, _) => MainThread.BeginInvokeOnMainThread(async () => await EmergencyStopAsync());
        _gamepadMapper.ToggleLightRequested   += (_, _) => MainThread.BeginInvokeOnMainThread(async () => await ToggleLightAsync());
        _gamepadMapper.MarkDefectRequested    += (_, _) => MainThread.BeginInvokeOnMainThread(() => MarkDefect());
        _gamepadMapper.ToggleRecordingRequested += (_, _) => MainThread.BeginInvokeOnMainThread(async () => await ToggleRecordingAsync());
        _gamepadMapper.ToggleInspectionRequested += (_, _) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (IsInspecting) await FinishInspectionAsync();
            else await StartInspectionAsync();
        });
    }

    private void OnTelemetryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TelemetryService.LightOn))
        {
            if (_telemetry.LightOn is bool b)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    IsLightOn = b;
                    if (IsLightPending)
                    {
                        IsLightPending = false;
                        _lightAckCts?.Cancel();
                        StatusMessage = b ? "Işık ON onaylandı" : "Işık OFF onaylandı";
                    }
                });
            }
        }
    }

    public async Task LoadSettingsAsync()
    {
        _settings = await _db.GetSettingsAsync();
        ProjectName = _settings.ProjectName;
        Neighborhood = _settings.Neighborhood;
        Street = _settings.Street;
        OperatorName = _settings.OperatorName;
        CompanyName = _settings.CompanyName;

        _mqtt?.Configure(_settings.TopicPrefix, _settings.RobotId);

        var customers = await _db.GetCustomersAsync();
        Customers.Clear();
        foreach (var c in customers) Customers.Add(c);

        OnPropertyChanged(nameof(MediaPlayer));
        StartClock();

        // Gamepad otomatik başlat (settings'e bağlı)
        if (_settings.GamepadAutoStart && !IsGamepadActive)
            EnableGamepad();
    }

    [RelayCommand]
    private void ToggleGamepad()
    {
        if (IsGamepadActive) DisableGamepad();
        else EnableGamepad();
    }

    private void EnableGamepad()
    {
        if (IsGamepadActive) return;
        _gamepad.StartPolling();
        _gamepadMapper.Attach();
        IsGamepadActive = true;
        GamepadStatusText = IsGamepadConnected ? "🎮 Joystick: Aktif" : "🎮 Joystick: Aktif (bekleniyor)";
        StatusMessage = "Joystick girdisi açıldı. F710'u XInput moduna ('X') al, dongle bağlı olsun.";
    }

    private void DisableGamepad()
    {
        if (!IsGamepadActive) return;
        _gamepadMapper.Detach();
        _gamepad.StopPolling();
        IsGamepadActive = false;
        GamepadStatusText = IsGamepadConnected ? "🎮 Joystick: Bağlı (kapalı)" : "🎮 Joystick: Yok";
        StatusMessage = "Joystick girdisi kapatıldı.";
    }

    private void StartClock()
    {
        if (_elapsedTimer is not null) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        _elapsedTimer = dispatcher.CreateTimer();
        _elapsedTimer.Interval = TimeSpan.FromSeconds(1);
        _elapsedTimer.Tick += (_, _) =>
        {
            NowDisplay = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");
            if (_streamStartedAt is DateTime t)
                ElapsedDisplay = (DateTime.Now - t).ToString(@"hh\:mm\:ss");

            if (_ffmpeg.IsRecording)
                _ffmpeg.UpdateOverlay(BuildOverlayState());
        };
        _elapsedTimer.Start();
    }

    // ----- Stream / Recording -----

    [RelayCommand]
    private void StartStream()
    {
        if (_settings is null) return;
        _video.Play(_settings.RtspUrl, _settings.NetworkCachingMs);
        IsStreaming = true;
        _streamStartedAt = DateTime.Now;
        StatusMessage = "Yayın başlatıldı";
        OnPropertyChanged(nameof(MediaPlayer));
    }

    [RelayCommand]
    private async Task StopStreamAsync()
    {
        if (_ffmpeg.IsRecording) await _ffmpeg.StopAsync();
        _video.Stop();
        IsStreaming = false;
        IsRecording = false;
        _streamStartedAt = null;
        ElapsedDisplay = "00:00:00";
        StatusMessage = "Yayın durduruldu";
    }

    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        if (_settings is null) await LoadSettingsAsync();
        if (_settings is null) return;

        if (IsRecording)
        {
            try
            {
                if (_ffmpeg.IsRecording) await _ffmpeg.StopAsync();
                else _video.StopRecording();
            }
            finally
            {
                IsRecording = false;
                StatusMessage = "Kayıt durduruldu";
            }
            return;
        }

        var dir = RecordingDirectory();
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"kayit_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

        var ffmpegAvailable = FfmpegRecorder.ProbeFfmpeg(_settings.FfmpegPath);
        if (ffmpegAvailable)
        {
            try
            {
                await _ffmpeg.StartAsync(
                    _settings.FfmpegPath,
                    _settings.RtspUrl,
                    file,
                    BuildOverlayState(),
                    _settings.BurnOverlayInRecording);
                StatusMessage = _settings.BurnOverlayInRecording
                    ? $"Kayıt (overlay gömülü): {Path.GetFileName(file)}"
                    : $"Kayıt: {Path.GetFileName(file)}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"FFmpeg başarısız, LibVLC'ye düşülüyor: {ex.Message}";
                _video.StartRecording(_settings.RtspUrl, file, _settings.NetworkCachingMs);
            }
        }
        else
        {
            StatusMessage = "FFmpeg bulunamadı, overlay'siz LibVLC kaydı kullanılıyor";
            _video.StartRecording(_settings.RtspUrl, file, _settings.NetworkCachingMs);
        }

        IsRecording = true;
        if (!IsStreaming)
        {
            _video.Play(_settings.RtspUrl, _settings.NetworkCachingMs);
            IsStreaming = true;
            _streamStartedAt = DateTime.Now;
            OnPropertyChanged(nameof(MediaPlayer));
        }
        _streamStartedAt ??= DateTime.Now;

        if (ActiveInspection is not null && string.IsNullOrEmpty(ActiveInspection.VideoPath))
        {
            ActiveInspection.VideoPath = file;
            await _db.SaveInspectionAsync(ActiveInspection);
        }
    }

    [RelayCommand]
    private async Task SnapshotAsync()
    {
        if (_settings is null) await LoadSettingsAsync();
        var dir = Path.Combine(StorageRoot(), "snapshots");
        var path = _video.TakeSnapshot(dir);
        StatusMessage = path is null
            ? "Snapshot alınamadı (akış yok?)"
            : $"Görüntü kaydedildi: {Path.GetFileName(path)}";
    }

    // ----- Inspection lifecycle -----

    [RelayCommand]
    private async Task StartInspectionAsync()
    {
        if (SelectedCustomer is null || _settings is null) return;
        try
        {
            IsBusy = true;
            var title = BuildInspectionTitle();
            var job = new Job
            {
                CustomerId = SelectedCustomer.Id,
                Title = title,
                Site = string.Join(" / ", new[] { Neighborhood, Street }.Where(s => !string.IsNullOrWhiteSpace(s))),
                Status = JobStatus.InProgress
            };
            await _db.SaveJobAsync(job);

            var inspection = new Inspection { JobId = job.Id, StartedAt = DateTime.Now };
            await _db.SaveInspectionAsync(inspection);
            ActiveInspection = inspection;
            ActiveDefects.Clear();
            StatusMessage = $"İnceleme başladı: {title}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"İnceleme başlatılamadı: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task FinishInspectionAsync()
    {
        if (ActiveInspection is null) return;
        if (_ffmpeg.IsRecording) await _ffmpeg.StopAsync();
        ActiveInspection.FinishedAt = DateTime.Now;
        if (Telemetry.DistanceMeters is double d) ActiveInspection.DistanceMeters = d;
        await _db.SaveInspectionAsync(ActiveInspection);
        StatusMessage = $"İnceleme tamamlandı ({DefectCount} kusur)";
        ActiveInspection = null;
        IsRecording = false;
        ActiveDefects.Clear();
    }

    // ----- Defect marking -----

    [RelayCommand]
    private void MarkDefect()
    {
        if (ActiveInspection is null) return;

        var dir = Path.Combine(StorageRoot(), "inspections", ActiveInspection.Id.ToString(), "defects");
        EditingDefectSnapshotPath = _video.TakeSnapshot(dir);

        EditingDefect = new Defect
        {
            InspectionId = ActiveInspection.Id,
            VideoTimestampMs = MediaPlayer?.Time ?? 0,
            DistanceMeters = Telemetry.DistanceMeters,
            Severity = DefectSeverity.Medium,
            PhotoPath = EditingDefectSnapshotPath
        };
        IsMarkingDefect = true;
    }

    [RelayCommand]
    private async Task SaveDefectAsync()
    {
        if (ActiveInspection is null) return;
        try
        {
            await _db.SaveDefectAsync(EditingDefect);
            ActiveDefects.Insert(0, EditingDefect); // newest first
            StatusMessage = $"Kusur eklendi ({DefectCount})";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Kusur kaydedilemedi: {ex.Message}";
        }
        finally
        {
            IsMarkingDefect = false;
        }
    }

    [RelayCommand]
    private void CancelDefect()
    {
        if (!string.IsNullOrEmpty(EditingDefectSnapshotPath) && File.Exists(EditingDefectSnapshotPath))
        {
            try { File.Delete(EditingDefectSnapshotPath); } catch { }
        }
        EditingDefectSnapshotPath = null;
        IsMarkingDefect = false;
    }

    // ----- Defect mini-list interactions -----

    [RelayCommand]
    private void PreviewDefectAt(Defect? defect)
    {
        if (defect is null) return;
        PreviewDefect = defect;

        // Best-effort seek: works if MediaPlayer source is a file. For live RTSP this is a no-op
        // (most LibVLC builds reject seeks on RTSP), so we still show the snapshot as the fallback.
        if (MediaPlayer is not null && defect.VideoTimestampMs > 0 && MediaPlayer.IsSeekable)
        {
            try { MediaPlayer.Time = defect.VideoTimestampMs; }
            catch { /* live stream — ignore */ }
        }
    }

    [RelayCommand]
    private void ClosePreview() => PreviewDefect = null;

    [RelayCommand]
    private async Task DeleteDefectAsync(Defect? defect)
    {
        if (defect is null) return;
        await _db.DeleteDefectAsync(defect);
        ActiveDefects.Remove(defect);
        if (PreviewDefect == defect) PreviewDefect = null;
        StatusMessage = $"Kusur silindi (kalan: {DefectCount})";
    }

    // ----- Robot / MQTT -----

    [RelayCommand]
    private async Task ConnectRobotAsync()
    {
        if (_settings is null) await LoadSettingsAsync();
        try
        {
            IsBusy = true;
            if (_mqtt is not null)
            {
                await _mqtt.ConnectAsync(_settings!.BrokerHost, _settings.BrokerPort,
                    _settings.BrokerUsername, _settings.BrokerPassword);
                await _telemetry.SubscribeAsync();
            }
            else
            {
                await _robot.ConnectAsync(_settings!.BrokerHost, _settings.BrokerPort);
            }
            StatusMessage = "Broker'a bağlandı";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Bağlantı başarısız: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DisconnectRobotAsync()
    {
        await _robot.DisconnectAsync();
        StatusMessage = "Broker bağlantısı kesildi";
    }

    // Press-and-hold dpad — robot moves only while the operator's finger/pointer is on the button.
    // Release/exit always publishes Stop. Pattern: command on press, Stop on release/exit.
    [RelayCommand] private Task PressForwardAsync()  => SendAsync(RobotCommandType.MoveForward,  MoveSpeed);
    [RelayCommand] private Task PressBackwardAsync() => SendAsync(RobotCommandType.MoveBackward, MoveSpeed);
    [RelayCommand] private Task PressLeftAsync()     => SendAsync(RobotCommandType.TurnLeft,     MoveSpeed);
    [RelayCommand] private Task PressRightAsync()    => SendAsync(RobotCommandType.TurnRight,    MoveSpeed);
    [RelayCommand] private Task ReleaseDpadAsync()   => SendAsync(RobotCommandType.Stop);

    /// <summary>Emergency stop — same as Stop but with explicit user feedback and StatusMessage flag.</summary>
    [RelayCommand]
    private async Task EmergencyStopAsync()
    {
        StatusMessage = "■ ACİL DURDURMA gönderildi";
        try
        {
            if (_robot.IsConnected)
                await _robot.SendAsync(new RobotCommand(RobotCommandType.Stop));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Acil durdurma gönderilemedi: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ToggleLightAsync()
    {
        if (!_robot.IsConnected)
        {
            StatusMessage = "Broker bağlı değil";
            return;
        }

        var desired = !IsLightOn;

        // Cancel any prior pending ack timer
        _lightAckCts?.Cancel();
        _lightAckCts = new CancellationTokenSource();
        var ct = _lightAckCts.Token;

        IsLightPending = true;
        StatusMessage = desired ? "Işık ON gönderildi, onay bekleniyor..." : "Işık OFF gönderildi, onay bekleniyor...";

        try
        {
            await _robot.SendAsync(new RobotCommand(desired ? RobotCommandType.LightOn : RobotCommandType.LightOff));
        }
        catch (Exception ex)
        {
            IsLightPending = false;
            StatusMessage = $"Komut gönderilemedi: {ex.Message}";
            return;
        }

        // Wait up to 3 seconds for telemetry to reflect the new state.
        try
        {
            await Task.Delay(3000, ct);
            if (!ct.IsCancellationRequested)
            {
                IsLightPending = false;
                StatusMessage = "Işık komutu onaylanmadı (timeout)";
            }
        }
        catch (TaskCanceledException) { /* confirmed via telemetry */ }
    }

    private async Task SendAsync(RobotCommandType type, float? value = null)
    {
        if (!_robot.IsConnected)
        {
            StatusMessage = "Broker bağlı değil";
            return;
        }
        try
        {
            await _robot.SendAsync(new RobotCommand(type, value));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Komut hatası: {ex.Message}";
        }
    }

    // ----- Overlay state for ffmpeg textfiles -----

    private OverlayState BuildOverlayState()
    {
        var tl = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(CompanyName)) tl.AppendLine(CompanyName);
        if (!string.IsNullOrWhiteSpace(ProjectName)) tl.AppendLine(ProjectName);
        if (!string.IsNullOrWhiteSpace(Neighborhood)) tl.AppendLine($"Mahalle: {Neighborhood}");
        if (!string.IsNullOrWhiteSpace(Street)) tl.AppendLine($"Sokak: {Street}");
        if (!string.IsNullOrWhiteSpace(OperatorName)) tl.AppendLine($"Operatör: {OperatorName}");

        var tr = new StringBuilder();
        tr.AppendLine(DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"));
        tr.AppendLine($"Süre: {ElapsedDisplay}");
        if (IsRecording) tr.AppendLine("● KAYIT");
        if (IsInspecting) tr.AppendLine($"INCELEME AKTIF ({DefectCount} kusur)");

        var bl = Telemetry.DistanceMeters is double d ? $"{d:0.00} m" : "— m";

        var br = new StringBuilder();
        if (Telemetry.TiltDegrees is double t) br.AppendLine($"Egim: {t:0.0} derece");
        if (Telemetry.PressureBar is double p) br.AppendLine($"Basinc: {p:0.0} bar");
        if (Telemetry.TemperatureC is double c) br.AppendLine($"Sicaklik: {c:0.0} C");
        if (Telemetry.BatteryPercent is double b) br.AppendLine($"Batarya: {b:0}%");
        if (Telemetry.GasAlarm) br.AppendLine("!! GAZ ALARMI !!");
        if (Telemetry.WaterAlarm) br.AppendLine("!! SU ALARMI !!");

        return new OverlayState(
            TopLeft: tl.ToString().TrimEnd(),
            TopRight: tr.ToString().TrimEnd(),
            BottomLeft: bl,
            BottomRight: br.ToString().TrimEnd());
    }

    private string StorageRoot() => string.IsNullOrWhiteSpace(_settings?.StoragePath)
        ? FileSystem.AppDataDirectory
        : _settings!.StoragePath;

    private string RecordingDirectory()
    {
        var root = StorageRoot();
        return ActiveInspection is null
            ? Path.Combine(root, "recordings")
            : Path.Combine(root, "inspections", ActiveInspection.Id.ToString());
    }

    private string BuildInspectionTitle()
    {
        var parts = new[] { ProjectName, Neighborhood, Street }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        var combined = string.Join(" - ", parts);
        return string.IsNullOrWhiteSpace(combined)
            ? $"İnceleme {DateTime.Now:dd.MM.yyyy HH:mm}"
            : combined;
    }
}
