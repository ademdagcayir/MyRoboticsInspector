using System.Text;

namespace MyRoboticsInspector.Services;

/// <summary>
/// Proje / mahalle / sokak bazlı dosya sistemi düzeni (referans "pipecam" ürünüyle uyumlu):
/// <code>
///   {StorageRoot}/projeler/{ProjeAdı}/{Mahalle}/{Sokak}/
///        ├── egim/    eğim (slope) veri/grafik dosyaları
///        ├── rapor/   _{Kanal}_{Sokak}_{GB}_{ÇB}_rapor.pdf  +  ..._rapor_13508-2.pdf
///        ├── resim/   {Kanal}_{Sokak}_{GB}_{ÇB}_{dk}_{sn}_{ms}.jpg
///        └── video/   _{Kanal}_{Sokak}_{GB}_{ÇB}.mp4
/// </code>
/// Klasör/dosya adları için yalnızca dosya sistemi açısından geçersiz karakterler temizlenir;
/// Türkçe karakterler (ç, ş, ı, ğ, ö, ü) korunur.
/// </summary>
public static class StoragePaths
{
    /// <summary>Tüm projelerin altında toplandığı kök klasör adı.</summary>
    public const string ProjectsFolder = "projeler";

    /// <summary>Sokak klasörü altında kanal resimlerinin toplandığı klasör.</summary>
    public const string PhotosFolder = "resim";

    /// <summary>Sokak klasörü altında kanal videolarının toplandığı klasör.</summary>
    public const string VideosFolder = "video";

    /// <summary>Sokak klasörü altında kanal raporlarının (PDF) toplandığı klasör.</summary>
    public const string ReportsFolder = "rapor";

    /// <summary>Sokak klasörü altında eğim (slope) verisi/grafiğinin toplandığı klasör.</summary>
    public const string EgimFolder = "egim";

    /// <summary>Dosya/klasör adı için geçersiz karakterleri '_' ile değiştirir.</summary>
    public static string Sanitize(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        var cleaned = sb.ToString().Trim().TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    /// <summary>{root}/projeler</summary>
    public static string ProjectsRoot(string storageRoot)
        => Path.Combine(storageRoot, ProjectsFolder);

    /// <summary>{root}/projeler/{ProjeAdı}</summary>
    public static string ProjectDir(string storageRoot, string? projectTitle)
        => Path.Combine(ProjectsRoot(storageRoot), Sanitize(projectTitle, "Proje"));

    /// <summary>{root}/projeler/{ProjeAdı}/{Mahalle}</summary>
    public static string MahalleDir(string storageRoot, string? projectTitle, string? neighborhood)
        => Path.Combine(ProjectDir(storageRoot, projectTitle), Sanitize(neighborhood, "Mahalle Belirsiz"));

    /// <summary>{root}/projeler/{ProjeAdı}/{Mahalle}/{Sokak}</summary>
    public static string StreetDir(string storageRoot, string? projectTitle, string? neighborhood, string? street)
        => Path.Combine(MahalleDir(storageRoot, projectTitle, neighborhood), Sanitize(street, "Sokak Belirsiz"));

    // ── Alt klasörler (sokak altında) ──
    public static string VideoDir(string storageRoot, string? projectTitle, string? neighborhood, string? street)
        => Path.Combine(StreetDir(storageRoot, projectTitle, neighborhood, street), VideosFolder);

    public static string PhotosDir(string storageRoot, string? projectTitle, string? neighborhood, string? street)
        => Path.Combine(StreetDir(storageRoot, projectTitle, neighborhood, street), PhotosFolder);

    public static string ReportsDir(string storageRoot, string? projectTitle, string? neighborhood, string? street)
        => Path.Combine(StreetDir(storageRoot, projectTitle, neighborhood, street), ReportsFolder);

    public static string EgimDir(string storageRoot, string? projectTitle, string? neighborhood, string? street)
        => Path.Combine(StreetDir(storageRoot, projectTitle, neighborhood, street), EgimFolder);

    /// <summary>
    /// Kanal dosya kök adı (uzantısız, başında alt çizgi YOK):
    /// <c>{Kanal}_{Sokak}_{GirişBaca}_{ÇıkışBaca}</c>, ör. <c>1_asort_1_2</c>.
    /// Tüm bilgiler boşsa <paramref name="fallbackBase"/> kullanılır.
    /// </summary>
    public static string ChannelStem(int? kanalNo, string? street,
                                     string? entryManhole, string? exitManhole, string fallbackBase)
    {
        var parts = new List<string>(4);
        if (kanalNo is int n) parts.Add(n.ToString());
        var s = Sanitize(street, "");
        if (s.Length > 0) parts.Add(s);
        var entry = Sanitize(entryManhole, "");
        var exit  = Sanitize(exitManhole, "");
        if (entry.Length > 0) parts.Add(entry);
        if (exit.Length > 0)  parts.Add(exit);
        return parts.Count > 0 ? string.Join("_", parts) : Sanitize(fallbackBase, "kanal");
    }

    /// <summary>Video dosya adı: <c>_{Kanal}_{Sokak}_{GB}_{ÇB}.mp4</c> (başında alt çizgi).</summary>
    public static string VideoFileName(int? kanalNo, string? street,
                                       string? entryManhole, string? exitManhole, string fallbackBase)
        => "_" + ChannelStem(kanalNo, street, entryManhole, exitManhole, fallbackBase) + ".mp4";

    /// <summary>
    /// Fotoğraf dosya adı: <c>{Kanal}_{Sokak}_{GB}_{ÇB}_{dk}_{sn}_{ms}.jpg</c>.
    /// Zaman = VİDEO zamanı (kayıt başından beri geçen süre), duvar saati değil.
    /// </summary>
    public static string PhotoFileName(int? kanalNo, string? street,
                                       string? entryManhole, string? exitManhole,
                                       string fallbackBase, TimeSpan videoTime)
    {
        if (videoTime < TimeSpan.Zero) videoTime = TimeSpan.Zero;
        var stamp = $"_{(int)videoTime.TotalMinutes:00}_{videoTime.Seconds:00}_{videoTime.Milliseconds:000}";
        return ChannelStem(kanalNo, street, entryManhole, exitManhole, fallbackBase) + stamp + ".jpg";
    }

    /// <summary>Normal "Görüntüleme Raporu" dosya adı: <c>_{stem}_rapor.pdf</c>.</summary>
    public static string ReportFileName(int? kanalNo, string? street,
                                        string? entryManhole, string? exitManhole, string fallbackBase)
        => "_" + ChannelStem(kanalNo, street, entryManhole, exitManhole, fallbackBase) + "_rapor.pdf";

    /// <summary>TSE EN 13508-2 rapor dosya adı: <c>_{stem}_rapor_13508-2.pdf</c>.</summary>
    public static string Report13508FileName(int? kanalNo, string? street,
                                             string? entryManhole, string? exitManhole, string fallbackBase)
        => "_" + ChannelStem(kanalNo, street, entryManhole, exitManhole, fallbackBase) + "_rapor_13508-2.pdf";
}
