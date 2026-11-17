using Microsoft.EntityFrameworkCore;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Server.Data.Entities;

namespace TelloGameRoom.Tests.Integration;

public sealed class DatabaseRulesTests : IAsyncLifetime
{
    private readonly TelloFactory _factory = new();
    private Guid _branchId;

    public async Task InitializeAsync() =>
        _branchId = (await _factory.SetupAsync(_factory.CreateClient())).User.Branches[0].Id;

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Database_runs_in_wal_mode()
    {
        var mode = await _factory.WithDbAsync(db => db.Database.SqlQueryRaw<string>("PRAGMA journal_mode").ToListAsync());
        Assert.Equal("wal", Assert.Single(mode));
    }

    [Fact]
    public async Task Audit_log_cannot_be_changed_through_the_application()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _factory.WithDbAsync(async db =>
        {
            var entry = await db.AuditLog.FirstAsync();
            entry.Username = "someone-else";
            return await db.SaveChangesAsync();
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _factory.WithDbAsync(async db =>
        {
            db.AuditLog.Remove(await db.AuditLog.FirstAsync());
            return await db.SaveChangesAsync();
        }));
    }

    [Fact]
    public async Task Audit_log_cannot_be_changed_with_raw_sql_either()
    {
        var update = await Assert.ThrowsAnyAsync<Exception>(() => _factory.WithDbAsync(db =>
            db.Database.ExecuteSqlRawAsync("UPDATE AuditLog SET Username = 'x'")));
        Assert.Contains("append-only", update.Message, StringComparison.Ordinal);

        var delete = await Assert.ThrowsAnyAsync<Exception>(() => _factory.WithDbAsync(db =>
            db.Database.ExecuteSqlRawAsync("DELETE FROM AuditLog")));
        Assert.Contains("append-only", delete.Message, StringComparison.Ordinal);

        Assert.True(await _factory.WithDbAsync(db => db.AuditLog.AnyAsync()));
    }

    [Fact]
    public async Task A_machine_can_only_have_one_open_session()
    {
        var (machineId, userId) = await _factory.WithDbAsync(async db =>
        {
            var machine = new Machine { Id = Guid.CreateVersion7(), BranchId = _branchId, Name = "PC-01", TokenHash = "x", CreatedAtUtc = DateTime.UtcNow };
            db.Machines.Add(machine);
            await db.SaveChangesAsync();
            return (machine.Id, await db.Users.Select(u => u.Id).FirstAsync());
        });

        Session NewSession(SessionStatus status) => new()
        {
            Id = Guid.CreateVersion7(),
            BranchId = _branchId,
            MachineId = machineId,
            StartedByUserId = userId,
            Status = status,
            StartedAtUtc = DateTime.UtcNow,
        };

        await _factory.WithDbAsync(async db =>
        {
            db.Sessions.AddRange(NewSession(SessionStatus.Ended), NewSession(SessionStatus.Ended), NewSession(SessionStatus.Active));
            return await db.SaveChangesAsync();
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => _factory.WithDbAsync(async db =>
        {
            db.Sessions.Add(NewSession(SessionStatus.Paused));
            return await db.SaveChangesAsync();
        }));
    }

    [Fact]
    public async Task Timestamps_come_back_as_utc()
    {
        var created = await _factory.WithDbAsync(db => db.Users.Select(u => u.CreatedAtUtc).FirstAsync());
        Assert.Equal(DateTimeKind.Utc, created.Kind);
    }
}
