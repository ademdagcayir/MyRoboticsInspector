using MyRoboticsInspector.Models;

namespace MyRoboticsInspector.Services;

/// <summary>
/// DB-bound wrapper around <see cref="ReportRenderer"/>: loads inspection + related entities
/// from SQLite, then hands them to the pure renderer.
/// </summary>
public class ReportService
{
    private readonly DatabaseService _db;

    public ReportService(DatabaseService db)
    {
        _db = db;
    }

    public async Task<string> GenerateInspectionReportAsync(int inspectionId, string outputDir)
    {
        var conn = await _db.GetConnectionAsync();
        var inspection = await conn.GetAsync<Inspection>(inspectionId);
        var job = await conn.FindAsync<Job>(inspection.JobId);
        Customer? customer = job is null ? null : await conn.FindAsync<Customer>(job.CustomerId);
        var defects = await _db.GetDefectsAsync(inspectionId);
        var settings = await _db.GetSettingsAsync();

        Directory.CreateDirectory(outputDir);
        var fileName = $"Inceleme_{inspectionId}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
        var fullPath = Path.Combine(outputDir, fileName);

        ReportRenderer.Render(settings, inspection, job, customer, defects, fullPath);
        return fullPath;
    }

    /// <summary>
    /// Bir kanal için referans formatında İKİ raporu da üretir ve sokağın
    /// <c>rapor/</c> klasörüne yazar. Döndürür: (klasikYol, en13508Yol).
    /// </summary>
    public async Task<(string classic, string std13508)> GenerateChannelReportsAsync(int inspectionId)
    {
        var conn = await _db.GetConnectionAsync();
        var inspection = await conn.GetAsync<Inspection>(inspectionId);
        var job = await conn.FindAsync<Job>(inspection.JobId);
        Customer? customer = job is null ? null : await conn.FindAsync<Customer>(job.CustomerId);
        var defects = await _db.GetDefectsAsync(inspectionId);
        var settings = await _db.GetSettingsAsync();

        // Teknisyen/Görüntüleme Yapan boşsa son giriş yapan profil adını kullan (bellekte, kaydedilmez)
        if (string.IsNullOrWhiteSpace(settings.OperatorName))
        {
            try
            {
                var profiles = await conn.Table<Profile>().ToListAsync();
                var op = profiles
                    .OrderByDescending(p => p.LastLoginAt ?? p.CreatedAt)
                    .FirstOrDefault();
                if (op is not null && !string.IsNullOrWhiteSpace(op.Name))
                    settings.OperatorName = op.Name;
            }
            catch { /* profil yoksa boş kalır */ }
        }

        var root = string.IsNullOrWhiteSpace(settings.StoragePath)
            ? FileSystem.AppDataDirectory
            : settings.StoragePath;

        var dir = StoragePaths.ReportsDir(root, job?.Title, job?.Neighborhood, inspection.Street);
        Directory.CreateDirectory(dir);

        var fallback = $"kanal_{inspection.KanalNo ?? inspection.Id}";
        var classicPath = Path.Combine(dir,
            StoragePaths.ReportFileName(inspection.KanalNo, inspection.Street,
                inspection.EntryManhole, inspection.ExitManhole, fallback));
        var stdPath = Path.Combine(dir,
            StoragePaths.Report13508FileName(inspection.KanalNo, inspection.Street,
                inspection.EntryManhole, inspection.ExitManhole, fallback));

        ChannelReportRenderer.RenderClassic(settings, job, customer, inspection, defects, classicPath);
        ChannelReportRenderer.Render13508(settings, job, customer, inspection, defects, stdPath);

        return (classicPath, stdPath);
    }
}
