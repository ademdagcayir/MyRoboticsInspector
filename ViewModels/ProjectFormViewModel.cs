using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRoboticsInspector.Models;
using MyRoboticsInspector.Services;

namespace MyRoboticsInspector.ViewModels;

[QueryProperty(nameof(ProjectId), "id")]
public partial class ProjectFormViewModel : BaseViewModel
{
    private readonly DatabaseService _db;

    public ObservableCollection<Customer> Customers { get; } = new();

    [ObservableProperty] private int projectId;
    [ObservableProperty] private Job editingProject = new();
    [ObservableProperty] private Customer? selectedCustomer;
    [ObservableProperty] private bool isNew = true;

    [ObservableProperty] private LabeledOption<InspectionStage>? selectedStage;
    [ObservableProperty] private LabeledOption<InspectionPurpose>? selectedPurpose;

    public List<LabeledOption<InspectionStage>> StageOptions { get; } =
        Enum.GetValues<InspectionStage>()
            .Select(s => new LabeledOption<InspectionStage>(s, s.GetLabel()))
            .ToList();

    public List<LabeledOption<InspectionPurpose>> PurposeOptions { get; } =
        Enum.GetValues<InspectionPurpose>()
            .Select(p => new LabeledOption<InspectionPurpose>(p, p.GetLabel()))
            .ToList();

    public string PageHeader => IsNew ? "Yeni Proje" : "Proje Düzenle";

    partial void OnIsNewChanged(bool value) => OnPropertyChanged(nameof(PageHeader));

    public ProjectFormViewModel(DatabaseService db)
    {
        _db = db;
        Title = "Proje";
    }

    partial void OnProjectIdChanged(int value)
    {
        // Shell QueryProperty bu setter'ı tetikler — formu yükle
        _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        var customers = await _db.GetCustomersAsync();
        Customers.Clear();
        foreach (var c in customers) Customers.Add(c);

        if (ProjectId > 0)
        {
            var conn = await _db.GetConnectionAsync();
            var existing = await conn.FindAsync<Job>(ProjectId);
            if (existing is not null)
            {
                EditingProject = existing;
                IsNew = false;
                SelectedCustomer = Customers.FirstOrDefault(c => c.Id == existing.CustomerId);
            }
        }
        else
        {
            EditingProject = new Job { ProjectDate = DateTime.Now };
            IsNew = true;
        }

        SelectedStage = StageOptions.FirstOrDefault(s => s.Value == EditingProject.Stage)
                        ?? StageOptions[0];
        SelectedPurpose = PurposeOptions.FirstOrDefault(p => p.Value == EditingProject.Purpose)
                          ?? PurposeOptions[0];
        Title = PageHeader;
    }

    [RelayCommand]
    private async Task PickLogoAsync()
    {
        try
        {
            var photo = await MediaPicker.Default.PickPhotoAsync(new MediaPickerOptions
            {
                Title = "Proje logosu seç"
            });
            if (photo is null) return;

            // Kopyala — orijinal dosya silinirse referansımız kalsın
            var targetDir = Path.Combine(FileSystem.AppDataDirectory, "logos");
            Directory.CreateDirectory(targetDir);
            var target = Path.Combine(targetDir, $"logo_{Guid.NewGuid():N}{Path.GetExtension(photo.FileName)}");
            using (var src = await photo.OpenReadAsync())
            using (var dst = File.Create(target))
                await src.CopyToAsync(dst);

            EditingProject.LogoPath = target;
            OnPropertyChanged(nameof(EditingProject));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Logo seçilemedi: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearLogo()
    {
        EditingProject.LogoPath = null;
        OnPropertyChanged(nameof(EditingProject));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(EditingProject.Title))
        {
            StatusMessage = "Proje adı zorunlu";
            return;
        }
        if (SelectedCustomer is null)
        {
            StatusMessage = "Müşteri seçilmeli";
            return;
        }

        EditingProject.CustomerId = SelectedCustomer.Id;
        EditingProject.Stage = SelectedStage?.Value ?? InspectionStage.B;
        EditingProject.Purpose = SelectedPurpose?.Value ?? InspectionPurpose.A;

        await _db.SaveJobAsync(EditingProject);

        // Her proje için projeler/{ProjeAdı} klasörünü oluştur (videolar buraya gelecek)
        try
        {
            var settings = await _db.GetSettingsAsync();
            var root = string.IsNullOrWhiteSpace(settings.StoragePath)
                ? FileSystem.AppDataDirectory
                : settings.StoragePath;
            Directory.CreateDirectory(StoragePaths.ProjectDir(root, EditingProject.Title));
        }
        catch { /* klasör oluşturulamazsa kayıt yine de geçerli */ }

        StatusMessage = IsNew ? "Proje oluşturuldu" : "Proje kaydedildi";

        await Shell.Current.GoToAsync("..");
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        await Shell.Current.GoToAsync("..");
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (IsNew) return;
        var confirmed = await Shell.Current.DisplayAlert(
            "Projeyi sil",
            $"\"{EditingProject.Title}\" silinsin mi?\n\nBu işlem geri alınamaz.",
            "Sil", "Vazgeç");
        if (!confirmed) return;
        await _db.DeleteJobAsync(EditingProject);
        await Shell.Current.GoToAsync("..");
    }

    [RelayCommand]
    private async Task OpenChannelsAsync()
    {
        if (IsNew) return;
        await Shell.Current.GoToAsync($"//projects/projectchannels?id={EditingProject.Id}");
    }

    [RelayCommand]
    private async Task DuplicateAsync()
    {
        if (IsNew) return;
        var copy = new Job
        {
            CustomerId = EditingProject.CustomerId,
            Title = EditingProject.Title + " (Kopya)",
            Province = EditingProject.Province,
            District = EditingProject.District,
            Neighborhood = EditingProject.Neighborhood,
            Site = EditingProject.Site,
            ProjectDate = DateTime.Now,
            HakedisNo = EditingProject.HakedisNo,
            Stage = EditingProject.Stage,
            Purpose = EditingProject.Purpose,
            LogoPath = EditingProject.LogoPath,
            Status = JobStatus.Planned,
            Notes = EditingProject.Notes
        };
        await _db.SaveJobAsync(copy);
        await Shell.Current.GoToAsync($"..?id={copy.Id}");
    }
}
