using SQLite;

namespace MyRoboticsInspector.Models;

[Table("customers")]
public class Customer
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [NotNull, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? Phone { get; set; }

    [MaxLength(120)]
    public string? Email { get; set; }

    [MaxLength(400)]
    public string? Address { get; set; }

    [MaxLength(50)]
    public string? TaxNo { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
