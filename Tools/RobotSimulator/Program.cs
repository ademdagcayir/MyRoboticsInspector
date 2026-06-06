using System.Buffers;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;

namespace MyRoboticsInspector.Simulator;

/// <summary>
/// Synthetic robot for testing the MAUI app's MQTT integration.
///
/// - Connects to a broker (default localhost:1883, anonymous)
/// - Publishes telemetry to {prefix}/{robotId}/telemetry every 500 ms
/// - Subscribes to {prefix}/{robotId}/cmd and reacts to MoveForward / TurnLeft / Stop / LightOn ...
/// - Distance increases when MoveForward command is active, decreases on MoveBackward
/// - Battery slowly drains
/// - Periodic gas + water alarms for demo visibility
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var host = GetArg(args, "--host") ?? "localhost";
        var port = int.TryParse(GetArg(args, "--port"), out var p) ? p : 1883;
        var prefix = GetArg(args, "--prefix") ?? "myrobotics";
        var robotId = GetArg(args, "--robot") ?? "robot1";

        var cmdTopic = $"{prefix}/{robotId}/cmd";
        var telemetryTopic = $"{prefix}/{robotId}/telemetry";
        var statusTopic = $"{prefix}/{robotId}/status";

        Console.WriteLine($"== MyRoboticsInspector Simulator ==");
        Console.WriteLine($"Broker     : {host}:{port}");
        Console.WriteLine($"Robot ID   : {robotId}");
        Console.WriteLine($"Telemetry  : {telemetryTopic}");
        Console.WriteLine($"Commands   : {cmdTopic}");
        Console.WriteLine();

        var factory = new MqttClientFactory();
        var client = factory.CreateMqttClient();

        var state = new SimState();

        client.ApplicationMessageReceivedAsync += async e =>
        {
            try
            {
                var json = Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray());
                using var doc = JsonDocument.Parse(json);
                var cmd = doc.RootElement.TryGetProperty("cmd", out var c) ? c.GetString() : null;
                float? val = doc.RootElement.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number
                    ? v.GetSingle() : null;

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] CMD <- {cmd}{(val is float fv ? $" ({fv:0.00})" : "")}");
                state.ApplyCommand(cmd, val);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ! malformed cmd payload: {ex.Message}");
            }
            await Task.CompletedTask;
        };

        client.DisconnectedAsync += async e =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Disconnected, reconnecting in 2s...");
            await Task.Delay(2000);
            try { await Connect(client, host, port, cmdTopic, statusTopic); } catch { /* retry next tick */ }
        };

        await Connect(client, host, port, cmdTopic, statusTopic);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; cts.Cancel(); };

        Console.WriteLine("Publishing telemetry. Press Ctrl+C to stop.\n");

        var startTime = DateTime.Now;
        var tickIndex = 0L;      // her 100 ms'de bir artar
        var telemetryIndex = 0L; // her 500 ms'lik telemetri yayınını sayar
        try
        {
            while (!cts.IsCancellationRequested)
            {
                // Watchdog'u sık (100 ms) kontrol et — gerçek firmware'in hızlı loop'u gibi.
                if (state.CheckWatchdog())
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠ WATCHDOG — komut akışı {500}ms kesildi, robot DURDURULDU");

                // Telemetriyi 500 ms'de bir yayınla (5 × 100 ms). Tick'in 0.5s hesabı korunur.
                if (tickIndex % 5 == 0)
                {
                    state.Tick(telemetryIndex, DateTime.Now - startTime);
                    var payload = JsonSerializer.SerializeToUtf8Bytes(state.ToTelemetry(),
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                    if (client.IsConnected)
                    {
                        var msg = new MqttApplicationMessageBuilder()
                            .WithTopic(telemetryTopic)
                            .WithPayload(payload)
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                            .Build();
                        await client.PublishAsync(msg, cts.Token);

                        if (telemetryIndex % 4 == 0) // throttle console
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] TX dist={state.DistanceMeters:0.00}m " +
                                              $"tilt={state.TiltDegrees:0.0}° batt={state.BatteryPercent:0}% " +
                                              $"gas={state.GasAlarm} water={state.WaterAlarm}");
                    }
                    telemetryIndex++;
                }

                tickIndex++;
                await Task.Delay(100, cts.Token);
            }
        }
        catch (OperationCanceledException) { }

        if (client.IsConnected)
        {
            await PublishStatus(client, statusTopic, "offline");
            await client.DisconnectAsync();
        }
        Console.WriteLine("Done.");
        return 0;
    }

    private static async Task Connect(IMqttClient client, string host, int port,
                                      string cmdTopic, string statusTopic)
    {
        var opts = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId($"simulator-{Guid.NewGuid().ToString("N")[..6]}")
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
            .WithWillTopic(statusTopic)
            .WithWillPayload(Encoding.UTF8.GetBytes("offline"))
            .WithWillRetain(true)
            .Build();

        await client.ConnectAsync(opts);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Connected.");
        await client.SubscribeAsync(cmdTopic, MqttQualityOfServiceLevel.AtLeastOnce);
        await PublishStatus(client, statusTopic, "online");
    }

    private static async Task PublishStatus(IMqttClient client, string statusTopic, string payload)
    {
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(statusTopic)
            .WithPayload(payload)
            .WithRetainFlag(true)
            .Build();
        await client.PublishAsync(msg);
    }

    private static string? GetArg(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == key) return args[i + 1];
        return null;
    }
}

internal class SimState
{
    public double DistanceMeters { get; set; }
    public double Speed { get; set; }
    public double TiltDegrees { get; set; }
    public double PressureBar { get; set; } = 1.0;
    public double TemperatureC { get; set; } = 22.0;
    public double HumidityPercent { get; set; } = 60.0;
    public double BatteryPercent { get; set; } = 100.0;
    public bool GasAlarm { get; set; }
    public bool WaterAlarm { get; set; }
    public bool LightOn { get; set; }

    private string _activeMove = "Stop";
    private float _moveValue = 0.5f;
    private readonly Random _rng = new(42);

    // ===== HAREKET WATCHDOG (gerçek firmware davranışını taklit eder) =====
    // PC, aktif hareketi 100 ms aralıkla yeniden yayınlar (MQTT_PROTOKOL §3.4).
    // Son hareket komutundan beri MoveWatchdogMs geçtiyse akış kopmuş demektir →
    // güvenli dur. Anlık komutlar (LightOn/CameraPan) watchdog'u BESLEMEZ.
    private DateTime _lastMoveCmdAt = DateTime.MinValue;
    private const double MoveWatchdogMs = 500;

    public void ApplyCommand(string? cmd, float? value)
    {
        if (cmd is null) return;
        switch (cmd)
        {
            case "Stop": _activeMove = "Stop"; Speed = 0; break;
            case "MoveForward": _activeMove = "Forward"; _moveValue = value ?? 0.5f; _lastMoveCmdAt = DateTime.UtcNow; break;
            case "MoveBackward": _activeMove = "Backward"; _moveValue = value ?? 0.5f; _lastMoveCmdAt = DateTime.UtcNow; break;
            case "TurnLeft":  _activeMove = "TurnLeft"; _moveValue = value ?? 0.5f; _lastMoveCmdAt = DateTime.UtcNow; break;
            case "TurnRight": _activeMove = "TurnRight"; _moveValue = value ?? 0.5f; _lastMoveCmdAt = DateTime.UtcNow; break;
            case "LightOn":  LightOn = true; break;
            case "LightOff": LightOff(); break;
        }
    }

    /// <summary>
    /// Aktif hareket varken komut akışı MoveWatchdogMs boyunca kesildiyse robotu durdurur.
    /// Yeni durduysa true döner (çağıran tarafın bir kez log yazması için).
    /// </summary>
    public bool CheckWatchdog()
    {
        if (_activeMove == "Stop") return false;
        if ((DateTime.UtcNow - _lastMoveCmdAt).TotalMilliseconds > MoveWatchdogMs)
        {
            _activeMove = "Stop";
            Speed = 0;
            return true;
        }
        return false;
    }

    private void LightOff() => LightOn = false;

    public void Tick(long tick, TimeSpan elapsed)
    {
        // Distance evolves based on the active movement command.
        switch (_activeMove)
        {
            case "Forward":
                Speed = _moveValue * 0.3;            // 0.3 m/s @ value=1.0
                DistanceMeters += Speed * 0.5;       // 0.5s tick
                break;
            case "Backward":
                Speed = -_moveValue * 0.2;
                DistanceMeters = Math.Max(0, DistanceMeters + Speed * 0.5);
                break;
            case "TurnLeft":
            case "TurnRight":
                Speed = 0;
                break;
            default:
                Speed = 0;
                break;
        }

        // Sensors with smooth pseudo-noise.
        var t = elapsed.TotalSeconds;
        TiltDegrees     = Math.Sin(t * 0.6) * 3.0 + (_rng.NextDouble() - 0.5) * 0.3;
        PressureBar     = 1.0 + Math.Sin(t * 0.3) * 0.05 + (_rng.NextDouble() - 0.5) * 0.02;
        TemperatureC    = 22.0 + Math.Sin(t * 0.05) * 0.8 + (_rng.NextDouble() - 0.5) * 0.1;
        HumidityPercent = 60.0 + Math.Sin(t * 0.1) * 8.0 + (_rng.NextDouble() - 0.5);
        BatteryPercent  = Math.Max(0, 100.0 - elapsed.TotalMinutes * 0.5); // 0.5%/min drain

        // Alarm demo: gas at 30-35s, water at 60-63s (loops every 90s).
        var sec = (int)elapsed.TotalSeconds % 90;
        GasAlarm   = sec >= 30 && sec < 35;
        WaterAlarm = sec >= 60 && sec < 63;
    }

    public object ToTelemetry() => new
    {
        distanceMeters = Math.Round(DistanceMeters, 2),
        speed = Math.Round(Speed, 2),
        tiltDegrees = Math.Round(TiltDegrees, 1),
        pressureBar = Math.Round(PressureBar, 2),
        temperatureC = Math.Round(TemperatureC, 1),
        humidityPercent = Math.Round(HumidityPercent, 1),
        batteryPercent = Math.Round(BatteryPercent, 1),
        gasAlarm = GasAlarm,
        waterAlarm = WaterAlarm,
        lightOn = LightOn,
        activeMove = _activeMove
    };
}
