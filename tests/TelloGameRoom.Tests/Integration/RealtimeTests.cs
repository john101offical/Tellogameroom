using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Protocol;

namespace TelloGameRoom.Tests.Integration;

/// <summary>The SignalR flow with several simulated machines and a dashboard, against the real server.</summary>
public sealed class RealtimeTests : BranchTestBase
{
    [Fact]
    public async Task Several_machines_each_receive_only_their_own_session_in_real_time()
    {
        var machines = new List<SimulatedMachine>();
        for (var i = 1; i <= 5; i++)
        {
            machines.Add(await ConnectMachineAsync($"PC-{i:00}"));
        }

        // Online machines can be started without the offline override.
        await StartAsync(machines[0].Id, 3600, allowOffline: false);
        await StartAsync(machines[2].Id, 1800, allowOffline: false);

        Assert.True(await TestHubs.WaitUntilAsync(() => machines[0].Latest?.State == MachineLockState.Active && machines[2].Latest?.State == MachineLockState.Active));
        Assert.InRange(machines[0].Latest!.RemainingSeconds, 3595, 3600);
        Assert.InRange(machines[2].Latest!.RemainingSeconds, 1795, 1800);
        Assert.Equal(300, machines[0].Latest!.WarningThresholdSeconds);
        Assert.Empty(machines[1].States);
        Assert.Empty(machines[3].States);
        Assert.Empty(machines[4].States);

        // Every later action reaches the affected machine.
        await PostAsync(Owner, $"{B}/machines/{machines[0].Id}/session/add-time", new AdjustTimeRequest(600));
        Assert.True(await TestHubs.WaitUntilAsync(() => machines[0].Latest!.RemainingSeconds > 4100));

        await PostAsync(Owner, $"{B}/machines/{machines[0].Id}/session/pause", new { });
        Assert.True(await TestHubs.WaitUntilAsync(() => machines[0].Latest!.State == MachineLockState.Paused));

        await PostAsync(Owner, $"{B}/machines/{machines[2].Id}/session/stop", new EndSessionRequest());
        Assert.True(await TestHubs.WaitUntilAsync(() => machines[2].Latest!.State == MachineLockState.Locked));

        // Versions only go up, so a machine can discard stale messages.
        var versions = machines[0].States.Select(s => s.Version).ToList();
        Assert.Equal(versions.Order(), versions);

        foreach (var machine in machines)
        {
            await machine.DisposeAsync();
        }
    }

    [Fact]
    public async Task Machine_locks_when_the_session_expires()
    {
        await using var machine = await ConnectMachineAsync("PC-01");
        await StartAsync(machine.Id, 300, allowOffline: false);
        Assert.True(await TestHubs.WaitUntilAsync(() => machine.Latest?.State == MachineLockState.Active));

        Factory.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.True(await TestHubs.WaitUntilAsync(() => machine.Latest?.State == MachineLockState.Locked));
    }

    [Fact]
    public async Task Machine_that_reconnects_gets_the_servers_value()
    {
        var identity = await PairAsync("PC-01");
        var first = new SimulatedMachine(Factory, identity);
        await first.ConnectAsync();
        await StartAsync(first.Id, 3600, allowOffline: false);
        await first.DisconnectAsync();
        await first.DisposeAsync();

        // While the machine is offline, time passes and staff remove some time.
        Factory.Clock.Advance(TimeSpan.FromMinutes(10));
        await PostAsync(Owner, $"{B}/machines/{identity.MachineId}/session/remove-time", new AdjustTimeRequest(600, Reason: "correction"));

        await using var again = new SimulatedMachine(Factory, identity);
        await again.ConnectAsync();
        var synced = await again.SyncAsync();
        Assert.Equal(MachineLockState.Active, synced.State);
        Assert.InRange(synced.RemainingSeconds, 2395, 2400);

        // If it had expired while the machine was away, sync says locked.
        Factory.Clock.Advance(TimeSpan.FromHours(1));
        Assert.True(await TestHubs.WaitUntilAsync(() => again.Latest?.State == MachineLockState.Locked));
        Assert.Equal(MachineLockState.Locked, (await again.SyncAsync()).State);
    }

    [Fact]
    public async Task Transfer_locks_the_old_machine_and_unlocks_the_new_one()
    {
        await using var pc1 = await ConnectMachineAsync("PC-01");
        await using var pc2 = await ConnectMachineAsync("PC-02");
        await StartAsync(pc1.Id, 3600, allowOffline: false);
        Assert.True(await TestHubs.WaitUntilAsync(() => pc1.Latest?.State == MachineLockState.Active));

        (await PostAsync(Owner, $"{B}/machines/{pc1.Id}/session/transfer", new TransferSessionRequest(pc2.Id))).EnsureSuccessStatusCode();
        Assert.True(await TestHubs.WaitUntilAsync(() => pc1.Latest?.State == MachineLockState.Locked && pc2.Latest?.State == MachineLockState.Active));
        Assert.InRange(pc2.Latest!.RemainingSeconds, 3590, 3600);
    }

    [Fact]
    public async Task Dashboard_gets_a_snapshot_then_live_updates_including_presence()
    {
        var identity = await PairAsync("PC-01");
        await using var dashboard = TestHubs.Dashboard(Factory, OwnerAuth.AccessToken);
        var updates = new System.Collections.Concurrent.ConcurrentQueue<MachineStateDto>();
        dashboard.On<MachineStateDto>(nameof(IDashboardClient.MachineChanged), updates.Enqueue);
        await dashboard.StartAsync();

        var snapshot = await dashboard.InvokeAsync<BranchStateDto>(nameof(IDashboardHub.Subscribe), BranchId);
        Assert.Equal("Tello Test Shop", snapshot.ShopName);
        Assert.Equal(500_000, snapshot.Pricing.HourlyRateCents);
        Assert.Equal(MachineStatus.Offline, Assert.Single(snapshot.Machines).Status);

        await using var machine = new SimulatedMachine(Factory, identity);
        await machine.ConnectAsync();
        Assert.True(await TestHubs.WaitUntilAsync(() => updates.LastOrDefault()?.Status == MachineStatus.Free));

        await StartAsync(identity.MachineId, 3600, allowOffline: false);
        Assert.True(await TestHubs.WaitUntilAsync(() => updates.LastOrDefault()?.Status == MachineStatus.InUse));
        Assert.Equal(500_000, updates.Last().Session!.AmountCents);

        await machine.DisconnectAsync();
        Assert.True(await TestHubs.WaitUntilAsync(() => updates.LastOrDefault() is { IsOnline: false, Status: MachineStatus.InUse }));
    }

    [Fact]
    public async Task Hubs_reject_the_wrong_kind_of_caller()
    {
        // No token at all.
        await using var anonymous = TestHubs.Build(Factory, ProtocolInfo.DashboardHubPath, _ => { });
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => anonymous.StartAsync());

        // A staff token on the machine hub.
        await using var staffOnMachineHub = TestHubs.Build(Factory, ProtocolInfo.MachineHubPath,
            o => o.AccessTokenProvider = () => Task.FromResult<string?>(OwnerAuth.AccessToken));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => staffOnMachineHub.StartAsync());

        // An employee cannot subscribe to a branch they do not work in.
        var other = await ReadAsync<BranchSettingsDto>(await PostAsync(Owner, "/branches", new CreateBranchRequest("North")));
        await Factory.CreateEmployeeAsync("maria", "maria-password-1", BranchId);
        var auth = await Factory.LoginAsync(Factory.CreateClient(), "maria", "maria-password-1");
        await using var employee = TestHubs.Dashboard(Factory, auth.AccessToken);
        await employee.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => employee.InvokeAsync<BranchStateDto>(nameof(IDashboardHub.Subscribe), other.Id));
        Assert.NotNull(await employee.InvokeAsync<BranchStateDto>(nameof(IDashboardHub.Subscribe), BranchId));
    }

    [Fact]
    public async Task Messages_and_commands_reach_the_right_machines_and_are_audited()
    {
        await using var pc1 = await ConnectMachineAsync("PC-01");
        await using var pc2 = await ConnectMachineAsync("PC-02");
        var offline = await PairAsync("PC-03");

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(Owner, $"{B}/machines/{pc1.Id}/message", new SendMessageRequest("5 minutes left"))).StatusCode);
        Assert.True(await TestHubs.WaitUntilAsync(() => pc1.Messages.Count == 1));
        Assert.Equal("5 minutes left", pc1.Messages.Single().Text);
        Assert.Empty(pc2.Messages);

        var all = await ReadAsync<BulkResult>(await PostAsync(Owner, $"{B}/message-all", new SendMessageRequest("Closing in 10 minutes")));
        Assert.Equal(new BulkResult(2, 1), all);
        Assert.True(await TestHubs.WaitUntilAsync(() => pc1.Messages.Count == 2 && pc2.Messages.Count == 1));

        await HttpAssert.ProblemAsync(await PostAsync(Owner, $"{B}/machines/{offline.MachineId}/message", new SendMessageRequest("hi")),
            HttpStatusCode.Conflict, ErrorCodes.MachineOffline);
        await HttpAssert.ProblemAsync(await PostAsync(Owner, $"{B}/machines/{pc1.Id}/message", new SendMessageRequest(new string('x', 301))),
            HttpStatusCode.BadRequest, ErrorCodes.Validation);

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(Owner, $"{B}/machines/{pc2.Id}/command", new MachineCommandRequest(MachineCommand.Restart))).StatusCode);
        Assert.True(await TestHubs.WaitUntilAsync(() => pc2.Commands.Contains(MachineCommand.Restart)));
        Assert.Empty(pc1.Commands);

        await ReadAsync<BulkResult>(await PostAsync(Owner, $"{B}/command-all", new MachineCommandRequest(MachineCommand.Shutdown)));
        Assert.True(await TestHubs.WaitUntilAsync(() => pc1.Commands.Contains(MachineCommand.Shutdown) && pc2.Commands.Contains(MachineCommand.Shutdown)));

        var actions = await Factory.WithDbAsync(db => db.AuditLog.Where(a => a.Action.StartsWith("remote.")).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync());
        Assert.Equal([AuditActions.RemoteMessage, AuditActions.RemoteMessageAll, AuditActions.RemoteCommand, AuditActions.RemoteCommandAll], actions);
    }

    [Fact]
    public async Task Remote_lock_pauses_a_running_session_so_no_paid_time_is_lost()
    {
        await using var pc1 = await ConnectMachineAsync("PC-01");
        await StartAsync(pc1.Id, 3600, allowOffline: false);
        (await PostAsync(Owner, $"{B}/machines/{pc1.Id}/command", new MachineCommandRequest(MachineCommand.Lock))).EnsureSuccessStatusCode();

        Assert.True(await TestHubs.WaitUntilAsync(() => pc1.Latest?.State == MachineLockState.Paused && pc1.Commands.Contains(MachineCommand.Lock)));
        Assert.Equal(MachineStatus.Paused, (await GetStateAsync()).Machines.Single().Status);
    }

    [Fact]
    public async Task Employees_can_message_and_lock_but_not_shut_down_or_take_screenshots()
    {
        await using var pc1 = await ConnectMachineAsync("PC-01");
        var employee = await EmployeeClientAsync();

        (await PostAsync(employee, $"{B}/machines/{pc1.Id}/message", new SendMessageRequest("hello"))).EnsureSuccessStatusCode();
        (await PostAsync(employee, $"{B}/machines/{pc1.Id}/command", new MachineCommandRequest(MachineCommand.Lock))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(employee, $"{B}/machines/{pc1.Id}/command", new MachineCommandRequest(MachineCommand.Shutdown))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(employee, $"{B}/command-all", new MachineCommandRequest(MachineCommand.Restart))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(employee, $"{B}/machines/{pc1.Id}/screenshot", new { })).StatusCode);
        Assert.DoesNotContain(MachineCommand.Shutdown, pc1.Commands);
    }

    [Fact]
    public async Task Screenshot_round_trip_is_logged_and_only_the_asked_machine_can_answer()
    {
        await using var pc1 = await ConnectMachineAsync("PC-01");
        await using var pc2 = await ConnectMachineAsync("PC-02");
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

        var request = PostAsync(Owner, $"{B}/machines/{pc1.Id}/screenshot", new { });
        Assert.True(await TestHubs.WaitUntilAsync(() => pc1.ScreenshotRequests.Count == 1));
        var requestId = pc1.ScreenshotRequests.Single();

        HttpClient MachineHttp(SimulatedMachine m)
        {
            var c = Factory.CreateClient();
            c.DefaultRequestHeaders.Authorization = new(ProtocolInfo.MachineAuthScheme, m.Identity.Token);
            return c;
        }

        ByteArrayContent Jpeg(byte[] bytes) => new(bytes) { Headers = { ContentType = new("image/jpeg") } };

        // Another machine cannot answer for PC-01, and non-JPEG data is refused.
        Assert.Equal(HttpStatusCode.NotFound, (await MachineHttp(pc2).PostAsync($"{Api}/machines/me/screenshots/{requestId}", Jpeg(jpeg))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await MachineHttp(pc1).PostAsync($"{Api}/machines/me/screenshots/{requestId}", Jpeg([1, 2, 3, 4, 5]))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await MachineHttp(pc1).PostAsync($"{Api}/machines/me/screenshots/{requestId}", Jpeg(jpeg))).StatusCode);

        var response = await request;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(jpeg, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, await Factory.WithDbAsync(db => db.AuditLog.CountAsync(a => a.Action == AuditActions.RemoteScreenshot && a.MachineName == "PC-01")));
    }

    [Fact]
    public async Task Machine_receives_branding_and_is_told_when_settings_change()
    {
        await using var pc1 = await ConnectMachineAsync("PC-01");
        var config = await pc1.GetConfigAsync();
        Assert.Equal(("Tello Test Shop", "Main", "PC-01", 300), (config.ShopName, config.BranchName, config.MachineName, config.WarningThresholdSeconds));
        Assert.Null(config.AdminPinHash);

        var put = await Owner.PutAsJsonAsync($"{Api}/settings/shop", new UpdateShopSettingsRequest("Tello Arena", "es"), TelloJson.Options);
        put.EnsureSuccessStatusCode();
        Assert.True(await TestHubs.WaitUntilAsync(() => pc1.Configs.LastOrDefault() is { ShopName: "Tello Arena", Language: "es" }));
    }
}
