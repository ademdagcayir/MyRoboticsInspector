using SQLite;

namespace MyRoboticsInspector.Models;

/// <summary>
/// Bir kanal incelemesi — projenin altında, iki baca arasında yapılan tek bir CCTV kaydı.
/// Alanlar EN 13508-2:2003+A1:2011 standardına ve legacy "Kanal" formuna uygun.
/// </summary>
[Table("inspections")]
public class Inspection
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public int JobId { get; set; }

    /// <summary>Proje içinde sıralı kanal numarası (legacy "Kanal Numarası", örn. 10).</summary>
    public int? KanalNo { get; set; }

    /// <summary>Kanal kodu (örn. "YK3697A"). Belediyenin numaralandırması.</summary>
    [MaxLength(60)] public string? ChannelCode { get; set; }

    /// <summary>Atık su / yağmur suyu / birleşik.</summary>
    public ProjectType ProjectType { get; set; } = ProjectType.AtikSu;

    // ----- Bacalar -----
    [MaxLength(60)] public string? EntryManhole { get; set; }
    [MaxLength(60)] public string? ExitManhole { get; set; }

    /// <summary>Giriş bacası derinliği (m).</summary>
    public double? EntryManholeDepth { get; set; }

    /// <summary>Çıkış bacası derinliği (m).</summary>
    public double? ExitManholeDepth { get; set; }

    // ----- Konum -----
    [MaxLength(200)] public string? Street { get; set; }

    /// <summary>Harita pafta numarası (örn. "30.93").</summary>
    [MaxLength(40)] public string? Pafta { get; set; }

    // ----- Boru özellikleri -----
    /// <summary>Boru kesit şekli.</summary>
    public PipeShape PipeShape { get; set; } = PipeShape.Dairesel;

    /// <summary>Boru iç çapı / genişliği (mm).</summary>
    public int? PipeWidthMm { get; set; }

    /// <summary>Boru iç yüksekliği (mm). Dairesel için Width ile aynı.</summary>
    public int? PipeHeightMm { get; set; }

    /// <summary>Boru malzemesi tanımı (legacy: "A (BETON)" gibi). Free-text kalmasında esneklik var.</summary>
    [MaxLength(200)] public string? PipeMaterial { get; set; }

    [MaxLength(200)] public string? PipeDiameter { get; set; } // legacy alias / display

    /// <summary>Kanal inceleme öncesi temizlendi mi (true/false/null).</summary>
    public bool? Cleaned { get; set; }

    // ----- Görüntüleme parametreleri -----
    public FlowDirection FlowDirection { get; set; } = FlowDirection.Downstream;

    /// <summary>Görüntülemenin başladığı konum (kanal başından mı, belli mesafede mi).</summary>
    public ViewStartLocation ViewStart { get; set; } = ViewStartLocation.KanalBasi;

    /// <summary>GDD — Görüntü Derinliği Değeri / kanal derinliği (m).</summary>
    public double? Gdd { get; set; }

    /// <summary>Projede planlanan kanal metresi (legacy "Proje Metresi"), m.</summary>
    public double? PlannedMeters { get; set; }

    // ----- Operasyonel -----
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? FinishedAt { get; set; }
    [MaxLength(500)] public string? VideoPath { get; set; }
    public double? DistanceMeters { get; set; }
    public string? Notes { get; set; }

    /// <summary>
    /// Ekranda gösterilecek kanal adı: "Kanal 1", varsa belediye kodu da eklenir
    /// ("Kanal 1 · YK3697A"). KanalNo yoksa koda, o da yoksa Id'ye düşer.
    /// </summary>
    [Ignore]
    public string DisplayName
    {
        get
        {
            var num = KanalNo.HasValue ? $"Kanal {KanalNo}" : null;
            if (!string.IsNullOrWhiteSpace(ChannelCode))
                return num is null ? ChannelCode! : $"{num} · {ChannelCode}";
            return num ?? $"#{Id}";
        }
    }
}
