using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Core.Sessions;

namespace TelloGameRoom.Tests.Integration;

/// <summary>Whole-system scenarios: the real server, simulated machines running the real client-side timer, and dashboards.</summary>
public sealed class EndToEndTests : BranchTestBase
{
    [Fact]
    public async Task A_working_day_from_first_setup_to_the_cash_count()
    {
        // The owner registers an employee and three PCs.
        var maria = await ReadAsync<EmployeeDto>(await PostAsync(Owner, "/employees",
            new CreateEmployeeRequest("maria", "María", "maria-password-1", UserRole.Employee, [BranchId])));
        await using var pc1 = await ConnectMachineAsync("PC-01");
        await using var pc2 = await ConnectMachineAsync("PC-02");
        await using var pc3 = await ConnectMachineAsync("PC-03");

        // María signs in on her own account and clocks in with COP 20,000 in the drawer.
        var staff = Factory.CreateClient();
        TelloFactory.Authorize(staff, (await Factory.LoginAsync(staff, "maria", "maria-password-1")).AccessToken);
        (await PostAsync(staff, $"{B}/shifts/open", new OpenShiftRequest(2_000_000))).EnsureSuccessStatusCode();

        // Each simulated PC runs the same tracker the Machine app uses, on a clock we control.
        var local = TimeSpan.Zero;
        MachineSessionTracker TrackerFor(SimulatedMachine m)
        {
            var tracker = new MachineSessionTracker(() => local);
            foreach (var state in m.States)
            {
                tracker.ApplyServerState(state);
            }

            return tracker;
        }

        // Three customers pay: 1 h, 2 h and 30 minutes.
        await StartAsync(pc1.Id, 3600, staff, allowOffline: false);
        await StartAsync(pc2.Id, 7200, staff, allowOffline: false);
        await StartAsync(pc3.Id, 1800, staff, allowOffline: false);
        Assert.True(await TestHubs.WaitUntilAsync(() => new[] { pc1, pc2, pc3 }.All(p => p.Latest?.State == MachineLockState.Active)));
        Assert.Equal(MachineLockState.Active, TrackerFor(pc1).Tick().State);

        // 25 minutes later PC-03 is inside its warning window; its customer pays for 30 more minutes.
        Factory.Clock.Advance(TimeSpan.FromMinutes(25));
        local += TimeSpan.FromMinutes(25);
        var warning = new MachineSessionTracker(() => local);
        warning.ApplyServerState(await pc3.SyncAsync());
        Assert.True(warning.Tick().WarningActive);
        (await PostAsync(staff, $"{B}/machines/{pc3.Id}/session/add-time", new AdjustTimeRequest(1800))).EnsureSuccessStatusCode();
        Assert.True(await TestHubs.WaitUntilAsync(() => pc3.Latest!.RemainingSeconds > 2000));

        // María tells the customer on PC-01 that the hour is almost over.
        (await PostAsync(staff, $"{B}/machines/{pc1.Id}/message", new SendMessageRequest("5 minutes left"))).EnsureSuccessStatusCode();
        Assert.True(await TestHubs.WaitUntilAsync(() => pc1.Messages.Count == 1));

        // PC-01's hour runs out: the server ends the session and the PC locks.
        Factory.Clock.Advance(TimeSpan.FromMinutes(36));
        Assert.True(await TestHubs.WaitUntilAsync(() => pc1.Latest?.State == MachineLockState.Locked));

        // The owner corrects an overcharge on PC-02 (refund of 30 minutes) and María's shift is unaffected by it.
        (await PostAsync(Owner, $"{B}/machines/{pc2.Id}/session/remove-time", new AdjustTimeRequest(1800, Reason: "charged too much"))).EnsureSuccessStatusCode();

        // End of shift: María sold 5,000 + 10,000 + 2,500 + 2,500 = COP 20,000, so the drawer should hold 40,000.
        var closed = await ReadAsync<ShiftDto>(await PostAsync(staff, $"{B}/shifts/close", new CloseShiftRequest(4_000_000)));
        Assert.Equal(2_000_000, closed.SalesCents);
        Assert.Equal(4_000_000, closed.ExpectedCashCents);
        Assert.Equal(0, closed.DifferenceCents);

        // The owner's report agrees with the ledger: María's 20,000 minus the owner's 2,500 refund.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Factory.Clock.GetUtcNow().UtcDateTime, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota")));
        var report = (await Owner.GetFromJsonAsync<ReportDto>($"{Api}{B}/reports/summary?from={today.AddDays(-1):yyyy-MM-dd}&to={today:yyyy-MM-dd}", TelloJson.Options))!;
        Assert.Equal(1_750_000, report.IncomeCents);
        Assert.Equal(3, report.Sessions);
        Assert.Equal(2_000_000, report.ByEmployee.Single(e => e.Id == maria.Id).IncomeCents);
        Assert.Equal(await Factory.WithDbAsync(db => db.SessionEvents.SumAsync(e => e.AmountDeltaCents)), report.IncomeCents);

        // And every step left a trace in the audit log.
        var actions = await Factory.WithDbAsync(db => db.AuditLog.Select(a => a.Action).Distinct().ToListAsync());
        Assert.Superset(
            new HashSet<string> { AuditActions.EmployeeCreated, AuditActions.MachinePaired, AuditActions.ShiftOpened, AuditActions.SessionStarted,
                AuditActions.SessionTimeAdded, AuditActions.RemoteMessage, AuditActions.SessionExpired, AuditActions.SessionTimeRemoved, AuditActions.ShiftClosed },
            actions.ToHashSet());
    }

    [Fact]
    public async Task One_hundred_machines_and_ten_dashboards_stay_in_sync()
    {
        const int MachineCount = 100;
        const int DashboardCount = 10;

        // Pair sequentially (a one-time setup step), connect in parallel.
        var machines = new List<SimulatedMachine>();
        for (var i = 1; i <= MachineCount; i++)
        {
            machines.Add(new SimulatedMachine(Factory, await PairAsync($"PC-{i:000}")));
        }

        await Task.WhenAll(machines.Select(m => m.ConnectAsync()));

        var dashboards = new List<HubConnection>();
        var seen = new ConcurrentDictionary<int, ConcurrentDictionary<Guid, MachineStatus>>();
        for (var d = 0; d < DashboardCount; d++)
        {
            var index = d;
            var hub = TestHubs.Dashboard(Factory, OwnerAuth.AccessToken);
            seen[index] = new ConcurrentDictionary<Guid, MachineStatus>();
            hub.On<MachineStateDto>(nameof(IDashboardClient.MachineChanged), m => seen[index][m.Id] = m.Status);
            await hub.StartAsync();
            var snapshot = await hub.InvokeAsync<BranchStateDto>(nameof(IDashboardHub.Subscribe), BranchId);
            Assert.Equal(MachineCount, snapshot.Machines.Count);
            dashboards.Add(hub);
        }

        // Start a session on every machine at once.
        var stopwatch = Stopwatch.StartNew();
        var responses = await Task.WhenAll(machines.Select(m =>
            PostAsync(Owner, $"{B}/machines/{m.Id}/session/start", new StartSessionRequest(3600))));
        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode));

        // Every machine unlocks and every dashboard shows every machine in use.
        Assert.True(await TestHubs.WaitUntilAsync(() => machines.All(m => m.Latest?.State == MachineLockState.Active), 30_000));
        Assert.True(await TestHubs.WaitUntilAsync(() => seen.Values.All(s => s.Count(kv => kv.Value == MachineStatus.InUse) == MachineCount), 30_000));
        stopwatch.Stop();

        // Generous bound for a loaded CI machine; locally this takes a second or two.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"Took {stopwatch.Elapsed.TotalSeconds:0.0}s");
        Assert.Equal(MachineCount, await Factory.WithDbAsync(db => db.Sessions.CountAsync(s => s.Status == SessionStatus.Active)));

        // All of them expire together and every machine locks again.
        Factory.Clock.Advance(TimeSpan.FromMinutes(61));
        Assert.True(await TestHubs.WaitUntilAsync(() => machines.All(m => m.Latest?.State == MachineLockState.Locked), 30_000));
        Assert.Equal(0, await Factory.WithDbAsync(db => db.Sessions.CountAsync(s => s.Status != SessionStatus.Ended)));

        foreach (var hub in dashboards)
        {
            await hub.DisposeAsync();
        }

        await Task.WhenAll(machines.Select(m => m.DisposeAsync().AsTask()));
    }
}
