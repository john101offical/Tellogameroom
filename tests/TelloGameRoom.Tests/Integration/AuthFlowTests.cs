using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Core.Security;

namespace TelloGameRoom.Tests.Integration;

public sealed class AuthFlowTests : IDisposable
{
    private const string Api = ProtocolInfo.ApiPrefix;
    private readonly TelloFactory _factory = new();
    private readonly HttpClient _client;

    public AuthFlowTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Fresh_server_reports_setup_required_and_health_is_ok()
    {
        var status = await _client.GetFromJsonAsync<SetupStatusResponse>($"{Api}/setup/status", TelloJson.Options);
        Assert.True(status!.SetupRequired);
        Assert.Equal(ProtocolInfo.Version, status.ProtocolVersion);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Setup_creates_the_owner_once_and_only_once()
    {
        var auth = await _factory.SetupAsync(_client);
        Assert.Equal(UserRole.Owner, auth.User.Role);
        Assert.Equal("Main", Assert.Single(auth.User.Branches).Name);
        Assert.Equal(Enum.GetValues<Permission>().Length, auth.User.Permissions.Count);

        var status = await _client.GetFromJsonAsync<SetupStatusResponse>($"{Api}/setup/status", TelloJson.Options);
        Assert.False(status!.SetupRequired);

        var again = await _factory.PostAsync(_client, "/setup", new SetupRequest("Evil", "Evil", "intruder", "Intruder", "intruder-password"));
        await HttpAssert.ProblemAsync(again, HttpStatusCode.Conflict, ErrorCodes.SetupAlreadyCompleted);

        Assert.Equal(500_000, await _factory.WithDbAsync(db => db.Branches.Select(b => b.HourlyRateCents).SingleAsync()));
        var stored = await _factory.WithDbAsync(db => db.Users.Select(u => u.PasswordHash).SingleAsync());
        Assert.StartsWith("$argon2id$", stored, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "Main", "owner", "short-but-ok-pw")]
    [InlineData("Shop", "Main", "ow", "long-enough-password")]
    [InlineData("Shop", "Main", "bad name!", "long-enough-password")]
    [InlineData("Shop", "Main", "owner", "short")]
    public async Task Setup_validates_input(string shop, string branch, string username, string password)
    {
        var response = await _factory.PostAsync(_client, "/setup", new SetupRequest(shop, branch, username, "Owner", password));
        await HttpAssert.ProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.False(await _factory.WithDbAsync(db => db.Users.AnyAsync()));
    }

    [Fact]
    public async Task Login_works_case_insensitively_and_me_returns_the_user()
    {
        await _factory.SetupAsync(_client);
        var auth = await _factory.LoginAsync(_client, "OWNER", TelloFactory.OwnerPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync($"{Api}/auth/me")).StatusCode);

        TelloFactory.Authorize(_client, auth.AccessToken);
        var me = await _client.GetFromJsonAsync<UserInfo>($"{Api}/auth/me", TelloJson.Options);
        Assert.Equal("owner", me!.Username);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_look_the_same()
    {
        await _factory.SetupAsync(_client);
        var wrong = await _factory.PostAsync(_client, "/auth/login", new LoginRequest("owner", "not-the-password"));
        var unknown = await _factory.PostAsync(_client, "/auth/login", new LoginRequest("nobody", "not-the-password"));
        await HttpAssert.ProblemAsync(wrong, HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        await HttpAssert.ProblemAsync(unknown, HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);

        var failures = await _factory.WithDbAsync(db => db.AuditLog.CountAsync(a => a.Action == AuditActions.LoginFailed));
        Assert.Equal(2, failures);
    }

    [Fact]
    public async Task Account_locks_after_five_failures_and_unlocks_after_the_lockout_period()
    {
        await _factory.SetupAsync(_client);
        for (var i = 0; i < 4; i++)
        {
            var attempt = await _factory.PostAsync(_client, "/auth/login", new LoginRequest("owner", "wrong-password"));
            await HttpAssert.ProblemAsync(attempt, HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        }

        var fifth = await _factory.PostAsync(_client, "/auth/login", new LoginRequest("owner", "wrong-password"));
        await HttpAssert.ProblemAsync(fifth, HttpStatusCode.Locked, ErrorCodes.AccountLocked);

        // Even the right password is refused while locked.
        var locked = await _factory.PostAsync(_client, "/auth/login", new LoginRequest("owner", TelloFactory.OwnerPassword));
        await HttpAssert.ProblemAsync(locked, HttpStatusCode.Locked, ErrorCodes.AccountLocked);
        Assert.Equal(1, await _factory.WithDbAsync(db => db.AuditLog.CountAsync(a => a.Action == AuditActions.AccountLockedOut)));

        _factory.Clock.Advance(TimeSpan.FromMinutes(16));
        await _factory.LoginAsync(_client, "owner", TelloFactory.OwnerPassword);
    }

    [Fact]
    public async Task Successful_login_resets_the_failure_counter()
    {
        await _factory.SetupAsync(_client);
        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < 4; i++)
            {
                await _factory.PostAsync(_client, "/auth/login", new LoginRequest("owner", "wrong-password"));
            }

            await _factory.LoginAsync(_client, "owner", TelloFactory.OwnerPassword);
        }
    }

    [Fact]
    public async Task Refresh_rotates_tokens_and_reuse_revokes_the_whole_family()
    {
        var first = await _factory.SetupAsync(_client);

        var refreshed = await _factory.PostAsync(_client, "/auth/refresh", new RefreshRequest(first.RefreshToken));
        var second = (await refreshed.Content.ReadFromJsonAsync<AuthResponse>(TelloJson.Options))!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        // Replaying the old token is treated as theft...
        var replay = await _factory.PostAsync(_client, "/auth/refresh", new RefreshRequest(first.RefreshToken));
        await HttpAssert.ProblemAsync(replay, HttpStatusCode.Unauthorized, ErrorCodes.InvalidRefreshToken);
        Assert.Equal(1, await _factory.WithDbAsync(db => db.AuditLog.CountAsync(a => a.Action == AuditActions.RefreshTokenReuse)));

        // ...and kills the newest token too.
        var newest = await _factory.PostAsync(_client, "/auth/refresh", new RefreshRequest(second.RefreshToken));
        await HttpAssert.ProblemAsync(newest, HttpStatusCode.Unauthorized, ErrorCodes.InvalidRefreshToken);
    }

    [Fact]
    public async Task Refresh_tokens_are_stored_hashed_expire_and_die_on_logout()
    {
        var auth = await _factory.SetupAsync(_client);
        Assert.False(await _factory.WithDbAsync(db => db.RefreshTokens.AnyAsync(t => t.TokenHash == auth.RefreshToken)));

        var garbage = await _factory.PostAsync(_client, "/auth/refresh", new RefreshRequest("not-a-token"));
        await HttpAssert.ProblemAsync(garbage, HttpStatusCode.Unauthorized, ErrorCodes.InvalidRefreshToken);

        var login = await _factory.LoginAsync(_client, "owner", TelloFactory.OwnerPassword);
        Assert.Equal(HttpStatusCode.NoContent, (await _factory.PostAsync(_client, "/auth/logout", new LogoutRequest(login.RefreshToken))).StatusCode);
        var afterLogout = await _factory.PostAsync(_client, "/auth/refresh", new RefreshRequest(login.RefreshToken));
        await HttpAssert.ProblemAsync(afterLogout, HttpStatusCode.Unauthorized, ErrorCodes.InvalidRefreshToken);

        _factory.Clock.Advance(TimeSpan.FromDays(31));
        var expired = await _factory.PostAsync(_client, "/auth/refresh", new RefreshRequest(auth.RefreshToken));
        await HttpAssert.ProblemAsync(expired, HttpStatusCode.Unauthorized, ErrorCodes.InvalidRefreshToken);
    }

    [Fact]
    public async Task Access_token_expires_after_fifteen_minutes()
    {
        var auth = await _factory.SetupAsync(_client);
        TelloFactory.Authorize(_client, auth.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"{Api}/auth/me")).StatusCode);

        _factory.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync($"{Api}/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Tampered_access_token_is_rejected()
    {
        var auth = await _factory.SetupAsync(_client);
        var parts = auth.AccessToken.Split('.');
        var forged = $"{parts[0]}.{parts[1]}.{new string('A', parts[2].Length)}";
        TelloFactory.Authorize(_client, forged);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync($"{Api}/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Deactivating_a_user_takes_effect_immediately()
    {
        var owner = await _factory.SetupAsync(_client);
        var employee = await _factory.CreateEmployeeAsync("maria", "maria-password-1", owner.User.Branches[0].Id);
        var auth = await _factory.LoginAsync(_client, "maria", "maria-password-1");
        TelloFactory.Authorize(_client, auth.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"{Api}/auth/me")).StatusCode);

        await _factory.WithDbAsync(db => db.Users.Where(u => u.Id == employee.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false)));

        // The still-valid access token stops working, and so do refresh and login.
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync($"{Api}/auth/me")).StatusCode);
        await HttpAssert.ProblemAsync(await _factory.PostAsync(_client, "/auth/refresh", new RefreshRequest(auth.RefreshToken)),
            HttpStatusCode.Forbidden, ErrorCodes.AccountDisabled);
        await HttpAssert.ProblemAsync(await _factory.PostAsync(_client, "/auth/login", new LoginRequest("maria", "maria-password-1")),
            HttpStatusCode.Forbidden, ErrorCodes.AccountDisabled);
    }

    [Fact]
    public async Task Changing_the_password_signs_out_other_devices()
    {
        var auth = await _factory.SetupAsync(_client);
        TelloFactory.Authorize(_client, auth.AccessToken);

        var wrong = await _factory.PostAsync(_client, "/auth/change-password", new ChangePasswordRequest("nope-nope-nope", "brand-new-password"));
        await HttpAssert.ProblemAsync(wrong, HttpStatusCode.BadRequest, ErrorCodes.InvalidCredentials);
        var weak = await _factory.PostAsync(_client, "/auth/change-password", new ChangePasswordRequest(TelloFactory.OwnerPassword, "short"));
        await HttpAssert.ProblemAsync(weak, HttpStatusCode.BadRequest, ErrorCodes.Validation);

        var ok = await _factory.PostAsync(_client, "/auth/change-password", new ChangePasswordRequest(TelloFactory.OwnerPassword, "brand-new-password"));
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        await HttpAssert.ProblemAsync(await _factory.PostAsync(_client, "/auth/refresh", new RefreshRequest(auth.RefreshToken)),
            HttpStatusCode.Unauthorized, ErrorCodes.InvalidRefreshToken);
        await _factory.LoginAsync(_client, "owner", "brand-new-password");
    }

    [Fact]
    public async Task Client_with_a_different_protocol_version_is_told_to_upgrade()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}/setup/status");
        request.Headers.Add(ProtocolInfo.VersionHeader, "999");
        await HttpAssert.ProblemAsync(await _client.SendAsync(request), HttpStatusCode.UpgradeRequired, ErrorCodes.ProtocolMismatch);
    }
}
