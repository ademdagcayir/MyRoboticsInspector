namespace MyRoboticsInspector.Models;

/// <summary>
/// Türkiye'de kanal görüntüleme raporlarında kullanılan İSKİ / TS EN 13508-2 türevi standart
/// kusur kodları. Belediye ihalelerine teslim edilen raporlarda bu kodlar bekleniyor.
/// Kullanıcı serbest tür de yazabilir (Defect.Type free-text), ama bu liste hızlı seçim sunar.
/// </summary>
public record IskiDefectCode(string Code, string Title, string Description, DefectSeverity DefaultSeverity);

public static class IskiDefectCodes
{
    public static readonly IReadOnlyList<IskiDefectCode> All = new[]
    {
        // BAA — Deformasyon
        new IskiDefectCode("BAA-A", "Deformasyon %10-25", "Düşey yönde %10 < x ≤ %25 deformasyon", DefectSeverity.Low),
        new IskiDefectCode("BAA-B", "Deformasyon %25-40", "Düşey yönde %25 < x ≤ %40 deformasyon", DefectSeverity.Medium),
        new IskiDefectCode("BAA-C", "Deformasyon %40-50", "Düşey yönde %40 < x ≤ %50 deformasyon", DefectSeverity.High),

        // BAB — Çatlak
        new IskiDefectCode("BAB-A", "Çatlak (kıl)", "Kıl çatlak — hava sızıntısı yok", DefectSeverity.Low),
        new IskiDefectCode("BAB-B", "Çatlak (açık)", "Açık çatlak — toprak/su girişi mümkün", DefectSeverity.Medium),
        new IskiDefectCode("BAB-C", "Çatlak (kırık)", "Geniş çatlak / kırık — yapısal", DefectSeverity.High),

        // BAC — Boru kırığı
        new IskiDefectCode("BAC-A", "Boru kırığı — yerinde", "Kırılan parça yerinde duruyor", DefectSeverity.High),
        new IskiDefectCode("BAC-B", "Boru kırığı — eksik", "Parça eksik / yıkılmış", DefectSeverity.High),
        new IskiDefectCode("BAC-C", "Boru kırığı — çökme", "Tamamen parçalanmış / çökme", DefectSeverity.Critical),

        // BAD — Birleşim kusuru
        new IskiDefectCode("BAD-A", "Birleşim açıklığı", "Birleşim yerinde aralık", DefectSeverity.Medium),
        new IskiDefectCode("BAD-B", "Birleşim ofset", "Eksenden kayık birleşim", DefectSeverity.Medium),

        // BAE — Yer değiştirme
        new IskiDefectCode("BAE-A", "Yer değiştirme", "Borunun ekseninden kayması", DefectSeverity.Medium),

        // BAF — Hatalı bağlantı
        new IskiDefectCode("BAF-A", "Yan bağlantı yanlış", "Kontrolsüz/hatalı lateral bağlantı", DefectSeverity.Medium),

        // BAH — İç astar hasarı
        new IskiDefectCode("BAH-A", "Astar hasarı", "İç astar hasarlı / sıyrılma", DefectSeverity.Medium),

        // BAI — Yüzey hasarı
        new IskiDefectCode("BAI-A", "Yüzey hasarı", "Yüzeyde aşınma / pürüzlenme", DefectSeverity.Low),

        // BBA — Kök girişi
        new IskiDefectCode("BBA-A", "Kök girişi (tek)", "Tek noktada kök girişi", DefectSeverity.Low),
        new IskiDefectCode("BBA-B", "Kök girişi (yoğun)", "Yoğun kök kümesi — akış kısıtlı", DefectSeverity.Medium),

        // BBB — Yağ / Tortu
        new IskiDefectCode("BBB-A", "Yağ / tortu", "Yağ veya tortu birikimi", DefectSeverity.Low),

        // BBC — Birikinti
        new IskiDefectCode("BBC-A", "Sert birikinti", "Çamur, kum, taş birikintisi", DefectSeverity.Medium),

        // BBD — Yabancı cisim
        new IskiDefectCode("BBD-A", "Yabancı cisim", "Bez, plastik, vb. tıkayıcı", DefectSeverity.Medium),

        // BBE — Sızıntı (infiltration — dışarıdan içeri)
        new IskiDefectCode("BBE-A", "Sızıntı (infilt.)", "Toprak suyu içeri sızıyor", DefectSeverity.Medium),

        // BBF — Kaçak (exfiltration — içeriden dışarı)
        new IskiDefectCode("BBF-A", "Kaçak (exfilt.)", "Boru içinden dışarı kaçak", DefectSeverity.High),

        // BCA — Bağlantı şekilleri (referans için)
        new IskiDefectCode("BCA-A", "C-tipi bağlantı", "Kanala C şeklinde bağlantı (bilgi)", DefectSeverity.Info),
    };

    /// <summary>Hızlı sıkça kullanılan alt küme — chip UI'da gösterilebilir.</summary>
    public static readonly IReadOnlyList<IskiDefectCode> QuickPick = All.Where(c => new[]
    {
        "BAC-A", "BAC-B", "BAC-C",
        "BAB-A", "BAB-B",
        "BBA-A", "BBA-B",
        "BBB-A",
        "BBE-A",
        "BAA-B",
    }.Contains(c.Code)).ToList();
}
