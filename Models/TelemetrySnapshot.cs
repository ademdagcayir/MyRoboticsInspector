namespace MyRoboticsInspector.Models;

/// <summary>
/// Anlık robot telemetrisi. UI binding için kullanılır, kalıcı kayıt değildir.
/// Robot tarafının yayınladığı JSON yapısına göre alan eklenebilir/çıkarılabilir.
/// </summary>
public class TelemetrySnapshot
{
    public double? DistanceMeters { get; set; }
    public double? Speed { get; set; }
    public double? TiltDegrees { get; set; }
    public double? PressureBar { get; set; }
    public double? TemperatureC { get; set; }
    public double? HumidityPercent { get; set; }
    public double? BatteryPercent { get; set; }
    public bool? GasAlarm { get; set; }
    public bool? WaterAlarm { get; set; }

    /// <summary>True when the robot reports its light is on (handshake/ack for LightOn cmd).</summary>
    public bool? LightOn { get; set; }

    /// <summary>Echo of the active movement state (Stop / Forward / Backward / TurnLeft / TurnRight).</summary>
    public string? ActiveMove { get; set; }

    public DateTime ReceivedAt { get; set; } = DateTime.Now;
}
