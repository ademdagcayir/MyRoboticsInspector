using MyRoboticsInspector.Models;

namespace MyRoboticsInspector.Services;

/// <summary>
/// Currently a local-only profile/operator store — no real auth or cloud.
/// Cloud sync (Supabase / custom backend) is a separate piece of work; this lays the foundation
/// so when sync arrives, every record can be tagged with the operator who created it.
/// </summary>
public class AuthService
{
    private readonly DatabaseService _db;

    public Profile? CurrentProfile { get; private set; }
    public event EventHandler? CurrentProfileChanged;

    public AuthService(DatabaseService db) => _db = db;

    public async Task<List<Profile>> GetProfilesAsync()
    {
        var conn = await _db.GetConnectionAsync();
        return await conn.Table<Profile>().OrderByDescending(p => p.LastLoginAt ?? p.CreatedAt).ToListAsync();
    }

    public async Task<Profile> CreateAsync(string name, string? pin, string? email)
    {
        var conn = await _db.GetConnectionAsync();
        var p = new Profile { Name = name.Trim(), Pin = pin, Email = email };
        await conn.InsertAsync(p);
        return p;
    }

    public async Task<bool> LoginAsync(Profile profile, string? pinAttempt)
    {
        if (!string.IsNullOrEmpty(profile.Pin) && profile.Pin != pinAttempt) return false;

        profile.LastLoginAt = DateTime.Now;
        var conn = await _db.GetConnectionAsync();
        await conn.UpdateAsync(profile);

        CurrentProfile = profile;
        CurrentProfileChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Logout()
    {
        CurrentProfile = null;
        CurrentProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteAsync(Profile profile)
    {
        var conn = await _db.GetConnectionAsync();
        await conn.DeleteAsync(profile);
        if (CurrentProfile?.Id == profile.Id) Logout();
    }
}
