using SQLite;

namespace MyRoboticsInspector.Models;

public enum JobStatus
{
    Planned = 0,
    InProgress = 1,
    Completed = 2,
    Cancelled = 3
}

/// <summary>
/// Bir "iş" / "proje" — bir müşteriye ait, belirli bir konumda, belirli bir İSKİ aşama/amacıyla
/// gerçekleştirilen kanal görüntüleme çalışması. Bir Job altında birden fazla Inspection (kanal)
/// olabilir. Türk belediye ihalelerinde "Proje" olarak adlandırılır.
/// </summary>
[Table("jobs")]
public class Job
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public int CustomerId { get; set; }

    /// <summary>Proje Adı.</summary>
    [NotNull, MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    // ----- Konum (legacy ile birebir alan adları, UI'da İl/İlçe/Semt-Mahalle) -----
    [MaxLength(100)] public string? Province { get; set; }      // İl
    [MaxLength(100)] public string? District { get; set; }      // İlçe
    [MaxLength(200)] public string? Neighborhood { get; set; }  // Semt / Mahalle

    /// <summary>Serbest açıklama / saha (mahalle dışı detay).</summary>
    [MaxLength(400)] public string? Site { get; set; }

    /// <summary>Projenin kapak tarihi (legacy: Tarih). Sahaya çıkıştan farklı olabilir.</summary>
    public DateTime ProjectDate { get; set; } = DateTime.Now;

    // ----- İSKİ / belediye kontrat alanları -----
    [MaxLength(60)] public string? HakedisNo { get; set; }

    public InspectionStage Stage { get; set; } = InspectionStage.B;
    public InspectionPurpose Purpose { get; set; } = InspectionPurpose.A;

    /// <summary>Proje logosu — PDF raporlarda bu kullanılır.</summary>
    [MaxLength(500)] public string? LogoPath { get; set; }

    // ----- Operasyonel -----
    public DateTime? ScheduledAt { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Planned;
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
