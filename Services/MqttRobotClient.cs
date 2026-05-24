using System.Buffers;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;
using MyRoboticsInspector.Models;

namespace MyRoboticsInspector.Services;

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

    public bool IsConnected => _client.IsConnected;

    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<MqttApplicationMessageReceivedEventArgs>? MessageReceived;

    public MqttRobotClient()
    {
        var factory = new MqttClientFactory();
        _client = factory.CreateMqttClient();

        _client.ConnectedAsync += _ =>
        {
            ConnectionChanged?.Invoke(this, true);
            return Task.CompletedTask;
        };
        _client.DisconnectedAsync += _ =>
        {
            ConnectionChanged?.Invoke(this, false);
            return Task.CompletedTask;
        };
        _client.ApplicationMessageReceivedAsync += async e =>
        {
            try
            {
                MessageReceived?.Invoke(this, e);
                DataReceived?.Invoke(this, e.ApplicationMessage.Payload.ToArray());
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Mesaj işleme hatası: {ex.Message}");
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
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Broker'a bağlanılamadı: {ex.Message}");
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
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

        try
        {
            await _client.PublishAsync(msg, ct);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Komut yayınlanamadı: {ex.Message}");
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}
