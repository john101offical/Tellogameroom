using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using TelloGameRoom.Core.Protocol;

namespace TelloGameRoom.Server.Infrastructure;

public static class RateLimits
{
    /// <summary>Per-IP limit for credential-guessing targets: login, refresh, setup, pairing.</summary>
    public const string Auth = "auth";

    public static IServiceCollection AddTelloRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await ApiProblem.Create(StatusCodes.Status429TooManyRequests, ErrorCodes.RateLimited, "Too many requests. Wait a minute and try again.")
                    .ExecuteAsync(context.HttpContext);
            };
            options.AddPolicy(Auth, http =>
            {
                var limit = http.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value.AuthRateLimitPerMinute;
                var key = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });
        });
}
