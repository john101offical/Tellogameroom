using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Core.Security;

namespace TelloGameRoom.Tests.Integration;

public sealed class ShiftTests : BranchTestBase
{
    [Fact]
    public async Task Shift_reconciles_cash_from_the_sessions_the_employee_sold()
    {
        var pc1 = (await PairAsync("PC-01")).MachineId;
        var pc2 = (await PairAsync("PC-02")).MachineId;
        var maria = await EmployeeClientAsync("maria", clockIn: false);

        var opened = await ReadAsync<ShiftDto>(await PostAsync(maria, $"{B}/shifts/open", new OpenShiftRequest(2_000_000)));
        Assert.Equal(2_000_000, opened.ExpectedCashCents);
        await HttpAssert.ProblemAsync(await PostAsync(maria, $"{B}/shifts/open", new OpenShiftRequest(0)), HttpStatusCode.Conflict, ErrorCodes.ShiftAlreadyOpen);

        await StartAsync(pc1, 3600, maria);                                                                  // +5,000
        await PostAsync(maria, $"{B}/machines/{pc1}/session/add-time", new AdjustTimeRequest(1800));           // +2,500
        await StartAsync(pc2, 7200, maria);                                                                  // +10,000
        // The owner refunds one hour on Maria's sale. The refund comes out of the owner's pocket, not Maria's drawer.
        await PostAsync(Owner, $"{B}/machines/{pc2}/session/remove-time", new AdjustTimeRequest(3600, Reason: "overcharged"));

        var current = (await maria.GetFromJsonAsync<ShiftDto>($"{Api}{B}/shifts/current", TelloJson.Options))!;
        Assert.Equal(1_750_000, current.SalesCents);
        Assert.Equal(0, current.RefundsCents);
        Assert.Equal(3_750_000, current.ExpectedCashCents);
        Assert.Equal(2, current.SessionsStarted);
        Assert.Null(current.DifferenceCents);

        var closed = await ReadAsync<ShiftDto>(await PostAsync(maria, $"{B}/shifts/close", new CloseShiftRequest(3_700_000, "short 500")));
        Assert.Equal(3_750_000, closed.ExpectedCashCents);
        Assert.Equal(-50_000, closed.DifferenceCents);
        Assert.NotNull(closed.ClosedAtUtc);

        Assert.Equal(HttpStatusCode.NoContent, (await maria.GetAsync($"{Api}{B}/shifts/current")).StatusCode);
        await HttpAssert.ProblemAsync(await PostAsync(maria, $"{B}/shifts/close", new CloseShiftRequest(0)), HttpStatusCode.Conflict, ErrorCodes.NoOpenShift);

        var audit = await Factory.WithDbAsync(db => db.AuditLog.SingleAsync(a => a.Action == AuditActions.ShiftClosed));
        Assert.Contains("difference -50000c", audit.NewValue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refund_by_the_same_employee_reduces_their_expected_cash()
    {
        var pc1 = (await PairAsync("PC-01")).MachineId;
        await ReadAsync<ShiftDto>(await PostAsync(Owner, $"{B}/shifts/open", new OpenShiftRequest(0)));
        await StartAsync(pc1, 7200);
        await PostAsync(Owner, $"{B}/machines/{pc1}/session/cancel", new EndSessionRequest("customer changed mind", 1_000_000));

        var shift = (await Owner.GetFromJsonAsync<ShiftDto>($"{Api}{B}/shifts/current", TelloJson.Options))!;
        Assert.Equal((1_000_000, 1_000_000, 0), (shift.SalesCents, shift.RefundsCents, shift.ExpectedCashCents));
    }

    [Fact]
    public async Task Several_employees_can_be_on_duty_and_each_sees_only_their_own_shifts()
    {
        var maria = await EmployeeClientAsync("maria");
        var pedro = await EmployeeClientAsync("pedro");
        var pc1 = (await PairAsync("PC-01")).MachineId;
        await StartAsync(pc1, 3600, pedro);

        var state = await GetStateAsync(maria);
        Assert.Equal(["maria", "pedro"], state.OnDuty.Select(o => o.DisplayName).Order());

        var mine = (await maria.GetFromJsonAsync<List<ShiftDto>>($"{Api}{B}/shifts", TelloJson.Options))!;
        Assert.Equal("maria", Assert.Single(mine).UserDisplayName);
        var all = (await Owner.GetFromJsonAsync<List<ShiftDto>>($"{Api}{B}/shifts", TelloJson.Options))!;
        Assert.Equal(2, all.Count);
        Assert.Equal(500_000, all.Single(s => s.UserDisplayName == "pedro").SalesCents);

        // Maria cannot read or close Pedro's shift; the owner can close it for him.
        var pedroShift = all.Single(s => s.UserDisplayName == "pedro").Id;
        Assert.Equal(HttpStatusCode.NotFound, (await maria.GetAsync($"{Api}{B}/shifts/{pedroShift}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(maria, $"{B}/shifts/{pedroShift}/close", new CloseShiftRequest(0))).StatusCode);
        var closed = await ReadAsync<ShiftDto>(await PostAsync(Owner, $"{B}/shifts/{pedroShift}/close", new CloseShiftRequest(500_000)));
        Assert.Equal(0, closed.DifferenceCents);
        Assert.Equal("maria", Assert.Single((await GetStateAsync()).OnDuty).DisplayName);
    }

    [Fact]
    public async Task Sales_after_closing_do_not_change_a_closed_shift()
    {
        var pc1 = (await PairAsync("PC-01")).MachineId;
        await PostAsync(Owner, $"{B}/shifts/open", new OpenShiftRequest(0));
        await StartAsync(pc1, 3600);
        var closed = await ReadAsync<ShiftDto>(await PostAsync(Owner, $"{B}/shifts/close", new CloseShiftRequest(500_000)));

        await PostAsync(Owner, $"{B}/machines/{pc1}/session/add-time", new AdjustTimeRequest(3600));
        var again = (await Owner.GetFromJsonAsync<ShiftDto>($"{Api}{B}/shifts/{closed.Id}", TelloJson.Options))!;
        Assert.Equal((500_000, 500_000, 0L), (again.SalesCents, again.ExpectedCashCents, again.DifferenceCents));
    }

    [Fact]
    public async Task Shift_report_pdf_and_csv_can_be_exported()
    {
        var maria = await EmployeeClientAsync("maria");
        var shift = (await maria.GetFromJsonAsync<ShiftDto>($"{Api}{B}/shifts/current", TelloJson.Options))!;

        var pdf = await maria.GetAsync($"{Api}{B}/shifts/{shift.Id}/report.pdf?lang=es");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.Equal("%PDF", Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]));

        var from = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("o"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("o"));
        Assert.Equal(HttpStatusCode.Forbidden, (await maria.GetAsync($"{Api}{B}/shifts/export.csv?fromUtc={from}&toUtc={to}")).StatusCode);
        var csv = await (await Owner.GetAsync($"{Api}{B}/shifts/export.csv?fromUtc={from}&toUtc={to}&lang=es")).Content.ReadAsStringAsync();
        Assert.Contains("Efectivo esperado", csv, StringComparison.Ordinal);
        Assert.Contains("maria", csv, StringComparison.Ordinal);
    }
}

public sealed class EmployeeTests : BranchTestBase
{
    private async Task<EmployeeDto> CreateAsync(string username, UserRole role = UserRole.Employee) =>
        await ReadAsync<EmployeeDto>(await PostAsync(Owner, "/employees",
            new CreateEmployeeRequest(username, username.ToUpperInvariant(), username + "-password-1", role, [BranchId])));

    [Fact]
    public async Task Owner_registers_an_employee_who_can_then_log_in()
    {
        var created = await CreateAsync("maria");
        Assert.Equal((UserRole.Employee, true), (created.Role, created.IsActive));
        Assert.Equal([BranchId], created.BranchIds);

        var auth = await Factory.LoginAsync(Factory.CreateClient(), "maria", "maria-password-1");
        Assert.Equal("Main", Assert.Single(auth.User.Branches).Name);
        Assert.DoesNotContain(Permission.EmployeesManage, auth.User.Permissions);

        await HttpAssert.ProblemAsync(await PostAsync(Owner, "/employees", new CreateEmployeeRequest("MARIA", "Dup", "dup-password-1", UserRole.Employee, [])),
            HttpStatusCode.Conflict, ErrorCodes.Conflict);
        await HttpAssert.ProblemAsync(await PostAsync(Owner, "/employees", new CreateEmployeeRequest("x", "Bad", "short", UserRole.Employee, [])),
            HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.Equal(2, (await Owner.GetFromJsonAsync<List<EmployeeDto>>($"{Api}/employees", TelloJson.Options))!.Count);
    }

    [Fact]
    public async Task Employees_cannot_manage_employees()
    {
        var maria = await EmployeeClientAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await maria.GetAsync($"{Api}/employees")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(maria, "/employees",
            new CreateEmployeeRequest("evil", "Evil", "evil-password-1", UserRole.Owner, []))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await maria.GetAsync($"{Api}/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await maria.GetAsync($"{Api}/admin/backup")).StatusCode);
    }

    [Fact]
    public async Task Deactivating_signs_the_employee_out_and_reactivating_lets_them_back_in()
    {
        var created = await CreateAsync("maria");
        var client = Factory.CreateClient();
        var auth = await Factory.LoginAsync(client, "maria", "maria-password-1");
        TelloFactory.Authorize(client, auth.AccessToken);

        (await Owner.PutAsJsonAsync($"{Api}/employees/{created.Id}", new UpdateEmployeeRequest("Maria", UserRole.Employee, false, [BranchId]), TelloJson.Options)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Api}/auth/me")).StatusCode);
        Assert.False((await PostAsync(client, "/auth/refresh", new RefreshRequest(auth.RefreshToken))).IsSuccessStatusCode);

        (await Owner.PutAsJsonAsync($"{Api}/employees/{created.Id}", new UpdateEmployeeRequest("Maria", UserRole.Employee, true, [BranchId]), TelloJson.Options)).EnsureSuccessStatusCode();
        await Factory.LoginAsync(Factory.CreateClient(), "maria", "maria-password-1");
    }

    [Fact]
    public async Task Password_reset_replaces_the_password_and_clears_a_lockout()
    {
        var created = await CreateAsync("maria");
        for (var i = 0; i < 5; i++)
        {
            await PostAsync(Factory.CreateClient(), "/auth/login", new LoginRequest("maria", "wrong-password"));
        }

        Assert.True((await Owner.GetFromJsonAsync<List<EmployeeDto>>($"{Api}/employees", TelloJson.Options))!.Single(e => e.Id == created.Id).IsLockedOut);
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(Owner, $"/employees/{created.Id}/reset-password", new ResetPasswordRequest("new-password-22"))).StatusCode);

        await Factory.LoginAsync(Factory.CreateClient(), "maria", "new-password-22");
        Assert.False((await PostAsync(Factory.CreateClient(), "/auth/login", new LoginRequest("maria", "maria-password-1"))).IsSuccessStatusCode);
    }

    [Fact]
    public async Task The_last_owner_cannot_be_removed()
    {
        var me = OwnerAuth.User.Id;
        await HttpAssert.ProblemAsync(await Owner.PutAsJsonAsync($"{Api}/employees/{me}", new UpdateEmployeeRequest("Me", UserRole.Employee, true, []), TelloJson.Options),
            HttpStatusCode.Conflict, ErrorCodes.LastOwner);
        await HttpAssert.ProblemAsync(await Owner.PutAsJsonAsync($"{Api}/employees/{me}", new UpdateEmployeeRequest("Me", UserRole.Owner, false, []), TelloJson.Options),
            HttpStatusCode.Conflict, ErrorCodes.LastOwner);

        // With a second owner, that one can be demoted again.
        var second = await CreateAsync("partner", UserRole.Owner);
        (await Owner.PutAsJsonAsync($"{Api}/employees/{second.Id}", new UpdateEmployeeRequest("Partner", UserRole.Employee, true, [BranchId]), TelloJson.Options)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Owner_can_let_employees_remove_time_and_the_change_applies_at_once()
    {
        var pc1 = (await PairAsync("PC-01")).MachineId;
        var maria = await EmployeeClientAsync();
        await StartAsync(pc1, 7200, maria);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(maria, $"{B}/machines/{pc1}/session/remove-time", new AdjustTimeRequest(600, Reason: "x"))).StatusCode);

        var settings = await ReadAsync<List<PermissionSetting>>(await Owner.PutAsJsonAsync($"{Api}/settings/employee-permissions",
            new UpdateEmployeePermissionsRequest(new Dictionary<Permission, bool> { [Permission.SessionsRemoveTime] = true, [Permission.RemoteMessage] = false }), TelloJson.Options));
        Assert.True(settings.Single(s => s.Permission == Permission.SessionsRemoveTime).Allowed);
        Assert.False(settings.Single(s => s.Permission == Permission.SessionsRemoveTime).DefaultAllowed);

        (await PostAsync(maria, $"{B}/machines/{pc1}/session/remove-time", new AdjustTimeRequest(600, Reason: "x"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(maria, $"{B}/machines/{pc1}/message", new SendMessageRequest("hi"))).StatusCode);

        // Owner-only permissions can never be handed out.
        await HttpAssert.ProblemAsync(await Owner.PutAsJsonAsync($"{Api}/settings/employee-permissions",
            new UpdateEmployeePermissionsRequest(new Dictionary<Permission, bool> { [Permission.SessionsFreeTime] = true }), TelloJson.Options),
            HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.Equal(1, await Factory.WithDbAsync(db => db.AuditLog.CountAsync(a => a.Action == AuditActions.PermissionsChanged)));
    }
}

public sealed class ReportAndSettingsTests : BranchTestBase
{
    private static string Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5)).ToString("yyyy-MM-dd");

    private async Task<ReportDto> ReportAsync() =>
        (await Owner.GetFromJsonAsync<ReportDto>($"{Api}{B}/reports/summary?from={Today}&to={Today}", TelloJson.Options))!;

    [Fact]
    public async Task Report_totals_match_the_ledger()
    {
        var pc1 = (await PairAsync("PC-01")).MachineId;
        var pc2 = (await PairAsync("PC-02")).MachineId;
        var maria = await EmployeeClientAsync("maria");
        await StartAsync(pc1, 3600, maria);                                                           // maria +5,000 on PC-01
        await StartAsync(pc2, 7200);                                                                  // owner +10,000 on PC-02
        await PostAsync(Owner, $"{B}/machines/{pc2}/session/remove-time", new AdjustTimeRequest(3600, Reason: "fix")); // owner -5,000
        await PostAsync(Owner, $"{B}/machines/{pc1}/session/stop", new EndSessionRequest());
        await PostAsync(Owner, $"{B}/machines/{pc1}/session/start", new StartSessionRequest(1800, IsFree: true, Reason: "promo", AllowOffline: true));

        var r = await ReportAsync();
        Assert.Equal((1_000_000, 1_500_000, 500_000), (r.IncomeCents, r.SalesCents, r.RefundsCents));
        Assert.Equal((3, 1), (r.Sessions, r.FreeSessions));
        Assert.Equal(3600 + 7200 - 3600 + 1800, r.SecondsSold);
        Assert.Equal(r.IncomeCents, r.ByDay.Sum(d => d.IncomeCents));
        Assert.Equal(r.IncomeCents, r.ByEmployee.Sum(e => e.IncomeCents));
        Assert.Equal(r.IncomeCents, r.ByMachine.Sum(m => m.IncomeCents));
        Assert.Equal(r.IncomeCents, r.ByHour.Sum(h => h.IncomeCents));
        Assert.Equal(500_000, r.ByEmployee.Single(e => e.Name == "maria").IncomeCents);
        Assert.Equal(500_000, r.ByMachine.Single(m => m.Name == "PC-02").IncomeCents);
        Assert.Equal(24, r.ByHour.Count);
        Assert.Equal(await Factory.WithDbAsync(db => db.SessionEvents.SumAsync(e => e.AmountDeltaCents)), r.IncomeCents);
    }

    [Fact]
    public async Task Report_exports_as_csv_and_pdf_and_bad_ranges_are_rejected()
    {
        var pc1 = (await PairAsync("PC-01")).MachineId;
        await StartAsync(pc1, 3600);

        var csv = await (await Owner.GetAsync($"{Api}{B}/reports/summary.csv?from={Today}&to={Today}&lang=es")).Content.ReadAsStringAsync();
        Assert.Contains("Informe de ingresos", csv, StringComparison.Ordinal);
        Assert.Contains("Ingresos,5000", csv, StringComparison.Ordinal);
        Assert.Contains("PC-01,5000,1,1", csv, StringComparison.Ordinal);

        var pdf = await Owner.GetAsync($"{Api}{B}/reports/summary.pdf?from={Today}&to={Today}");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        var bytes = await pdf.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes[..4]));
        Assert.True(bytes.Length > 1000);

        await HttpAssert.ProblemAsync(await Owner.GetAsync($"{Api}{B}/reports/summary?from=2026-02-01&to=2026-01-01"), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await HttpAssert.ProblemAsync(await Owner.GetAsync($"{Api}{B}/reports/summary?from=2020-01-01&to=2026-01-01"), HttpStatusCode.BadRequest, ErrorCodes.Validation);

        var maria = await EmployeeClientAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await maria.GetAsync($"{Api}{B}/reports/summary?from={Today}&to={Today}")).StatusCode);
        Assert.Equal(2, await Factory.WithDbAsync(db => db.AuditLog.CountAsync(a => a.Action == AuditActions.ReportExported)));
    }

    [Fact]
    public async Task Audit_log_can_be_filtered_searched_paged_and_exported()
    {
        var pc1 = (await PairAsync("PC-01")).MachineId;
        await StartAsync(pc1, 3600);
        await PostAsync(Owner, $"{B}/machines/{pc1}/session/remove-time", new AdjustTimeRequest(600, Reason: "wrong button"));

        var sessions = (await Owner.GetFromJsonAsync<PagedResult<AuditEntryDto>>($"{Api}/audit?action=session.", TelloJson.Options))!;
        Assert.Equal(2, sessions.Total);
        Assert.Equal(AuditActions.SessionTimeRemoved, sessions.Items[0].Action); // newest first

        var search = (await Owner.GetFromJsonAsync<PagedResult<AuditEntryDto>>($"{Api}/audit?search=wrong%20button", TelloJson.Options))!;
        Assert.Equal("wrong button", Assert.Single(search.Items).Reason);

        var byMachine = (await Owner.GetFromJsonAsync<PagedResult<AuditEntryDto>>($"{Api}/audit?machineId={pc1}&pageSize=1&page=2", TelloJson.Options))!;
        Assert.Single(byMachine.Items);
        Assert.True(byMachine.Total >= 3);

        var future = Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("o"));
        Assert.Equal(0, (await Owner.GetFromJsonAsync<PagedResult<AuditEntryDto>>($"{Api}/audit?fromUtc={future}", TelloJson.Options))!.Total);

        var csv = await (await Owner.GetAsync($"{Api}/audit/export.csv?action=session.")).Content.ReadAsStringAsync();
        Assert.Contains("session.time_removed,owner,PC-01", csv, StringComparison.Ordinal);
        Assert.Equal(1, await Factory.WithDbAsync(db => db.AuditLog.CountAsync(a => a.Action == AuditActions.AuditExported)));
    }

    [Fact]
    public async Task Branch_settings_update_pricing_and_the_price_change_is_audited()
    {
        var update = new UpdateBranchSettingsRequest("Main", true, "America/Bogota", 600_000, "COP", "$", 0, RoundingMode.Up, 50_000, 600, "4321");
        var saved = await ReadAsync<BranchSettingsDto>(await Owner.PutAsJsonAsync($"{Api}/branches/{BranchId}/settings", update, TelloJson.Options));
        Assert.Equal((600_000, RoundingMode.Up, 600, true), (saved.HourlyRateCents, saved.RoundingMode, saved.WarningThresholdSeconds, saved.HasAdminPin));

        var state = await GetStateAsync();
        Assert.Equal(600_000, state.Pricing.HourlyRateCents);

        var price = await Factory.WithDbAsync(db => db.AuditLog.SingleAsync(a => a.Action == AuditActions.PriceChanged));
        Assert.Equal(("500000c/h", "600000c/h"), (price.OldValue, price.NewValue));

        // The PIN is stored hashed and never echoed back.
        var hash = await Factory.WithDbAsync(db => db.Branches.Select(b => b.AdminPinHash).SingleAsync());
        Assert.True(PinHasher.Verify("4321", hash));
        Assert.DoesNotContain("4321", await (await Owner.GetAsync($"{Api}/branches")).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await HttpAssert.ProblemAsync(await Owner.PutAsJsonAsync($"{Api}/branches/{BranchId}/settings",
            update with { HourlyRateCents = -1, TimeZoneId = "Mars/Olympus", NewAdminPin = "12" }, TelloJson.Options), HttpStatusCode.BadRequest, ErrorCodes.Validation);

        var maria = await EmployeeClientAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await maria.PutAsJsonAsync($"{Api}/branches/{BranchId}/settings", update, TelloJson.Options)).StatusCode);
    }

    [Fact]
    public async Task Shop_logo_can_be_uploaded_served_and_removed()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        Assert.Equal(HttpStatusCode.NotFound, (await Factory.CreateClient().GetAsync($"{Api}/settings/shop/logo")).StatusCode);

        var bad = await Owner.PutAsync($"{Api}/settings/shop/logo", new ByteArrayContent(Encoding.ASCII.GetBytes("<svg onload=alert(1)>")));
        await HttpAssert.ProblemAsync(bad, HttpStatusCode.BadRequest, ErrorCodes.Validation);

        var saved = await ReadAsync<ShopSettingsDto>(await Owner.PutAsync($"{Api}/settings/shop/logo", new ByteArrayContent(png)));
        Assert.NotNull(saved.LogoVersion);
        var served = await Factory.CreateClient().GetAsync($"{Api}/settings/shop/logo");
        Assert.Equal("image/png", served.Content.Headers.ContentType!.MediaType);
        Assert.Equal(png, await served.Content.ReadAsByteArrayAsync());

        Assert.Null((await ReadAsync<ShopSettingsDto>(await Owner.DeleteAsync($"{Api}/settings/shop/logo"))).LogoVersion);
    }

    [Fact]
    public async Task Machines_can_be_renamed_disabled_repaired_and_deleted_when_unused()
    {
        var pc1 = await PairAsync("PC-01");
        var pc2 = await PairAsync("PC-02");
        await StartAsync(pc1.MachineId, 3600);

        (await Owner.PutAsJsonAsync($"{Api}{B}/machines/{pc2.MachineId}", new UpdateMachineRequest("VIP-01", null, true), TelloJson.Options)).EnsureSuccessStatusCode();
        await HttpAssert.ProblemAsync(await Owner.PutAsJsonAsync($"{Api}{B}/machines/{pc2.MachineId}", new UpdateMachineRequest("PC-01", null, true), TelloJson.Options),
            HttpStatusCode.Conflict, ErrorCodes.Conflict);

        // History protects PC-01 from deletion; the unused machine can go.
        await HttpAssert.ProblemAsync(await Owner.DeleteAsync($"{Api}{B}/machines/{pc1.MachineId}"), HttpStatusCode.Conflict, ErrorCodes.Conflict);
        Assert.Equal(HttpStatusCode.NoContent, (await Owner.DeleteAsync($"{Api}{B}/machines/{pc2.MachineId}")).StatusCode);

        // Repair: same machine id, new secret, old secret dead.
        var code = await ReadAsync<PairingCodeResponse>(await PostAsync(Owner, $"{B}/machines/{pc1.MachineId}/repair-code", new { }));
        var repaired = await ReadAsync<PairMachineResponse>(await PostAsync(Factory.CreateClient(), "/machines/pair", new PairMachineRequest(code.Code)));
        Assert.Equal(pc1.MachineId, repaired.MachineId);
        Assert.NotEqual(pc1.Token, repaired.Token);

        HttpClient With(string token)
        {
            var c = Factory.CreateClient();
            c.DefaultRequestHeaders.Authorization = new(ProtocolInfo.MachineAuthScheme, token);
            return c;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await With(pc1.Token).GetAsync($"{Api}/machines/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await With(repaired.Token).GetAsync($"{Api}/machines/me")).StatusCode);
        Assert.Single((await GetStateAsync()).Machines);
    }

    [Fact]
    public async Task Backup_downloads_a_database_that_restore_accepts_and_garbage_is_refused()
    {
        var backup = await Owner.GetAsync($"{Api}/admin/backup");
        backup.EnsureSuccessStatusCode();
        var bytes = await backup.Content.ReadAsByteArrayAsync();
        Assert.Equal("SQLite format 3", Encoding.ASCII.GetString(bytes[..15]));

        var garbage = await Owner.PostAsync($"{Api}/admin/restore", new ByteArrayContent(Encoding.ASCII.GetBytes("definitely not a database")));
        await HttpAssert.ProblemAsync(garbage, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.False(File.Exists(Path.Combine(Factory.DataDirectory, "restore-pending.db")));

        var accepted = await Owner.PostAsync($"{Api}/admin/restore", new ByteArrayContent(bytes));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.True(File.Exists(Path.Combine(Factory.DataDirectory, "restore-pending.db")));
    }

    [Fact]
    public async Task A_staged_restore_replaces_the_database_on_the_next_start()
    {
        // Take a backup, then change the data, then restore: the change must be gone.
        var bytes = await (await Owner.GetAsync($"{Api}/admin/backup")).Content.ReadAsByteArrayAsync();
        await PairAsync("PC-AFTER-BACKUP");
        (await Owner.PostAsync($"{Api}/admin/restore", new ByteArrayContent(bytes))).EnsureSuccessStatusCode();

        var directory = Factory.DetachDataDirectory();
        Factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var restarted = new TelloFactory(directory, null);
        Assert.Equal(0, await restarted.WithDbAsync(db => db.Machines.CountAsync()));
        Assert.Equal(1, await restarted.WithDbAsync(db => db.Users.CountAsync()));
        Assert.Single(Directory.GetFiles(Path.Combine(directory, "backups"), "pre-restore-*.db"));
        restarted.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }
}
