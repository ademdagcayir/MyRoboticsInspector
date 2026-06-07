using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRoboticsInspector.Models;
using MyRoboticsInspector.Services;

// MAUI'nin de Microsoft.Maui.FlowDirection enum'ı var — alias ile namespace çakışmasını engelle
using FlowDirection = MyRoboticsInspector.Models.FlowDirection;

namespace MyRoboticsInspector.ViewModels;

/// <summary>Sağ panelde görünen proje kanalı satırı.</summary>
public class SiblingChannel
{
    public Inspection Channel { get; init; } = new();
    public bool HasVideo => !string.IsNullOrWhiteSpace(Channel.VideoPath) && File.Exists(Channel.VideoPath);
    public bool IsCurrent { get; init; }

    public string Label => Channel.DisplayName;

    public string Meta
    {
        get
        {
            var parts = new List<string>();
            if (Channel.DistanceMeters > 0) parts.Add($"{Channel.DistanceMeters:0.0} m");
            if (Channel.StartedAt != default) parts.Add(Channel.StartedAt.ToString("dd.MM.yyyy"));
            return string.Join("  ·  ", parts);
        }
    }
}

[QueryProperty(nameof(ChannelId), "id")]
[QueryProperty(nameof(JobId), "jobId")]
public partial class ChannelEditViewModel : BaseViewModel
{
    private readonly DatabaseService _db;
    private readonly ReportService _report;

    /// <summary>Düzenleme açıldığında kanalın sokağı — sokak değişirse klasör taşıma için.</summary>
    private string? _originalStreet;

    public ObservableCollection<SiblingChannel> SiblingChannels { get; } = new();

    /// <summary>Bu projede daha önce tanımlanmış sokak adları (Picker'da seçilebilir).</summary>
    public ObservableCollection<string> KnownStreets { get; } = new();
    [ObservableProperty] private string? selectedStreet;

    [ObservableProperty] private int channelId;
    [ObservableProperty] private int jobId;
    [ObservableProperty] private bool isNew = true;
    [ObservableProperty] private Inspection editingChannel = new();
    [ObservableProperty] private Job? parentProject;

    [ObservableProperty] private LabeledOption<FlowDirection>? selectedFlow;
    [ObservableProperty] private LabeledOption<ProjectType>? selectedProjectType;
    [ObservableProperty] private LabeledOption<PipeShape>? selectedPipeShape;
    [ObservableProperty] private LabeledOption<ViewStartLocation>? selectedViewStart;

    public List<LabeledOption<FlowDirection>> FlowOptions { get; } =
        Enum.GetValues<FlowDirection>()
            .Select(f => new LabeledOption<FlowDirection>(f, f.GetLabel()))
            .ToList();

    public List<LabeledOption<ProjectType>> ProjectTypeOptions { get; } =
        Enum.GetValues<ProjectType>()
            .Select(t => new LabeledOption<ProjectType>(t, t.GetLabel()))
            .ToList();

    public List<LabeledOption<PipeShape>> PipeShapeOptions { get; } =
        Enum.GetValues<PipeShape>()
            .Select(s => new LabeledOption<PipeShape>(s, s.GetLabel()))
            .ToList();

    public List<LabeledOption<ViewStartLocation>> ViewStartOptions { get; } =
        Enum.GetValues<ViewStartLocation>()
            .Select(v => new LabeledOption<ViewStartLocation>(v, v.GetLabel()))
            .ToList();

    public List<string> CleanedOptions { get; } = new() { "Evet", "Hayır", "Belirsiz" };

    [ObservableProperty] private string? selectedCleaned;

    public ChannelEditViewModel(DatabaseService db, ReportService report)
    {
        _db = db;
        _report = report;
        Title = "Kanal Düzenle";
    }

    partial void OnChannelIdChanged(int value)
    {
        if (value > 0) _ = LoadAsync();
    }

    partial void OnSelectedStreetChanged(string? value)
    {
        // Picker'dan seçilen sokağı forma (Entry) yaz
        if (!string.IsNullOrWhiteSpace(value) && EditingChannel is not null)
        {
            EditingChannel.Street = value;
            OnPropertyChanged(nameof(EditingChannel));
        }
    }

    /// <summary>
    /// Bu projede daha önce kullanılmış sokakları topla: hem DB'deki kanallardan
    /// hem de diskteki projeler/{Proje}/{Sokak} klasörlerinden.
    /// </summary>
    private async Task LoadKnownStreetsAsync(int forJobId)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var channels = await _db.GetInspectionsAsync(forJobId);
        foreach (var c in channels)
            if (!string.IsNullOrWhiteSpace(c.Street))
                set.Add(c.Street.Trim());

        try
        {
            var settings = await _db.GetSettingsAsync();
            var root = string.IsNullOrWhiteSpace(settings.StoragePath)
                ? FileSystem.AppDataDirectory
                : settings.StoragePath;
            // Yapı: projeler/{Proje}/{Mahalle}/{Sokak} → sokaklar iki seviye altta
            var projDir = StoragePaths.ProjectDir(root, ParentProject?.Title);
            if (Directory.Exists(projDir))
                foreach (var mahalle in Directory.GetDirectories(projDir))
                    foreach (var sokak in Directory.GetDirectories(mahalle))
                        set.Add(Path.GetFileName(sokak));
        }
        catch { /* disk taranamazsa DB listesi yeterli */ }

        KnownStreets.Clear();
        foreach (var s in set.OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase))
            KnownStreets.Add(s);
    }

    partial void OnJobIdChanged(int value)
    {
        // Yeni kanal: jobId var ama channelId yok
        if (value > 0 && ChannelId == 0) _ = InitNewAsync(value);
    }

    public async Task LoadAsync()
    {
        var conn = await _db.GetConnectionAsync();
        var existing = await conn.FindAsync<Inspection>(ChannelId);
        if (existing is null) return;

        EditingChannel = existing;
        _originalStreet = existing.Street;
        IsNew = false;
        ParentProject = await conn.FindAsync<Job>(existing.JobId);

        SelectedFlow = FlowOptions.First(f => f.Value == EditingChannel.FlowDirection);
        SelectedProjectType = ProjectTypeOptions.First(t => t.Value == EditingChannel.ProjectType);
        SelectedPipeShape = PipeShapeOptions.First(s => s.Value == EditingChannel.PipeShape);
        SelectedViewStart = ViewStartOptions.First(v => v.Value == EditingChannel.ViewStart);
        SelectedCleaned = EditingChannel.Cleaned switch
        {
            true => "Evet",
            false => "Hayır",
            _ => "Belirsiz"
        };

        Title = $"Kanal {EditingChannel.KanalNo}: {EditingChannel.ChannelCode ?? "—"}";
        await LoadKnownStreetsAsync(EditingChannel.JobId);
        SelectedStreet = string.IsNullOrWhiteSpace(EditingChannel.Street) ? null : EditingChannel.Street;
        await LoadSiblingsAsync(EditingChannel.JobId);
    }

    /// <summary>Yeni kanal oluştururken — proje ID'si biliniyor ama kanal ID'si yok.</summary>
    public async Task InitNewAsync(int forJobId)
    {
        var conn = await _db.GetConnectionAsync();
        ParentProject = await conn.FindAsync<Job>(forJobId);
        _originalStreet = null;
        // Mevcut kanalları geriye dönük numaralandır, sonra yeni kanala sıradaki numarayı ver
        await _db.EnsureKanalNumbersAsync(forJobId);
        EditingChannel = new Inspection
        {
            JobId   = forJobId,
            KanalNo = await _db.GetNextKanalNoAsync(forJobId)
        };
        IsNew = true;
        ChannelId = 0;
        SelectedFlow        = null;   // zorunlu — kullanıcı bilinçli seçmeli
        SelectedProjectType = null;   // zorunlu — Atık Su / Yağmur Suyu seçilmeli
        SelectedPipeShape   = PipeShapeOptions[0];
        SelectedViewStart   = ViewStartOptions[0];
        SelectedCleaned     = "Belirsiz";
        SelectedStreet      = null;
        Title = "Yeni Kanal";
        await LoadKnownStreetsAsync(forJobId);
        await LoadSiblingsAsync(forJobId);
    }

    private async Task LoadSiblingsAsync(int jobId)
    {
        if (jobId <= 0) return;
        await _db.EnsureKanalNumbersAsync(jobId);
        var channels = await _db.GetInspectionsAsync(jobId);
        SiblingChannels.Clear();
        foreach (var ch in channels.OrderBy(c => c.KanalNo).ThenBy(c => c.ChannelCode))
        {
            SiblingChannels.Add(new SiblingChannel
            {
                Channel = ch,
                IsCurrent = ch.Id == ChannelId
            });
        }
    }

    /// <summary>
    /// Kanal açmadan önce zorunlu alanları doğrular: giriş/çıkış bacası, sokak,
    /// kanal çapı ve akış yönü. Eksikse kullanıcıya bildirir ve false döner.
    /// </summary>
    private async Task<bool> ValidateRequiredAsync()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(EditingChannel.EntryManhole)) missing.Add("Giriş bacası");
        if (string.IsNullOrWhiteSpace(EditingChannel.ExitManhole))  missing.Add("Çıkış bacası");
        if (string.IsNullOrWhiteSpace(EditingChannel.Street))       missing.Add("Sokak adı");

        var hasDiameter = (EditingChannel.PipeWidthMm is int w && w > 0)
                          || !string.IsNullOrWhiteSpace(EditingChannel.PipeDiameter);
        if (!hasDiameter) missing.Add("Kanal çapı");

        if (SelectedFlow is null) missing.Add("Akış yönü");
        if (SelectedProjectType is null) missing.Add("Proje türü (Atık Su / Yağmur Suyu)");

        if (missing.Count == 0) return true;

        var msg = "Kanal açmak için şu zorunlu alanlar girilmeli:\n\n• " +
                  string.Join("\n• ", missing);
        StatusMessage = "Eksik: " + string.Join(", ", missing);
        await Shell.Current.DisplayAlert("Zorunlu alanlar eksik", msg, "Tamam");
        return false;
    }

    /// <summary>
    /// Sokak adı düzenlendiyse: yeni sokak klasörünü oluştur, varsa videoyu ve resim
    /// klasörünü taşı, eski sokak klasöründe hiç video kalmadıysa onu sil.
    /// </summary>
    private async Task ReconcileStreetFolderAsync()
    {
        if (IsNew || ParentProject is null) return;

        var oldStreet = _originalStreet?.Trim();
        var newStreet = EditingChannel.Street?.Trim();
        if (string.Equals(oldStreet, newStreet, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var settings = await _db.GetSettingsAsync();
            var root = string.IsNullOrWhiteSpace(settings.StoragePath)
                ? FileSystem.AppDataDirectory
                : settings.StoragePath;

            var hood = ParentProject.Neighborhood;
            var oldDir = StoragePaths.StreetDir(root, ParentProject.Title, hood, oldStreet);
            var fallback = $"kanal_{EditingChannel.KanalNo ?? EditingChannel.Id}";

            // Sokak adı hem klasörde hem dosya adlarında geçtiği için her ikisi de yenilenir.
            var oldStem = StoragePaths.ChannelStem(EditingChannel.KanalNo, oldStreet,
                EditingChannel.EntryManhole, EditingChannel.ExitManhole, fallback);
            var newStem = StoragePaths.ChannelStem(EditingChannel.KanalNo, newStreet,
                EditingChannel.EntryManhole, EditingChannel.ExitManhole, fallback);

            // ── Video: yeni sokak/video klasörüne taşı + yeniden adlandır + DB güncelle ──
            if (!string.IsNullOrEmpty(EditingChannel.VideoPath) && File.Exists(EditingChannel.VideoPath))
            {
                var newVideoDir = StoragePaths.VideoDir(root, ParentProject.Title, hood, newStreet);
                Directory.CreateDirectory(newVideoDir);
                var newVideoPath = Path.Combine(newVideoDir,
                    StoragePaths.VideoFileName(EditingChannel.KanalNo, newStreet,
                        EditingChannel.EntryManhole, EditingChannel.ExitManhole, fallback));
                if (!string.Equals(newVideoPath, EditingChannel.VideoPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(newVideoPath)) { try { File.Delete(newVideoPath); } catch { } }
                    File.Move(EditingChannel.VideoPath, newVideoPath);
                    EditingChannel.VideoPath = newVideoPath;
                    await _db.SaveInspectionAsync(EditingChannel);
                }
            }

            // ── Fotoğraflar: yeni sokak/resim klasörüne taşı + yeniden adlandır + DB güncelle ──
            var newPhotosDir = StoragePaths.PhotosDir(root, ParentProject.Title, hood, newStreet);
            var photos = await _db.GetDefectsAsync(EditingChannel.Id);
            foreach (var ph in photos)
            {
                if (string.IsNullOrEmpty(ph.PhotoPath) || !File.Exists(ph.PhotoPath)) continue;
                var fn = Path.GetFileName(ph.PhotoPath);
                // Dosya adının başındaki eski kanal kökünü yenisiyle değiştir
                var newFn = fn.StartsWith(oldStem, StringComparison.Ordinal)
                    ? newStem + fn.Substring(oldStem.Length)
                    : fn;
                Directory.CreateDirectory(newPhotosDir);
                var newPhotoPath = Path.Combine(newPhotosDir, newFn);
                if (!string.Equals(newPhotoPath, ph.PhotoPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(newPhotoPath)) { try { File.Delete(newPhotoPath); } catch { } }
                    File.Move(ph.PhotoPath, newPhotoPath);
                    ph.PhotoPath = newPhotoPath;
                    await _db.SaveDefectAsync(ph);
                }
            }

            // Eski sokak klasöründe hiç video yoksa sil
            if (Directory.Exists(oldDir) &&
                !Directory.EnumerateFiles(oldDir, "*.mp4", SearchOption.AllDirectories).Any())
            {
                try { Directory.Delete(oldDir, recursive: true); } catch { /* yoksay */ }
            }

            _originalStreet = newStreet;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Sokak klasörü güncellenemedi: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!await ValidateRequiredAsync()) return;

        EditingChannel.FlowDirection = SelectedFlow?.Value ?? FlowDirection.Downstream;
        EditingChannel.ProjectType   = SelectedProjectType?.Value ?? ProjectType.AtikSu;
        EditingChannel.PipeShape     = SelectedPipeShape?.Value ?? PipeShape.Dairesel;
        EditingChannel.ViewStart     = SelectedViewStart?.Value ?? ViewStartLocation.KanalBasi;
        EditingChannel.Cleaned = SelectedCleaned switch
        {
            "Evet"  => true,
            "Hayır" => false,
            _       => null
        };

        await _db.SaveInspectionAsync(EditingChannel);

        var savedJobId = EditingChannel.JobId;
        var wasNew     = ChannelId == 0;

        // Düzenleme modunda sokak değiştiyse klasörleri uzlaştır
        if (!wasNew) await ReconcileStreetFolderAsync();

        // Listeyi yenile (kaydedilen kanalı göster)
        await LoadSiblingsAsync(savedJobId);

        if (wasNew)
        {
            // Yeni kanal modunda: formu sıfırla — sayfada kal, bir sonraki kanalı girebilsin
            ChannelId = 0;
            await InitNewAsync(savedJobId);
            StatusMessage = $"Kanal kaydedildi ✓  —  yeni kanal girebilirsiniz";
        }
        else
        {
            // Düzenleme modunda: kapat
            StatusMessage = "Kanal güncellendi";
            await Shell.Current.GoToAsync("..");
        }
    }

    [RelayCommand]
    private async Task SaveAndCloseAsync()
    {
        if (!await ValidateRequiredAsync()) return;

        EditingChannel.FlowDirection = SelectedFlow?.Value ?? FlowDirection.Downstream;
        EditingChannel.ProjectType   = SelectedProjectType?.Value ?? ProjectType.AtikSu;
        EditingChannel.PipeShape     = SelectedPipeShape?.Value ?? PipeShape.Dairesel;
        EditingChannel.ViewStart     = SelectedViewStart?.Value ?? ViewStartLocation.KanalBasi;
        EditingChannel.Cleaned = SelectedCleaned switch
        {
            "Evet"  => true,
            "Hayır" => false,
            _       => null
        };
        await _db.SaveInspectionAsync(EditingChannel);
        await ReconcileStreetFolderAsync();
        await Shell.Current.GoToAsync("..");
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        await Shell.Current.GoToAsync("..");
    }

    // ── Sağ panel kanal aksiyonları ──

    [RelayCommand]
    private async Task EditSiblingAsync(SiblingChannel? item)
    {
        if (item is null) return;
        await Shell.Current.GoToAsync($"channeledit?id={item.Channel.Id}");
    }

    [RelayCommand]
    private void OpenSiblingVideo(SiblingChannel? item)
    {
        if (item is null || !item.HasVideo) return;
        try { Process.Start(new ProcessStartInfo(item.Channel.VideoPath!) { UseShellExecute = true }); }
        catch (Exception ex) { StatusMessage = $"Video açılamadı: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task GenerateSiblingReportAsync(SiblingChannel? item)
    {
        if (item is null) return;
        try
        {
            StatusMessage = "Raporlar oluşturuluyor…";
            var (classic, _) = await _report.GenerateChannelReportsAsync(item.Channel.Id);
            var dir = Path.GetDirectoryName(classic);
            if (!string.IsNullOrEmpty(dir))
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            StatusMessage = "✓ 2 rapor oluşturuldu (rapor/ klasörü)";
        }
        catch (Exception ex) { StatusMessage = $"Rapor hatası: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        var confirmed = await Shell.Current.DisplayAlert(
            "Kanalı sil",
            $"\"{EditingChannel.ChannelCode}\" silinsin mi? Bağlı kusurlar referansını kaybeder.",
            "Sil", "Vazgeç");
        if (!confirmed) return;

        var conn = await _db.GetConnectionAsync();
        await conn.DeleteAsync(EditingChannel);
        await Shell.Current.GoToAsync("..");
    }
}
