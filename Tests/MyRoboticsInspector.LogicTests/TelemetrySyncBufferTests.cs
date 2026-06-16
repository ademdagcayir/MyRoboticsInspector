using MyRoboticsInspector.Services;
using Xunit;

namespace MyRoboticsInspector.LogicTests;

public class TelemetrySyncBufferTests
{
    [Fact]
    public void Bos_Buffer_NullDoner()
    {
        var buf = new TelemetrySyncBuffer();
        Assert.Null(buf.MetersAt(buf.NowSeconds));
    }

    [Fact]
    public void TekOrnek_HerZamanIcinAyniDeger()
    {
        var buf = new TelemetrySyncBuffer();
        buf.AddMeters(6.0);
        Assert.Equal(6.0, buf.MetersAt(-100));                  // tüm örneklerden eski
        Assert.Equal(6.0, buf.MetersAt(buf.NowSeconds + 100));  // tüm örneklerden yeni
    }

    [Fact]
    public void GecmisSorgu_EnEskiDegereTutunur()
    {
        var buf = new TelemetrySyncBuffer();
        buf.AddMeters(1.0);
        buf.AddMeters(2.0);
        buf.AddMeters(3.0);
        Assert.Equal(1.0, buf.MetersAt(-1)); // hepsinden eski → ilk örnek
    }

    [Fact]
    public void GelecekSorgu_SonDegereTutunur()
    {
        var buf = new TelemetrySyncBuffer();
        buf.AddMeters(1.0);
        buf.AddMeters(2.0);
        buf.AddMeters(3.0);
        Assert.Equal(3.0, buf.MetersAt(buf.NowSeconds + 60));
    }

    [Fact]
    public void AraSorgu_IkiOrnekArasindaInterpolasyon()
    {
        var buf = new TelemetrySyncBuffer();
        var t0 = buf.NowSeconds;
        buf.AddMeters(2.0);
        Thread.Sleep(15); // gerçek saat — örnekler farklı zamana damgalansın
        buf.AddMeters(4.0);
        var t1 = buf.NowSeconds;

        var mid = buf.MetersAt((t0 + t1) / 2);
        Assert.NotNull(mid);
        Assert.InRange(mid!.Value, 2.0, 4.0); // monotonik: aradaki sorgu aralık içinde kalmalı
    }

    [Fact]
    public void TouchOrnekleri_MetreSayilmaz()
    {
        var buf = new TelemetrySyncBuffer();
        buf.Touch();
        buf.Touch();
        Assert.Null(buf.MetersAt(buf.NowSeconds));
    }

    [Fact]
    public void RingWrap_EskiOrneklerDusulur()
    {
        var buf = new TelemetrySyncBuffer(capacity: 8);
        for (int i = 1; i <= 20; i++) buf.AddMeters(i);

        // Son 8 örnek kaldı (13..20): geçmiş sorgu en eski KALAN örneğe tutunur.
        Assert.Equal(13.0, buf.MetersAt(-1));
        Assert.Equal(20.0, buf.MetersAt(buf.NowSeconds + 60));
    }

    [Fact]
    public void Clear_SonrasiNull()
    {
        var buf = new TelemetrySyncBuffer();
        buf.AddMeters(5.0);
        buf.Clear();
        Assert.Null(buf.MetersAt(buf.NowSeconds));
    }

    [Fact]
    public async Task EsZamanliEkleSorgula_KilitGuvenli()
    {
        var buf = new TelemetrySyncBuffer(capacity: 64);
        var stop = false;
        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 5000; i++) buf.AddMeters(i * 0.01);
            stop = true;
        });
        var reader = Task.Run(() =>
        {
            while (!stop) _ = buf.MetersAt(buf.NowSeconds - 0.05);
        });
        await Task.WhenAll(writer, reader); // istisna fırlamadan bitmeli
        Assert.NotNull(buf.MetersAt(buf.NowSeconds));
    }
}
