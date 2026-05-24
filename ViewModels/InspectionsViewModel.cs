using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRoboticsInspector.Models;
using MyRoboticsInspector.Services;

namespace MyRoboticsInspector.ViewModels;

public partial class InspectionsViewModel : BaseViewModel
{
    private readonly DatabaseService _db;
    private readonly ReportService _reports;

    public ObservableCollection<Inspection> Inspections { get; } = new();

    [ObservableProperty] private Inspection? selected;

    public InspectionsViewModel(DatabaseService db, ReportService reports)
    {
        _db = db;
        _reports = reports;
        Title = "İncelemeler";
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            IsBusy = true;
            var list = await _db.GetInspectionsAsync();
            Inspections.Clear();
            foreach (var i in list) Inspections.Add(i);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task OpenDetailAsync(Inspection? inspection)
    {
        if (inspection is null) return;
        await Shell.Current.GoToAsync($"inspectiondetail?id={inspection.Id}");
    }

    [RelayCommand]
    private async Task GenerateReportAsync(Inspection? inspection)
    {
        if (inspection is null) return;
        try
        {
            IsBusy = true;
            var dir = Path.Combine(FileSystem.AppDataDirectory, "reports");
            var path = await _reports.GenerateInspectionReportAsync(inspection.Id, dir);
            StatusMessage = $"Rapor oluşturuldu: {Path.GetFileName(path)}";
            await Launcher.OpenAsync(new OpenFileRequest("Rapor", new ReadOnlyFile(path)));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Rapor hatası: {ex.Message}";
        }
        finally { IsBusy = false; }
    }
}
