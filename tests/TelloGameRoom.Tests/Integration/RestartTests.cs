using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;

namespace TelloGameRoom.Tests.Integration;

/// <summary>Timers must survive a server restart: state is persisted and recomputed from timestamps.</summary>
public sealed class RestartTests : BranchTestBase
{
    [Fact]
    public async Task Sessions_survive_a_restart_and_overdue_ones_are_expired_afterwards()
    {
        var running = (await PairAsync("PC-01")).MachineId;
        var paused = (await PairAsync("PC-02")).MachineId;
        var shortOne = (await PairAsync("PC-03")).MachineId;
        await StartAsync(running, 7200);
        await StartAsync(paused, 3600);
        await StartAsync(shortOne, 600);
        Factory.Clock.Advance(TimeSpan.FromMinutes(5));
        (await PostAsync(Owner, $"{B}/machines/{paused}/session/pause", new { })).EnsureSuccessStatusCode();

        // The server goes down. Half an hour passes before it is back.
        var directory = Factory.DetachDataDirectory();
        var clock = Factory.Clock;
        Factory.Dispose();
        SqliteConnection.ClearAllPools();
        clock.Advance(TimeSpan.FromMinutes(30));

        using var restarted = new TelloFactory(directory, clock) { AccessTokenMinutes = 60 * 24 * 30 };
        var owner = restarted.CreateClient();
        TelloFactory.Authorize(owner, (await restarted.LoginAsync(owner, TelloFactory.OwnerUsername, TelloFactory.OwnerPassword)).AccessToken);

        Assert.True(await TestHubs.WaitUntilAsync(() =>
            restarted.WithDbAsync(db => db.Sessions.AnyAsync(s => s.MachineId == shortOne && s.Status == SessionStatus.Ended)).GetAwaiter().GetResult()));

        var response = await owner.GetAsync($"{Api}{B}/state");
        var state = await ReadAsync<BranchStateDto>(response);
        var byId = state.Machines.ToDictionary(m => m.Id);

        // Running: 2 h minus the 35 minutes that passed, downtime included.
        Assert.InRange(byId[running].Session!.RemainingSeconds, 7200 - 35 * 60 - 5, 7200 - 35 * 60);
        // Paused: exactly what was left when it was paused.
        Assert.Equal(MachineStatus.Paused, byId[paused].Status);
        Assert.InRange(byId[paused].Session!.RemainingSeconds, 3295, 3300);
        // The 10-minute session ran out during the downtime and is recorded as ending when its time was up.
        Assert.Null(byId[shortOne].Session);
        var expired = await restarted.WithDbAsync(db => db.Sessions.SingleAsync(s => s.MachineId == shortOne));
        Assert.Equal(SessionEndReason.TimeExpired, expired.EndReason);
        Assert.Equal(expired.StartedAtUtc.AddSeconds(600), expired.EndedAtUtc!.Value, TimeSpan.FromSeconds(1));

        // The signing key is persisted too, so a token issued before the restart is still accepted.
        var old = restarted.CreateClient();
        TelloFactory.Authorize(old, OwnerAuth.AccessToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await old.GetAsync($"{Api}/auth/me")).StatusCode);
        Directory.Delete(directory, recursive: true);
    }
}
