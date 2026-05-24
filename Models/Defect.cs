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

    /// <summary>İSKİ / TS EN 13508-2 standart kodu (ör. "BAC-A"). Opsiyonel.</summary>
    [MaxLength(10)]
    public string? IskiCode { get; set; }

    [MaxLength(120)]
    public string? Type { get; set; }

    public string? Description { get; set; }

    [MaxLength(500)]
    public string? PhotoPath { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
