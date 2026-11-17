using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Server.Data;
using TelloGameRoom.Server.Data.Entities;
using TelloGameRoom.Server.Security;
using TelloGameRoom.Server.Services;

namespace TelloGameRoom.Tests.Integration;

/// <summary>A clock tests can move. Starts at the real time so JWT validation and file timestamps stay sane.</summary>
public sealed class MutableTimeProvider : TimeProvider
{
    private TimeSpan _offset;

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + _offset;

    public void Advance(TimeSpan by) => _offset += by;
}

/// <summary>Hosts the real server in-process against a throwaway SQLite database.</summary>
public sealed class TelloFactory : WebApplicationFactory<Program>
{
    public const string OwnerUsername = "owner";
    public const string OwnerPassword = "owner-password-1";

    private readonly string _dataDirectory;
    private bool _ownsDirectory;

    public TelloFactory()
        : this(null, null)
    {
    }

    /// <summary>Starts a server on an existing data directory, as a restarted server process would.</summary>
    public TelloFactory(string? dataDirectory, MutableTimeProvider? clock)
    {
        _ownsDirectory = dataDirectory is null;
        _dataDirectory = dataDirectory ?? Path.Combine(Path.GetTempPath(), "tello-tests", Guid.NewGuid().ToString("N"));
        Clock = clock ?? new MutableTimeProvider();
    }

    public string DataDirectory => _dataDirectory;

    /// <summary>Access token lifetime. Tests that move the clock far ahead raise it so they are not signed out mid-test.</summary>
    public int AccessTokenMinutes { get; init; } = 15;

    /// <summary>Keeps the data directory on dispose so another factory can "restart" on it.</summary>
    public string DetachDataDirectory()
    {
        _ownsDirectory = false;
        return _dataDirectory;
    }

    public MutableTimeProvider Clock { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Storage:DataDirectory", _dataDirectory);
        builder.UseSetting("Security:Argon2MemoryKb", "1024");
        builder.UseSetting("Security:Argon2Iterations", "1");
        builder.UseSetting("Security:AuthRateLimitPerMinute", "100000");
        builder.UseSetting("Jwt:AccessTokenMinutes", AccessTokenMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _ownsDirectory)
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: a log file may still be held for a moment.
            }
        }
    }

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public Task<HttpResponseMessage> PostAsync<T>(HttpClient client, string url, T body) =>
        client.PostAsJsonAsync(ProtocolInfo.ApiPrefix + url, body, TelloJson.Options);

    public async Task<AuthResponse> SetupAsync(HttpClient client)
    {
        var response = await PostAsync(client, "/setup",
            new SetupRequest("Tello Test Shop", "Main", OwnerUsername, "The Owner", OwnerPassword, 5000m));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>(TelloJson.Options))!;
    }

    public async Task<AuthResponse> LoginAsync(HttpClient client, string username, string password)
    {
        var response = await PostAsync(client, "/auth/login", new LoginRequest(username, password, "tests"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>(TelloJson.Options))!;
    }

    /// <summary>Creates an employee directly in the database (the employee API arrives in Phase 6).</summary>
    public async Task<User> CreateEmployeeAsync(string username, string password, params Guid[] branchIds)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Username = username,
            NormalizedUsername = AuthService.Normalize(username),
            DisplayName = username,
            PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(password),
            Role = UserRole.Employee,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.Users.Add(user);
        foreach (var branchId in branchIds)
        {
            db.UserBranches.Add(new UserBranch { UserId = user.Id, BranchId = branchId });
        }

        await db.SaveChangesAsync();
        return user;
    }

    public static HttpClient Authorize(HttpClient client, string accessToken)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }
}

public static class HttpAssert
{
    /// <summary>Asserts the status code and the machine-readable problem code.</summary>
    public static async Task ProblemAsync(HttpResponseMessage response, System.Net.HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
    }
}
