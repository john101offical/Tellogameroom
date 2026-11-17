using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Protocol;

namespace TelloGameRoom.Tests.Integration;

public sealed class SessionTests : BranchTestBase
{
    private Guid _pc1;
    private Guid _pc2;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _pc1 = (await PairAsync("PC-01")).MachineId;
        _pc2 = (await PairAsync("PC-02")).MachineId;
    }

    private string M(Guid id) => $"{B}/machines/{id}";

    private Task<long> LedgerSumAsync() => Factory.WithDbAsync(db => db.SessionEvents.SumAsync(e => e.AmountDeltaCents));

    [Fact]
    public async Task Quote_then_start_charges_the_quoted_amount()
    {
        var quote = await Owner.GetFromJsonAsync<QuoteResponse>($"{Api}{M(_pc1)}/quote?seconds=5400", TelloJson.Options);
        Assert.Equal(750_000, quote!.AmountCents); // 1.5 h at COP 5,000

        var state = await ReadAsync<MachineStateDto>(await PostAsync(Owner, $"{M(_pc1)}/session/start",
            new StartSessionRequest(5400, quote.AmountCents, AllowOffline: true)));
        Assert.Equal(MachineStatus.InUse, state.Status);
        Assert.Equal(5400, state.Session!.RemainingSeconds);
        Assert.Equal(750_000, state.Session.AmountCents);
        Assert.Equal("The Owner", state.Session.StartedBy);
        Assert.Equal(750_000, await LedgerSumAsync());
    }

    [Fact]
    public async Task Start_is_rejected_when_the_price_shown_to_staff_is_stale()
    {
        var response = await PostAsync(Owner, $"{M(_pc1)}/session/start", new StartSessionRequest(3600, 400_000, AllowOffline: true));
        await HttpAssert.ProblemAsync(response, HttpStatusCode.Conflict, ErrorCodes.PriceChanged);
        Assert.Equal(0, await Factory.WithDbAsync(db => db.Sessions.CountAsync()));
    }

    [Fact]
    public async Task Offline_machine_needs_explicit_confirmation()
    {
        var response = await PostAsync(Owner, $"{M(_pc1)}/session/start", new StartSessionRequest(3600));
        await HttpAssert.ProblemAsync(response, HttpStatusCode.Conflict, ErrorCodes.MachineOffline);
    }

    [Fact]
    public async Task A_machine_cannot_have_two_sessions_even_under_concurrency()
    {
        var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            PostAsync(Owner, $"{M(_pc1)}/session/start", new StartSessionRequest(3600, AllowOffline: true))));
        Assert.Equal(1, attempts.Count(r => r.IsSuccessStatusCode));
        Assert.All(attempts.Where(r => !r.IsSuccessStatusCode), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(500_000, await LedgerSumAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(90_000)]
    [InlineData(-3600)]
    public async Task Invalid_durations_are_rejected(long seconds)
    {
        var response = await PostAsync(Owner, $"{M(_pc1)}/session/start", new StartSessionRequest(seconds, AllowOffline: true));
        await HttpAssert.ProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task Time_runs_down_on_the_server_clock()
    {
        await StartAsync(_pc1, 3600);
        Factory.Clock.Advance(TimeSpan.FromMinutes(20));
        var machine = (await GetStateAsync()).Machines.Single(m => m.Id == _pc1);
        Assert.InRange(machine.Session!.RemainingSeconds, 2395, 2400);
    }

    [Fact]
    public async Task Add_time_extends_the_session_and_charges_for_it()
    {
        await StartAsync(_pc1, 3600);
        Factory.Clock.Advance(TimeSpan.FromMinutes(30));
        var state = await ReadAsync<MachineStateDto>(await PostAsync(Owner, $"{M(_pc1)}/session/add-time", new AdjustTimeRequest(1800, 250_000)));
        Assert.InRange(state.Session!.RemainingSeconds, 3595, 3600);
        Assert.Equal(5400, state.Session.PurchasedSeconds);
        Assert.Equal(750_000, state.Session.AmountCents);
        Assert.Equal(750_000, await LedgerSumAsync());
    }

    [Fact]
    public async Task Remove_time_needs_a_reason_and_refunds_cash()
    {
        await StartAsync(_pc1, 7200);
        await HttpAssert.ProblemAsync(await PostAsync(Owner, $"{M(_pc1)}/session/remove-time", new AdjustTimeRequest(3600)),
            HttpStatusCode.BadRequest, ErrorCodes.ReasonRequired);

        var state = await ReadAsync<MachineStateDto>(await PostAsync(Owner, $"{M(_pc1)}/session/remove-time",
            new AdjustTimeRequest(3600, Reason: "charged two hours by mistake")));
        Assert.InRange(state.Session!.RemainingSeconds, 3595, 3600);
        Assert.Equal(500_000, state.Session.AmountCents);
        Assert.Equal(500_000, await LedgerSumAsync());

        // Removing everything is not allowed; that is what stop and cancel are for.
        await HttpAssert.ProblemAsync(await PostAsync(Owner, $"{M(_pc1)}/session/remove-time", new AdjustTimeRequest(3600, Reason: "all")),
            HttpStatusCode.Conflict, ErrorCodes.InvalidSessionState);
    }

    [Fact]
    public async Task Pause_freezes_time_and_resume_continues_from_where_it_stopped()
    {
        await StartAsync(_pc1, 3600);
        Factory.Clock.Advance(TimeSpan.FromMinutes(10));
        var paused = await ReadAsync<MachineStateDto>(await PostAsync(Owner, $"{M(_pc1)}/session/pause", new { }));
        Assert.Equal(MachineStatus.Paused, paused.Status);
        var frozen = paused.Session!.RemainingSeconds;
        Assert.InRange(frozen, 2995, 3000);

        Factory.Clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal(frozen, (await GetStateAsync()).Machines.Single(m => m.Id == _pc1).Session!.RemainingSeconds);
        await HttpAssert.ProblemAsync(await PostAsync(Owner, $"{M(_pc1)}/session/pause", new { }), HttpStatusCode.Conflict, ErrorCodes.InvalidSessionState);

        var resumed = await ReadAsync<MachineStateDto>(await PostAsync(Owner, $"{M(_pc1)}/session/resume", new { }));
        Assert.Equal(MachineStatus.InUse, resumed.Status);
        Assert.Equal(frozen, resumed.Session!.RemainingSeconds);
        await HttpAssert.ProblemAsync(await PostAsync(Owner, $"{M(_pc1)}/session/resume", new { }), HttpStatusCode.Conflict, ErrorCodes.InvalidSessionState);
    }

    [Fact]
    public async Task Time_can_be_added_while_paused()
    {
        await StartAsync(_pc1, 3600);
        await PostAsync(Owner, $"{M(_pc1)}/session/pause", new { });
        var state = await ReadAsync<MachineStateDto>(await PostAsync(Owner, $"{M(_pc1)}/session/add-time", new AdjustTimeRequest(1800)));
        Assert.Equal(MachineStatus.Paused, state.Status);
        Assert.InRange(state.Session!.RemainingSeconds, 5395, 5400);
    }

    [Fact]
    public async Task Stop_ends_the_session_without_a_refund()
    {
        await StartAsync(_pc1, 3600);
        var state = await ReadAsync<MachineStateDto>(await PostAsync(Owner, $"{M(_pc1)}/session/stop", new EndSessionRequest("customer left")));
        Assert.Null(state.Session);
        Assert.Equal(500_000, await LedgerSumAsync());
        await HttpAssert.ProblemAsync(await PostAsync(Owner, $"{M(_pc1)}/session/stop", new EndSessionRequest()), HttpStatusCode.Conflict, ErrorCodes.NoOpenSession);

        // The machine is free again.
        await StartAsync(_pc1, 1800);
    }

    [Fact]
    public async Task Cancel_refunds_the_unused_time_and_requires_a_reason()
    {
        await StartAsync(_pc1, 7200);
        Factory.Clock.Advance(TimeSpan.FromHours(1));
        await HttpAssert.ProblemAsync(await PostAsync(Owner, $"{M(_pc1)}/session/cancel", new EndSessionRequest()),
            HttpStatusCode.BadRequest, ErrorCodes.ReasonRequired);

        await ReadAsync<MachineStateDto>(await PostAsync(Owner, $"{M(_pc1)}/session/cancel", new EndSessionRequest("power cut")));
        Assert.Equal(500_000, await LedgerSumAsync()); // paid 10,000, used one hour, got 5,000 back
        var session = await Factory.WithDbAsync(db => db.Sessions.SingleAsync());
        Assert.Equal(SessionEndReason.Cancelled, session.EndReason);
        Assert.Equal(session.AmountCents, await LedgerSumAsync());
    }

    [Fact]
    public async Task Cancel_cannot_refund_more_than_was_paid()
    {
        await StartAsync(_pc1, 3600);
        await HttpAssert.ProblemAsync(await PostAsync(Owner, $"{M(_pc1)}/session/cancel", new EndSessionRequest("x", 600_000)),
            HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await ReadAsync<MachineStateDto>(await PostAsync(Owner, $"{M(_pc1)}/session/cancel", new EndSessionRequest("full refund", 500_000)));
        Assert.Equal(0, await LedgerSumAsync());
    }

    [Fact]
    public async Task Transfer_moves_the_session_with_its_remaining_time()
    {
        await StartAsync(_pc1, 3600);
        Factory.Clock.Advance(TimeSpan.FromMinutes(15));
        var target = await ReadAsync<MachineStateDto>(await PostAsync(Owner, $"{M(_pc1)}/session/transfer",
            new TransferSessionRequest(_pc2, "PC-01 crashed", AllowOffline: true)));
        Assert.Equal(_pc2, target.Id);
        Assert.InRange(target.Session!.RemainingSeconds, 2695, 2700);

        var state = await GetStateAsync();
        Assert.Null(state.Machines.Single(m => m.Id == _pc1).Session);
        Assert.Equal(500_000, await LedgerSumAsync()); // no money moved
    }

    [Fact]
    public async Task Transfer_to_a_busy_machine_is_rejected()
    {
        await StartAsync(_pc1);
        await StartAsync(_pc2);
        await HttpAssert.ProblemAsync(await PostAsync(Owner, $"{M(_pc1)}/session/transfer", new TransferSessionRequest(_pc2, AllowOffline: true)),
            HttpStatusCode.Conflict, ErrorCodes.MachineBusy);
        await HttpAssert.ProblemAsync(await PostAsync(Owner, $"{M(_pc1)}/session/transfer", new TransferSessionRequest(_pc1)),
            HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task Session_expires_on_its_own_when_time_runs_out()
    {
        await StartAsync(_pc1, 600);
        Factory.Clock.Advance(TimeSpan.FromMinutes(11));

        Assert.True(await TestHubs.WaitUntilAsync(() =>
            Factory.WithDbAsync(db => db.Sessions.AnyAsync(s => s.Status == SessionStatus.Ended)).GetAwaiter().GetResult()));
        var session = await Factory.WithDbAsync(db => db.Sessions.SingleAsync());
        Assert.Equal(SessionEndReason.TimeExpired, session.EndReason);
        Assert.Equal(session.StartedAtUtc.AddSeconds(600), session.EndedAtUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(MachineStatus.Offline, (await GetStateAsync()).Machines.Single(m => m.Id == _pc1).Status);
    }

    [Fact]
    public async Task Free_session_is_owner_only_needs_a_reason_and_costs_nothing()
    {
        await HttpAssert.ProblemAsync(await PostAsync(Owner, $"{M(_pc1)}/session/start", new StartSessionRequest(3600, IsFree: true, AllowOffline: true)),
            HttpStatusCode.BadRequest, ErrorCodes.ReasonRequired);

        var employee = await EmployeeClientAsync();
        var forbidden = await PostAsync(employee, $"{M(_pc1)}/session/start", new StartSessionRequest(3600, IsFree: true, Reason: "friend", AllowOffline: true));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var state = await ReadAsync<MachineStateDto>(await PostAsync(Owner, $"{M(_pc1)}/session/start",
            new StartSessionRequest(3600, IsFree: true, Reason: "tournament prize", AllowOffline: true)));
        Assert.True(state.Session!.IsFree);
        Assert.Equal(0, await LedgerSumAsync());
        Assert.Equal(1, await Factory.WithDbAsync(db => db.AuditLog.CountAsync(a => a.Action == AuditActions.SessionFreeStarted && a.Reason == "tournament prize")));
    }

    [Fact]
    public async Task Employee_permissions_are_enforced_on_the_server()
    {
        var employee = await EmployeeClientAsync();
        await StartAsync(_pc1, 3600, employee);
        (await PostAsync(employee, $"{M(_pc1)}/session/add-time", new AdjustTimeRequest(1800))).EnsureSuccessStatusCode();
        (await PostAsync(employee, $"{M(_pc1)}/session/pause", new { })).EnsureSuccessStatusCode();
        (await PostAsync(employee, $"{M(_pc1)}/session/resume", new { })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(employee, $"{M(_pc1)}/session/remove-time", new AdjustTimeRequest(600, Reason: "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(employee, $"{M(_pc1)}/session/cancel", new EndSessionRequest("x"))).StatusCode);
    }

    [Fact]
    public async Task Employee_must_be_clocked_in_to_sell()
    {
        var employee = await EmployeeClientAsync(clockIn: false);
        await HttpAssert.ProblemAsync(await PostAsync(employee, $"{M(_pc1)}/session/start", new StartSessionRequest(3600, AllowOffline: true)),
            HttpStatusCode.Conflict, ErrorCodes.ShiftRequired);
    }

    [Fact]
    public async Task Price_change_only_affects_future_sessions()
    {
        await StartAsync(_pc1, 3600);
        await Factory.WithDbAsync(db => db.Branches.ExecuteUpdateAsync(s => s.SetProperty(b => b.HourlyRateCents, 800_000)));

        // Extending the running session uses the rate it started with.
        var extended = await ReadAsync<MachineStateDto>(await PostAsync(Owner, $"{M(_pc1)}/session/add-time", new AdjustTimeRequest(3600)));
        Assert.Equal(1_000_000, extended.Session!.AmountCents);

        var fresh = await StartAsync(_pc2, 3600);
        Assert.Equal(800_000, fresh.Session!.AmountCents);
    }

    [Fact]
    public async Task Machine_group_rate_overrides_the_branch_rate()
    {
        var group = await ReadAsync<MachineGroupDto>(await PostAsync(Owner, $"{B}/groups", new UpsertMachineGroupRequest("VIP", 900_000)));
        (await Owner.PutAsJsonAsync($"{Api}{M(_pc2)}", new UpdateMachineRequest("PC-02", group.Id, true), TelloJson.Options)).EnsureSuccessStatusCode();

        Assert.Equal(900_000, (await StartAsync(_pc2, 3600)).Session!.AmountCents);
        Assert.Equal(500_000, (await StartAsync(_pc1, 3600)).Session!.AmountCents);
    }

    [Fact]
    public async Task Every_session_action_is_audited_with_who_and_which_machine()
    {
        await StartAsync(_pc1, 3600);
        await PostAsync(Owner, $"{M(_pc1)}/session/add-time", new AdjustTimeRequest(600));
        await PostAsync(Owner, $"{M(_pc1)}/session/remove-time", new AdjustTimeRequest(300, Reason: "oops"));
        await PostAsync(Owner, $"{M(_pc1)}/session/pause", new { });
        await PostAsync(Owner, $"{M(_pc1)}/session/resume", new { });
        await PostAsync(Owner, $"{M(_pc1)}/session/transfer", new TransferSessionRequest(_pc2, AllowOffline: true));
        await PostAsync(Owner, $"{M(_pc2)}/session/stop", new EndSessionRequest());

        var log = await Factory.WithDbAsync(db => db.AuditLog.Where(a => a.Action.StartsWith("session.")).OrderBy(a => a.Id).ToListAsync());
        Assert.Equal(
            [AuditActions.SessionStarted, AuditActions.SessionTimeAdded, AuditActions.SessionTimeRemoved, AuditActions.SessionPaused,
                AuditActions.SessionResumed, AuditActions.SessionTransferred, AuditActions.SessionStopped],
            log.Select(a => a.Action));
        Assert.All(log, a =>
        {
            Assert.Equal("owner", a.Username);
            Assert.NotNull(a.MachineName);
            Assert.NotNull(a.SessionId);
        });
        Assert.Equal("oops", log[2].Reason);
    }
}
