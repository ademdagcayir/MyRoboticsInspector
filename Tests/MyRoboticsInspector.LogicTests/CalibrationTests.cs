using MyRoboticsInspector.Services;
using Xunit;

namespace MyRoboticsInspector.LogicTests;

public class CalibrationTests
{
    // ── Eğim ──────────────────────────────────────────────────────────────

    [Fact]
    public void Tilt_KalibreDegilse_Null()
    {
        Assert.Null(Calibration.TiltDegrees(512, null, 300, 700));
    }

    [Fact]
    public void Tilt_SadeceSifirReferansi_Null()
    {
        // ±45 referansı yokken eksik kalibrasyon '0.0°' gibi sunulmaz → null ('—' gösterilir).
        Assert.Null(Calibration.TiltDegrees(700, 500, null, null));
        // Ölü-bölge kısayolu (raw ≈ r0) da ±45 referansı olmadan null kalmalı.
        Assert.Null(Calibration.TiltDegrees(500, 500, null, null));
    }

    [Fact]
    public void Tilt_DejenereReferanslar_Null()
    {
        // ±45 referansları 0° referansına eşitse (dejenere) kalibre edilmemiş sayılır.
        Assert.Null(Calibration.TiltDegrees(700, 500, 500, 500));
    }

    [Fact]
    public void Tilt_SifirReferansinda_SifirDerece()
    {
        Assert.Equal(0, Calibration.TiltDegrees(500, 500, 300, 700));
    }

    [Fact]
    public void Tilt_EksiTarafta_LineerEksiDeger()
    {
        // r0=500, -45°=300 → raw=400 yarı yolda → -22.5°
        var d = Calibration.TiltDegrees(400, 500, 300, 700);
        Assert.NotNull(d);
        Assert.Equal(-22.5, d!.Value, 3);
    }

    [Fact]
    public void Tilt_ArtiTarafta_LineerArtiDeger()
    {
        var d = Calibration.TiltDegrees(600, 500, 300, 700);
        Assert.NotNull(d);
        Assert.Equal(22.5, d!.Value, 3);
    }

    [Fact]
    public void Tilt_TersYonluSensor_DogruIsaret()
    {
        // Sensör ters bağlı: -45° konumda ADC YÜKSELİYOR (700), +45'te düşüyor (300).
        var d = Calibration.TiltDegrees(600, 500, 700, 300);
        Assert.NotNull(d);
        Assert.Equal(-22.5, d!.Value, 3); // raw yukarı gitti ama bu eksi tarafın yönü
    }

    [Fact]
    public void Tilt_TekReferansla_DigerTarafaEkstrapolasyon()
    {
        // Yalnız +45 referansı var; raw eksi tarafta → aynı formülle negatif döner.
        var d = Calibration.TiltDegrees(400, 500, null, 700);
        Assert.NotNull(d);
        Assert.Equal(-22.5, d!.Value, 3);
    }

    [Fact]
    public void Tilt_45DereceTamReferansta()
    {
        var d = Calibration.TiltDegrees(700, 500, 300, 700);
        Assert.NotNull(d);
        Assert.Equal(45.0, d!.Value, 3);
    }

    // ── Basınç ────────────────────────────────────────────────────────────

    [Fact]
    public void Basinc_KalibreDegilse_Null()
    {
        Assert.Null(Calibration.PressurePercent(500, null, 800));
        Assert.Null(Calibration.PressurePercent(500, 200, null));
        Assert.Null(Calibration.PressurePercent(500, 400, 400)); // hi == lo bölme koruması
    }

    [Fact]
    public void Basinc_OrtaNokta_Yuzde50()
    {
        var p = Calibration.PressurePercent(500, 200, 800);
        Assert.NotNull(p);
        Assert.Equal(50.0, p!.Value, 3);
    }

    [Theory]
    [InlineData(100, 0)]    // alt sınırın altı → %0'a kıskaçlanır
    [InlineData(900, 100)]  // üst sınırın üstü → %100'e kıskaçlanır
    public void Basinc_SinirDisi_Kiskaclanir(double raw, double expected)
    {
        var p = Calibration.PressurePercent(raw, 200, 800);
        Assert.NotNull(p);
        Assert.Equal(expected, p!.Value, 3);
    }

    [Theory]
    [InlineData(60.0, 0x30, 0xD1, 0x58)]  // tam eşik: yeşil
    [InlineData(75.0, 0x30, 0xD1, 0x58)]
    [InlineData(59.9, 0xFF, 0x9F, 0x0A)]  // eşiğin altı: turuncu
    [InlineData(40.0, 0xFF, 0x9F, 0x0A)]
    [InlineData(39.9, 0xFF, 0x3B, 0x30)]  // kritik: kırmızı
    [InlineData(0.0, 0xFF, 0x3B, 0x30)]
    public void BasincRenk_Esikler(double pct, byte r, byte g, byte b)
    {
        Assert.Equal((r, g, b), Calibration.PressureRgb(pct));
    }
}
