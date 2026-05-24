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
}
