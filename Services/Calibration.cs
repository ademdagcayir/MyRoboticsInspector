namespace MyRoboticsInspector.Services;

/// <summary>
/// Ham sensör ADC değerlerini kalibrasyon referanslarıyla birime çevirir.
/// Eğim: 3-nokta (0°, -45°, +45°) parçalı-lineer. Basınç: min/max → %0..100.
/// Tüm metotlar sensör yönünden (artan/azalan ADC) bağımsızdır.
/// </summary>
public static class Calibration
{
    /// <summary>
    /// Ham eğim ADC'sini dereceye çevirir (3-nokta parçalı lineer).
    /// 0° referansı + en az bir kullanılabilir ±45 referansı şart; raw'ın bulunduğu
    /// tarafa göre -45 ya da +45 referansı kullanılır. Kalibre değilse null.
    /// </summary>
    public static double? TiltDegrees(double raw, int? r0, int? rMinus45, int? rPlus45)
    {
        if (r0 is not int z) return null;

        // ±45 referanslarının en az biri kullanılabilir olmalı; yalnız 0° girilmişse
        // kalibre edilmemiş say — eksik kalibrasyon yanıltıcı '0.0°' gibi sunulmasın.
        // (Ölü-bölge kısayolundan ÖNCE kontrol: gösterim '0.0°'↔'—' arasında titremesin.)
        bool hasM = rMinus45 is int m45 && m45 != z;
        bool hasP = rPlus45 is int p45 && p45 != z;
        if (!hasM && !hasP) return null;

        if (Math.Abs(raw - z) < 0.5) return 0;

        // raw, z'ye göre hangi tarafta? Aynı taraftaki referansı kullan (sensör yönünden bağımsız).
        if (rMinus45 is int m && m != z && Math.Sign(raw - z) == Math.Sign(m - z))
            return -45.0 * (raw - z) / (m - z);
        if (rPlus45 is int p && p != z && Math.Sign(raw - z) == Math.Sign(p - z))
            return 45.0 * (raw - z) / (p - z);

        // Aynı taraf referansı yoksa diğer referansla yine de tahmin et
        if (rPlus45 is int p2 && p2 != z) return 45.0 * (raw - z) / (p2 - z);
        if (rMinus45 is int m2 && m2 != z) return -45.0 * (raw - z) / (m2 - z);
        return null; // savunmacı — yukarıdaki hasM/hasP guard'ı ile buraya erişilmez
    }

    /// <summary>Ham basınç ADC'sini %0..100'e çevirir. Kalibre değilse null.</summary>
    public static double? PressurePercent(double raw, int? rawMin, int? rawMax)
    {
        if (rawMin is not int lo || rawMax is not int hi || hi == lo) return null;
        double pct = (raw - lo) / (double)(hi - lo) * 100.0;
        return Math.Clamp(pct, 0.0, 100.0);
    }

    /// <summary>Basınç % → renk eşikleri: ≥60 yeşil, 40–60 turuncu, &lt;40 kırmızı.</summary>
    public static (byte r, byte g, byte b) PressureRgb(double pct)
        => pct >= 60 ? ((byte)0x30, (byte)0xD1, (byte)0x58)   // yeşil
         : pct >= 40 ? ((byte)0xFF, (byte)0x9F, (byte)0x0A)   // turuncu
         :             ((byte)0xFF, (byte)0x3B, (byte)0x30);  // kırmızı
}
