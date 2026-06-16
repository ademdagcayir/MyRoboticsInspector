using System.Buffers;
using System.Text;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using MyRoboticsInspector.Models;

namespace MyRoboticsInspector.Services;

public enum MqttLogDirection { TX, RX, Info, Error }

public record MqttLogEntry(
    DateTime Time,
    MqttLogDirection Direction,
    string Topic,
    string Payload)
{
    public string Label => Direction switch
    {
        MqttLogDirection.TX    => "▲ TX",
        MqttLogDirection.RX    => "▼ RX",
        MqttLogDirection.Info  => "● INFO",
        MqttLogDirection.Error => "✕ ERR",
        _ => "?"
    };
    public string TimeStr => Time.ToString("HH:mm:ss.fff");
}

/// <summary>
/// Robot control over MQTT. Implements IRobotProtocol with a broker-based pub/sub model.
///
/// Topics (assuming TopicPrefix="myrobotics", RobotId="robot1"):
///   PC -> robot: myrobotics/robot1/cmd               (JSON command)
///   robot -> PC: myrobotics/robot1/telemetry         (handled by TelemetryService)
///   robot -> PC: myrobotics/robot1/status (retain)   (online/offline)
///
/// Safety: a Last Will & Testament (LWT) publishes brake=1 to the bare firmware topic
/// (joystick/brake) if the PC disconnects unexpectedly — the firmware reads it via atoi()
/// and locks the drive motors (same as SendAsync's Stop path). Known limitation: MQTT allows
/// a single will per connection and the camera-head topics (180_up_down / 360_CW_CCW) have
/// no firmware watchdog, so head motion is NOT fail-safed on PC loss until the firmware
/// extends its watchdog to the head topics.
/// </summary>
public class MqttRobotClient : IRobotProtocol
{
    private readonly IMqttClient _client;
    private string _topicPrefix = "myrobotics";
    private string _robotId = "robot1";
    private string _cmdTopic = "myrobotics/robot1/cmd";

    // Reconnect için son bağlantı parametreleri
    private string? _lastHost;
    private int _lastPort;
    private string? _lastUser;
    private string? _lastPass;

    // İstenen abonelik seti — CleanSession nedeniyle broker her kopuşta abonelikleri
    // unutur; ConnectedAsync her bağlanışta bu seti replay eder. Kasıtlı kesmede de
    // TEMİZLENMEZ: üst katmanlar (örn. TelemetryService'in "_subscribedTopic" guard'ı)
    // elle kopar/bağlan döngüsünde tekrar abone olmayabilir, set sayesinde telemetri
    // ve log abonelikleri her bağlantıda kendiliğinden geri kurulur.
    private readonly HashSet<string> _subscriptions = new();

    public bool IsConnected => _client.IsConnected;

    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<MqttApplicationMessageReceivedEventArgs>? MessageReceived;
    public event EventHandler<MqttLogEntry>? TrafficLogged;

    public MqttRobotClient()
    {
        var factory = new MqttClientFactory();
        _client = factory.CreateMqttClient();

        _client.ConnectedAsync += async _ =>
        {
            // CleanSession ile bağlanıldığı için broker kopuşta TÜM abonelikleri siler —
            // istenen topic'lere yeniden abone ol (ConnectionChanged'den ÖNCE: dinleyiciler
            // "bağlandı" gördüğünde telemetri/log akışı hazır olsun).
            string[] topics;
            lock (_subscriptions) topics = _subscriptions.ToArray();
            foreach (var topic in topics)
            {
                try
                {
                    await _client.SubscribeAsync(topic, MqttQualityOfServiceLevel.AtLeastOnce);
                }
                catch (Exception ex)
                {
                    Log(MqttLogDirection.Error, topic, $"Yeniden abone olunamadı: {ex.Message}");
                }
            }
            ConnectionChanged?.Invoke(this, true);
            Log(MqttLogDirection.Info, "broker", "Bağlantı kuruldu");
        };
        _client.DisconnectedAsync += async args =>
        {
            ConnectionChanged?.Invoke(this, false);
            var reason = args.Exception?.Message ?? args.ReasonString ?? "bağlantı kesildi";
            Log(MqttLogDirection.Info, "broker", $"Bağlantı kesildi — {reason}");

            // Beklenmedik kopuşta otomatik yeniden bağlan (kullanıcı kasıtlı kestiyse _lastHost null olur)
            if (_lastHost is not null && args.ClientWasConnected)
            {
                await Task.Delay(3000); // 3 sn bekle
                try
                {
                    Log(MqttLogDirection.Info, "broker", "Yeniden bağlanılıyor...");
                    await ConnectAsync(_lastHost, _lastPort, _lastUser, _lastPass);
                }
                catch
                {
                    Log(MqttLogDirection.Error, "broker", "Yeniden bağlantı başarısız");
                }
            }
        };
        _client.ApplicationMessageReceivedAsync += async e =>
        {
            try
            {
                var topic = e.ApplicationMessage.Topic;
                var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray());
                Log(MqttLogDirection.RX, topic, payload);
                MessageReceived?.Invoke(this, e);
                DataReceived?.Invoke(this, e.ApplicationMessage.Payload.ToArray());
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Mesaj işleme hatası: {ex.Message}");
                Log(MqttLogDirection.Error, "rx", ex.Message);
            }
            await Task.CompletedTask;
        };
    }

    public void Configure(string topicPrefix, string robotId)
    {
        _topicPrefix = string.IsNullOrWhiteSpace(topicPrefix) ? "myrobotics" : topicPrefix;
        _robotId = string.IsNullOrWhiteSpace(robotId) ? "robot1" : robotId;
        _cmdTopic = $"{_topicPrefix}/{_robotId}/cmd";
    }

    public string CmdTopic => _cmdTopic;
    public string TelemetryTopic => $"{_topicPrefix}/{_robotId}/telemetry";
    public string StatusTopic => $"{_topicPrefix}/{_robotId}/status";

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        // LWT: PC beklenmedik koparsa broker, firmware'in GERÇEKTEN dinlediği bare topic'e
        // brake=1 yayınlar (atoi formatı — JSON değil) → sürüş motorları kilitlenir.
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            // MQTT 3.1.1 ŞART: robot/pano (Arduino PubSubClient) ve eski/gömülü Mosquitto
            // yalnız 3.1.1 konuşur. MQTTnet 5 varsayılanı 5.0 olduğundan açıkça sabitlenir
            // (yoksa eski broker CONNECT'i reddeder → "MQTT client is not connected").
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithClientId($"pc-{Environment.MachineName}-{Guid.NewGuid().ToString("N")[..6]}")
            .WithWillTopic(FirmwareTopics.Brake)
            .WithWillPayload(Encoding.UTF8.GetBytes("1"))
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
            .Build();

        try
        {
            await _client.ConnectAsync(options, ct);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Broker'a bağlanılamadı: {ex.Message}");
            throw;
        }
    }

    public async Task ConnectAsync(string host, int port, string? username, string? password, CancellationToken ct = default)
    {
        // LWT: PC beklenmedik koparsa broker, firmware'in GERÇEKTEN dinlediği bare topic'e
        // brake=1 yayınlar (atoi formatı — JSON değil) → sürüş motorları kilitlenir.
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithProtocolVersion(MqttProtocolVersion.V311) // Arduino/eski Mosquitto uyumu (yukarıya bkz)
            .WithClientId($"pc-{Environment.MachineName}-{Guid.NewGuid().ToString("N")[..6]}")
            .WithWillTopic(FirmwareTopics.Brake)
            .WithWillPayload(Encoding.UTF8.GetBytes("1"))
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(15));

        if (!string.IsNullOrWhiteSpace(username))
            builder = builder.WithCredentials(username, password);

        try
        {
            await _client.ConnectAsync(builder.Build(), ct);
            _lastHost = host; _lastPort = port; _lastUser = username; _lastPass = password;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Broker'a bağlanılamadı: {ex.Message}");
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        _lastHost = null; // kasıtlı kesme — auto-reconnect devreye girmesin
        try
        {
            if (_client.IsConnected) await _client.DisconnectAsync();
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Bağlantı kesilirken hata: {ex.Message}");
        }
    }

    public async Task SubscribeAsync(string topic, CancellationToken ct = default)
    {
        // Topic'i bağlı olunmasa bile sete ekle — ConnectedAsync bağlanınca replay eder.
        lock (_subscriptions) _subscriptions.Add(topic);
        if (!_client.IsConnected)
        {
            // Sessizce yutma: çağıran "abone oldum" sanmasın (SendAsync/PublishRawAsync ile aynı sözleşme).
            Log(MqttLogDirection.Error, topic, "Abone olunamadı — broker'a bağlı değil");
            throw new InvalidOperationException("Broker'a bağlı değil.");
        }
        await _client.SubscribeAsync(topic, MqttQualityOfServiceLevel.AtLeastOnce, ct);
    }

    /// <summary>
    /// RobotCommand'i MyRoboticsFirmware bare-topic protokolüne çevirip yayınlar.
    /// <para>Firmware sürüşü <b>bang-bang + ters işaret</b>tir (robot/yuruyus_ileri_geri.ino):
    /// İLERİ = forward_backward <b>-100</b> (negatif!), GERİ = <b>+100</b>; SAĞ = left_right
    /// <b>-100</b>, SOL = <b>+100</b> ve dönüş için forward_backward=0 şart. Bu yüzden dönüş
    /// komutundan önce fb=0 yayınlanır. JSON yok — firmware atoi() okur.</para>
    /// </summary>
    public async Task SendAsync(RobotCommand command, CancellationToken ct = default)
    {
        if (!_client.IsConnected) throw new InvalidOperationException("Broker'a bağlı değil.");

        switch (command.Type)
        {
            // Sürüş — firmware bang-bang: tam değer (±100), işaret ters.
            case RobotCommandType.MoveForward:  await PublishRawAsync(FirmwareTopics.ForwardBackward, "-100", ct); break;
            case RobotCommandType.MoveBackward: await PublishRawAsync(FirmwareTopics.ForwardBackward, "100",  ct); break;
            case RobotCommandType.TurnRight:    // dönüş için fb=0 olmalı
                await PublishRawAsync(FirmwareTopics.ForwardBackward, "0", ct);
                await PublishRawAsync(FirmwareTopics.LeftRight, "-100", ct);
                break;
            case RobotCommandType.TurnLeft:
                await PublishRawAsync(FirmwareTopics.ForwardBackward, "0", ct);
                await PublishRawAsync(FirmwareTopics.LeftRight, "100", ct);
                break;
            case RobotCommandType.Stop:
                await PublishRawAsync(FirmwareTopics.ForwardBackward, "0", ct);
                await PublishRawAsync(FirmwareTopics.LeftRight, "0", ct);
                await PublishRawAsync(FirmwareTopics.Brake, "1", ct);
                break;
            // Işık — NOT: mevcut firmware joystick/led'i kullanmıyor (ışık A2 basınca göre otomatik).
            // Yine de gönderiyoruz; firmware güncellenince etkili olur.
            case RobotCommandType.LightOn:         await PublishRawAsync(FirmwareTopics.Led, "1", ct); break;
            case RobotCommandType.LightOff:        await PublishRawAsync(FirmwareTopics.Led, "0", ct); break;
            case RobotCommandType.LightBrightness: await PublishRawAsync(FirmwareTopics.Led, ((int)Math.Round(command.Value ?? 0f)).ToString(), ct); break;
            // Kamera kafası — firmware eşiği |değer| ≥ 35.
            case RobotCommandType.CameraPan:       await PublishRawAsync(FirmwareTopics.Head360CwCcw, ((int)Math.Round(command.Value ?? 0f)).ToString(), ct); break;
            case RobotCommandType.CameraTilt:      await PublishRawAsync(FirmwareTopics.Head180UpDown, ((int)Math.Round(command.Value ?? 0f)).ToString(), ct); break;
            default: break; // CameraZoom / Custom — firmware'de karşılığı yok
        }
    }

    /// <summary>
    /// Belirli bir topic'e ham metin yayınlar (cmd dışı topic'ler için — örn. teşhis isteği
    /// robot/diag, pano/diag). SendAsync sadece komut topic'ine yazar; bu genel amaçlıdır.
    /// </summary>
    public async Task PublishRawAsync(string topic, string payload, CancellationToken ct = default)
    {
        if (!_client.IsConnected) throw new InvalidOperationException("Broker'a bağlı değil.");
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        try
        {
            await _client.PublishAsync(msg, ct);
            Log(MqttLogDirection.TX, topic, payload);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Yayın hatası ({topic}): {ex.Message}");
            Log(MqttLogDirection.Error, topic, ex.Message);
            throw;
        }
    }

    private void Log(MqttLogDirection dir, string topic, string payload)
        => TrafficLogged?.Invoke(this, new MqttLogEntry(DateTime.Now, dir, topic, payload));

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}
