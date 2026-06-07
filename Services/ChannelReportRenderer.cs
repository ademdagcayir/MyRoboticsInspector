using MyRoboticsInspector.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QColors = QuestPDF.Helpers.Colors;
using QContainer = QuestPDF.Infrastructure.IContainer;
using FlowDirection = MyRoboticsInspector.Models.FlowDirection;

namespace MyRoboticsInspector.Services;

/// <summary>
/// Referans "pipecam" ürünüyle uyumlu, kanal başına İKİ PDF üretir:
///   1) "Görüntüleme Raporu"      — klasik (üst bilgi bandı + eğim bölümü + fotoğraflı kusur kartları)
///   2) "TSE EN 13508-2:2003+A1:2011" — standart tablo formatı
/// Saf layout: girdi olarak yüklenmiş veriyi alır, sadece çıktı dosyasını yazar.
/// </summary>
public static class ChannelReportRenderer
{
    // Renkler
    private static string Ink   => QColors.Grey.Darken4;
    private static string Muted => QColors.Grey.Darken1;
    private static string Line  => QColors.Grey.Lighten1;
    private static string Head  => QColors.Grey.Lighten2;
    private static string Soft  => QColors.Grey.Lighten4;

    // ──────────────────────────────────────────────────────────────────────
    //  1) KLASİK "GÖRÜNTÜLEME RAPORU"
    // ──────────────────────────────────────────────────────────────────────
    public static void RenderClassic(
        AppSettings settings, Job? job, Customer? customer,
        Inspection insp, IReadOnlyList<Defect> defects, string outputPath)
    {
        Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.2f, Unit.Centimetre);
                page.PageColor(QColors.White);
                page.DefaultTextStyle(t => t.FontSize(9).FontColor(Ink));

                page.Header().Element(h => ClassicHeader(h, settings, job, customer, insp));

                page.Content().PaddingVertical(8).Column(col =>
                {
                    col.Spacing(10);
                    col.Item().Element(e => SlopeSection(e, insp));
                    col.Item().PaddingTop(2).Text($"Bulgular / Fotoğraflar ({defects.Count})")
                        .FontSize(12).SemiBold();
                    col.Item().Element(e => DefectGrid(e, insp, defects));
                });

                page.Footer().Element(FooterPager);
            });
        })
        .GeneratePdf(outputPath);
    }

    private static void ClassicHeader(QContainer c, AppSettings s, Job? job, Customer? cust, Inspection insp)
    {
        c.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(t =>
                {
                    t.Item().Text($"Görüntüleme Raporu: {insp.Street ?? "-"}").FontSize(15).Bold();
                    t.Item().Text(s.CompanyName).FontSize(9).FontColor(Muted);
                });
                row.ConstantItem(150).AlignRight().Column(t =>
                {
                    t.Item().Text($"Kanal No: {insp.KanalNo?.ToString() ?? "-"}").FontSize(11).SemiBold();
                    t.Item().Text(insp.StartedAt.ToString("dd.MM.yyyy HH:mm:ss")).FontSize(9).FontColor(Muted);
                });
            });

            col.Item().PaddingTop(6).Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(95); c.RelativeColumn();
                    c.ConstantColumn(95); c.RelativeColumn();
                });
                Kv(table, "Proje", job?.Title);
                Kv(table, "Proje Türü", ProjectTypeLabel(insp.ProjectType));
                Kv(table, "Teknisyen", s.OperatorName);
                Kv(table, "Kamera", "—"); // NOT: kamera etiketi; kimlik bilgisi basılmaz
                Kv(table, "Cad. / Sok.", insp.Street);
                Kv(table, "Çap / Kesit", PipeSize(insp));
                Kv(table, "Semt / Mah.", job?.Neighborhood);
                Kv(table, "Tür (Malzeme)", insp.PipeMaterial);
                Kv(table, "Şehir", JoinNonEmpty(" ", job?.Province, job?.District));
                Kv(table, "Akış Yönü", FlowLabel(insp.FlowDirection));
                Kv(table, "Giriş Bacası", insp.EntryManhole);
                Kv(table, "Çıkış Bacası", insp.ExitManhole);
                Kv(table, "G. Mesafe (m)", insp.DistanceMeters?.ToString("0.0"));
                Kv(table, "Müşteri", cust?.Name);
            });
        });
    }

    /// <summary>Eğim (slope) bölümü. Veri kaynağı (robot eğim sensörü) henüz tanımlı değil → şimdilik placeholder.</summary>
    private static void SlopeSection(QContainer c, Inspection insp)
    {
        c.Border(0.5f).BorderColor(Line).Background(Soft).Padding(8).Column(col =>
        {
            col.Item().Text("Eğim Raporu").FontSize(11).SemiBold();
            col.Item().Row(r =>
            {
                r.RelativeItem().Text($"G.B.: {insp.EntryManhole ?? "-"}").FontSize(9);
                r.RelativeItem().Text($"Ç.B.: {insp.ExitManhole ?? "-"}").FontSize(9);
                r.RelativeItem().Text($"Çap / Kesit: {PipeSize(insp)}").FontSize(9);
                r.RelativeItem().Text($"Mesafe: {insp.DistanceMeters?.ToString("0.0") ?? "-"} m").FontSize(9);
            });
            col.Item().PaddingTop(6).Height(70).Background(QColors.White)
               .Border(0.5f).BorderColor(Line).AlignCenter().AlignMiddle()
               .Text("Eğim profili — robot eğim sensörü verisi bağlandığında çizilecek")
               .FontSize(8).Italic().FontColor(Muted);
        });
    }

    private static void DefectGrid(QContainer c, Inspection insp, IReadOnlyList<Defect> defects)
    {
        if (defects.Count == 0)
        {
            c.Padding(8).Text("Bu kanal için fotoğraf / bulgu kaydı yok.")
             .Italic().FontColor(Muted);
            return;
        }

        // İki sütunlu kart düzeni
        c.Table(table =>
        {
            table.ColumnsDefinition(cd => { cd.RelativeColumn(); cd.RelativeColumn(); });
            int i = 1;
            foreach (var d in defects)
            {
                int idx = i++;
                table.Cell().Padding(4).Element(cell => DefectCard(cell, idx, insp, d));
            }
            // tek sayıdaysa son boş hücreyi dengele
            if (defects.Count % 2 == 1) table.Cell().Padding(4);
        });
    }

    private static void DefectCard(QContainer c, int index, Inspection insp, Defect d)
    {
        c.Border(0.5f).BorderColor(Line).Background(QColors.White).Padding(6).Column(col =>
        {
            // Fotoğraf
            if (!string.IsNullOrWhiteSpace(d.PhotoPath) && File.Exists(d.PhotoPath))
            {
                try { col.Item().Height(150).Image(d.PhotoPath).FitArea(); }
                catch { col.Item().Height(150).Background(Soft).AlignCenter().AlignMiddle().Text("(görsel okunamadı)").FontSize(8); }
            }
            else
            {
                col.Item().Height(150).Background(Soft).AlignCenter().AlignMiddle()
                   .Text("(görsel yok)").FontSize(8).FontColor(Muted);
            }

            col.Item().PaddingTop(4).Row(r =>
            {
                r.AutoItem().Text($"#{index}").FontSize(11).Bold();
                r.RelativeItem().AlignRight().Text(d.IskiCode ?? d.MainCode ?? "-").FontSize(10).SemiBold();
            });

            col.Item().Row(rr =>
            {
                rr.RelativeItem().Text($"Metre: {d.DistanceMeters?.ToString("0.0") ?? "-"}").FontSize(8.5f);
                rr.RelativeItem().Text($"Süre: {VideoClock(d.VideoTimestampMs)}").FontSize(8.5f);
            });
            col.Item().Text($"Kod: {d.IskiCode ?? d.MainCode ?? "-"}").FontSize(8.5f);
            if (!string.IsNullOrWhiteSpace(d.Type))
                col.Item().Text($"Yorum: {d.Type}").FontSize(8.5f);
            col.Item().Text($"Açıklama: {d.Description ?? "-"}").FontSize(8.5f);
            col.Item().Row(r =>
            {
                r.RelativeItem().Text($"G.B.: {insp.EntryManhole ?? "-"}").FontSize(8).FontColor(Muted);
                r.RelativeItem().Text($"Ç.B.: {insp.ExitManhole ?? "-"}").FontSize(8).FontColor(Muted);
            });
        });
    }

    // ──────────────────────────────────────────────────────────────────────
    //  2) TSE EN 13508-2
    // ──────────────────────────────────────────────────────────────────────
    public static void Render13508(
        AppSettings settings, Job? job, Customer? customer,
        Inspection insp, IReadOnlyList<Defect> defects, string outputPath)
    {
        Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.2f, Unit.Centimetre);
                page.PageColor(QColors.White);
                page.DefaultTextStyle(t => t.FontSize(8.5f).FontColor(Ink));

                page.Header().Element(h => Std13508Header(h, settings, job, customer, insp));
                page.Content().PaddingVertical(8).Element(e => Std13508Table(e, insp, defects));
                page.Footer().Element(FooterPager);
            });
        })
        .GeneratePdf(outputPath);
    }

    private static void Std13508Header(QContainer c, AppSettings s, Job? job, Customer? cust, Inspection insp)
    {
        c.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Text("TSE EN 13508-2:2003+A1:2011").FontSize(13).Bold();
                row.ConstantItem(150).AlignRight().Column(t =>
                {
                    t.Item().AlignRight().Text("Tarih · Saat").FontSize(8).FontColor(Muted);
                    t.Item().AlignRight().Text(insp.StartedAt.ToString("dd.MM.yyyy HH:mm")).FontSize(9).SemiBold();
                });
            });

            col.Item().PaddingTop(6).Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(110); c.RelativeColumn();
                    c.ConstantColumn(110); c.RelativeColumn();
                });
                Kv(table, "Müşteri", cust?.Name);
                Kv(table, "Görüntüleme Yapan", s.OperatorName);
                Kv(table, "Görüntüleme Aşaması", StageLabel(job?.Stage));
                Kv(table, "Akış Yönü", FlowLabel(insp.FlowDirection));
                Kv(table, "İl", job?.Province);
                Kv(table, "İlçe", job?.District);
                Kv(table, "Semt / Mahalle", job?.Neighborhood);
                Kv(table, "Cad. / Sok.", insp.Street);
                Kv(table, "Proje Türü", ProjectTypeLabel(insp.ProjectType));
                Kv(table, "Şekil", PipeShapeLabel(insp.PipeShape));
                Kv(table, "Malzeme", insp.PipeMaterial);
                Kv(table, "Pafta Metrajı", insp.Pafta);
                Kv(table, "Giriş Bacası", insp.EntryManhole);
                Kv(table, "Giriş Baca Derinlik", insp.EntryManholeDepth?.ToString("0.0"));
                Kv(table, "Çıkış Bacası", insp.ExitManhole);
                Kv(table, "Çıkış Baca Derinlik", insp.ExitManholeDepth?.ToString("0.0"));
                Kv(table, "Yükseklik (mm)", insp.PipeHeightMm?.ToString());
                Kv(table, "Genişlik (mm)", insp.PipeWidthMm?.ToString());
                Kv(table, "Kanal Uzunluğu (m)", (insp.DistanceMeters ?? insp.PlannedMeters)?.ToString("0.0"));
                Kv(table, "Temizlenmiş Mi", insp.Cleaned switch { true => "EVET", false => "HAYIR", _ => "-" });
                Kv(table, "GDD", insp.Gdd?.ToString("0.##"));
                Kv(table, "GDF", "-"); // NOT: GDF kaynağı belirsiz — ileride netleşecek
            });
        });
    }

    private static void Std13508Table(QContainer c, Inspection insp, IReadOnlyList<Defect> defects)
    {
        c.Column(col =>
        {
            col.Item().Row(r =>
            {
                r.AutoItem().Text($"Proje · Kanal No: {insp.KanalNo?.ToString() ?? "-"}").FontSize(9).SemiBold();
                r.RelativeItem().AlignRight()
                 .Text($"Giriş: {insp.EntryManhole ?? "-"}  ·  Çıkış: {insp.ExitManhole ?? "-"}  ·  Çap/Kesit: {PipeSize(insp)}")
                 .FontSize(8).FontColor(Muted);
            });

            col.Item().PaddingTop(6).Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.ConstantColumn(42);  // Metre
                    cd.ConstantColumn(46);  // Kod
                    cd.ConstantColumn(28);  // Karakterizasyon (A)
                    cd.ConstantColumn(60);  // Nicelik 1/2
                    cd.ConstantColumn(50);  // Saat Yönü
                    cd.RelativeColumn();    // Açıklama
                    cd.RelativeColumn();    // Resim Referansı / Remarks
                });

                table.Header(h =>
                {
                    h.Cell().Element(Hc).Text("Metre");
                    h.Cell().Element(Hc).Text("Kod");
                    h.Cell().Element(Hc).Text("A");
                    h.Cell().Element(Hc).Text("Nicelik");
                    h.Cell().Element(Hc).Text("Saat Yönü");
                    h.Cell().Element(Hc).Text("Açıklama");
                    h.Cell().Element(Hc).Text("Resim / Remarks");
                });

                foreach (var d in defects)
                {
                    table.Cell().Element(Bc).Text(d.DistanceMeters?.ToString("0.0") ?? "0");
                    table.Cell().Element(Bc).Text(d.MainCode ?? d.IskiCode ?? "-");
                    table.Cell().Element(Bc).Text(JoinNonEmpty("/", d.Char1, d.Char2) ?? "-");
                    table.Cell().Element(Bc).Text(JoinNonEmpty("/", d.Quant1, d.Quant2) ?? "-");
                    table.Cell().Element(Bc).Text(ClockLabel(d.ClockFrom, d.ClockTo));
                    table.Cell().Element(Bc).Text(d.Description ?? d.Type ?? "-");
                    table.Cell().Element(Bc).Text(
                        !string.IsNullOrWhiteSpace(d.Remarks) ? d.Remarks!
                        : (!string.IsNullOrWhiteSpace(d.PhotoPath) ? $"resim/{Path.GetFileName(d.PhotoPath)}" : "-"));
                }
            });

            if (defects.Count == 0)
                col.Item().PaddingTop(8).Text("Kayıtlı bulgu yok.").Italic().FontColor(Muted);
        });
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Yardımcılar
    // ──────────────────────────────────────────────────────────────────────
    // Tablo satırına etiket+değer çifti ekler (2 sütun çift = 4 hücre)
    private static void Kv(QuestPDF.Fluent.TableDescriptor table, string label, string? value)
    {
        table.Cell().Element(LabelCell).Text(label);
        table.Cell().Element(ValueCell).Text(string.IsNullOrWhiteSpace(value) ? "-" : value);
    }

    private static QContainer LabelCell(QContainer c) =>
        c.Background(Soft).BorderBottom(0.5f).BorderColor(Line).PaddingVertical(2).PaddingHorizontal(5);
    private static QContainer ValueCell(QContainer c) =>
        c.BorderBottom(0.5f).BorderColor(Line).PaddingVertical(2).PaddingHorizontal(5);

    private static QContainer Hc(QContainer c) =>
        c.Background(Head).Border(0.5f).BorderColor(Line).PaddingVertical(3).PaddingHorizontal(4)
         .DefaultTextStyle(t => t.SemiBold().FontSize(8));
    private static QContainer Bc(QContainer c) =>
        c.Border(0.5f).BorderColor(Line).PaddingVertical(3).PaddingHorizontal(4);

    private static void FooterPager(QContainer c) =>
        c.AlignCenter().Text(x =>
        {
            x.DefaultTextStyle(t => t.FontSize(8).FontColor(Muted));
            x.Span("Sayfa "); x.CurrentPageNumber(); x.Span(" / "); x.TotalPages();
        });

    // ── Etiketler / biçimleme ──
    private static string PipeSize(Inspection i)
        => !string.IsNullOrWhiteSpace(i.PipeDiameter) ? i.PipeDiameter!
         : i.PipeWidthMm is int w ? w.ToString()
         : "-";

    private static string VideoClock(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms < 0 ? 0 : ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    private static string ClockLabel(int? from, int? to)
        => from is int f
            ? (to is int t && t != f ? $"{f}-{t}" : f.ToString())
            : "-";

    private static string ProjectTypeLabel(ProjectType t) => t switch
    {
        ProjectType.AtikSu     => "ATIK SU",
        ProjectType.YagmurSuyu => "YAĞMUR SUYU",
        ProjectType.Birlesik   => "BİRLEŞİK",
        _ => t.ToString()
    };

    private static string FlowLabel(FlowDirection f) => f switch
    {
        FlowDirection.Downstream => "Akışla aynı yönde",
        FlowDirection.Upstream   => "Akışa ters yönde",
        _ => f.ToString()
    };

    private static string PipeShapeLabel(PipeShape p) => p switch
    {
        PipeShape.Dairesel   => "Dairesel",
        PipeShape.Yumurta    => "Yumurta",
        PipeShape.Kare       => "Kare",
        PipeShape.Dikdortgen => "Dikdörtgen",
        _ => "Diğer"
    };

    private static string StageLabel(InspectionStage? s) => s switch
    {
        InspectionStage.A => "Yapım Sonrası Kontrol",
        InspectionStage.B => "Gözlemciden Kuruma",
        InspectionStage.C => "Devir Sırasında",
        _ => "-"
    };

    private static string? JoinNonEmpty(string sep, params string?[] parts)
    {
        var joined = string.Join(sep, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return string.IsNullOrWhiteSpace(joined) ? null : joined;
    }
}
