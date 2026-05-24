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
        foreach (var sub in new[] { "inspections", "snapshots", "recordings", "reports" })
            Directory.CreateDirectory(Path.Combine(folder, sub));
        return folder;
    }

    /// <summary>
    /// Mevcut yerel verileri (AppDataDirectory altındaki) OneDrive klasörüne taşır.
    /// İşlem tamamlanınca eski yol boşalır. Hata olursa partial state mümkün — kullanıcı onaylamalı.
    /// </summary>
    public async Task<(int copied, int failed, long bytes)> MigrateAsync(
        string fromPath, string toPath, IProgress<string>? progress = null)
    {
        Directory.CreateDirectory(toPath);
        var copied = 0;
        var failed = 0;
        long totalBytes = 0;

        var subfolders = new[] { "inspections", "snapshots", "recordings", "reports" };
        foreach (var sub in subfolders)
        {
            var src = Path.Combine(fromPath, sub);
            if (!Directory.Exists(src)) continue;
            var dst = Path.Combine(toPath, sub);
            Directory.CreateDirectory(dst);
            await Task.Run(() => CopyDir(src, dst, ref copied, ref failed, ref totalBytes, progress));
        }
        return (copied, failed, totalBytes);
    }

    private static void CopyDir(string src, string dst, ref int copied, ref int failed,
                                ref long totalBytes, IProgress<string>? progress)
    {
        foreach (var file in Directory.EnumerateFiles(src))
        {
            try
            {
                var name = Path.GetFileName(file);
                var target = Path.Combine(dst, name);
                if (!File.Exists(target))
                {
                    File.Copy(file, target);
                    var info = new FileInfo(file);
                    totalBytes += info.Length;
                    copied++;
                    progress?.Report($"Kopyalandı: {name}");
                }
            }
            catch (Exception ex)
            {
                failed++;
                progress?.Report($"Hata: {Path.GetFileName(file)} — {ex.Message}");
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
