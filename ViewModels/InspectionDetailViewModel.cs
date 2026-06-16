using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRoboticsInspector.Models;
using MyRoboticsInspector.Services;

namespace MyRoboticsInspector.ViewModels;

[QueryProperty(nameof(InspectionId), "id")]
public partial class InspectionDetailViewModel : BaseViewModel
{
    private readonly DatabaseService _db;
    private readonly ReportService _reports;
    private readonly BackupService _backup;

    public ObservableCollection<Defect> Defects { get; } = new();

    [ObservableProperty] private int inspectionId;
    [ObservableProperty] private Inspection? inspection;
    [ObservableProperty] private Job? job;
    [ObservableProperty] private Customer? customer;
    [ObservableProperty] private string? summary;
    [ObservableProperty] private bool isBackedUp;
    [ObservableProperty] private string backupBadgeText = "";

    public InspectionDetailViewModel(DatabaseService db, ReportService reports, BackupService backup)
    {
        _db = db;
        _reports = reports;
        _backup = backup;
        Title = "İnceleme Detayı";
    }

    partial void OnInspectionIdChanged(int value)
    {
        if (value > 0) SafeLoad();
    }

    /// <summary>Fire-and-forget LoadAsync'i güvenle çalıştırır — hata sessizce kaybolmaz.</summary>
    private async void SafeLoad()
    {
        try { await LoadAsync(); }
        catch (Exception ex)
        {
            StatusMessage = $"Yükleme hatası: {ex.Message}";
        }
    }

    public async Task LoadAsync()
    {
        try
        {
            IsBusy = true;
            var conn = await _db.GetConnectionAsync();
            Inspection = await conn.FindAsync<Inspection>(InspectionId);
            if (Inspection is null) return;
            Job = await conn.FindAsync<Job>(Inspection.JobId);
            if (Job is not null) Customer = await conn.FindAsync<Customer>(Job.CustomerId);

            var defects = await _db.GetDefectsAsync(InspectionId);
            Defects.Clear();
            foreach (var d in defects) Defects.Add(d);

            Title = Job?.Title ?? $"İnceleme #{InspectionId}";
            Summary = $"{Customer?.Name ?? "-"} • {Inspection.StartedAt:dd.MM.yyyy HH:mm} • {Defects.Count} kusur";

            IsBackedUp = _backup.IsInBackup(Inspection.VideoPath)
                          || defects.Any(d => _backup.IsInBackup(d.PhotoPath));
            BackupBadgeText = IsBackedUp
                ? "✓ OneDrive yedek"
                : "⚠ Sadece yerel";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DeleteDefectAsync(Defect? defect)
    {
        if (defect is null) return;
        await _db.DeleteDefectAsync(defect);
        Defects.Remove(defect);
    }

    [RelayCommand]
    private async Task ReviewVideoAsync()
    {
        if (Inspection is null) return;
        await Shell.Current.GoToAsync($"inspectionreview?id={Inspection.Id}");
    }

    [RelayCommand]
    private async Task GenerateReportAsync()
    {
        if (Inspection is null) return;
        try
        {
            IsBusy = true;
            var dir = Path.Combine(FileSystem.AppDataDirectory, "reports");
            var path = await _reports.GenerateInspectionReportAsync(Inspection.Id, dir);
            StatusMessage = $"Rapor: {Path.GetFileName(path)}";
            await Launcher.OpenAsync(new OpenFileRequest("Rapor", new ReadOnlyFile(path)));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Rapor hatası: {ex.Message}";
        }
        finally { IsBusy = false; }
    }
}
