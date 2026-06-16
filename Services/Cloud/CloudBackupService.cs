using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MyRoboticsInspector.Models;

namespace MyRoboticsInspector.Services.Cloud;

public enum CloudBackupState
{
    Disabled,      // Ayarlardan kapalı
    NotSignedIn,   // Bulut hesabı yok
    Idle,          // Senkron bekliyor
    Syncing,       // Veri/dosya yükleniyor
    Offline,       // Ağ hatası — sonraki turda tekrar denenecek
    Error          // Kalıcı görünen hata (LastError dolu)
}

/// <summary>Buluttaki bir yedek dosyası (UI listesi için).</summary>
public sealed class CloudFileInfoDto
{
    [JsonPropertyName("rel_path")] public string RelPath { get; set; } = "";
    [JsonPropertyName("storage_path")] public string StoragePath { get; set; } = "";
    [JsonPropertyName("byte_size")] public long ByteSize { get; set; }
    [JsonPropertyName("uploaded_at")] public DateTime UploadedAt { get; set; }
}

/// <summary>
/// Bulut yedekleme — outbox deseni:
///   1. Tarayıcı: StorageRoot altındaki tüm video/resim/rapor dosyalarını keşfeder,
///      cloud_file_state tablosuna kuyruklar (hangi kod yolundan yazılırsa yazılsın yakalar).
///   2. İşçi: kuyruktaki dosyaları Supabase Storage'a stream'le yükler (retry + backoff).
///   3. Veri: SQLite satırları jsonb anlık görüntü olarak inspector_records'a upsert edilir,
///      ayrıca günde bir kez tam .db3 kopyası yüklenir.
/// ADD-ONLY: bu servis buluttan asla silmez. Silme yalnız PIN ile açılan pencere
/// içinde, kullanıcının açık eylemiyle yapılır (inspector_unlock_delete RPC).
/// </summary>
public class CloudBackupService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };
    private static readonly HttpClient UploadHttp = new() { Timeout = TimeSpan.FromMinutes(60) };

    private static readonly string[] BackupExtensions =
        { ".mp4", ".avi", ".mkv", ".jpg", ".jpeg", ".png", ".pdf" };
    private static readonly string[] VideoExtensions = { ".mp4", ".avi", ".mkv" };

    /// <summary>Yazımı henüz bitmemiş olabilecek dosyaları atlamak için minimum yaş.</summary>
    private static readonly TimeSpan MinFileAge = TimeSpan.FromSeconds(60);
    private const int MaxAttempts = 12;

    private readonly DatabaseService _db;
    private readonly CloudAuthService _auth;
    private readonly AuthService _localAuth;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private CancellationTokenSource? _loopCts;

    public CloudBackupState State { get; private set; } = CloudBackupState.NotSignedIn;
    public string? LastError { get; private set; }
    public DateTime? LastSyncAt { get; private set; }
    public int PendingCount { get; private set; }
    public int UploadedCount { get; private set; }
    public string? CurrentItem { get; private set; }
    public DateTime? DeleteUnlockedUntil { get; private set; }

    /// <summary>Durum değişti — UI ana thread'e kendisi marshal etmeli.</summary>
    public event EventHandler? StatusChanged;

    public CloudBackupService(DatabaseService db, CloudAuthService auth, AuthService localAuth)
    {
        _db = db;
        _auth = auth;
        _localAuth = localAuth;

        // Profil değişince o profilin bulut hesabına geç ve kısa süre sonra senkronla.
        _localAuth.CurrentProfileChanged += async (_, _) =>
        {
            try
            {
                var pid = _localAuth.CurrentProfile?.Id ?? -1;
                await _auth.AttachProfileAsync(pid);
                if (_auth.IsSignedIn) _ = SyncNowAsync();
                else SetState(CloudBackupState.NotSignedIn);
            }
            catch { /* arka plan — UI'yi düşürme */ }
        };
        _auth.SessionChanged += (_, _) => Notify();
    }

    public static string DeviceId
    {
        get
        {
            var id = Preferences.Get("cloud_device_id", "");
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N")[..12];
                Preferences.Set("cloud_device_id", id);
            }
            return id;
        }
    }

    // ── Yaşam döngüsü ──────────────────────────────────────────────────────

    /// <summary>Periyodik senkron döngüsünü başlatır (uygulama açılışında bir kez).</summary>
    public void Start()
    {
        if (_loopCts is not null) return;
        _loopCts = new CancellationTokenSource();
        var ct = _loopCts.Token;
        _ = Task.Run(async () =>
        {
            // İlk tur: açılış yükünü bekle.
            try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch { return; }
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var settings = await _db.GetSettingsAsync();
                    if (settings.CloudBackupEnabled && _auth.IsSignedIn)
                        await SyncNowAsync();

                    var interval = Math.Clamp(settings.CloudSyncIntervalMin, 2, 240);
                    await Task.Delay(TimeSpan.FromMinutes(interval), ct);
                }
                catch (OperationCanceledException) { return; }
                catch
                {
                    try { await Task.Delay(TimeSpan.FromMinutes(5), ct); } catch { return; }
                }
            }
        }, ct);
    }

    public void Stop()
    {
        _loopCts?.Cancel();
        _loopCts = null;
    }

    // ── Senkron ────────────────────────────────────────────────────────────

    /// <summary>Tam senkron turu: cihaz + veri + dosya tarama + kuyruk işleme.</summary>
    public async Task SyncNowAsync()
    {
        if (!await _syncGate.WaitAsync(0)) return; // zaten çalışıyor
        try
        {
            var settings = await _db.GetSettingsAsync();
            if (!settings.CloudBackupEnabled) { SetState(CloudBackupState.Disabled); return; }

            var token = await _auth.GetValidAccessTokenAsync();
            var tenant = _auth.Session?.UserId;
            if (token is null || tenant is null) { SetState(CloudBackupState.NotSignedIn); return; }

            SetState(CloudBackupState.Syncing);
            LastError = null;

            await UpsertDeviceAsync(token, settings);
            await SnapshotRecordsAsync(token);
            await SnapshotDatabaseFileAsync(token, tenant);
            await ScanFilesAsync(settings, tenant);
            await ProcessQueueAsync(token, tenant, settings);

            LastSyncAt = DateTime.Now;
            await RefreshCountsAsync(tenant);
            SetState(CloudBackupState.Idle);
        }
        catch (HttpRequestException ex)
        {
            LastError = "Ağ hatası: " + ex.Message;
            SetState(CloudBackupState.Offline);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            SetState(CloudBackupState.Error);
        }
        finally
        {
            CurrentItem = null;
            _syncGate.Release();
            Notify();
        }
    }

    private async Task UpsertDeviceAsync(string token, AppSettings settings)
    {
        // Cihaz adı: operatörün verdiği okunabilir ad ("Saha Tableti 1"); boşsa makine adı.
        var deviceName = string.IsNullOrWhiteSpace(settings.DeviceLabel)
            ? Environment.MachineName
            : settings.DeviceLabel;
        var body = JsonSerializer.Serialize(new[]
        {
            new
            {
                device_id = DeviceId,
                device_name = deviceName,
                platform = DeviceInfo.Platform.ToString(),
                app_version = AppInfo.VersionString,
                last_seen_at = DateTime.UtcNow,
            },
        });
        await PostgrestAsync(token, HttpMethod.Post,
            "/rest/v1/inspector_devices?on_conflict=tenant_id,device_id", body,
            prefer: "resolution=merge-duplicates,return=minimal");
    }

    /// <summary>
    /// Tüm SQLite satırlarını jsonb olarak upsert eder. AppSettings YÜKLENMEZ
    /// (kamera/broker parolası içerir); Profile satırından Pin alanı çıkarılır.
    /// </summary>
    private async Task SnapshotRecordsAsync(string token)
    {
        CurrentItem = "Veri tabanı senkronlanıyor…"; Notify();
        var conn = await _db.GetConnectionAsync();
        var rows = new List<object>();

        void Add(string table, int localId, JsonNode? payload)
        {
            if (payload is null) return;
            rows.Add(new
            {
                device_id = DeviceId,
                table_name = table,
                local_id = localId,
                payload,
                updated_at = DateTime.UtcNow,
            });
        }

        foreach (var c in await conn.Table<Customer>().ToListAsync())
            Add("customers", c.Id, JsonSerializer.SerializeToNode(c));
        foreach (var j in await conn.Table<Job>().ToListAsync())
            Add("jobs", j.Id, JsonSerializer.SerializeToNode(j));
        foreach (var i in await conn.Table<Inspection>().ToListAsync())
            Add("inspections", i.Id, JsonSerializer.SerializeToNode(i));
        foreach (var d in await conn.Table<Defect>().ToListAsync())
            Add("defects", d.Id, JsonSerializer.SerializeToNode(d));
        foreach (var p in await conn.Table<Profile>().ToListAsync())
        {
            var node = JsonSerializer.SerializeToNode(p)?.AsObject();
            node?.Remove(nameof(Profile.Pin)); // PIN buluta çıkmaz
            Add("profiles", p.Id, node);
        }

        // 500'lük parçalar halinde upsert (PostgREST gövde limiti güvenliği).
        for (var i = 0; i < rows.Count; i += 500)
        {
            var chunk = rows.Skip(i).Take(500).ToList();
            await PostgrestAsync(token, HttpMethod.Post,
                "/rest/v1/inspector_records?on_conflict=tenant_id,device_id,table_name,local_id",
                JsonSerializer.Serialize(chunk),
                prefer: "resolution=merge-duplicates,return=minimal");
        }
    }

    /// <summary>Günde bir kez tam .db3 kopyası yükler (felaket kurtarma için).</summary>
    private async Task SnapshotDatabaseFileAsync(string token, string tenant)
    {
        var name = $"db_{DateTime.Now:yyyy-MM-dd}.db3";
        var marker = "cloud_db_snap_done";
        var stamp = $"{tenant}|{name}"; // hesap değişirse yeni tenant'a da günlük yedek gitsin
        if (Preferences.Get(marker, "") == stamp) return;

        CurrentItem = "Veri tabanı yedeği yükleniyor…"; Notify();
        var temp = Path.Combine(FileSystem.CacheDirectory, name);
        try
        {
            var conn = await _db.GetConnectionAsync();
            await conn.BackupAsync(temp); // SQLite online backup — tutarlı kopya
            var storagePath = $"{tenant}/{DeviceId}/db/{name}";
            var ok = await UploadFileAsync(token, temp, storagePath, "application/octet-stream");
            if (ok) Preferences.Set(marker, stamp);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// <summary>
    /// Depo kökündeki tüm yedeklenebilir dosyaları keşfeder ve kuyruğa ekler.
    /// Kod yolundan bağımsız: hangi sayfa yazarsa yazsın dosya yakalanır.
    /// </summary>
    private async Task ScanFilesAsync(AppSettings settings, string tenant)
    {
        CurrentItem = "Dosyalar taranıyor…"; Notify();
        var root = string.IsNullOrWhiteSpace(settings.StoragePath)
            ? FileSystem.AppDataDirectory
            : settings.StoragePath;

        var scanDirs = new[]
        {
            Path.Combine(root, StoragePaths.ProjectsFolder),
            Path.Combine(root, "snapshots"),
            Path.Combine(root, "inspections"),
            Path.Combine(root, "reports"),
            Path.Combine(FileSystem.AppDataDirectory, "reports"),
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        var conn = await _db.GetConnectionAsync();
        var known = (await conn.Table<CloudFileState>()
                .Where(s => s.TenantId == tenant).ToListAsync())
            .Select(s => s.RelPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in scanDirs)
        {
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories); }
            catch { continue; }

            foreach (var file in files)
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!BackupExtensions.Contains(ext)) continue;
                if (!settings.CloudIncludeVideos && VideoExtensions.Contains(ext)) continue;

                FileInfo fi;
                try { fi = new FileInfo(file); } catch { continue; }
                if (DateTime.Now - fi.LastWriteTime < MinFileAge) continue; // hâlâ yazılıyor olabilir

                var rel = ToRelPath(root, file);
                if (known.Contains(rel)) continue;

                await conn.InsertAsync(new CloudFileState
                {
                    TenantId = tenant,
                    RelPath = rel,
                    AbsPath = file,
                    ByteSize = fi.Length,
                });
                known.Add(rel);
            }
        }
    }

    private static string ToRelPath(string root, string absPath)
    {
        var rel = Path.GetRelativePath(root, absPath);
        if (rel.StartsWith("..")) rel = Path.GetFileName(absPath); // kök dışı (AppData reports)
        return rel.Replace('\\', '/');
    }

    private async Task ProcessQueueAsync(string token, string tenant, AppSettings settings)
    {
        var conn = await _db.GetConnectionAsync();
        var pending = await conn.Table<CloudFileState>()
            .Where(s => s.TenantId == tenant && s.UploadedAt == null && s.Attempts < MaxAttempts)
            .OrderBy(s => s.ByteSize) // küçükler önce — hızlı görünür ilerleme
            .ToListAsync();

        foreach (var item in pending)
        {
            if (!File.Exists(item.AbsPath))
            {
                // Dosya taşınmış/silinmiş — bir daha deneme.
                item.Attempts = MaxAttempts;
                item.LastError = "Yerel dosya bulunamadı";
                await conn.UpdateAsync(item);
                continue;
            }

            CurrentItem = $"Yükleniyor: {Path.GetFileName(item.AbsPath)} ({FormatBytes(item.ByteSize)})";
            Notify();

            var storagePath = $"{tenant}/{DeviceId}/{item.RelPath}";
            try
            {
                var fresh = await _auth.GetValidAccessTokenAsync() ?? token;
                var ok = await UploadFileAsync(fresh, item.AbsPath, storagePath, GuessContentType(item.AbsPath));
                if (ok)
                {
                    item.UploadedAt = DateTime.Now;
                    item.StoragePath = storagePath;
                    item.LastError = null;
                    await conn.UpdateAsync(item);
                    await InsertFileMetadataAsync(fresh, item);
                }
                else
                {
                    // UploadFileAsync yalnızca dosya KİLİTLİYKEN (IOException) false döner —
                    // bu GEÇİCİ bir durum (uzun kayıt dosya tutamacını tutuyor olabilir).
                    // Attempts ARTIRMA: yoksa ~3 saat kilitli kalan dosya MaxAttempts'e ulaşıp
                    // kalıcı terk edilir. Sadece bu turda atla, sonraki turda yeniden denenir.
                    item.LastError = "Dosya kilitli — sonraki turda yeniden denenecek";
                    await conn.UpdateAsync(item);
                }
            }
            catch (HttpRequestException)
            {
                item.Attempts++;
                item.LastError = "Ağ hatası";
                await conn.UpdateAsync(item);
                throw; // tur Offline ile bitsin — kalanlar sonraki turda
            }
            catch (Exception ex)
            {
                item.Attempts++;
                item.LastError = Truncate(ex.Message, 480);
                await conn.UpdateAsync(item);
            }
        }
    }

    /// <summary>true = bulutta var (yeni yüklendi ya da zaten vardı/409).</summary>
    private async Task<bool> UploadFileAsync(string token, string absPath, string storagePath, string contentType)
    {
        // Kayıt sürerken kilitli dosyaya çarpmamak için paylaşımlı açmayı dene.
        FileStream fs;
        try
        {
            fs = new FileStream(absPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        }
        catch (IOException) { return false; } // kilitli — sonraki tur

        await using (fs)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{CloudConfig.Url}/storage/v1/object/{CloudConfig.Bucket}/{EncodePath(storagePath)}");
            req.Headers.Add("apikey", CloudConfig.AnonKey);
            req.Headers.Add("Authorization", "Bearer " + token);
            req.Content = new StreamContent(fs);
            req.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(45));
            using var resp = await UploadHttp.SendAsync(req, cts.Token);

            if (resp.IsSuccessStatusCode) return true;
            if (resp.StatusCode == System.Net.HttpStatusCode.Conflict) return true; // zaten yüklü (immutable bucket)

            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            if (body.Contains("already exists", StringComparison.OrdinalIgnoreCase)) return true;
            throw new InvalidOperationException($"Yükleme reddedildi ({(int)resp.StatusCode}): {Truncate(body, 200)}");
        }
    }

    private async Task InsertFileMetadataAsync(string token, CloudFileState item)
    {
        var body = JsonSerializer.Serialize(new[]
        {
            new
            {
                device_id = DeviceId,
                rel_path = item.RelPath,
                storage_path = item.StoragePath,
                byte_size = item.ByteSize,
                content_type = GuessContentType(item.AbsPath),
            },
        });
        try
        {
            await PostgrestAsync(token, HttpMethod.Post,
                "/rest/v1/inspector_files?on_conflict=tenant_id,storage_path", body,
                prefer: "resolution=merge-duplicates,return=minimal");
        }
        catch { /* metadata ikincil — dosya zaten bulutta */ }
    }

    private async Task RefreshCountsAsync(string tenant)
    {
        var conn = await _db.GetConnectionAsync();
        PendingCount = await conn.Table<CloudFileState>()
            .Where(s => s.TenantId == tenant && s.UploadedAt == null && s.Attempts < MaxAttempts)
            .CountAsync();
        UploadedCount = await conn.Table<CloudFileState>()
            .Where(s => s.TenantId == tenant && s.UploadedAt != null)
            .CountAsync();
    }

    // ── Bulut görünümü + korumalı silme ────────────────────────────────────

    public async Task<List<CloudFileInfoDto>> GetRecentCloudFilesAsync(int limit = 25)
    {
        var token = await _auth.GetValidAccessTokenAsync()
            ?? throw new CloudAuthException("Bulut oturumu yok.");
        var json = await PostgrestAsync(token, HttpMethod.Get,
            $"/rest/v1/inspector_files?select=rel_path,storage_path,byte_size,uploaded_at&order=uploaded_at.desc&limit={limit}", null);
        return JsonSerializer.Deserialize<List<CloudFileInfoDto>>(json) ?? new();
    }

    public async Task<(long files, long bytes)> GetCloudTotalsAsync()
    {
        var token = await _auth.GetValidAccessTokenAsync()
            ?? throw new CloudAuthException("Bulut oturumu yok.");
        var json = await PostgrestAsync(token, HttpMethod.Get,
            "/rest/v1/inspector_files?select=byte_size&limit=100000", null);
        using var doc = JsonDocument.Parse(json);
        long count = 0, bytes = 0;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            count++;
            if (el.TryGetProperty("byte_size", out var b)) bytes += b.GetInt64();
        }
        return (count, bytes);
    }

    /// <summary>Silme PIN'ini belirle/değiştir (sunucuda bcrypt hash).</summary>
    public async Task SetDeletePinAsync(string? currentPin, string newPin)
    {
        var token = await _auth.GetValidAccessTokenAsync()
            ?? throw new CloudAuthException("Bulut oturumu yok.");
        await PostgrestAsync(token, HttpMethod.Post, "/rest/v1/rpc/inspector_set_delete_pin",
            JsonSerializer.Serialize(new { p_current = currentPin ?? "", p_new = newPin }));
    }

    /// <summary>PIN doğrula → 5 dakikalık silme penceresi aç.</summary>
    public async Task UnlockDeleteAsync(string pin)
    {
        var token = await _auth.GetValidAccessTokenAsync()
            ?? throw new CloudAuthException("Bulut oturumu yok.");
        var json = await PostgrestAsync(token, HttpMethod.Post, "/rest/v1/rpc/inspector_unlock_delete",
            JsonSerializer.Serialize(new { p_pin = pin }));
        DeleteUnlockedUntil = DateTime.Now.AddMinutes(5);
        _ = json;
        Notify();
    }

    public async Task LockDeleteAsync()
    {
        var token = await _auth.GetValidAccessTokenAsync();
        if (token is not null)
        {
            try { await PostgrestAsync(token, HttpMethod.Post, "/rest/v1/rpc/inspector_lock_delete", "{}"); }
            catch { }
        }
        DeleteUnlockedUntil = null;
        Notify();
    }

    /// <summary>
    /// Buluttan dosya siler — yalnız silme penceresi açıkken çalışır (RLS sunucuda zorlar).
    /// Yerel kayıt "yüklendi" olarak KALIR: tarayıcı dosyayı yeniden kuyruklamaz,
    /// kullanıcının silme kararı geri alınmaz.
    /// </summary>
    public async Task DeleteCloudFileAsync(string storagePath)
    {
        var token = await _auth.GetValidAccessTokenAsync()
            ?? throw new CloudAuthException("Bulut oturumu yok.");

        using var req = new HttpRequestMessage(HttpMethod.Delete,
            $"{CloudConfig.Url}/storage/v1/object/{CloudConfig.Bucket}/{EncodePath(storagePath)}");
        req.Headers.Add("apikey", CloudConfig.AnonKey);
        req.Headers.Add("Authorization", "Bearer " + token);
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException(resp.StatusCode == System.Net.HttpStatusCode.Forbidden
                || body.Contains("row-level security", StringComparison.OrdinalIgnoreCase)
                ? "Silme kilidi kapalı — önce PIN ile kilidi açın."
                : $"Silinemedi ({(int)resp.StatusCode}): {Truncate(body, 200)}");
        }

        // Metadata satırını da temizle (silme penceresi açıkken RLS izin verir).
        try
        {
            await PostgrestAsync(token, HttpMethod.Delete,
                "/rest/v1/inspector_files?storage_path=eq." + Uri.EscapeDataString(storagePath), null,
                prefer: "return=minimal");
        }
        catch { }
    }

    // ── HTTP yardımcıları ──────────────────────────────────────────────────

    private static async Task<string> PostgrestAsync(string token, HttpMethod method,
        string pathAndQuery, string? jsonBody, string? prefer = null)
    {
        using var req = new HttpRequestMessage(method, CloudConfig.Url + pathAndQuery);
        req.Headers.Add("apikey", CloudConfig.AnonKey);
        req.Headers.Add("Authorization", "Bearer " + token);
        if (prefer is not null) req.Headers.Add("Prefer", prefer);
        if (jsonBody is not null)
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            // RPC hataları {"message": "..."} döner — Türkçe mesajı doğrudan yüzeye çıkar.
            var msg = TryExtractMessage(body) ?? $"Sunucu hatası {(int)resp.StatusCode}";
            if (body.Contains("relation") && body.Contains("does not exist"))
                msg = "Bulut tabloları kurulu değil — supabase/inspector_cloud.sql dosyasını Supabase SQL Editor'da çalıştırın.";
            throw new InvalidOperationException(msg);
        }
        return body;
    }

    private static string? TryExtractMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("message", out var m)) return m.GetString();
        }
        catch { }
        return null;
    }

    private static string EncodePath(string path)
        => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static string GuessContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".avi" => "video/x-msvideo",
        ".mkv" => "video/x-matroska",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream",
    };

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KB",
        _ => $"{bytes} B",
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private void SetState(CloudBackupState s)
    {
        State = s;
        Notify();
    }

    private void Notify() => StatusChanged?.Invoke(this, EventArgs.Empty);
}
