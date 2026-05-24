using MyRoboticsInspector.Models;
using MyRoboticsInspector.Services;
using QuestPDF.Infrastructure;

QuestPDF.Settings.License = LicenseType.Community;

var outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "myrobotics_pdf_preview");
Directory.CreateDirectory(outDir);

// 1) Generate three sample defect photos so the layout has real images.
var samplePhotos = GenerateSamplePhotos(outDir);

// 2) Build a fake inspection with realistic data.
var settings = new AppSettings
{
    CompanyName = "My Robotics",
    ProjectName = "Atakent Yağmursuyu Hat Kontrolü",
    Neighborhood = "Atakent",
    Street = "234. Sokak",
    OperatorName = "Adem Dagcayir",
};

var customer = new Customer
{
    Id = 1,
    Name = "Küçükçekmece Belediyesi",
    Phone = "0212 411 60 60",
    Email = "fen@kucukcekmece.gov.tr",
    Address = "Kartaltepe Mah. Belediye Cad. No:1 Küçükçekmece/İstanbul",
};

var job = new Job
{
    Id = 1,
    CustomerId = 1,
    Title = "Atakent - 234. Sok - Yağmursuyu Hat 1",
    Site = "Atakent / 234. Sokak (no:12 - no:38 arası)",
    Status = JobStatus.Completed,
};

var inspection = new Inspection
{
    Id = 42,
    JobId = 1,
    StartedAt = new DateTime(2026, 5, 21, 9, 15, 0),
    FinishedAt = new DateTime(2026, 5, 21, 10, 47, 0),
    DistanceMeters = 67.4,
    PipeDiameter = "300 mm",
    PipeMaterial = "Beton",
    Notes = "Sabah saatlerinde başlandı. 12 numaralı baca ağzından giriş. " +
            "20.4 m'de hat sağa dönüyor. Hat genel olarak kuru, akış yok. " +
            "Operatör notu: 38 numaralı bacaya ulaşıldı, başka hat takip edilmedi."
};

var defects = new List<Defect>
{
    new()
    {
        InspectionId = 42, Severity = DefectSeverity.High, Type = "Çatlak",
        DistanceMeters = 8.2, VideoTimestampMs = (long)TimeSpan.FromMinutes(3.5).TotalMilliseconds,
        PhotoPath = samplePhotos[0],
        Description = "Saat 3 yönünde 40 cm uzunluğunda boyuna çatlak. Dış cidardan henüz toprak akışı görülmüyor ama izlenmesi gerekir. Kanal hattının ilk bölümünde, beton boru birleşim yerinin hemen ardında.",
        CreatedAt = new DateTime(2026, 5, 21, 9, 18, 30),
    },
    new()
    {
        InspectionId = 42, Severity = DefectSeverity.Medium, Type = "Kök Girişi",
        DistanceMeters = 23.6, VideoTimestampMs = (long)TimeSpan.FromMinutes(18.2).TotalMilliseconds,
        PhotoPath = samplePhotos[1],
        Description = "Saat 12 yönünden ince kök kümesi sarkıyor. Şu an akışı engellemiyor ama bakım turunda mekanik temizlik önerilir.",
        CreatedAt = new DateTime(2026, 5, 21, 9, 33, 12),
    },
    new()
    {
        InspectionId = 42, Severity = DefectSeverity.Critical, Type = "Çökme",
        DistanceMeters = 45.1, VideoTimestampMs = (long)TimeSpan.FromMinutes(42.8).TotalMilliseconds,
        PhotoPath = samplePhotos[2],
        Description = "Boru çapının %35'ine varan üst yarıda yapısal çökme. Acil müdahale önerilir. Yerin üst kotunda araç trafiği var.",
        CreatedAt = new DateTime(2026, 5, 21, 9, 57, 51),
    },
    new()
    {
        InspectionId = 42, Severity = DefectSeverity.Low, Type = "Yağ / Tortu",
        DistanceMeters = 58.0, VideoTimestampMs = (long)TimeSpan.FromMinutes(56.4).TotalMilliseconds,
        // No photo — to test the placeholder branch
        Description = "Tabanda yumuşak çamur birikimi (~3 cm). Genel temizlikte halledilebilir.",
        CreatedAt = new DateTime(2026, 5, 21, 10, 11, 22),
    },
};

// 3) Render PDF.
var pdfPath = Path.Combine(outDir, "sample-report.pdf");
ReportRenderer.Render(settings, inspection, job, customer, defects, pdfPath);
Console.WriteLine($"PDF written: {pdfPath}");

// 4) Render each page to PNG so it can be previewed without a PDF viewer.
ReportRenderer.RenderToImages(settings, inspection, job, customer, defects,
    pageIndex => Path.Combine(outDir, $"sample-page-{pageIndex + 1}.png"));
foreach (var f in Directory.GetFiles(outDir, "sample-page-*.png"))
    Console.WriteLine($"PNG written: {f}");


static string[] GenerateSamplePhotos(string outDir)
{
    // Look up ffmpeg via PATH or known winget Links shim.
    var ffmpeg = ResolveFfmpeg();
    if (ffmpeg is null)
    {
        Console.WriteLine("(ffmpeg bulunamadi, foto placeholder'lar kullanilacak)");
        return new[] { "", "", "" };
    }

    var photos = new[]
    {
        Path.Combine(outDir, "defect-1.png"),
        Path.Combine(outDir, "defect-2.png"),
        Path.Combine(outDir, "defect-3.png"),
    };
    var sources = new[] { "testsrc2", "smptebars", "rgbtestsrc" };
    var labels = new[] { "ÇATLAK 8.2 m", "KÖK 23.6 m", "ÇÖKME 45.1 m" };
    var font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf")
                   .Replace("\\", "/").Replace(":", @"\:");

    for (int i = 0; i < 3; i++)
    {
        var vf = $"drawtext=text='{labels[i]}':fontfile='{font}':x=20:y=h-th-20:fontsize=42:fontcolor=yellow:" +
                 "box=1:boxcolor=black@0.6:boxborderw=8";
        var args = $"-y -hide_banner -loglevel error " +
                   $"-f lavfi -i {sources[i]}=size=640x360:rate=1 " +
                   $"-vf \"{vf}\" -frames:v 1 \"{photos[i]}\"";
        var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(15_000);
        if (!File.Exists(photos[i])) Console.WriteLine($"!! sample {i} failed: {p.StandardError.ReadToEnd()}");
    }
    return photos;
}

static string? ResolveFfmpeg()
{
    var candidates = new[]
    {
        "ffmpeg.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "Microsoft", "WinGet", "Links", "ffmpeg.exe")
    };
    foreach (var c in candidates)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(c, "-version")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) continue;
            p.WaitForExit(2000);
            if (p.ExitCode == 0) return c;
        }
        catch { }
    }
    return null;
}
