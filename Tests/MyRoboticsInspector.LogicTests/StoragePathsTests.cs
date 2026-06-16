using MyRoboticsInspector.Services;
using Xunit;

namespace MyRoboticsInspector.LogicTests;

public class StoragePathsTests
{
    [Fact]
    public void Sanitize_TurkceKarakterler_Korunur()
    {
        Assert.Equal("Çamlık Şehit Sokağı", StoragePaths.Sanitize("Çamlık Şehit Sokağı", "x"));
    }

    [Fact]
    public void Sanitize_GecersizKarakterler_AltCizgiOlur()
    {
        var result = StoragePaths.Sanitize("a/b\\c:d*e?f", "x");
        Assert.DoesNotContain('/', result);
        Assert.DoesNotContain('\\', result);
        Assert.DoesNotContain(':', result);
        Assert.Equal("a_b_c_d_e_f", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_Bos_FallbackDoner(string? input)
    {
        Assert.Equal("Varsayılan", StoragePaths.Sanitize(input, "Varsayılan"));
    }

    [Fact]
    public void Sanitize_SondakiNoktaVeBosluk_Temizlenir()
    {
        // Windows'ta "klasör." adı geçersizdir.
        Assert.Equal("rapor", StoragePaths.Sanitize("rapor. ", "x"));
    }

    [Fact]
    public void ChannelStem_TumParcalar_AltCizgiyleBirlesir()
    {
        Assert.Equal("1_asort_1_2", StoragePaths.ChannelStem(1, "asort", "1", "2", "kanal"));
    }

    [Fact]
    public void ChannelStem_EksikParcalar_Atlanir()
    {
        Assert.Equal("3_YK12", StoragePaths.ChannelStem(3, null, "YK12", "", "kanal"));
    }

    [Fact]
    public void ChannelStem_HepsiBos_FallbackKullanilir()
    {
        Assert.Equal("kanal", StoragePaths.ChannelStem(null, null, null, null, "kanal"));
    }

    [Fact]
    public void VideoFileName_BasindaAltCizgi_Mp4Uzantili()
    {
        var name = StoragePaths.VideoFileName(1, "asort", "1", "2", "kanal");
        Assert.Equal("_1_asort_1_2.mp4", name);
    }

    [Fact]
    public void PhotoFileName_VideoZamaniDamgalanir()
    {
        var t = new TimeSpan(0, 0, 1, 2, 3); // 1 dk 2 sn 3 ms
        var name = StoragePaths.PhotoFileName(1, "asort", "1", "2", "kanal", t);
        Assert.Equal("1_asort_1_2_01_02_003.jpg", name);
    }

    [Fact]
    public void PhotoFileName_NegatifZaman_SifiraKiskaclanir()
    {
        var name = StoragePaths.PhotoFileName(1, "a", "1", "2", "kanal", TimeSpan.FromSeconds(-5));
        Assert.EndsWith("_00_00_000.jpg", name);
    }

    [Fact]
    public void PhotoFileName_60DakikaUstu_DakikaTasmaz()
    {
        // 75 dk kayıt: TotalMinutes kullanılır (75), saat alanına taşmaz.
        var name = StoragePaths.PhotoFileName(1, "a", "1", "2", "kanal", TimeSpan.FromMinutes(75));
        Assert.EndsWith("_75_00_000.jpg", name);
    }

    [Fact]
    public void ReportFileNames_DogruSonEk()
    {
        Assert.Equal("_1_a_g_c_rapor.pdf", StoragePaths.ReportFileName(1, "a", "g", "c", "k"));
        Assert.Equal("_1_a_g_c_rapor_13508-2.pdf", StoragePaths.Report13508FileName(1, "a", "g", "c", "k"));
    }

    [Fact]
    public void StreetDir_HiyerarsiDogru()
    {
        var dir = StoragePaths.StreetDir(@"C:\root", "Proje X", "Yeni Mahalle", "1. Sokak");
        Assert.Equal(Path.Combine(@"C:\root", "projeler", "Proje X", "Yeni Mahalle", "1. Sokak"), dir);
    }

    [Fact]
    public void StreetDir_BosMahalleSokak_FallbackKlasorler()
    {
        var dir = StoragePaths.StreetDir(@"C:\root", "P", null, null);
        Assert.Contains("Mahalle Belirsiz", dir);
        Assert.Contains("Sokak Belirsiz", dir);
    }
}
