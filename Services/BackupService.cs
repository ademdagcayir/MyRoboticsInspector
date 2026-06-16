namespace MyRoboticsInspector.Services;

public enum BackupStatus
{
    /// <summary>OneDrive yok / kurulu değil.</summary>
    NotAvailable,

    /// <summary>OneDrive var ama uygulama yerel dosya yolunu kullanıyor (yedekleme pasif).</summary>
    NotConfigured,

    /// <summary>Uygulama dosyaları OneDrive klasörüne yazıyor — Windows istemcisi arka planda senkronluyor.</summary>
    Active
}

/// <summary>
/// Hafif backup yönetimi: kullanıcının OneDrive klasörünü algıla, uygulama Storage'ı oraya yönlendir.
/// Gerçek upload Windows OneDrive istemcisi tarafından yapılır — biz sadece doğru klasöre yazıyoruz.
///
/// Cross-device sync DEĞIL — sadece tek yönlü yedekleme (kullanıcı talebine göre).
/// </summary>
public class BackupService
{
    /// <summary>
    /// OneDrive synced root for the current platform.
    /// Windows: %OneDriveCommercial% / %OneDriveConsumer% / %OneDrive% env var.
    /// Mac: ~/Library/CloudStorage/OneDrive-*  veya ~/OneDrive (klasik konum).
    /// Android: OneDrive Android app yerel sync yapmaz — user manuel StoragePath.
    /// </summary>
    public string? OneDriveRoot
    {
        get
        {
            if (OperatingSystem.IsAndroid())
            {
                var hint = "/storage/emulated/0/OneDrive";
                return Directory.Exists(hint) ? hint : null;
            }

            if (OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS())
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                // Modern macOS: CloudStorage altında "OneDrive-Personal" veya "OneDrive-{Company}"
                var cloudStorage = Path.Combine(home, "Library", "CloudStorage");
                if (Directory.Exists(cloudStorage))
                {
                    var match = Directory.EnumerateDirectories(cloudStorage)
                        .FirstOrDefault(d => Path.GetFileName(d).StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase));
                    if (match is not null) return match;
                }
                // Eski / manual mount konumu
                var classic = Path.Combine(home, "OneDrive");
                if (Directory.Exists(classic)) return classic;
                return null;
            }

            // Windows + diğerleri
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("OneDriveCommercial"),
                Environment.GetEnvironmentVariable("OneDriveConsumer"),
                Environment.GetEnvironmentVariable("OneDrive"),
            };
            foreach (var c in candidates)
                if (!string.IsNullOrEmpty(c) && Directory.Exists(c)) return c;
            return null;
        }
    }

    public bool OneDriveAvailable => OneDriveRoot is not null;

    /// <summary>Önerilen yedek klasörü: {OneDrive}/MyRoboticsInspector.</summary>
    public string? RecommendedBackupFolder =>
        OneDriveRoot is { } r ? Path.Combine(r, "MyRoboticsInspector") : null;

    public BackupStatus GetStatus(string currentStoragePath)
    {
        var root = OneDriveRoot;
        if (root is null) return BackupStatus.NotAvailable;
        if (string.IsNullOrWhiteSpace(currentStoragePath)) return BackupStatus.NotConfigured;
        try
        {
            var normalized = Path.GetFullPath(currentStoragePath);
            var oneDrive = Path.GetFullPath(root);
            return normalized.StartsWith(oneDrive, StringComparison.OrdinalIgnoreCase)
                ? BackupStatus.Active
                : BackupStatus.NotConfigured;
        }
        catch
        {
            return BackupStatus.NotConfigured;
        }
    }

    /// <summary>Önerilen backup klasörünü ve alt klasörlerini oluşturur (varsa no-op).</summary>
    public string EnsureBackupFolder()
    {
        var folder = RecommendedBackupFolder
            ?? throw new InvalidOperationException("OneDrive bulunamadı — yedek hedefi yok.");
        Directory.CreateDirectory(folder);
        foreach (var sub in new[] { StoragePaths.ProjectsFolder, "inspections", "snapshots", "recordings", "reports" })
            Directory.CreateDirectory(Path.Combine(folder, sub));
        return folder;
    }

    /// <summary>
    /// Mevcut yerel verileri (AppDataDirectory altındaki) OneDrive klasörüne KOPYALAR
    /// (eski yol silinmez — taşıma değil yedekleme). Hata olursa partial state mümkün;
    /// tekrar çalıştırmak güvenlidir: tam kopyalar atlanır, yarım kalmış kopyalar tamamlanır.
    /// </summary>
    public async Task<(int copied, int failed, long bytes)> MigrateAsync(
        string fromPath, string toPath, IProgress<string>? progress = null)
    {
        Directory.CreateDirectory(toPath);
        var copied = 0;
        var failed = 0;
        long totalBytes = 0;

        // Asıl saha verisi "projeler/" ağacında yaşar (video/resim/rapor/egim) —
        // eski düz klasörler de geriye dönük uyumluluk için listede kalır.
        var subfolders = new[] { StoragePaths.ProjectsFolder, "inspections", "snapshots", "recordings", "reports" };
        foreach (var sub in subfolders)
        {
            var src = Path.Combine(fromPath, sub);
            if (!Directory.Exists(src)) continue;
            var dst = Path.Combine(toPath, sub);
            Directory.CreateDirectory(dst);
            await Task.Run(() => CopyDir(src, dst, ref copied, ref failed, ref totalBytes, progress));
        }

        // SQLite veritabanını da yedeğe kopyala — müşteri/proje/kanal/kusur kayıtları DB'de;
        // dosyalar yedeklense bile DB olmadan hangi videonun hangi kanala ait olduğu kaybolur.
        await Task.Run(() => BackupDatabase(toPath, ref copied, ref failed, ref totalBytes, progress));

        return (copied, failed, totalBytes);
    }

    /// <summary>
    /// SQLite veritabanı dosyasını yedek klasörüne kopyalar. DatabaseService DB'yi her zaman
    /// AppDataDirectory altında tutar — StoragePath OneDrive'a yönlense bile DB yerelde kalır.
    /// Günlük damgalı ad kullanılır; aynı gün tekrar çalıştırılırsa üzerine yazar (taze kopya).
    /// Not: canlı dosyanın kopyası best-effort'tur; varsa -wal dosyası da birlikte kopyalanır.
    /// </summary>
    private static void BackupDatabase(string toPath, ref int copied, ref int failed,
                                       ref long totalBytes, IProgress<string>? progress)
    {
        var dbPath = Path.Combine(FileSystem.AppDataDirectory, "myroboticsinspector.db3");
        if (!File.Exists(dbPath)) return;
        try
        {
            var dbDir = Path.Combine(toPath, "db_backup");
            Directory.CreateDirectory(dbDir);
            var target = Path.Combine(dbDir, $"db_{DateTime.Now:yyyy-MM-dd}.db3");
            if (TryCopyFile(dbPath, target, force: true))
            {
                totalBytes += new FileInfo(dbPath).Length;
                copied++;
                progress?.Report("Kopyalandı: veritabanı (myroboticsinspector.db3)");
            }

            // WAL modunda son işlemler -wal dosyasında olabilir — varsa birlikte kopyala.
            var wal = dbPath + "-wal";
            if (File.Exists(wal) && TryCopyFile(wal, target + "-wal", force: true))
            {
                totalBytes += new FileInfo(wal).Length;
                copied++;
            }
        }
        catch (Exception ex)
        {
            failed++;
            progress?.Report($"Hata: veritabanı yedeği — {ex.Message}");
        }
    }

    private static void CopyDir(string src, string dst, ref int copied, ref int failed,
                                ref long totalBytes, IProgress<string>? progress)
    {
        foreach (var file in Directory.EnumerateFiles(src))
        {
            var name = Path.GetFileName(file);
            // Önceki yarım kalmış kopyalardan artakalan geçici dosyaları kaynaktan kopyalama.
            if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var target = Path.Combine(dst, name);
                if (TryCopyFile(file, target))
                {
                    totalBytes += new FileInfo(file).Length;
                    copied++;
                    progress?.Report($"Kopyalandı: {name}");
                }
            }
            catch (Exception ex)
            {
                failed++;
                progress?.Report($"Hata: {name} — {ex.Message}");
            }
        }
        foreach (var subDir in Directory.EnumerateDirectories(src))
        {
            var name = Path.GetFileName(subDir);
            var target = Path.Combine(dst, name);
            Directory.CreateDirectory(target);
            CopyDir(subDir, target, ref copied, ref failed, ref totalBytes, progress);
        }
    }

    /// <summary>
    /// Dosyayı güvenli kopyalar: önce hedef klasörde geçici ".part" adına yazar, kopya bitince
    /// gerçek ada taşır — kesinti olursa hedefte yarım dosya değil yalnızca .part artığı kalır.
    /// Hedef zaten varsa boyutları karşılaştırır: aynıysa atlar (false), farklıysa (önceki yarım
    /// kopya) üzerine yazar. <paramref name="force"/> true ise boyut kontrolsüz her zaman kopyalar.
    /// Kopyalama yapıldıysa true döner; hata durumunda istisna fırlatır (çağıran sayar).
    /// </summary>
    private static bool TryCopyFile(string source, string target, bool force = false)
    {
        var srcLen = new FileInfo(source).Length;
        if (!force && File.Exists(target) && new FileInfo(target).Length == srcLen)
            return false; // tam kopya zaten var

        var tmp = target + ".part";
        File.Copy(source, tmp, overwrite: true); // kesinti olursa yalnızca .part kalır
        if (File.Exists(target)) File.Delete(target);
        File.Move(tmp, target); // hedef adı ancak kopya tamamlanınca oluşur
        return true;
    }

    /// <summary>Bir dosya OneDrive senkronlanan bir yolda mı? (üst-üste rozet için.)</summary>
    public bool IsInBackup(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var root = OneDriveRoot;
        if (root is null) return false;
        try
        {
            return Path.GetFullPath(path).StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
