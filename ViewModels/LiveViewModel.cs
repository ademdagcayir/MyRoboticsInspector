using System.Buffers;
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
    private readonly BrokerService _broker;
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

    // ===== Büyük alan görünüm seçici (teşhis ekranı): Çevre Birimleri / Kamera / MQTT Log / Robot-Pano Konsolu =====
    public List<string> BigViewOptions { get; } = new() { "Çevre Birimleri", "Kamera", "MQTT Log", "Robot/Pano Konsolu" };
    [ObservableProperty] private string bigViewMode = "Çevre Birimleri"; // varsayılan: durum paneli
    public bool IsBigStatus  => BigViewMode == "Çevre Birimleri";
    public bool IsBigCamera  => BigViewMode == "Kamera";
    public bool IsBigMqtt    => BigViewMode == "MQTT Log";
    public bool IsBigConsole => BigViewMode == "Robot/Pano Konsolu";
    partial void OnBigViewModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsBigStatus));
        OnPropertyChanged(nameof(IsBigCamera));
        OnPropertyChanged(nameof(IsBigMqtt));
        OnPropertyChanged(nameof(IsBigConsole));
    }

    // ===== Çevre birimi online/offline göstergeleri =====
    // Broker = IsRobotConnected (MQTT bağlı), Joystick = IsGamepadConnected.
    // Robot = telemetri son ~6 sn içinde geldiyse, Pano = pano/log son ~10 sn içinde geldiyse.
    [ObservableProperty] private bool isRobotOnline;
    [ObservableProperty] private bool isPanoOnline;
    [ObservableProperty] private string brokerEndpoint = "—";
    [ObservableProperty] private string? lastPanoMessage;
    [ObservableProperty] private string? lastRobotMessage;
    [ObservableProperty] private string? gamepadDiag;   // XInput slot teşhisi
    private DateTime? _lastPanoAt;
    private DateTime? _lastRobotAt;   // robot/log + robot/diag cevabı da canlılık işareti

    /// <summary>Joystick sökülüp takıldığında elle yeniden bağlama.</summary>
    [RelayCommand]
    private void ReconnectGamepad()
    {
        DisableGamepad();
        EnableGamepad();
        StatusMessage = "Joystick yeniden bağlanıyor…";
    }

    /// <summary>Broker'a (yerel başlat + ) elle yeniden bağlanma.</summary>
    [RelayCommand]
    private async Task ReconnectBroker()
    {
        await EnsureLocalBrokerAsync();
        await AutoConnectBrokerAsync();
    }

    /// <summary>Saat timer'ından çağrılır — robot/pano canlılığını tazeler.</summary>
    private void RefreshPeripheralStatus()
    {
        var now = DateTime.Now;
        // Robot canlı = son ~6 sn içinde TELEMETRİ (accl_x/voltage/...) VEYA robot/log geldiyse.
        // Firmware telemetriyi yalnızca değer değişince yayınlar; robot dururken log/diag akar ama
        // telemetri akmaz — bu yüzden robot/log da canlılık işareti sayılır.
        DateTime? lastRobot = Telemetry.LastUpdate;
        if (_lastRobotAt is DateTime ra && (lastRobot is null || ra > lastRobot)) lastRobot = ra;

        IsRobotOnline = lastRobot is DateTime ru && (now - ru).TotalSeconds < 6;
        IsPanoOnline  = _lastPanoAt is DateTime pu && (now - pu).TotalSeconds < 10;
    }

    // ===== Kamera durumu + Kalkış öncesi "Sistem Hazır" kontrolü =====
    /// <summary>Kamera bağlı = canlı kare akıyor (RTSP yayını çalışıyor).</summary>
    public bool IsCameraOnline => HasLiveFrame;

    /// <summary>Kalkışa hazır: kamera + broker + robot + joystick tamam.</summary>
    public bool SystemReady => HasLiveFrame && IsRobotConnected && IsRobotOnline && IsGamepadConnected;

    public string SystemReadyText => SystemReady
        ? "✓ SİSTEM HAZIR — Kalkışa hazır"
        : "⚠ Sistem hazır değil — eksik birimleri tamamlayın";

    private void RaiseReadiness()
    {
        OnPropertyChanged(nameof(IsCameraOnline));
        OnPropertyChanged(nameof(SystemReady));
        OnPropertyChanged(nameof(SystemReadyText));
    }

    partial void OnHasLiveFrameChanged(bool value)       => RaiseReadiness();
    partial void OnIsRobotConnectedChanged(bool value)   => RaiseReadiness();
    partial void OnIsRobotOnlineChanged(bool value)      => RaiseReadiness();
    partial void OnIsGamepadConnectedChanged(bool value) => RaiseReadiness();

    // ===== Cihaz Konsolu (robot/log + pano/log) — uzaktan Serial Monitor =====
    public ObservableCollection<DeviceLogEntry> DeviceLog { get; } = new();
    [ObservableProperty] private bool showDeviceConsole = true;
    private const int DeviceLogMaxEntries = 300;

    [RelayCommand] private void ToggleDeviceConsole() => ShowDeviceConsole = !ShowDeviceConsole;
    [RelayCommand] private void ClearDeviceLog() => DeviceLog.Clear();

    // ===== Yerel broker (Mosquitto) — uygulama içinden başlat/durdur =====
    [ObservableProperty] private bool isBrokerRunning;

    [RelayCommand]
    private async Task ToggleBroker()
    {
        if (IsBrokerRunning)
        {
            await _broker.StopAsync();
            StatusMessage = "Broker durduruldu";
            return;
        }
        if (BrokerService.FindMosquitto() is null)
        {
            StatusMessage = "Mosquitto kurulu değil — winget install -e --id EclipseFoundation.Mosquitto";
            return;
        }
        StatusMessage = "Broker başlatılıyor...";
        if (!await _broker.StartAsync())
        {
            StatusMessage = "Broker başlatılamadı (port 1883 dolu olabilir)";
            return;
        }
        await Task.Delay(800);          // mosquitto ayağa kalksın
        await AutoConnectBrokerAsync(); // PC'yi otomatik broker'a bağla
    }

    // ===== Sürekli teşhis (toggle): basınca 500 ms'de bir robot/diag · pano/diag yayınlar =====
    // Joystick komutlarına robot/pano nasıl tepki veriyor canlı izlemek için. Tekrar basınca durur.
    [ObservableProperty] private bool isRobotDiagPolling;
    [ObservableProperty] private bool isPanoDiagPolling;

    public string RobotDiagButtonText => IsRobotDiagPolling ? "■ Robot Teşhis Durdur" : "🤖 Robot Teşhis";
    public string PanoDiagButtonText  => IsPanoDiagPolling  ? "■ Pano Teşhis Durdur"  : "🛠 Pano Teşhis";

    partial void OnIsRobotDiagPollingChanged(bool value) => OnPropertyChanged(nameof(RobotDiagButtonText));
    partial void OnIsPanoDiagPollingChanged(bool value)  => OnPropertyChanged(nameof(PanoDiagButtonText));

    [RelayCommand]
    private void RunRobotDiagnostics()
    {
        if (_mqtt is null || !_mqtt.IsConnected) { StatusMessage = "Broker bağlı değil"; return; }
        IsRobotDiagPolling = !IsRobotDiagPolling;
        StatusMessage = IsRobotDiagPolling
            ? "Robot teşhis: 500 ms'de bir gönderiliyor — tekrar bas durdur"
            : "Robot teşhis durduruldu";
        if (IsRobotDiagPolling) _ = RequestDiagnosticsAsync("robot/diag", "Robot"); // ilk istek hemen
    }

    [RelayCommand]
    private void RunPanoDiagnostics()
    {
        if (_mqtt is null || !_mqtt.IsConnected) { StatusMessage = "Broker bağlı değil"; return; }
        IsPanoDiagPolling = !IsPanoDiagPolling;
        StatusMessage = IsPanoDiagPolling
            ? "Pano teşhis: 500 ms'de bir gönderiliyor — tekrar bas durdur"
            : "Pano teşhis durduruldu";
        if (IsPanoDiagPolling) _ = RequestDiagnosticsAsync("pano/diag", "Pano"); // ilk istek hemen
    }

    // ===== Elle yayın (bring-up testi): joystick olmadan bilinen komut gönder =====
    // Robotu izole test eder — "firmware komutu harekete çeviriyor mu?" sorusunu joystick eşlemesinden ayırır.
    public List<string> ManualTopicOptions { get; } = new()
    {
        FirmwareTopics.ForwardBackward, FirmwareTopics.LeftRight, FirmwareTopics.Brake,
        FirmwareTopics.Head180UpDown, FirmwareTopics.Head360CwCcw, FirmwareTopics.Led,
        FirmwareTopics.Gerisarma, FirmwareTopics.MetreSifir,
    };
    [ObservableProperty] private string manualTopic = FirmwareTopics.ForwardBackward;
    [ObservableProperty] private string manualPayload = "-100";

    /// <summary>Seçili topic'e elle ham değer yayınlar (Entry'den) — protokol seviyesi tek-atış test.</summary>
    [RelayCommand]
    private Task ManualPublishCustom() => PublishRawSafeAsync(ManualTopic, (ManualPayload ?? "").Trim());

    /// <summary>
    /// Test sürüşü — streamer üzerinden SÜREKLİ yayınlar (watchdog beslenir, robot DUR'a kadar hareket eder).
    /// Firmware doğru işaret/eşiğini streamer uygular: İleri→fb=-100, Geri→+100, Sağ→lr=-100, Sol→+100.
    /// </summary>
    [RelayCommand]
    private void TestDrive(string dir)
    {
        if (_mqtt is null || !_mqtt.IsConnected) { StatusMessage = "Broker bağlı değil"; return; }
        switch (dir)
        {
            case "fwd":   _drive.SetMove(RobotCommandType.MoveForward, 1f);  StatusMessage = "TEST: İleri (fb=-100) — DUR'a bas"; break;
            case "back":  _drive.SetMove(RobotCommandType.MoveBackward, 1f); StatusMessage = "TEST: Geri (fb=+100) — DUR'a bas";  break;
            case "left":  _drive.SetMove(RobotCommandType.TurnLeft, 1f);     StatusMessage = "TEST: Sol (lr=+100) — DUR'a bas";    break;
            case "right": _drive.SetMove(RobotCommandType.TurnRight, 1f);    StatusMessage = "TEST: Sağ (lr=-100) — DUR'a bas";    break;
            default:      _ = _drive.StopAsync();                            StatusMessage = "DUR (fb=0, lr=0, brake=1)";          break;
        }
    }

    private async Task PublishRawSafeAsync(string topic, string payload)
    {
        if (_mqtt is null || !_mqtt.IsConnected) { StatusMessage = "Broker bağlı değil"; return; }
        if (string.IsNullOrWhiteSpace(topic)) { StatusMessage = "Topic boş"; return; }
        try
        {
            await _mqtt.PublishRawAsync(topic, payload);
            StatusMessage = $"Yayınlandı: {topic} = {payload}";
        }
        catch (Exception ex) { StatusMessage = $"Yayın hatası: {ex.Message}"; }
    }

    private async Task RequestDiagnosticsAsync(string topic, string label)
    {
        if (_mqtt is null || !_mqtt.IsConnected) return;
        try { await _mqtt.PublishRawAsync(topic, "1"); }
        catch { /* geçici hata — sürekli teşhis bir sonraki tick'te yeniden dener */ }
    }

    /// <summary>robot/log ve pano/log topic'lerine abone olur (broker bağlanınca çağrılır).</summary>
    private async Task SubscribeDeviceLogsAsync()
    {
        if (_mqtt is null || !_mqtt.IsConnected) return;
        try
        {
            await _mqtt.SubscribeAsync("robot/log");
            await _mqtt.SubscribeAsync("pano/log");
            await _mqtt.SubscribeAsync(FirmwareTopics.RobotAlive); // canlılık heartbeat
            await _mqtt.SubscribeAsync(FirmwareTopics.PanoAlive);
        }
        catch { /* abone olunamadıysa log gelmez ama akış kritik değil */ }
    }

    /// <summary>Cihaz log topic'lerinden (robot/log, pano/log) gelen mesajları konsola ekler.</summary>
    private void OnDeviceMessage(object? sender, MQTTnet.MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;

        // Heartbeat — yalnızca canlılık işareti, konsola YAZMA (temiz kalsın)
        if (topic == FirmwareTopics.RobotAlive) { _lastRobotAt = DateTime.Now; return; }
        if (topic == FirmwareTopics.PanoAlive)  { _lastPanoAt  = DateTime.Now; return; }

        string? source = topic switch
        {
            "robot/log" => "robot",
            "pano/log"  => "pano",
            _ => null
        };
        if (source is null) return;

        var msg = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray());
        if (source == "pano")       _lastPanoAt  = DateTime.Now;   // pano canlılık işareti
        else if (source == "robot") _lastRobotAt = DateTime.Now;   // robot canlılık işareti (log/diag)
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (source == "pano") LastPanoMessage = msg;
            else if (source == "robot") LastRobotMessage = msg;
            DeviceLog.Insert(0, new DeviceLogEntry(DateTime.Now, source, msg)); // en yeni üstte
            while (DeviceLog.Count > DeviceLogMaxEntries)
                DeviceLog.RemoveAt(DeviceLog.Count - 1);
        });
    }

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
                        RobotDriveStreamer drive, BrokerService broker)
    {
        _video = video;
        _sync = sync;
        _pipeline = pipeline;
        _recorder = recorder;
        _recorder.Error += (_, m) => MainThread.BeginInvokeOnMainThread(() => StatusMessage = $"Kayıt: {m}");
        // Kayıt ffmpeg'i beklenmedik durursa: REC durumunu kapat + kullanıcıyı uyar (sebebi göster)
        _recorder.RecordingFailed += (_, diag) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            _pipeline.ShowRecBadge = false;
            _pipeline.RecordingStartedAt = null;
            IsRecording = false;
            StatusMessage = "⚠ Kayıt beklenmedik şekilde durdu";
            try { await Shell.Current.DisplayAlert("Kayıt durdu", diag, "Tamam"); } catch { }
        });
        _robot = robot;
        _mqtt = robot as MqttRobotClient;
        _telemetry = telemetry;
        _db = db;
        _gamepad = gamepad;
        _gamepadMapper = gamepadMapper;
        _drive = drive;
        _drive.StreamError += (_, msg) => MainThread.BeginInvokeOnMainThread(() => StatusMessage = msg);
        _broker = broker;
        _broker.StatusChanged += (_, running) => MainThread.BeginInvokeOnMainThread(() => IsBrokerRunning = running);
        Title = "Canlı Görüntü";

        _robot.ConnectionChanged += (_, c) => MainThread.BeginInvokeOnMainThread(() => IsRobotConnected = c);
        _robot.ErrorOccurred += (_, e) => MainThread.BeginInvokeOnMainThread(() => StatusMessage = e);

        // MQTT traffic log
        if (_mqtt is not null)
        {
            _mqtt.TrafficLogged += (_, entry) => MainThread.BeginInvokeOnMainThread(() =>
            {
                MqttLog.Insert(0, entry); // en yeni üstte
                while (MqttLog.Count > MqttLogMaxEntries)
                    MqttLog.RemoveAt(MqttLog.Count - 1);
            });
            _mqtt.MessageReceived += OnDeviceMessage; // robot/log + pano/log → Cihaz Konsolu
        }
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
        BrokerEndpoint = $"{_settings.BrokerHost}:{_settings.BrokerPort}";

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

        // Yerel Mosquitto broker'ı otomatik başlat (host local + kurulu ise)
        await EnsureLocalBrokerAsync();

        // Broker otomatik bağlan (henüz bağlı değilse)
        if (_mqtt is not null && !_mqtt.IsConnected)
            await AutoConnectBrokerAsync();
    }

    /// <summary>
    /// Host yerel (127.0.0.1/localhost) ve Mosquitto kuruluysa, broker sürecini açılışta
    /// otomatik başlatır. Zaten çalışıyorsa (port dolu) StartAsync false döner; sorun değil,
    /// AutoConnect mevcut broker'a bağlanır. Uzak broker'da hiçbir şey başlatılmaz.
    /// </summary>
    private async Task EnsureLocalBrokerAsync()
    {
        if (_settings is null) return;
        var host = _settings.BrokerHost?.Trim().ToLowerInvariant();
        bool isLocal = host is "127.0.0.1" or "localhost" or "::1" or "0.0.0.0" or "";
        if (!isLocal) return;            // uzak broker → başlatma
        if (IsBrokerRunning) return;     // zaten bizim sürecimiz çalışıyor
        if (BrokerService.FindMosquitto() is null) return; // kurulu değil → AutoConnect yine de dener
        try
        {
            await _broker.StartAsync();  // zaten çalışıyorsa port dolu → false döner, yoksayılır
            await Task.Delay(600);       // mosquitto ayağa kalksın
        }
        catch { /* başlatılamazsa AutoConnect zaten hata mesajı verir */ }
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
            await SubscribeDeviceLogsAsync();
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
    private int _diagTickCounter;   // sürekli teşhis: 100ms tick sayacı (5 = ~500ms)

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
            RefreshPeripheralStatus();
        };
        _elapsedTimer.Start();

        // 100ms timer — joystick UI göstergesi (CurrentState doğrudan okunur, event yok)
        _gamepadUiTimer = dispatcher.CreateTimer();
        _gamepadUiTimer.Interval = TimeSpan.FromMilliseconds(100);
        _gamepadUiTimer.Tick += (_, _) =>
        {
            // Sürekli teşhis: 100 ms timer'ın her 5. tick'i = ~500 ms → robot/diag · pano/diag yayınla.
            if (IsRobotDiagPolling || IsPanoDiagPolling)
            {
                if (++_diagTickCounter >= 5)
                {
                    _diagTickCounter = 0;
                    if (IsRobotDiagPolling) _ = RequestDiagnosticsAsync("robot/diag", "Robot");
                    if (IsPanoDiagPolling)  _ = RequestDiagnosticsAsync("pano/diag", "Pano");
                }
            }

            GamepadDiag = _gamepad.SlotSummary;   // XInput slot teşhisi (her tick)
            var s = _gamepad.CurrentState;

            // Canlı bağlantı durumunu yansıt (ConnectionChanged event'i kaçsa bile dot doğru olsun)
            if (IsGamepadConnected != s.IsConnected)
            {
                IsGamepadConnected = s.IsConnected;
                GamepadStatusText = s.IsConnected
                    ? (IsGamepadActive ? "🎮 Joystick: Aktif" : "🎮 Joystick: Bağlı (kapalı)")
                    : "🎮 Joystick: Yok";
            }

            if (!s.IsConnected)
            {
                // Bağlı değil → değerleri sıfırla (eski veri ekranda kalmasın)
                GamepadLX = GamepadLY = GamepadRX = GamepadRY = GamepadLT = GamepadRT = 0f;
                GamepadButtons = "—";
                return;
            }

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
        _pipeline.RecordingStartedAt = null;
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
        _pipeline.RecordingStartedAt = null;
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
            _pipeline.RecordingStartedAt = DateTime.Now; // video zaman sayacı 00:00'dan başlasın
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
        _pipeline.RecordingStartedAt = null;
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
                await SubscribeDeviceLogsAsync();
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

        // Yeni düzen: {root}/projeler/{ProjeAdı}/{Mahalle}/{Sokak}/video/_{Kanal}_{Sokak}_{GB}_{ÇB}.mp4
        var root = StorageRoot();
        var dir  = StoragePaths.VideoDir(root, project.Title, project.Neighborhood, channel.Street);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir,
            StoragePaths.VideoFileName(channel.KanalNo, channel.Street,
                                       channel.EntryManhole, channel.ExitManhole,
                                       $"kanal_{channel.KanalNo ?? channel.Id}"));

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
            _pipeline.RecordingStartedAt = DateTime.Now; // video zaman sayacı 00:00'dan başlasın
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
        _pipeline.RecordingStartedAt = null;
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
