using SQLite;

namespace MyRoboticsInspector.Models;

[Table("profiles")]
public class Profile
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [NotNull, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional 4-6 digit PIN; null means anyone can log in as this profile.</summary>
    [MaxLength(6)]
    public string? Pin { get; set; }

    [MaxLength(120)]
    public string? Email { get; set; }

    [MaxLength(500)]
    public string? AvatarPath { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? LastLoginAt { get; set; }

    // ── Görüntü yardımcıları (yalnız get — sqlite-net persist etmez) ──

    /// <summary>Avatar baş harfleri: "Adem Dağcayır" → "AD".</summary>
    public string Initials
    {
        get
        {
            var parts = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length switch
            {
                0 => "?",
                1 => parts[0][..1].ToUpperInvariant(),
                _ => string.Concat(parts[0][..1], parts[^1][..1]).ToUpperInvariant(),
            };
        }
    }

    /// <summary>"Son giriş: bugün 14:32 / dün / 12.05.2026" — profil kartı alt yazısı.</summary>
    public string LastLoginText
    {
        get
        {
            if (LastLoginAt is not { } at) return "İlk giriş bekleniyor";
            var today = DateTime.Today;
            return at.Date == today ? $"Son giriş bugün {at:HH:mm}"
                 : at.Date == today.AddDays(-1) ? $"Son giriş dün {at:HH:mm}"
                 : $"Son giriş {at:dd.MM.yyyy}";
        }
    }

    public bool HasPin => !string.IsNullOrEmpty(Pin);
}
