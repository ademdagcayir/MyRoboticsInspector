using MyRoboticsInspector.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QColors = QuestPDF.Helpers.Colors;
using QContainer = QuestPDF.Infrastructure.IContainer;

namespace MyRoboticsInspector.Services;

/// <summary>
/// Pure PDF layout. No I/O beyond the output file — takes already-loaded data and writes a PDF.
/// Kept separate from <see cref="ReportService"/> so it can be exercised by the standalone
/// PdfPreview tool without dragging in the SQLite/DI graph.
/// </summary>
public static class ReportRenderer
{
    public static void Render(
        AppSettings settings,
        Inspection inspection,
        Job? job,
        Customer? customer,
        IReadOnlyList<Defect> defects,
        string outputPath)
    {
        Document.Create(container =>
        {
            container.Page(page => ComposeCoverAndSummary(page, settings, inspection, job, customer, defects));

            if (defects.Count > 0)
            {
                container.Page(page => ComposeDefectCards(page, inspection, defects));
            }
        })
        .GeneratePdf(outputPath);
    }

    public static void RenderToImages(
        AppSettings settings,
        Inspection inspection,
        Job? job,
        Customer? customer,
        IReadOnlyList<Defect> defects,
        Func<int, string> imagePathFactory)
    {
        Document.Create(container =>
        {
            container.Page(page => ComposeCoverAndSummary(page, settings, inspection, job, customer, defects));
            if (defects.Count > 0)
                container.Page(page => ComposeDefectCards(page, inspection, defects));
        })
        .GenerateImages(i => imagePathFactory(i), new ImageGenerationSettings { RasterDpi = 150 });
    }

    private static void ComposeCoverAndSummary(
        PageDescriptor page, AppSettings settings, Inspection inspection,
        Job? job, Customer? customer, IReadOnlyList<Defect> defects)
    {
        page.Size(PageSizes.A4);
        page.Margin(2, Unit.Centimetre);
        page.PageColor(QColors.White);
        page.DefaultTextStyle(t => t.FontSize(11));

        page.Header().Row(row =>
        {
            row.RelativeItem().Column(c =>
            {
                c.Item().Text(settings.CompanyName).FontSize(20).Bold();
                c.Item().Text("Kanal Görüntüleme Raporu").FontSize(14).SemiBold();
            });
            row.ConstantItem(140).AlignRight().Text($"Rapor No: {inspection.Id}\n{DateTime.Now:dd.MM.yyyy HH:mm}")
                .FontSize(10);
        });

        page.Content().PaddingVertical(10).Column(col =>
        {
            col.Spacing(10);

            col.Item().Row(row =>
            {
                row.RelativeItem().Element(c => InfoBox(c, "Müşteri", new[]
                {
                    ("Ad", customer?.Name ?? "-"),
                    ("Telefon", customer?.Phone ?? "-"),
                    ("E-posta", customer?.Email ?? "-"),
                    ("Adres", customer?.Address ?? "-"),
                }));
                row.ConstantItem(15);
                row.RelativeItem().Element(c => InfoBox(c, "İş Bilgileri", new[]
                {
                    ("İş", job?.Title ?? "-"),
                    ("Saha", job?.Site ?? "-"),
                    ("Başlangıç", inspection.StartedAt.ToString("dd.MM.yyyy HH:mm")),
                    ("Bitiş", inspection.FinishedAt?.ToString("dd.MM.yyyy HH:mm") ?? "-"),
                    ("Süre", FormatDuration(inspection.StartedAt, inspection.FinishedAt)),
                    ("Mesafe", inspection.DistanceMeters is double m ? $"{m:0.0} m" : "-"),
                    ("Boru çapı", inspection.PipeDiameter ?? "-"),
                    ("Boru malzemesi", inspection.PipeMaterial ?? "-"),
                }));
            });

            if (!string.IsNullOrWhiteSpace(inspection.Notes))
            {
                col.Item().Element(c => InfoBox(c, "İnceleme Notları", new[] { ("", inspection.Notes!) }));
            }

            col.Item().PaddingTop(5).Text($"Bulgular ({defects.Count})").FontSize(14).SemiBold();

            if (defects.Count == 0)
            {
                col.Item().Padding(8).Text("Herhangi bir kusur işaretlenmedi.").Italic().FontColor(QColors.Grey.Darken1);
            }
            else
            {
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(30);
                        c.ConstantColumn(70);
                        c.ConstantColumn(60);
                        c.ConstantColumn(85);
                        c.RelativeColumn();
                    });

                    table.Header(h =>
                    {
                        h.Cell().Element(HeaderCell).Text("#").Bold();
                        h.Cell().Element(HeaderCell).Text("Video").Bold();
                        h.Cell().Element(HeaderCell).Text("Mesafe").Bold();
                        h.Cell().Element(HeaderCell).Text("Şiddet").Bold();
                        h.Cell().Element(HeaderCell).Text("Tür / Açıklama").Bold();
                    });

                    int i = 1;
                    foreach (var d in defects)
                    {
                        table.Cell().Element(BodyCell).Text(i++.ToString());
                        table.Cell().Element(BodyCell).Text(TimeSpan.FromMilliseconds(d.VideoTimestampMs).ToString(@"hh\:mm\:ss"));
                        table.Cell().Element(BodyCell).Text(d.DistanceMeters?.ToString("0.0") ?? "-");
                        table.Cell().Element(BodyCell).Element(c => SeverityChip(c, d.Severity));
                        table.Cell().Element(BodyCell).Text($"{d.Type}  {d.Description}".Trim());
                    }
                });
            }
        });

        page.Footer().AlignCenter().Text(x =>
        {
            x.DefaultTextStyle(t => t.FontSize(9).FontColor(QColors.Grey.Darken1));
            x.Span("Sayfa ");
            x.CurrentPageNumber();
            x.Span(" / ");
            x.TotalPages();
        });
    }

    private static void ComposeDefectCards(
        PageDescriptor page, Inspection inspection, IReadOnlyList<Defect> defects)
    {
        page.Size(PageSizes.A4);
        page.Margin(2, Unit.Centimetre);
        page.PageColor(QColors.White);
        page.DefaultTextStyle(t => t.FontSize(10));

        page.Header().PaddingBottom(8).Row(row =>
        {
            row.RelativeItem().Text("Detaylı Bulgular").FontSize(14).Bold();
            row.AutoItem().Text($"Rapor No: {inspection.Id}").FontSize(10).FontColor(QColors.Grey.Darken1);
        });

        page.Content().Column(col =>
        {
            col.Spacing(10);
            int i = 1;
            foreach (var d in defects)
            {
                var index = i++;
                col.Item().ShowEntire().Element(c => DefectCard(c, index, d));
            }
        });

        page.Footer().AlignCenter().Text(x =>
        {
            x.DefaultTextStyle(t => t.FontSize(9).FontColor(QColors.Grey.Darken1));
            x.Span("Sayfa ");
            x.CurrentPageNumber();
            x.Span(" / ");
            x.TotalPages();
        });
    }

    private static void DefectCard(QContainer container, int index, Defect d)
    {
        container.Border(0.5f).BorderColor(QColors.Grey.Lighten1)
            .Background(QColors.Grey.Lighten5).Padding(10)
            .Row(row =>
            {
                row.ConstantItem(220).Column(c =>
                {
                    if (!string.IsNullOrWhiteSpace(d.PhotoPath) && File.Exists(d.PhotoPath))
                    {
                        try { c.Item().Height(160).Image(d.PhotoPath).FitArea(); }
                        catch { c.Item().Height(160).Background(QColors.Grey.Lighten3).AlignCenter().AlignMiddle().Text("(görsel okunamadı)").FontSize(9); }
                    }
                    else
                    {
                        c.Item().Height(160).Background(QColors.Grey.Lighten3).AlignCenter().AlignMiddle().Text("(görsel yok)").FontSize(9).FontColor(QColors.Grey.Darken1);
                    }
                });

                row.ConstantItem(12);

                row.RelativeItem().Column(c =>
                {
                    c.Item().Row(r =>
                    {
                        r.AutoItem().Text($"#{index}").FontSize(20).Bold();
                        r.ConstantItem(10);
                        r.AutoItem().AlignBottom().Element(x => SeverityChip(x, d.Severity));
                        r.RelativeItem().AlignRight().AlignBottom().Text(d.Type ?? "-")
                            .FontSize(14).SemiBold();
                    });

                    c.Item().PaddingTop(6).Row(r =>
                    {
                        r.RelativeItem().Column(meta =>
                        {
                            meta.Item().Text("Mesafe").FontSize(8).FontColor(QColors.Grey.Darken1);
                            meta.Item().Text(d.DistanceMeters?.ToString("0.0 m") ?? "-").FontSize(13).SemiBold();
                        });
                        r.RelativeItem().Column(meta =>
                        {
                            meta.Item().Text("Video Zamanı").FontSize(8).FontColor(QColors.Grey.Darken1);
                            meta.Item().Text(TimeSpan.FromMilliseconds(d.VideoTimestampMs).ToString(@"hh\:mm\:ss"))
                                .FontSize(13).SemiBold();
                        });
                        r.RelativeItem().Column(meta =>
                        {
                            meta.Item().Text("Eklenme").FontSize(8).FontColor(QColors.Grey.Darken1);
                            meta.Item().Text(d.CreatedAt.ToString("dd.MM HH:mm")).FontSize(13).SemiBold();
                        });
                    });

                    if (!string.IsNullOrWhiteSpace(d.Description))
                    {
                        c.Item().PaddingTop(8).Text("Açıklama").FontSize(8).FontColor(QColors.Grey.Darken1);
                        c.Item().Text(d.Description).FontSize(11);
                    }
                });
            });
    }

    private static void InfoBox(QContainer container, string title, IEnumerable<(string label, string value)> rows)
    {
        container.Border(0.5f).BorderColor(QColors.Grey.Lighten1)
            .Background(QColors.Grey.Lighten5).Padding(10)
            .Column(c =>
            {
                c.Item().Text(title).FontSize(11).Bold().FontColor(QColors.Grey.Darken3);
                c.Item().PaddingTop(4);
                foreach (var (label, value) in rows)
                {
                    if (string.IsNullOrEmpty(label))
                    {
                        c.Item().Text(value).FontSize(10);
                    }
                    else
                    {
                        c.Item().Row(row =>
                        {
                            row.ConstantItem(90).Text(label).FontSize(9).FontColor(QColors.Grey.Darken1);
                            row.RelativeItem().Text(value).FontSize(10);
                        });
                    }
                }
            });
    }

    private static void SeverityChip(QContainer container, DefectSeverity severity)
    {
        var (label, bg) = severity switch
        {
            DefectSeverity.Critical => ("Kritik", QColors.Red.Darken1),
            DefectSeverity.High     => ("Yüksek", QColors.Red.Lighten1),
            DefectSeverity.Medium   => ("Orta",   QColors.Orange.Lighten1),
            DefectSeverity.Low      => ("Düşük",  QColors.Yellow.Darken2),
            _                       => ("Bilgi",  QColors.Grey.Lighten1)
        };
        container.Background(bg).PaddingVertical(2).PaddingHorizontal(8)
            .Text(label).FontSize(9).Bold().FontColor(QColors.White);
    }

    private static QContainer HeaderCell(QContainer c) =>
        c.Background(QColors.Grey.Lighten2).PaddingVertical(4).PaddingHorizontal(6);

    private static QContainer BodyCell(QContainer c) =>
        c.BorderBottom(0.5f).BorderColor(QColors.Grey.Lighten1).PaddingVertical(3).PaddingHorizontal(6);

    private static string FormatDuration(DateTime start, DateTime? end)
    {
        if (end is null) return "-";
        var d = end.Value - start;
        return d.TotalHours >= 1
            ? $"{(int)d.TotalHours}s {d.Minutes}dk"
            : $"{d.Minutes}dk {d.Seconds}sn";
    }
}
