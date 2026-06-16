using MyRoboticsInspector.Models;
using SQLite;

namespace MyRoboticsInspector.Services;

public class DatabaseService
{
    private SQLiteAsyncConnection? _db;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public string DatabasePath { get; }

    public DatabaseService()
    {
        DatabasePath = Path.Combine(FileSystem.AppDataDirectory, "myroboticsinspector.db3");
    }

    public async Task<SQLiteAsyncConnection> GetConnectionAsync()
    {
        if (_db is not null) return _db;

        await _initLock.WaitAsync();
        try
        {
            if (_db is not null) return _db;

            var conn = new SQLiteAsyncConnection(
                DatabasePath,
                SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache);

            await conn.CreateTableAsync<Customer>();
            await conn.CreateTableAsync<Job>();
            await conn.CreateTableAsync<Inspection>();
            await conn.CreateTableAsync<Defect>();
            await conn.CreateTableAsync<AppSettings>();
            await conn.CreateTableAsync<Profile>();
            await conn.CreateTableAsync<CloudFileState>();

            if (await conn.Table<AppSettings>().CountAsync() == 0)
                await conn.InsertAsync(new AppSettings());

            _db = conn;
            return _db;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<List<Customer>> GetCustomersAsync()
    {
        var db = await GetConnectionAsync();
        return await db.Table<Customer>().OrderBy(c => c.Name).ToListAsync();
    }

    public async Task<int> SaveCustomerAsync(Customer customer)
    {
        var db = await GetConnectionAsync();
        return customer.Id == 0
            ? await db.InsertAsync(customer)
            : await db.UpdateAsync(customer);
    }

    public async Task<int> DeleteCustomerAsync(Customer customer)
    {
        var db = await GetConnectionAsync();
        // Cascade: müşteriye bağlı projeler (ve onların kanal/kusur satırları + disk dosyaları)
        // öksüz kalmasın — her proje kendi cascade'iyle silinir.
        var jobs = await db.Table<Job>().Where(j => j.CustomerId == customer.Id).ToListAsync();
        foreach (var job in jobs)
            await DeleteJobAsync(job);
        return await db.DeleteAsync(customer);
    }

    public async Task<List<Job>> GetJobsAsync(int? customerId = null)
    {
        var db = await GetConnectionAsync();
        var query = db.Table<Job>();
        if (customerId is int cid) query = query.Where(j => j.CustomerId == cid);
        return await query.OrderByDescending(j => j.ProjectDate).ToListAsync();
    }

    public async Task<int> SaveJobAsync(Job job)
    {
        var db = await GetConnectionAsync();
        return job.Id == 0
            ? await db.InsertAsync(job)
            : await db.UpdateAsync(job);
    }

    public async Task<int> DeleteJobAsync(Job job)
    {
        var db = await GetConnectionAsync();
        // Cascade: sqlite-net FK/ON DELETE CASCADE üretmediği için uygulama katmanında —
        // bağlı kanallar (inspections) kusur satırları ve disk dosyalarıyla birlikte silinir.
        // Aksi halde öksüz inceleme/kusur satırları DB'de, fotoğraf/video/rapor dosyaları diskte kalıyordu.
        var inspections = await db.Table<Inspection>().Where(i => i.JobId == job.Id).ToListAsync();
        foreach (var insp in inspections)
            await DeleteInspectionCascadeAsync(insp, job);
        return await db.DeleteAsync(job);
    }

    /// <summary>
    /// Bir kanalı (inspection) bağlı kusur satırları ve disk dosyalarıyla birlikte siler:
    /// kusur fotoğrafları (jpg), kanal videosu (mp4) ve üretilmiş rapor PDF'leri.
    /// Kanal silme akışları doğrudan satır silmek yerine bunu çağırmalı — aksi halde
    /// defect satırları öksüz kalır ve disk alanı geri kazanılmaz.
    /// </summary>
    public async Task<int> DeleteInspectionCascadeAsync(Inspection inspection, Job? job = null)
    {
        var db = await GetConnectionAsync();
        job ??= await db.FindAsync<Job>(inspection.JobId);

        // Kusur satırları + fotoğraf dosyaları
        var defects = await db.Table<Defect>()
            .Where(d => d.InspectionId == inspection.Id)
            .ToListAsync();
        foreach (var d in defects)
        {
            TryDeleteFile(d.PhotoPath);
            await db.DeleteAsync(d);
        }

        // Video dosyası
        TryDeleteFile(inspection.VideoPath);

        // Üretilmiş rapor PDF'leri
        await TryDeleteChannelReportsAsync(inspection, job);

        return await db.DeleteAsync(inspection);
    }

    /// <summary>Diskten dosyayı sessizce siler — dosya yoksa/kilitliyse satır silmeyi engellemez.</summary>
    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* kilitli olabilir */ }
    }

    /// <summary>
    /// Kanala ait üretilmiş rapor PDF'lerini sokağın <c>rapor/</c> klasöründen siler.
    /// KanalNo varsa <c>_{n}_*rapor*.pdf</c> deseni (ProjectChannelsViewModel ile aynı);
    /// yoksa yalnızca bu kanalın üreteceği iki dosya adı hedeflenir (komşu kanallara dokunma).
    /// </summary>
    private async Task TryDeleteChannelReportsAsync(Inspection insp, Job? job)
    {
        try
        {
            var settings = await GetSettingsAsync();
            var root = string.IsNullOrWhiteSpace(settings.StoragePath)
                ? FileSystem.AppDataDirectory
                : settings.StoragePath;
            var dir = StoragePaths.ReportsDir(root, job?.Title, job?.Neighborhood, insp.Street);
            if (!Directory.Exists(dir)) return;

            if (insp.KanalNo is int n)
            {
                foreach (var f in Directory.EnumerateFiles(dir, $"_{n}_*rapor*.pdf").ToList())
                    TryDeleteFile(f);
            }
            else
            {
                var fallback = $"kanal_{insp.Id}";
                TryDeleteFile(Path.Combine(dir, StoragePaths.ReportFileName(
                    insp.KanalNo, insp.Street, insp.EntryManhole, insp.ExitManhole, fallback)));
                TryDeleteFile(Path.Combine(dir, StoragePaths.Report13508FileName(
                    insp.KanalNo, insp.Street, insp.EntryManhole, insp.ExitManhole, fallback)));
            }
        }
        catch { /* rapor klasörüne erişilemezse satır silme akışını durdurma */ }
    }

    public async Task<int> GetInspectionCountForJobAsync(int jobId)
    {
        var db = await GetConnectionAsync();
        return await db.Table<Inspection>().Where(i => i.JobId == jobId).CountAsync();
    }

    public async Task<List<Inspection>> GetInspectionsAsync(int? jobId = null)
    {
        var db = await GetConnectionAsync();
        var query = db.Table<Inspection>();
        if (jobId is int jid) query = query.Where(i => i.JobId == jid);
        return await query.OrderByDescending(i => i.StartedAt).ToListAsync();
    }

    public async Task<int> SaveInspectionAsync(Inspection inspection)
    {
        var db = await GetConnectionAsync();
        return inspection.Id == 0
            ? await db.InsertAsync(inspection)
            : await db.UpdateAsync(inspection);
    }

    /// <summary>
    /// Bir projede bir sonraki boş kanal sıra numarasını döndürür (mevcut en büyük + 1, ilk kanal = 1).
    /// </summary>
    public async Task<int> GetNextKanalNoAsync(int jobId)
    {
        var db = await GetConnectionAsync();
        var list = await db.Table<Inspection>().Where(i => i.JobId == jobId).ToListAsync();
        var max = list.Where(i => i.KanalNo.HasValue)
                      .Select(i => i.KanalNo!.Value)
                      .DefaultIfEmpty(0)
                      .Max();
        return max + 1;
    }

    /// <summary>
    /// Projedeki numarası olmayan kanallara kronolojik sırayla (oluşturulma tarihine göre)
    /// 1'den başlayarak sıra numarası atar. Zaten numarası olanlara dokunmaz —
    /// eski / içe aktarılmış kanalları geriye dönük numaralandırmak için.
    /// </summary>
    public async Task EnsureKanalNumbersAsync(int jobId)
    {
        var db = await GetConnectionAsync();
        var list = await db.Table<Inspection>().Where(i => i.JobId == jobId).ToListAsync();
        if (list.Count == 0) return;

        var next = list.Where(i => i.KanalNo.HasValue)
                       .Select(i => i.KanalNo!.Value)
                       .DefaultIfEmpty(0)
                       .Max();

        foreach (var ch in list.OrderBy(i => i.StartedAt).ThenBy(i => i.Id))
        {
            if (!ch.KanalNo.HasValue)
            {
                ch.KanalNo = ++next;
                await db.UpdateAsync(ch);
            }
        }
    }

    public async Task<List<Defect>> GetDefectsAsync(int inspectionId)
    {
        var db = await GetConnectionAsync();
        return await db.Table<Defect>()
            .Where(d => d.InspectionId == inspectionId)
            .OrderBy(d => d.VideoTimestampMs)
            .ToListAsync();
    }

    public async Task<int> SaveDefectAsync(Defect defect)
    {
        var db = await GetConnectionAsync();
        return defect.Id == 0
            ? await db.InsertAsync(defect)
            : await db.UpdateAsync(defect);
    }

    public async Task<int> DeleteDefectAsync(Defect defect)
    {
        var db = await GetConnectionAsync();
        return await db.DeleteAsync(defect);
    }

    public async Task<AppSettings> GetSettingsAsync()
    {
        var db = await GetConnectionAsync();
        var s = await db.Table<AppSettings>().FirstAsync();

        bool dirty = false;

        // Migrasyon: RecordingRtspUrl boşsa önizleme URL'sinden /101 ana akışını türet ve kaydet.
        // Kamera eş zamanlı iki bağlantıyı destekliyorsa önizleme /102, kayıt /101 olmalı.
        if (string.IsNullOrWhiteSpace(s.RecordingRtspUrl) && !string.IsNullOrWhiteSpace(s.RtspUrl))
        {
            s.RecordingRtspUrl = DeriveMainStreamUrl(s.RtspUrl);
            dirty = true;
        }

        // Migrasyon: yeni eklenen overlay kolonları eski DB satırlarında 0/boş gelir → makul varsayılana çek.
        if (s.OverlayFontScale <= 0) { s.OverlayFontScale = 1.0; dirty = true; }
        if (string.IsNullOrWhiteSpace(s.OverlayFontFamily)) { s.OverlayFontFamily = "Arial"; dirty = true; }

        if (dirty) await db.UpdateAsync(s);

        return s;
    }

    /// <summary>
    /// Önizleme (sub-stream) URL'sinden kayıt (main-stream) URL'si türetir.
    /// Hikvision /Channels/102 → /Channels/101.
    /// Tanınamayan format varsa aynı URL döner (en kötü ihtimalle aynı stream kullanılır).
    /// </summary>
    private static string DeriveMainStreamUrl(string subStreamUrl)
    {
        // Hikvision: /Streaming/Channels/102 → /Streaming/Channels/101
        if (subStreamUrl.Contains("/Channels/102", StringComparison.OrdinalIgnoreCase))
            return subStreamUrl.Replace("/Channels/102", "/Channels/101", StringComparison.OrdinalIgnoreCase);
        // Hikvision generic /Channels/N02 → /Channels/N01 (multi-camera heads)
        if (subStreamUrl.Contains("/Channels/", StringComparison.OrdinalIgnoreCase))
        {
            var idx = subStreamUrl.LastIndexOf("/Channels/", StringComparison.OrdinalIgnoreCase) + 10;
            if (idx < subStreamUrl.Length && subStreamUrl[^1] == '2')
                return subStreamUrl[..^1] + "1";
        }
        // Dahua: subtype=1 → subtype=0
        if (subStreamUrl.Contains("subtype=1", StringComparison.OrdinalIgnoreCase))
            return subStreamUrl.Replace("subtype=1", "subtype=0", StringComparison.OrdinalIgnoreCase);
        // Tanınamadı — aynı URL (kötü ama hata vermez)
        return subStreamUrl;
    }

    public async Task<int> SaveSettingsAsync(AppSettings settings)
    {
        var db = await GetConnectionAsync();
        return await db.UpdateAsync(settings);
    }
}
