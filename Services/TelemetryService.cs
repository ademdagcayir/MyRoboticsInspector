using System.Buffers;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using MQTTnet;
using MyRoboticsInspector.Models;

namespace MyRoboticsInspector.Services;

/// <summary>
/// Listens to the robot's telemetry topic via the shared MqttRobotClient and exposes
/// the latest snapshot as observable properties for UI binding.
/// </summary>
public partial class TelemetryService : ObservableObject
{
    private readonly MqttRobotClient _mqtt;
    private string? _subscribedTopic;

    [ObservableProperty] private double? distanceMeters;
    [ObservableProperty] private double? speed;
    [ObservableProperty] private double? tiltDegrees;
    [ObservableProperty] private double? pressureBar;
    [ObservableProperty] private double? temperatureC;
    [ObservableProperty] private double? humidityPercent;
    [ObservableProperty] private double? batteryPercent;
    [ObservableProperty] private bool gasAlarm;
    [ObservableProperty] private bool waterAlarm;
    [ObservableProperty] private bool? lightOn;
    [ObservableProperty] private string? activeMove;
    [ObservableProperty] private DateTime? lastUpdate;

    public TelemetryService(IRobotProtocol robot)
    {
        // Designed for the MQTT impl. If someone swaps in a non-MQTT IRobotProtocol later,
        // this service is a no-op.
        _mqtt = robot as MqttRobotClient ?? throw new InvalidOperationException(
            "TelemetryService requires an MqttRobotClient implementation of IRobotProtocol.");

        _mqtt.MessageReceived += OnMessage;
    }

    public async Task SubscribeAsync()
    {
        var topic = _mqtt.TelemetryTopic;
        if (_subscribedTopic == topic) return;
        await _mqtt.SubscribeAsync(topic);
        _subscribedTopic = topic;
    }

    private void OnMessage(object? sender, MqttApplicationMessageReceivedEventArgs e)
    {
        if (e.ApplicationMessage.Topic != _mqtt.TelemetryTopic) return;
        try
        {
            var json = e.ApplicationMessage.Payload.ToArray();
            if (json.Length == 0) return;
            var snap = JsonSerializer.Deserialize<TelemetrySnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (snap is null) return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (snap.DistanceMeters.HasValue) DistanceMeters = snap.DistanceMeters;
                if (snap.Speed.HasValue) Speed = snap.Speed;
                if (snap.TiltDegrees.HasValue) TiltDegrees = snap.TiltDegrees;
                if (snap.PressureBar.HasValue) PressureBar = snap.PressureBar;
                if (snap.TemperatureC.HasValue) TemperatureC = snap.TemperatureC;
                if (snap.HumidityPercent.HasValue) HumidityPercent = snap.HumidityPercent;
                if (snap.BatteryPercent.HasValue) BatteryPercent = snap.BatteryPercent;
                if (snap.GasAlarm.HasValue) GasAlarm = snap.GasAlarm.Value;
                if (snap.WaterAlarm.HasValue) WaterAlarm = snap.WaterAlarm.Value;
                if (snap.LightOn.HasValue) LightOn = snap.LightOn;
                if (!string.IsNullOrWhiteSpace(snap.ActiveMove)) ActiveMove = snap.ActiveMove;
                LastUpdate = DateTime.Now;
            });
        }
        catch
        {
            // Ignore malformed payloads — telemetry is best-effort.
        }
    }
}
