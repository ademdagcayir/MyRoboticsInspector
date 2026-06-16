using SQLite;

namespace MyRoboticsInspector.Models;

/// <summary>
/// Bulut yedek kuyruğu — outbox deseni. Tarayıcı yeni dosyaları buraya ekler,
/// arka plan işçisi sırayla yükler. UploadedAt dolu = bulutta.
/// Tenant (bulut hesabı) başına ayrı satır: hesap değişirse dosyalar yeni
/// hesaba da yedeklenir.
/// </summary>
[Table("cloud_file_state")]
public class CloudFileState
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Supabase auth.uid() — hangi bulut hesabına ait.</summary>
    [Indexed(Name = "IX_TenantRel", Order = 1, Unique = true), MaxLength(60)]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>StorageRoot'a göre dosya yolu (ör. projeler/X/Mahalle/Sokak/video/_1_a.mp4).</summary>
    [Indexed(Name = "IX_TenantRel", Order = 2, Unique = true), MaxLength(900)]
    public string RelPath { get; set; } = string.Empty;

    /// <summary>Kuyruğa eklendiği andaki tam yerel yol.</summary>
    [MaxLength(1000)]
    public string AbsPath { get; set; } = string.Empty;

    public long ByteSize { get; set; }

    public int Attempts { get; set; }

    [MaxLength(500)]
    public string? LastError { get; set; }

    public DateTime EnqueuedAt { get; set; } = DateTime.Now;

    /// <summary>Dolu ise yükleme başarıyla tamamlandı.</summary>
    public DateTime? UploadedAt { get; set; }

    /// <summary>Bucket içi yol ({uid}/{deviceId}/{relPath}).</summary>
    [MaxLength(1000)]
    public string? StoragePath { get; set; }
}
