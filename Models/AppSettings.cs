using SQLite;

namespace MyRoboticsInspector.Models;

[Table("app_settings")]
public class AppSettings
{
    [PrimaryKey]
    public int Id { get; set; } = 1;

    // ----- Camera -----
    [MaxLength(500)]
    public string RtspUrl { get; set; } = "rtsp://admin:password@192.168.1.64:554/Streaming/Channels/101";

    /// <summary>LibVLC network-caching value (ms). Lower = less latency, more frame drops.</summary>
    public int NetworkCachingMs { get; set; } = 200;

    // ----- MQTT broker -----
    [MaxLength(120)]
    public string BrokerHost { get; set; } = "192.168.1.10";

    public int BrokerPort { get; set; } = 1883;

    [MaxLength(120)]
    public string? BrokerUsername { get; set; }

    [MaxLength(120)]
    public string? BrokerPassword { get; set; }

    [MaxLength(80)]
    public string RobotId { get; set; } = "robot1";

    /// <summary>Topic prefix — full topics will be e.g. "{TopicPrefix}/{RobotId}/cmd".</summary>
    [MaxLength(60)]
    public string TopicPrefix { get; set; } = "myrobotics";

    // ----- Project / location (operator-entered, shown on overlay & in reports) -----
    [MaxLength(200)]
    public string? ProjectName { get; set; }

    [MaxLength(200)]
    public string? Neighborhood { get; set; }

    [MaxLength(200)]
    public string? Street { get; set; }

    [MaxLength(120)]
    public string? OperatorName { get; set; }

    // ----- Storage -----
    [MaxLength(500)]
    public string StoragePath { get; set; } = string.Empty;

    // ----- FFmpeg burn-in -----
    [MaxLength(500)]
    public string FfmpegPath { get; set; } = "ffmpeg.exe";

    /// <summary>Burn the live overlay (project/distance/sensors) into the recorded MP4.</summary>
    public bool BurnOverlayInRecording { get; set; } = true;

    // ----- Gamepad (Logitech F710 / XInput) -----
    /// <summary>Uygulama açılınca XInput polling otomatik başlasın mı.</summary>
    public bool GamepadAutoStart { get; set; } = true;

    // ----- Otomatik güncelleme (Velopack) -----
    /// <summary>Güncelleme manifesti (RELEASES + *.nupkg) bu URL'den çekilir.</summary>
    [MaxLength(500)]
    public string UpdateServerUrl { get; set; } = "https://myrobotics.com.tr/updates/inspector/";

    /// <summary>Uygulama açılınca arka planda güncelleme var mı kontrol edilsin mi.</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    // ----- Company / branding -----
    [MaxLength(200)]
    public string CompanyName { get; set; } = "My Robotics";

    [MaxLength(500)]
    public string? CompanyLogoPath { get; set; }
}
