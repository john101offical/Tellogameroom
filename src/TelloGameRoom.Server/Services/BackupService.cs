using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TelloGameRoom.Server.Data;
using TelloGameRoom.Server.Infrastructure;

namespace TelloGameRoom.Server.Services;

/// <summary>
/// SQLite backup and restore. Backups are consistent snapshots made with <c>VACUUM INTO</c> while the server keeps
/// running. A restore is staged as a file and swapped in on the next start, because replacing a live database is unsafe.
/// </summary>
public sealed class BackupService(IServiceScopeFactory scopes, IOptions<StorageOptions> storage, TimeProvider time, ILogger<BackupService> logger)
{
    public const string DatabaseFileName = "tellogameroom.db";
    private const string PendingFileName = "restore-pending.db";
    private static readonly byte[] SqliteMagic = "SQLite format 3\0"u8.ToArray();

    public string BackupDirectory => Path.Combine(storage.Value.DataDirectory, "backups");

    /// <summary>Writes a snapshot to the backups folder and returns its path.</summary>
    public async Task<string> CreateAsync(string prefix, CancellationToken ct)
    {
        Directory.CreateDirectory(BackupDirectory);
        var path = Path.Combine(BackupDirectory, $"{prefix}-{time.GetUtcNow():yyyyMMdd-HHmmss}.db");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlAsync($"VACUUM INTO {path}", ct);
        return path;
    }

    /// <summary>Keeps the newest <paramref name="keep"/> automatic backups.</summary>
    public void Prune(string prefix, int keep)
    {
        if (!Directory.Exists(BackupDirectory))
        {
            return;
        }

        foreach (var old in Directory.GetFiles(BackupDirectory, $"{prefix}-*.db").OrderByDescending(f => f, StringComparer.Ordinal).Skip(keep))
        {
            try
            {
                File.Delete(old);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not delete old backup {File}", old);
            }
        }
    }

    /// <summary>Validates an uploaded database and stages it for the next start.</summary>
    /// <returns>An error message, or null when the file was accepted.</returns>
    public async Task<string?> StageRestoreAsync(Stream upload, CancellationToken ct)
    {
        var staging = Path.Combine(storage.Value.DataDirectory, PendingFileName + ".upload");
        await using (var file = File.Create(staging))
        {
            await upload.CopyToAsync(file, ct);
        }

        try
        {
            var header = new byte[SqliteMagic.Length];
            await using (var check = File.OpenRead(staging))
            {
                if (await check.ReadAsync(header, ct) != header.Length || !header.AsSpan().SequenceEqual(SqliteMagic))
                {
                    return "This file is not a TelloGameRoom backup.";
                }
            }

            await using (var connection = new SqliteConnection($"Data Source={staging};Mode=ReadOnly;Pooling=False"))
            {
                await connection.OpenAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT (SELECT COUNT(*) FROM \"__EFMigrationsHistory\"), (SELECT COUNT(*) FROM \"Users\"), (SELECT COUNT(*) FROM pragma_integrity_check WHERE integrity_check <> 'ok')";
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct) || reader.GetInt64(0) == 0 || reader.GetInt64(1) == 0 || reader.GetInt64(2) != 0)
                {
                    return "This backup is empty or damaged.";
                }
            }

            File.Move(staging, Path.Combine(storage.Value.DataDirectory, PendingFileName), overwrite: true);
            return null;
        }
        catch (SqliteException)
        {
            return "This file is not a TelloGameRoom backup.";
        }
        finally
        {
            File.Delete(staging);
        }
    }

    /// <summary>
    /// Called at startup before the database is opened. If a restore was staged, the current database is kept as a
    /// safety copy and the staged file takes its place.
    /// </summary>
    /// <returns>True when a restore was applied.</returns>
    public static bool ApplyPendingRestore(string dataDirectory)
    {
        var pending = Path.Combine(dataDirectory, PendingFileName);
        if (!File.Exists(pending))
        {
            return false;
        }

        var current = Path.Combine(dataDirectory, DatabaseFileName);
        if (File.Exists(current))
        {
            var backups = Path.Combine(dataDirectory, "backups");
            Directory.CreateDirectory(backups);
            File.Move(current, Path.Combine(backups, $"pre-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db"), overwrite: true);
        }

        File.Delete(current + "-wal");
        File.Delete(current + "-shm");
        File.Move(pending, current);
        return true;
    }
}

/// <summary>Takes one automatic backup per day and keeps the last 14.</summary>
public sealed class DailyBackupWorker(BackupService backups, TimeProvider time, ILogger<DailyBackupWorker> logger) : BackgroundService
{
    private const string Prefix = "auto";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var today = $"{Prefix}-{time.GetUtcNow():yyyyMMdd}";
                if (Directory.Exists(backups.BackupDirectory) && Directory.GetFiles(backups.BackupDirectory, today + "-*.db").Length > 0)
                {
                    continue;
                }

                var path = await backups.CreateAsync(Prefix, stoppingToken);
                backups.Prune(Prefix, keep: 14);
                logger.LogInformation("Automatic backup written to {Path}", path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Automatic backup failed");
            }
        }
    }
}
