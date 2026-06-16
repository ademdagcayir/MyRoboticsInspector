using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRoboticsInspector.Models;
using MyRoboticsInspector.Services;

namespace MyRoboticsInspector.ViewModels;

/// <summary>UI satırı: proje + müşteri adı + kanal sayısı bir arada.</summary>
public partial class ProjectListItem : ObservableObject
{
    public Job Project { get; }
    [ObservableProperty] private string customerName = "";
    [ObservableProperty] private int channelCount;

    public ProjectListItem(Job p) { Project = p; }

    public string LocationLabel
    {
        get
        {
            var parts = new[] { Project.Province, Project.District, Project.Neighborhood }
                .Where(s => !string.IsNullOrWhiteSpace(s));
            return string.Join(" / ", parts);
        }
    }

    public string StageLabel => Project.Stage.GetLabel();
    public string PurposeLabel => Project.Purpose.GetLabel();
}

public partial class ProjectsViewModel : BaseViewModel
{
    private readonly DatabaseService _db;

    public ObservableCollection<ProjectListItem> Projects { get; } = new();

    [ObservableProperty] private string filterText = "";

    public ProjectsViewModel(DatabaseService db)
    {
        _db = db;
        Title = "Projeler";
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            IsBusy = true;
            var jobs = await _db.GetJobsAsync();
            var customers = await _db.GetCustomersAsync();
            var customerMap = customers.ToDictionary(c => c.Id, c => c.Name);

            Projects.Clear();
            foreach (var j in jobs)
            {
                var item = new ProjectListItem(j)
                {
                    CustomerName = customerMap.TryGetValue(j.CustomerId, out var n) ? n : "-",
                    ChannelCount = await _db.GetInspectionCountForJobAsync(j.Id),
                };
                Projects.Add(item);
            }
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task NewProjectAsync()
    {
        await Shell.Current.GoToAsync("projectform");
    }

    /// <summary>Karta tıklayınca kanallara gider; düzenleme ayrı butonla yapılır.</summary>
    [RelayCommand]
    private async Task OpenChannelsAsync(ProjectListItem? item)
    {
        if (item is null) return;
        await Shell.Current.GoToAsync($"projectchannels?id={item.Project.Id}");
    }

    [RelayCommand]
    private async Task EditAsync(ProjectListItem? item)
    {
        if (item is null) return;
        await Shell.Current.GoToAsync($"projectform?id={item.Project.Id}");
    }

    [RelayCommand]
    private async Task AddChannelAsync(ProjectListItem? item)
    {
        if (item is null) return;
        await Shell.Current.GoToAsync($"channeledit?jobId={item.Project.Id}");
    }

    [RelayCommand]
    private async Task DeleteAsync(ProjectListItem? item)
    {
        if (item is null) return;
        var confirmed = await Shell.Current.DisplayAlert(
            "Projeyi sil",
            $"\"{item.Project.Title}\" silinsin mi?\n\nProjeye bağlı tüm kanallar, kusurlar, fotoğraf ve video dosyaları da silinir.\nBu işlem geri alınamaz.",
            "Sil", "Vazgeç");
        if (!confirmed) return;

        try
        {
            // DeleteJobAsync artık tam cascade yapıyor: bağlı kanallar → kusur satırları +
            // fotoğraf/video/rapor PDF dosyaları → proje. Inline döngü gereksizdi ve
            // DeleteInspectionCascadeAsync'i atladığı için rapor PDF'lerini sızdırıyordu.
            await _db.DeleteJobAsync(item.Project);
            Projects.Remove(item);
            StatusMessage = "Proje silindi";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Silme hatası: {ex.Message}";
        }
    }
}
