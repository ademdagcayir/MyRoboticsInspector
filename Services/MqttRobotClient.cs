using System.Buffers;
using System.Text;
using System.Text.Json;
using MQTTnet;
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
///   robot -> PC: myrobotics/robot1/status (retain)   (online/offline; LWT publishes "offline")
///
/// Safety: a Last Will & Testament (LWT) publishes an emergency STOP command to the cmd topic
/// if the PC disconnects unexpectedly — the robot firmware must subscribe and act on it.
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

        _client.ConnectedAsync += _ =>
        {
            ConnectionChanged?.Invoke(this, true);
            Log(MqttLogDirection.Info, "broker", "Bağlantı kuruldu");
            return Task.CompletedTask;
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
        var willPayload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { cmd = "Stop", reason = "pc_disconnected" }));

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId($"pc-{Environment.MachineName}-{Guid.NewGuid().ToString("N")[..6]}")
            .WithWillTopic(_cmdTopic)
            .WithWillPayload(willPayload)
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
        var willPayload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { cmd = "Stop", reason = "pc_disconnected" }));

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId($"pc-{Environment.MachineName}-{Guid.NewGuid().ToString("N")[..6]}")
            .WithWillTopic(_cmdTopic)
            .WithWillPayload(willPayload)
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
        if (!_client.IsConnected) return;
        await _client.SubscribeAsync(topic, MqttQualityOfServiceLevel.AtLeastOnce, ct);
    }

    public async Task SendAsync(RobotCommand command, CancellationToken ct = default)
    {
        if (!_client.IsConnected) throw new InvalidOperationException("Broker'a bağlı değil.");

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            cmd = command.Type.ToString(),
            value = command.Value,
            payload = command.Payload,
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(_cmdTopic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        var payloadStr = Encoding.UTF8.GetString(payload);
        try
        {
            await _client.PublishAsync(msg, ct);
            Log(MqttLogDirection.TX, _cmdTopic, payloadStr);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Komut yayınlanamadı: {ex.Message}");
            Log(MqttLogDirection.Error, _cmdTopic, ex.Message);
            throw;
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
