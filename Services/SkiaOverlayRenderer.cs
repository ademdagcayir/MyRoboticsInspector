using SkiaSharp;

namespace MyRoboticsInspector.Services;

/// <summary>
/// Video karesine gömülecek overlay verisi. Statik alanlar inceleme boyunca sabittir;
/// <see cref="Meters"/> her kareye senkron seçilen dinamik değerdir.
/// </summary>
public sealed class VideoOverlayModel
{
    public string? CompanyName { get; set; }
    public string? ProjectName { get; set; }
    public string? Location { get; set; }     // İl / İlçe
    public string? ManholeIn { get; set; }     // Giriş bacası
    public string? ManholeOut { get; set; }    // Çıkış bacası
    public string? FlowText { get; set; }      // Akış yönü

    // Dinamik
    public double? Meters { get; set; }
    public string? NowText { get; set; }
    public bool IsRecording { get; set; }

    public VideoOverlayModel Clone() => (VideoOverlayModel)MemberwiseClone();
}

/// <summary>
/// SkiaSharp ile video karesinin üstüne overlay metinlerini çizer (tek kaynak: canlı=kayıt).
/// Satırlar FontMetrics ile TOP-tabanlı yerleştirilir → farklı boyutlu satırlar üst üste binmez.
/// Font ailesi ve boyut çarpanı Ayarlar'dan yapılandırılabilir.
/// </summary>
public sealed class SkiaOverlayRenderer
{
    private static readonly SKColor White = new(0xFF, 0xFF, 0xFF);
    private static readonly SKColor Highlight = new(0xFF, 0xD6, 0x0A);   // proje/metre sarısı
    private static readonly SKColor Sub = new(0xCC, 0xCC, 0xCC);
    private static readonly SKColor Cyan = new(0x64, 0xD2, 0xFF);        // akış yönü
    private static readonly SKColor RecRed = new(0xFF, 0x3B, 0x30);
    private static readonly SKColor BoxBg = new(0, 0, 0, 150);           // yarı saydam kutu

    private string _fontFamily = "Arial";
    private SKTypeface _bold = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
    private SKTypeface _regular = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

    /// <summary>Overlay font ailesi (sistem fontu adı). Değişince typeface'ler yeniden oluşturulur.</summary>
    public string FontFamily
    {
        get => _fontFamily;
        set
        {
            var fam = string.IsNullOrWhiteSpace(value) ? "Arial" : value.Trim();
            if (fam.Equals(_fontFamily, StringComparison.OrdinalIgnoreCase)) return;
            _fontFamily = fam;
            var oldBold = _bold; var oldRegular = _regular;
            _bold = SKTypeface.FromFamilyName(fam, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            _regular = SKTypeface.FromFamilyName(fam, SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            oldBold?.Dispose(); oldRegular?.Dispose();
        }
    }

    /// <summary>Yazı boyutu çarpanı (1.0 = varsayılan). 0.4–3.0 arasına sıkıştırılır.</summary>
    public double FontScale { get; set; } = 1.0;

    public void DrawOverlay(SKCanvas canvas, float w, float h, VideoOverlayModel m)
    {
        float fs = (float)Math.Clamp(FontScale, 0.4, 3.0);
        float pad = h * 0.022f;
        float bodySize = MathF.Max(11f, h * 0.030f) * fs;
        float smallSize = bodySize * 0.80f;
        float titleSize = bodySize * 1.20f;
        float meterSize = MathF.Max(28f, h * 0.085f) * fs;
        float gap = bodySize * 0.30f;

        // ── ÜST SOL: şirket / proje / il-ilçe / bacalar / akış ──
        float y = pad;
        if (!string.IsNullOrWhiteSpace(m.CompanyName))
            y = DrawLeft(canvas, m.CompanyName!, pad, y, smallSize, Sub, _regular) + gap;
        if (!string.IsNullOrWhiteSpace(m.ProjectName))
            y = DrawLeft(canvas, m.ProjectName!, pad, y, titleSize, Highlight, _bold) + gap;
        if (!string.IsNullOrWhiteSpace(m.Location))
            y = DrawLeft(canvas, m.Location!, pad, y, bodySize, White, _regular) + gap;

        var baca = new List<string>();
        if (!string.IsNullOrWhiteSpace(m.ManholeIn)) baca.Add($"Giriş: {m.ManholeIn}");
        if (!string.IsNullOrWhiteSpace(m.ManholeOut)) baca.Add($"Çıkış: {m.ManholeOut}");
        if (baca.Count > 0)
            y = DrawLeft(canvas, string.Join("    ", baca), pad, y, bodySize, White, _regular) + gap;

        if (!string.IsNullOrWhiteSpace(m.FlowText))
            y = DrawLeft(canvas, $"Akış yönü: {m.FlowText}", pad, y, bodySize, Cyan, _regular) + gap;

        // ── ÜST SAĞ: saat + REC ──
        float yr = pad;
        if (!string.IsNullOrWhiteSpace(m.NowText))
            yr = DrawRight(canvas, m.NowText!, w - pad, yr, bodySize, White, _regular) + gap;
        if (m.IsRecording)
            yr = DrawRight(canvas, "● REC", w - pad, yr, bodySize, RecRed, _bold) + gap;

        // ── ALT SOL: büyük metre (alttan hizalı) ──
        if (m.Meters is double meters)
        {
            string val = $"{meters:0.00} m";
            string lbl = "Mesafe";
            float valH = BoxHeight(meterSize);
            float lblH = BoxHeight(smallSize);
            float valTop = h - pad - valH;
            float lblTop = valTop - lblH;
            DrawLeft(canvas, lbl, pad, lblTop, smallSize, Sub, _regular);
            DrawLeft(canvas, val, pad, valTop, meterSize, Highlight, _bold);
        }
    }

    // ── Çizim yardımcıları: top-tabanlı, FontMetrics ile kesin yükseklik ──

    private float DrawLeft(SKCanvas c, string text, float x, float topY, float size, SKColor color, SKTypeface tf)
        => DrawBoxed(c, text, x, topY, size, color, tf, rightAlign: false, rightX: 0);

    private float DrawRight(SKCanvas c, string text, float rightX, float topY, float size, SKColor color, SKTypeface tf)
        => DrawBoxed(c, text, 0, topY, size, color, tf, rightAlign: true, rightX: rightX);

    /// <summary>Bir satırı kutulu çizer; satırın ALT y koordinatını döner (bir sonraki satır için).</summary>
    private float DrawBoxed(SKCanvas c, string text, float x, float topY, float size, SKColor color, SKTypeface tf, bool rightAlign, float rightX)
    {
        using var paint = new SKPaint { IsAntialias = true, Color = color, TextSize = size, Typeface = tf };
        var fm = paint.FontMetrics;
        float padX = size * 0.30f, padY = size * 0.18f;
        float tw = paint.MeasureText(text);
        float bx = rightAlign ? rightX - tw : x;
        float baseline = topY + padY - fm.Ascent;          // Ascent negatif
        float boxBottom = baseline + fm.Descent + padY;
        var box = new SKRect(bx - padX, topY, bx + tw + padX, boxBottom);
        using var bg = new SKPaint { Color = BoxBg, IsAntialias = true };
        c.DrawRoundRect(box, size * 0.16f, size * 0.16f, bg);
        c.DrawText(text, bx, baseline, paint);
        return boxBottom;
    }

    private float BoxHeight(float size)
    {
        using var paint = new SKPaint { TextSize = size, Typeface = _regular };
        var fm = paint.FontMetrics;
        float padY = size * 0.18f;
        return (fm.Descent - fm.Ascent) + padY * 2;
    }
}
