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
        // Bağlı incelemeler için cascade DB'de yok; manuel temizlik gerek varsa
        // future bir migration ile foreign key ON DELETE CASCADE eklenir.
        return await db.DeleteAsync(job);
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
        return await db.Table<AppSettings>().FirstAsync();
    }

    public async Task<int> SaveSettingsAsync(AppSettings settings)
    {
        var db = await GetConnectionAsync();
        return await db.UpdateAsync(settings);
    }
}
