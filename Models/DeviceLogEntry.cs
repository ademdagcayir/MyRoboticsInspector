using Microsoft.Maui.Graphics;

namespace MyRoboticsInspector.Models;

/// <summary>
/// Robot/pano cihazlarından gelen tek bir log/teşhis satırı (robot/log, pano/log topic'leri).
/// "Cihaz Konsolu" panelinde gösterilir — Arduino Serial Monitor'ün uzaktan karşılığı.
/// </summary>
public record DeviceLogEntry(DateTime Time, string Source, string Message)
{
    public string TimeStr => Time.ToString("HH:mm:ss");
    public string SourceLabel => Source.ToUpperInvariant();

    /// <summary>Kaynağa göre etiket rengi (robot mavi, pano turuncu).</summary>
    public Color SourceColor => Source switch
    {
        "robot" => Color.FromArgb("#2D6CDF"),
        "pano"  => Color.FromArgb("#E08A2B"),
        _        => Color.FromArgb("#6B7280")
    };
}
