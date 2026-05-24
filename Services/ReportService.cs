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
}
