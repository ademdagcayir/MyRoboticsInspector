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
    private readonly TelemetrySyncBuffer _sync;
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

    /// <summary>Voltaj — ham ADC (0..1023). Firmware 'voltage' topic'i. Kalibrasyon sonra V'ye çevrilir.</summary>
    [ObservableProperty] private int? voltageRaw;

    public TelemetryService(IRobotProtocol robot, TelemetrySyncBuffer sync)
    {
        // Designed for the MQTT impl. If someone swaps in a non-MQTT IRobotProtocol later,
        // this service is a no-op.
        _mqtt = robot as MqttRobotClient ?? throw new InvalidOperationException(
            "TelemetryService requires an MqttRobotClient implementation of IRobotProtocol.");
        _sync = sync;

        _mqtt.MessageReceived += OnMessage;
    }

    public async Task SubscribeAsync()
    {
        if (_subscribedTopic == "firmware") return;
        // MyRoboticsFirmware bare topic'leri: accl_x, pressure, voltage, metre (düz tamsayı)
        foreach (var t in FirmwareTopics.TelemetrySubscriptions)
            await _mqtt.SubscribeAsync(t);
        _subscribedTopic = "firmware";
    }

    private void OnMessage(object? sender, MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var bytes = e.ApplicationMessage.Payload.ToArray();
        if (bytes.Length == 0) return;
        var text = System.Text.Encoding.UTF8.GetString(bytes).Trim();

        // Firmware düz tamsayı yayınlar (atoi). Ham ADC (0..1023) — kalibrasyon sonra birime çevrilir.
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var val))
            return;

        switch (topic)
        {
            case FirmwareTopics.Metre:
                // Gerçek metre (encoder/200). Video overlay metre senkronunu da besle.
                _sync.AddMeters(val);
                MainThread.BeginInvokeOnMainThread(() => { DistanceMeters = val; LastUpdate = DateTime.Now; });
                break;
            case FirmwareTopics.AcclX:
                MainThread.BeginInvokeOnMainThread(() => { TiltDegrees = val; LastUpdate = DateTime.Now; });
                break;
            case FirmwareTopics.Pressure:
                MainThread.BeginInvokeOnMainThread(() => { PressureBar = val; LastUpdate = DateTime.Now; });
                break;
            case FirmwareTopics.Voltage:
                MainThread.BeginInvokeOnMainThread(() => { VoltageRaw = (int)val; LastUpdate = DateTime.Now; });
                break;
        }
    }
}
