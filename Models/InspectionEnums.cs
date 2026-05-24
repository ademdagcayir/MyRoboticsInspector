namespace MyRoboticsInspector.Models;

/// <summary>
/// İSKİ "Görüntüleme Aşaması" — incelemenin hangi safhada yapıldığı.
/// Belediye ihaleleri (özellikle İstanbul İSKİ) bu kodları rapor formatında bekler.
/// </summary>
public enum InspectionStage
{
    A = 0, // Yapım Sonrası Kontrol
    B = 1, // Gözlemciden Kuruma Devir (varsayılan)
    C = 2, // Devir Sırasında
}

/// <summary>
/// İSKİ "Görüntüleme Amacı" — incelemenin neden yapıldığı.
/// </summary>
public enum InspectionPurpose
{
    A = 0, // Rutin İnceleme
    B = 1, // İnfiltrasyon Problemi İncelemesi
    C = 2, // Sızdırmazlık Doğrulaması
    D = 3, // Yapısal Hasar İncelemesi
    E = 4, // İnşaat / Onarım Sonrası Kabul
}

public static class InspectionEnumExtensions
{
    public static string GetLabel(this InspectionStage s) => s switch
    {
        InspectionStage.A => "A — Yapım Sonrası Kontrol",
        InspectionStage.B => "B — Gözlemciden Kuruma Devir",
        InspectionStage.C => "C — Devir Sırasında",
        _ => s.ToString()
    };

    public static string GetLabel(this InspectionPurpose p) => p switch
    {
        InspectionPurpose.A => "A — Rutin İnceleme",
        InspectionPurpose.B => "B — İnfiltrasyon Problemi İncelemesi",
        InspectionPurpose.C => "C — Sızdırmazlık Doğrulaması",
        InspectionPurpose.D => "D — Yapısal Hasar İncelemesi",
        InspectionPurpose.E => "E — İnşaat / Onarım Sonrası Kabul",
        _ => p.ToString()
    };

    public static string GetLabel(this FlowDirection f) => f switch
    {
        FlowDirection.Downstream => "Akış Yönü",
        FlowDirection.Upstream   => "Akış Yönü Tersi",
        _ => f.ToString()
    };
}

/// <summary>Kanaldaki inceleme yönü — su akışıyla mı, tersine mi gidiyor robot.</summary>
public enum FlowDirection
{
    Downstream = 0, // Akış yönünde (normal — varsayılan)
    Upstream   = 1, // Akış yönüne ters (yokuş yukarı)
}

/// <summary>Kanal tipi (Atık Su / Yağmur Suyu / Birleşik).</summary>
public enum ProjectType
{
    AtikSu      = 0,
    YagmurSuyu  = 1,
    Birlesik    = 2,
}

/// <summary>EN 13508-2 boru kesit şekli.</summary>
public enum PipeShape
{
    Dairesel = 0, // A
    Yumurta  = 1, // B
    Kare     = 2, // C
    Dikdortgen = 3, // D
    Diger    = 9, // Z
}

/// <summary>Görüntüleme başlangıç yeri.</summary>
public enum ViewStartLocation
{
    KanalBasi      = 0, // A — Kanal başından (varsayılan)
    BelliMesafede  = 1, // B — Belli mesafede başlatıldı (sebep var)
    Diger          = 2,
}

public static class InspectionEnumExtensions2
{
    public static string GetLabel(this ProjectType t) => t switch
    {
        ProjectType.AtikSu     => "ATIK SU",
        ProjectType.YagmurSuyu => "YAĞMUR SUYU",
        ProjectType.Birlesik   => "BİRLEŞİK",
        _ => t.ToString()
    };

    public static string GetLabel(this PipeShape s) => s switch
    {
        PipeShape.Dairesel   => "A — Dairesel",
        PipeShape.Yumurta    => "B — Yumurta",
        PipeShape.Kare       => "C — Kare",
        PipeShape.Dikdortgen => "D — Dikdörtgen",
        PipeShape.Diger      => "Z — Diğer",
        _ => s.ToString()
    };

    public static string GetLabel(this ViewStartLocation v) => v switch
    {
        ViewStartLocation.KanalBasi     => "A — Kanal Başı",
        ViewStartLocation.BelliMesafede => "B — Belli Mesafede",
        ViewStartLocation.Diger         => "Z — Diğer",
        _ => v.ToString()
    };
}

/// <summary>UI Picker'ları için enum+display wrapper.</summary>
public record LabeledOption<T>(T Value, string Label) where T : struct
{
    public override string ToString() => Label;
}
