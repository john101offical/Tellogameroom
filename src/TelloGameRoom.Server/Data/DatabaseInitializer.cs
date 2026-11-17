using Microsoft.EntityFrameworkCore;

namespace TelloGameRoom.Server.Data;

public static class DatabaseInitializer
{
    /// <summary>Applies pending migrations and, on SQLite, switches the database to WAL journaling.</summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync(ct);

        if (db.Database.IsSqlite())
        {
            // WAL lets dashboards read while a session write is in progress; NORMAL sync is durable in WAL mode
            // against application crashes and loses at most the last transaction on power loss.
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", ct);
        }
    }
}
