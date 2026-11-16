using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Server.Data;
using TelloGameRoom.Server.Endpoints;
using TelloGameRoom.Server.Infrastructure;
using TelloGameRoom.Server.Security;
using TelloGameRoom.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// ---- Options -------------------------------------------------------------------------------------------------
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.Section));
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.Section));

var storage = builder.Configuration.GetSection(StorageOptions.Section).Get<StorageOptions>() ?? new StorageOptions();
var dataDirectory = Path.GetFullPath(storage.DataDirectory);
Directory.CreateDirectory(dataDirectory);
builder.Services.PostConfigure<StorageOptions>(o => o.DataDirectory = dataDirectory);

// ---- Logging -------------------------------------------------------------------------------------------------
builder.Services.AddSerilog((_, config) => config
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture)
    .WriteTo.File(
        Path.Combine(dataDirectory, "logs", "server-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        formatProvider: System.Globalization.CultureInfo.InvariantCulture));

// ---- Database ------------------------------------------------------------------------------------------------
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? $"Data Source={Path.Combine(dataDirectory, "tellogameroom.db")}";
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(connectionString));

// ---- Application services ------------------------------------------------------------------------------------
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
builder.Services.AddSingleton<JwtKeyProvider>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<PermissionService>();
builder.Services.AddScoped<CurrentUserAccessor>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<SetupService>();
builder.Services.AddScoped<PairingService>();

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();

// ---- Authentication and authorization ------------------------------------------------------------------------
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer()
    .AddScheme<AuthenticationSchemeOptions, MachineAuthenticationHandler>(MachineAuthenticationHandler.SchemeName, null);

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<JwtKeyProvider, TimeProvider>((options, keys, time) =>
    {
        options.MapInboundClaims = false;
        options.TimeProvider = time;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = JwtOptions.Issuer,
            ValidAudience = JwtOptions.Audience,
            IssuerSigningKey = keys.Key,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            // Validate lifetimes against the injected clock, the same one that issued the token.
            LifetimeValidator = (notBefore, expires, _, parameters) =>
            {
                var now = time.GetUtcNow().UtcDateTime;
                return expires is not null
                    && now < expires.Value + parameters.ClockSkew
                    && (notBefore is null || now >= notBefore.Value - parameters.ClockSkew);
            },
            NameClaimType = TelloClaims.Username,
            RoleClaimType = TelloClaims.Role,
        };
        options.Events = new JwtBearerEvents
        {
            // Browsers and some WebSocket stacks cannot send headers, so SignalR passes the token in the query string.
            OnMessageReceived = context =>
            {
                if (context.HttpContext.Request.Path.StartsWithSegments(ProtocolInfo.DashboardHubPath)
                    && context.Request.Query.TryGetValue("access_token", out var token))
                {
                    context.Token = token;
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddScoped<IAuthorizationHandler, PermissionHandler>();
builder.Services.AddAuthorization(o => o.AddTelloPolicies());
builder.Services.AddTelloRateLimiting();

// ---- Reverse proxy -------------------------------------------------------------------------------------------
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    var security = builder.Configuration.GetSection(SecurityOptions.Section).Get<SecurityOptions>() ?? new SecurityOptions();
    foreach (var proxy in security.TrustedProxies)
    {
        if (proxy.Contains('/', StringComparison.Ordinal) && System.Net.IPNetwork.TryParse(proxy, out var network))
        {
            options.KnownIPNetworks.Add(network);
        }
        else if (IPAddress.TryParse(proxy, out var address))
        {
            options.KnownProxies.Add(address);
        }
    }
});

var app = builder.Build();

await app.Services.InitializeDatabaseAsync();

app.UseForwardedHeaders();
app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();

// Reject clients built for a different protocol version before they can misinterpret anything.
app.Use(async (context, next) =>
{
    if (context.Request.Headers.TryGetValue(ProtocolInfo.VersionHeader, out var header)
        && (!int.TryParse(header, out var clientVersion) || clientVersion != ProtocolInfo.Version))
    {
        await ApiProblem.Create(StatusCodes.Status426UpgradeRequired, ErrorCodes.ProtocolMismatch,
            $"This server speaks protocol version {ProtocolInfo.Version}. Update the application.").ExecuteAsync(context);
        return;
    }

    await next();
});

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", async (AppDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ok" }) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

var api = app.MapGroup(ProtocolInfo.ApiPrefix);
api.MapSetupEndpoints();
api.MapAuthEndpoints();
api.MapMachineEndpoints();

await app.RunAsync();

/// <summary>Exposed so integration tests can host the server in-process.</summary>
public partial class Program;
