using SQLite;

namespace MyRoboticsInspector.Models;

public enum DefectSeverity
{
    Info = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

[Table("defects")]
public class Defect
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public int InspectionId { get; set; }

    /// <summary>Position in the recorded video (ms) where the defect was marked.</summary>
    public long VideoTimestampMs { get; set; }

    /// <summary>Distance from the pipe entry in meters, if known.</summary>
    public double? DistanceMeters { get; set; }

    public DefectSeverity Severity { get; set; } = DefectSeverity.Info;

    /// <summary>İSKİ / TS EN 13508-2 tam kodu (ör. "BAC-A"). Opsiyonel.</summary>
    [MaxLength(10)]
    public string? IskiCode { get; set; }

    [MaxLength(120)]
    public string? Type { get; set; }

    public string? Description { get; set; }

    [MaxLength(500)]
    public string? PhotoPath { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // ───────── EN 13508-2 kodlama alanları (rapor #2 için — opsiyonel) ─────────

    /// <summary>Ana kusur kodu — 3 harf (ör. "BAC", "BAA"). IskiCode'dan ayrı tutulur.</summary>
    [MaxLength(5)] public string? MainCode { get; set; }

    /// <summary>Karakterizasyon 1 (ör. "A"). EN 13508-2 "A" kolonu.</summary>
    [MaxLength(5)] public string? Char1 { get; set; }

    /// <summary>Karakterizasyon 2 (ör. "B"). EN 13508-2 ikinci nitelik.</summary>
    [MaxLength(5)] public string? Char2 { get; set; }

    /// <summary>Nicelik 1 (sayısal ölçüm — mm/%/adet, serbest metin).</summary>
    [MaxLength(20)] public string? Quant1 { get; set; }

    /// <summary>Nicelik 2 (ikinci ölçüm).</summary>
    [MaxLength(20)] public string? Quant2 { get; set; }

    /// <summary>Saat yönü — tekil konum ya da aralık başlangıcı (1–12).</summary>
    public int? ClockFrom { get; set; }

    /// <summary>Saat yönü — aralık bitişi (1–12). Tekil konumda null.</summary>
    public int? ClockTo { get; set; }

    /// <summary>EN 13508-2 "Remarks" — serbest açıklama/not.</summary>
    [MaxLength(300)] public string? Remarks { get; set; }
}
