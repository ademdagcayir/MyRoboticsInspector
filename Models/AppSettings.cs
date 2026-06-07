using SQLite;

namespace MyRoboticsInspector.Models;

[Table("app_settings")]
public class AppSettings
{
    [PrimaryKey]
    public int Id { get; set; } = 1;

    // ----- Camera -----
    [MaxLength(500)]
    /// <summary>Canlı önizleme akışı — sub-stream (düşük bant, düşük gecikme).</summary>
    public string RtspUrl { get; set; } = "rtsp://admin:emayetencere1@192.168.1.64:554/Streaming/Channels/102";

    /// <summary>Kayıt akışı — ana stream, yüksek kalite (boş bırakılırsa RtspUrl kullanılır).</summary>
    public string RecordingRtspUrl { get; set; } = "rtsp://admin:emayetencere1@192.168.1.64:554/Streaming/Channels/101";

    /// <summary>LibVLC network-caching value (ms). Lower = less latency, more frame drops.</summary>
    public int NetworkCachingMs { get; set; } = 200;

    // ----- MQTT broker -----
    [MaxLength(120)]
    public string BrokerHost { get; set; } = "127.0.0.1";

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
    public string FfmpegPath { get; set; } = ""; // boş = otomatik bul (WinGet, Scoop, Choco, Program Files)

    /// <summary>Burn the live overlay (project/distance/sensors) into the recorded MP4.</summary>
    public bool BurnOverlayInRecording { get; set; } = true;

    /// <summary>
    /// Metre↔kare senkron gecikme telafisi (ms). Video boru hattı gecikmesi kadar:
    /// gösterilen/kaydedilen metre = buffer.At(kareZamanı − bu değer). Operatör robotu
    /// bilinen bir işaretten geçirip ince ayar yapar. Tipik 150–400 ms.
    /// </summary>
    public int MeterSyncOffsetMs { get; set; } = 250;

    /// <summary>
    /// Kayıt (/101 4K) metre↔kare senkron gecikme telafisi (ms). Önizleme (/102) offset'inden AYRI —
    /// teslim edilen video /101 olduğu için kalibrasyon buna göre yapılır.
    /// </summary>
    public int RecordingMeterSyncOffsetMs { get; set; } = 250;

    /// <summary>Kayıt encoder tercihi: auto (nvenc→qsv→libx264 probe) | nvenc | qsv | libx264.</summary>
    [MaxLength(20)]
    public string RecordingEncoder { get; set; } = "auto";

    /// <summary>Görüntü üstüne gömülen overlay yazılarının font ailesi (sistem fontu adı).</summary>
    [MaxLength(80)]
    public string OverlayFontFamily { get; set; } = "Arial";

    /// <summary>Overlay yazı boyutu çarpanı (1.0 = varsayılan). 0.5–2.5 arası.</summary>
    public double OverlayFontScale { get; set; } = 1.0;

    // ----- Gamepad (Logitech F710 / XInput) -----
    /// <summary>Uygulama açılınca XInput polling otomatik başlasın mı.</summary>
    public bool GamepadAutoStart { get; set; } = true;

    // ----- Otomatik güncelleme (Velopack) -----
    /// <summary>
    /// Güncelleme kaynağı. GitHub repo URL'i ise Velopack GithubSource (public releases) kullanır;
    /// release asset'lerinden (RELEASES + *.nupkg) otomatik güncelleme çeker.
    /// </summary>
    [MaxLength(500)]
    public string UpdateServerUrl { get; set; } = "https://github.com/ademdagcayir/MyRoboticsInspector";

    /// <summary>Uygulama açılınca arka planda güncelleme var mı kontrol edilsin mi.</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    // ----- Company / branding -----
    [MaxLength(200)]
    public string CompanyName { get; set; } = "My Robotics";

    [MaxLength(500)]
    public string? CompanyLogoPath { get; set; }
}
