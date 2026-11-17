using System.Net.Http;
using Serilog;
using TelloGameRoom.Core.Client;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Security;

namespace TelloGameRoom.Dashboard.Services;

/// <summary>
/// The signed-in staff member and the authenticated API client. Access tokens are refreshed shortly before they
/// expire and again on a 401; tokens live only in memory, so closing the Dashboard signs the user out.
/// </summary>
public sealed class Session(AppSettings settings)
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private AuthResponse? _auth;

    public AppSettings Settings { get; } = settings;

    public ApiClient Api { get; private set; } = new(new HttpClient());

    public Uri? Server { get; private set; }

    public UserInfo? User => _auth?.User;

    public bool IsOwner => User?.Role == UserRole.Owner;

    /// <summary>Raised (on any thread) when the session can no longer be refreshed and the user must sign in again.</summary>
    public event Action? Expired;

    public bool Can(Permission permission) => User?.Permissions.Contains(permission) == true;

    /// <summary>Points the client at a server. Returns false when the address is not usable.</summary>
    public bool Configure(string? serverUrl, string[] pins)
    {
        var uri = TelloHttp.NormalizeServerUrl(serverUrl);
        if (uri is null)
        {
            return false;
        }

        Server = uri;
        _auth = null;
        Api = new ApiClient(new HttpClient(TelloHttp.CreateHandler(pins)) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(60) })
        {
            AuthorizationProvider = async ct => await GetAccessTokenAsync(ct) is { } token ? "Bearer " + token : null,
            UnauthorizedHandler = async ct => _auth is not null && await RefreshAsync(force: true, ct),
        };
        return true;
    }

    public Task<SetupStatusResponse> GetSetupStatusAsync(CancellationToken ct = default) => Api.GetAsync<SetupStatusResponse>("/setup/status", ct);

    public async Task SetupAsync(SetupRequest request) => _auth = await Api.PostAsync<AuthResponse>("/setup", request);

    public async Task LoginAsync(string username, string password)
    {
        _auth = await Api.PostAsync<AuthResponse>("/auth/login", new LoginRequest(username, password, Environment.MachineName));
        Settings.LastUsername = username;
        Settings.Save();
    }

    public async Task LogoutAsync()
    {
        var auth = _auth;
        _auth = null;
        if (auth is not null)
        {
            try
            {
                await Api.PostAsync("/auth/logout", new LogoutRequest(auth.RefreshToken));
            }
            catch (ApiException ex)
            {
                Log.Information("Logout request failed: {Message}", ex.Message);
            }
        }
    }

    /// <summary>Re-reads the user (permissions and branches may have been changed by the owner).</summary>
    public async Task ReloadUserAsync()
    {
        if (_auth is not null)
        {
            _auth = _auth with { User = await Api.GetAsync<UserInfo>("/auth/me") };
        }
    }

    /// <summary>A valid access token, refreshed if needed; null when nobody is signed in.</summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_auth is null)
        {
            return null;
        }

        if (_auth.AccessTokenExpiresAtUtc - DateTime.UtcNow < RefreshMargin)
        {
            await RefreshAsync(force: false, ct);
        }

        return _auth?.AccessToken;
    }

    private async Task<bool> RefreshAsync(bool force, CancellationToken ct)
    {
        var before = _auth;
        await _refreshLock.WaitAsync(ct);
        try
        {
            if (_auth is null)
            {
                return false;
            }

            // Another caller refreshed while we waited for the lock.
            if (!ReferenceEquals(before, _auth) || (!force && _auth.AccessTokenExpiresAtUtc - DateTime.UtcNow >= RefreshMargin))
            {
                return true;
            }

            // A bare client call: going through Api would recurse into this method.
            using var http = new HttpClient(TelloHttp.CreateHandler(Settings.PinnedCertificates)) { BaseAddress = Server, Timeout = TimeSpan.FromSeconds(20) };
            _auth = await new ApiClient(http).PostAsync<AuthResponse>("/auth/refresh", new RefreshRequest(_auth.RefreshToken), ct);
            return true;
        }
        catch (ApiException ex) when (!ex.IsNetwork)
        {
            Log.Information("Session ended: {Code}", ex.Code);
            _auth = null;
            Expired?.Invoke();
            return false;
        }
        catch (ApiException)
        {
            // Network trouble: keep the session; the caller's request fails and can be retried.
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}
