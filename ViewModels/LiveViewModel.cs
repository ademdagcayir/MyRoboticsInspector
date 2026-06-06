using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRoboticsInspector.Models;
using MyRoboticsInspector.Services;

namespace MyRoboticsInspector.ViewModels;

public partial class LiveViewModel : BaseViewModel
{
    private readonly VideoService _video;
    private readonly TelemetrySyncBuffer _sync;
    private readonly SyncedVideoPipeline _pipeline;
    private readonly FfmpegOverlayRecorder _recorder;
    private string? _pipelineRecordFile;
    private readonly IRobotProtocol _robot;

    /// <summary>Birleşik senkron video pipeline'ı — MainPage SKCanvasView buradan çizer.</summary>
    public SyncedVideoPipeline Pipeline => _pipeline;
    private readonly MqttRobotClient? _mqtt;
    private readonly TelemetryService _telemetry;
    private readonly DatabaseService _db;
    private readonly IGamepadInput _gamepad;
    private readonly GamepadCommandMapper _gamepadMapper;
    private readonly RobotDriveStreamer _drive;
    private AppSettings? _settings;
    private DateTime? _streamStartedAt;
    private IDispatcherTimer? _elapsedTimer;
    private CancellationTokenSource? _lightAckCts;

    public VideoService VideoService => _video;
    public TelemetryService Telemetry => _telemetry;

    // Canlı kamera durumu (composite kare SKCanvasView'da gösterilir)
    [ObservableProperty] private bool hasLiveFrame;

    // Son kaydedilen dosya yolu
    [ObservableProperty] private string? lastRecordingPath;
    public bool HasLastRecording => !string.IsNullOrEmpty(LastRecordingPath) && File.Exists(LastRecordingPath);

    // FFmpeg kayıt durumu — ProjectChannelsViewModel diagnostics için
    public bool   IsFfmpegRecording  => _recorder.IsRecording;
    public string? FfmpegLastError   => _recorder.LastError;
    public string? RecordingRtspUrl  => string.IsNullOrWhiteSpace(_settings?.RecordingRtspUrl)
                                            ? _settings?.RtspUrl
                                            : _settings?.RecordingRtspUrl;

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

    // Gamepad anlık girdi göstergesi
    [ObservableProperty] private string gamepadButtons = "—";
    [ObservableProperty] private float gamepadLX;
    [ObservableProperty] private float gamepadLY;
    [ObservableProperty] private float gamepadRX;
    [ObservableProperty] private float gamepadRY;
    [ObservableProperty] private float gamepadLT;
    [ObservableProperty] private float gamepadRT;

    // Overlay-bound fields (XAML side)
    [ObservableProperty] private string? projectName;
    [ObservableProperty] private string? neighborhood;
    [ObservableProperty] private string? street;
    [ObservableProperty] private string? operatorName;
    [ObservableProperty] private string? companyName;
    [ObservableProperty] private string elapsedDisplay = "00:00:00";
    [ObservableProperty] private string nowDisplay = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");

    // MQTT traffic log
    public ObservableCollection<MqttLogEntry> MqttLog { get; } = new();
    [ObservableProperty] private bool showMqttLog;
    private const int MqttLogMaxEntries = 200;

    [RelayCommand]
    private void ToggleMqttLog() => ShowMqttLog = !ShowMqttLog;

    [RelayCommand]
    private void ClearMqttLog() => MqttLog.Clear();

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

    partial void OnLastRecordingPathChanged(string? value)
        => OnPropertyChanged(nameof(HasLastRecording));

    public LiveViewModel(VideoService video,
                        TelemetrySyncBuffer sync, SyncedVideoPipeline pipeline,
                        FfmpegOverlayRecorder recorder,
                        IRobotProtocol robot,
                        TelemetryService telemetry, DatabaseService db,
                        IGamepadInput gamepad, GamepadCommandMapper gamepadMapper,
                        RobotDriveStreamer drive)
    {
        _video = video;
        _sync = sync;
        _pipeline = pipeline;
        _recorder = recorder;
        _recorder.Error += (_, m) => MainThread.BeginInvokeOnMainThread(() => StatusMessage = $"Kayıt: {m}");
        _robot = robot;
        _mqtt = robot as MqttRobotClient;
        _telemetry = telemetry;
        _db = db;
        _gamepad = gamepad;
        _gamepadMapper = gamepadMapper;
        _drive = drive;
        _drive.StreamError += (_, msg) => MainThread.BeginInvokeOnMainThread(() => StatusMessage = msg);
        Title = "Canlı Görüntü";

        _robot.ConnectionChanged += (_, c) => MainThread.BeginInvokeOnMainThread(() => IsRobotConnected = c);
        _robot.ErrorOccurred += (_, e) => MainThread.BeginInvokeOnMainThread(() => StatusMessage = e);

        // MQTT traffic log
        if (_mqtt is not null)
            _mqtt.TrafficLogged += (_, entry) => MainThread.BeginInvokeOnMainThread(() =>
            {
                MqttLog.Insert(0, entry); // en yeni üstte
                while (MqttLog.Count > MqttLogMaxEntries)
                    MqttLog.RemoveAt(MqttLog.Count - 1);
            });
        _video.Error += (_, e) => MainThread.BeginInvokeOnMainThread(() => StatusMessage = e);

        // Birleşik senkron pipeline — ilk kare gelince placeholder gizlenir; hata olunca durumu sıfırla
        _pipeline.Error += (_, e) => MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusMessage = $"Kamera: {MaskCreds(e)}";
            if (IsStreaming) { IsStreaming = false; HasLiveFrame = false; }
        });
        _pipeline.FrameReady += (_, _) =>
        {
            if (!HasLiveFrame) MainThread.BeginInvokeOnMainThread(() => HasLiveFrame = true);
        };

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
                if (!connected)
                {
                    GamepadButtons = "—";
                    GamepadLX = GamepadLY = GamepadRX = GamepadRY = GamepadLT = GamepadRT = 0f;
                }
            });

        // Anlık girdi göstergesi — timer ile CurrentState doğrudan okunur, event yok

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
        // NOT: _sync.AddMeters artık TelemetryService'in MQTT receive callback'inde (MainThread'den
        // ÖNCE) yapılıyor → UI dispatch jitter'ı yok, kare↔metre senkronu daha kesin.

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
        // LibVLC'yi sayfa görünür olduğunda init et — constructor'da hata olursa
        // tüm sayfa oluşmaz, burada try/catch güvenli.
        _video.Initialize();

        _settings = await _db.GetSettingsAsync();
        ProjectName = _settings.ProjectName;
        Neighborhood = _settings.Neighborhood;
        Street = _settings.Street;
        OperatorName = _settings.OperatorName;
        CompanyName = _settings.CompanyName;

        // Pipeline overlay statik alanları + metre↔kare gecikme telafisi
        _pipeline.Overlay.CompanyName = CompanyName;
        _pipeline.Overlay.ProjectName = ProjectName;
        _pipeline.Overlay.Location = JoinNonEmpty(" / ", Neighborhood, Street);
        _pipeline.Overlay.ManholeIn = null;
        _pipeline.Overlay.ManholeOut = null;
        _pipeline.Overlay.FlowText = null;
        _pipeline.OffsetSeconds = Math.Clamp(_settings.MeterSyncOffsetMs, 0, 3000) / 1000.0;
        _pipeline.ConfigureOverlayFont(_settings.OverlayFontFamily, _settings.OverlayFontScale);

        _mqtt?.Configure(_settings.TopicPrefix, _settings.RobotId);

        var customers = await _db.GetCustomersAsync();
        Customers.Clear();
        foreach (var c in customers) Customers.Add(c);

        StartClock();

        // Gamepad otomatik başlat
        if (_settings.GamepadAutoStart && !IsGamepadActive)
            EnableGamepad();

        // Yayını otomatik başlat (henüz çalışmıyorsa)
        if (!IsStreaming)
            await StartStreamAsync();

        // Broker otomatik bağlan (henüz bağlı değilse)
        if (_mqtt is not null && !_mqtt.IsConnected)
            await AutoConnectBrokerAsync();
    }

    private async Task AutoConnectBrokerAsync()
    {
        if (_settings is null || _mqtt is null) return;
        if (_mqtt.IsConnected) return;
        try
        {
            StatusMessage = $"Broker bağlanıyor → {_settings.BrokerHost}:{_settings.BrokerPort}";
            await _mqtt.ConnectAsync(
                _settings.BrokerHost, _settings.BrokerPort,
                _settings.BrokerUsername, _settings.BrokerPassword);
            await _telemetry.SubscribeAsync();
            StatusMessage = "Broker bağlantısı kuruldu";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Broker bağlanamadı: {ex.Message}";
        }
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

    private IDispatcherTimer? _gamepadUiTimer;

    private void StartClock()
    {
        if (_elapsedTimer is not null) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        // Saniye timer — saat + overlay
        _elapsedTimer = dispatcher.CreateTimer();
        _elapsedTimer.Interval = TimeSpan.FromSeconds(1);
        _elapsedTimer.Tick += (_, _) =>
        {
            NowDisplay = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");
            if (_streamStartedAt is DateTime t)
                ElapsedDisplay = (DateTime.Now - t).ToString(@"hh\:mm\:ss");
        };
        _elapsedTimer.Start();

        // 100ms timer — joystick UI göstergesi (CurrentState doğrudan okunur, event yok)
        _gamepadUiTimer = dispatcher.CreateTimer();
        _gamepadUiTimer.Interval = TimeSpan.FromMilliseconds(100);
        _gamepadUiTimer.Tick += (_, _) =>
        {
            if (!IsGamepadActive) return;
            var s = _gamepad.CurrentState;
            if (!s.IsConnected) return;

            GamepadLX = s.LeftStickX;
            GamepadLY = s.LeftStickY;
            GamepadRX = s.RightStickX;
            GamepadRY = s.RightStickY;
            GamepadLT = s.LeftTrigger;
            GamepadRT = s.RightTrigger;

            var btns = new System.Text.StringBuilder();
            if (s.IsDown(GamepadButton.DPadUp))    btns.Append("↑ ");
            if (s.IsDown(GamepadButton.DPadDown))  btns.Append("↓ ");
            if (s.IsDown(GamepadButton.DPadLeft))  btns.Append("← ");
            if (s.IsDown(GamepadButton.DPadRight)) btns.Append("→ ");
            if (s.IsDown(GamepadButton.A))         btns.Append("A ");
            if (s.IsDown(GamepadButton.B))         btns.Append("B ");
            if (s.IsDown(GamepadButton.X))         btns.Append("X ");
            if (s.IsDown(GamepadButton.Y))         btns.Append("Y ");
            if (s.IsDown(GamepadButton.LB))        btns.Append("LB ");
            if (s.IsDown(GamepadButton.RB))        btns.Append("RB ");
            if (s.IsDown(GamepadButton.LT))        btns.Append("LT ");
            if (s.IsDown(GamepadButton.RT))        btns.Append("RT ");
            if (s.IsDown(GamepadButton.Start))     btns.Append("START ");
            if (s.IsDown(GamepadButton.Back))      btns.Append("BACK ");
            if (s.IsDown(GamepadButton.LStick))    btns.Append("L3 ");
            if (s.IsDown(GamepadButton.RStick))    btns.Append("R3 ");
            GamepadButtons = btns.Length > 0 ? btns.ToString().TrimEnd() : "—";
        };
        _gamepadUiTimer.Start();
    }

    // ----- Stream / Recording -----

    [RelayCommand]
    private async Task StartStreamAsync()
    {
        // Ayarlar henüz yüklenmediyse yükle
        if (_settings is null)
        {
            StatusMessage = "Ayarlar yükleniyor...";
            await LoadSettingsAsync();
        }

        if (_settings is null)
        {
            StatusMessage = "⚠ Ayarlar yüklenemedi";
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.RtspUrl))
        {
            StatusMessage = "⚠ RTSP adresi boş — Ayarlar sayfasından girin (Ör: rtsp://192.168.1.100:554/stream)";
            return;
        }

        StatusMessage = "FFmpeg aranıyor...";
        IsBusy = true;

        string? ffmpeg = null;
        try
        {
            // ResolveFfmpeg process spawn + WaitForExit içeriyor — UI thread'ini bloklamayalım
            ffmpeg = await Task.Run(() => FfmpegRecorder.ResolveFfmpeg(_settings.FfmpegPath ?? "ffmpeg"));
        }
        finally
        {
            IsBusy = false;
        }

        if (ffmpeg is null)
        {
            StatusMessage = "⚠ FFmpeg bulunamadı — Ayarlar sayfasına gidip yolunu girin (ffmpeg.exe)";
            return;
        }

        // ÖNİZLEME: SUB akış /102 (düşük gecikme + düşük CPU). Kayıt AYRI bir process'tir
        // (FfmpegOverlayRecorder, /101 4K drawtext) → önizleme kayıttan etkilenmez, DONMAZ.
        var previewUrl = _settings.RtspUrl;
        _pipeline.Start(ffmpeg, previewUrl);
        IsStreaming = true;
        _streamStartedAt = DateTime.Now;
        StatusMessage = $"▶ Bağlanıyor… {MaskCreds(previewUrl)}";
    }

    /// <summary>RTSP/HTTP URL'lerindeki kullanıcı:parola bilgisini maskeler — ekranda/logda ifşa olmasın.</summary>
    private static string MaskCreds(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty :
        System.Text.RegularExpressions.Regex.Replace(s, @"://[^/@\s:]+:[^/@\s]+@", "://***@");

    /// <summary>Boş olmayan parçaları ayraçla birleştirir; hepsi boşsa null döner.</summary>
    private static string? JoinNonEmpty(string sep, params string?[] parts)
    {
        var joined = string.Join(sep, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return string.IsNullOrWhiteSpace(joined) ? null : joined;
    }

    [RelayCommand]
    private async Task StopStreamAsync()
    {
        if (_recorder.IsRecording) await _recorder.StopAsync();
        _pipeline.ShowRecBadge = false;
        _pipeline.Stop();
        _video.Stop();
        IsStreaming = false;
        IsRecording = false;
        HasLiveFrame = false;
        _streamStartedAt = null;
        ElapsedDisplay = "00:00:00";
        StatusMessage = "Yayın durduruldu";
    }

    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        if (_settings is null) await LoadSettingsAsync();
        if (_settings is null) return;

        // Durdur
        if (IsRecording)
        {
            await _recorder.StopAsync();
            _pipeline.ShowRecBadge = false;
            IsRecording = false;
            if (!string.IsNullOrEmpty(_pipelineRecordFile) && File.Exists(_pipelineRecordFile))
            {
                LastRecordingPath = _pipelineRecordFile;
                StatusMessage = $"Kayıt tamamlandı: {Path.GetFileName(_pipelineRecordFile)}";
            }
            else StatusMessage = "Kayıt durduruldu";
            return;
        }

        // Önizleme aktif değilse başlat (önizleme /102; kayıt AYRI /101)
        if (!IsStreaming) await StartStreamAsync();

        var ffmpegPath = await Task.Run(() => FfmpegRecorder.ResolveFfmpeg(_settings.FfmpegPath ?? "ffmpeg"));
        if (ffmpegPath is null) { StatusMessage = "⚠ FFmpeg bulunamadı"; return; }

        var dir = RecordingDirectory();
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"kayit_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

        // TEK-GEÇİŞ drawtext kayıt: /101 4K → overlay gömülü → MP4 (tam kalite, senkron, donma yok)
        var rtsp4k = string.IsNullOrWhiteSpace(_settings.RecordingRtspUrl) ? _settings.RtspUrl : _settings.RecordingRtspUrl;
        double recOffset = Math.Clamp(_settings.RecordingMeterSyncOffsetMs, 0, 3000) / 1000.0;
        StatusMessage = "● Kayıt başlatılıyor…";
        if (await _recorder.StartAsync(ffmpegPath, rtsp4k, file, _pipeline.Overlay,
                _settings.OverlayFontFamily, _settings.OverlayFontScale, recOffset, _settings.RecordingEncoder))
        {
            _pipelineRecordFile = file;
            _pipeline.ShowRecBadge = true;
            IsRecording = true;
            _streamStartedAt ??= DateTime.Now;
            StatusMessage = $"● Kayıt: {Path.GetFileName(file)}";

            if (ActiveInspection is not null && string.IsNullOrEmpty(ActiveInspection.VideoPath))
            {
                ActiveInspection.VideoPath = file;
                await _db.SaveInspectionAsync(ActiveInspection);
            }
        }
    }

    [RelayCommand]
    private async Task SnapshotAsync()
    {
        if (_settings is null) await LoadSettingsAsync();

        var frame = _pipeline.Snapshot();
        if (frame is null)
        {
            StatusMessage = "Snapshot alınamadı — yayın aktif değil";
            return;
        }

        try
        {
            var dir = Path.Combine(StorageRoot(), "snapshots");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"snap_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
            await File.WriteAllBytesAsync(path, frame);
            StatusMessage = $"Görüntü kaydedildi: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Snapshot kaydedilemedi: {ex.Message}";
        }
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
        if (_recorder.IsRecording) await _recorder.StopAsync();
        _pipeline.ShowRecBadge = false;
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

        // Anlık composite kareyi (overlay gömülü) dosyaya kaydet
        var frame = _pipeline.Snapshot();
        if (frame is not null)
        {
            try
            {
                var dir = Path.Combine(StorageRoot(), "inspections", ActiveInspection.Id.ToString(), "defects");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"defect_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
                File.WriteAllBytes(path, frame);
                EditingDefectSnapshotPath = path;
            }
            catch { EditingDefectSnapshotPath = null; }
        }
        else
        {
            EditingDefectSnapshotPath = null;
        }

        EditingDefect = new Defect
        {
            InspectionId = ActiveInspection.Id,
            VideoTimestampMs = 0,  // MediaPlayer no longer available
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
        // Video seek not available (MediaPlayer no longer available)
        // The snapshot is still displayed as a fallback
    }

    [RelayCommand]
    private void ClosePreview() => PreviewDefect = null;

    [RelayCommand]
    private void OpenLastRecording()
    {
        if (string.IsNullOrEmpty(LastRecordingPath) || !File.Exists(LastRecordingPath))
        {
            StatusMessage = "Kayıt dosyası bulunamadı";
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LastRecordingPath)
            {
                UseShellExecute = true // Windows varsayılan oynatıcıyla aç
            });
            StatusMessage = $"Açılıyor: {Path.GetFileName(LastRecordingPath)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Açılamadı: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenRecordingsFolder()
    {
        var dir = string.IsNullOrEmpty(LastRecordingPath)
            ? RecordingDirectory()
            : Path.GetDirectoryName(LastRecordingPath) ?? RecordingDirectory();
        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Klasör açılamadı: {ex.Message}";
        }
    }

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

    // Press-and-hold dpad — robot, butona basılı tutulduğu sürece hareket eder. Sürüş komutları
    // RobotDriveStreamer üzerinden gider: basıldığında aktif hareket set edilir (streamer 100 ms'de
    // bir yeniden yayınlar → robot watchdog'u canlı görür), bırakıldığında Stop. Böylece parmak/işaretçi
    // butonda sabit dursa bile robot durmaz (eski "tek komut" davranışının aksine).
    [RelayCommand] private void PressForward()  => _drive.SetMove(RobotCommandType.MoveForward,  MoveSpeed);
    [RelayCommand] private void PressBackward() => _drive.SetMove(RobotCommandType.MoveBackward, MoveSpeed);
    [RelayCommand] private void PressLeft()     => _drive.SetMove(RobotCommandType.TurnLeft,     MoveSpeed);
    [RelayCommand] private void PressRight()    => _drive.SetMove(RobotCommandType.TurnRight,    MoveSpeed);
    [RelayCommand] private Task ReleaseDpadAsync() => _drive.StopAsync();

    /// <summary>Emergency stop — streaming akışını keser ve anında Stop yayınlar.</summary>
    [RelayCommand]
    private async Task EmergencyStopAsync()
    {
        StatusMessage = "■ ACİL DURDURMA gönderildi";
        try
        {
            await _drive.StopAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Acil durdurma gönderilemedi: {ex.Message}";
        }
    }

    // ----- Klavye sürüşü (MainPage code-behind'den çağrılır) -----
    // Tuş basılı tutulduğunda streamer aktif hareketi 100 ms'de bir yeniden yayınlar; tuş
    // bırakıldığında Stop. Gamepad/dpad ile aynı RobotDriveStreamer'ı paylaşır → tek watchdog kaynağı.

    /// <summary>WASD / ok tuşları → sürüş. Bağlı değilse sessizce yok sayar (UI spam'i olmasın).</summary>
    public void KeyboardDrive(RobotCommandType type)
    {
        if (!_robot.IsConnected) return;
        _drive.SetMove(type, MoveSpeed);
    }

    /// <summary>Sürüş tuşu bırakıldı → dur.</summary>
    public Task KeyboardStopAsync() => _drive.StopAsync();

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

    // ── Kanal odaklı kayıt (ProjectChannelsPage'den çağrılır) ──

    /// <summary>
    /// Seçili kanal için kayıt başlatır. Video dosyası kanal klasörüne yazılır,
    /// overlay proje/kanal bilgilerini içerir.
    /// </summary>
    public async Task StartRecordingForChannelAsync(Inspection channel, Job project)
    {
        if (_settings is null) await LoadSettingsAsync();
        if (_settings is null)
        {
            StatusMessage = "⚠ Ayarlar yüklenemedi";
            return;
        }
        if (string.IsNullOrWhiteSpace(_settings.RtspUrl))
        {
            StatusMessage = "⚠ RTSP adresi boş — Ayarlar sayfasından girin";
            return;
        }

        var root = StorageRoot();
        var dir  = Path.Combine(root, "inspections", channel.Id.ToString());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"video_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

        // Overlay alanlarını kanal bilgisiyle doldur (gömülecek metinler)
        ProjectName = project.Title;
        _pipeline.Overlay.ProjectName = project.Title;
        _pipeline.Overlay.Location = JoinNonEmpty(" / ", project.Province, project.District);
        _pipeline.Overlay.ManholeIn = channel.EntryManhole;
        _pipeline.Overlay.ManholeOut = channel.ExitManhole;
        _pipeline.Overlay.FlowText = channel.FlowDirection == Models.FlowDirection.Upstream ? "Akışa ters" : "Akış yönünde";

        ActiveInspection = channel;

        // Eski video dosyasını sil (üzerine yazma onaylandı)
        if (!string.IsNullOrEmpty(channel.VideoPath) && File.Exists(channel.VideoPath))
        {
            try { File.Delete(channel.VideoPath); } catch { /* yoksay */ }
        }

        // Her zaman yeni yolu DB'ye kaydet
        channel.VideoPath = file;
        await _db.SaveInspectionAsync(channel);

        // Önizleme (/102) aktif değilse başlat
        if (!IsStreaming) await StartStreamAsync();

        StatusMessage = "Kayıt başlatılıyor…";
        var ffmpegPath = await Task.Run(() => FfmpegRecorder.ResolveFfmpeg(_settings.FfmpegPath ?? "ffmpeg"));
        if (ffmpegPath is null) { StatusMessage = "⚠ FFmpeg bulunamadı"; return; }

        // TEK-GEÇİŞ drawtext kayıt: /101 4K → overlay (proje/il-ilçe/baca/akış + senkron metre) gömülü → MP4
        var rtsp4k = string.IsNullOrWhiteSpace(_settings.RecordingRtspUrl) ? _settings.RtspUrl : _settings.RecordingRtspUrl;
        double recOffset = Math.Clamp(_settings.RecordingMeterSyncOffsetMs, 0, 3000) / 1000.0;
        if (await _recorder.StartAsync(ffmpegPath, rtsp4k, file, _pipeline.Overlay,
                _settings.OverlayFontFamily, _settings.OverlayFontScale, recOffset, _settings.RecordingEncoder))
        {
            _pipelineRecordFile = file;
            _pipeline.ShowRecBadge = true;
            IsRecording = true;
            _streamStartedAt ??= DateTime.Now;
            StatusMessage = $"● Kayıt: {channel.ChannelCode ?? $"#{channel.Id}"} → {Path.GetFileName(file)}";
        }
        else
        {
            StatusMessage = "⚠ Kayıt başlatılamadı";
        }
    }

    /// <summary>Kanal kaydını durdurur, dosya yolunu LastRecordingPath'e yazar.</summary>
    public async Task StopChannelRecordingAsync()
    {
        try
        {
            await _recorder.StopAsync();
            _pipeline.ShowRecBadge = false;
            if (!string.IsNullOrEmpty(_pipelineRecordFile) && File.Exists(_pipelineRecordFile))
            {
                LastRecordingPath = _pipelineRecordFile;
                StatusMessage = $"Kayıt tamamlandı: {Path.GetFileName(_pipelineRecordFile)}";
            }
            else StatusMessage = "Kayıt durduruldu";
        }
        finally
        {
            IsRecording = false;
        }
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
