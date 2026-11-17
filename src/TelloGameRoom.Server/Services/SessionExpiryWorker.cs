namespace TelloGameRoom.Server.Services;

/// <summary>
/// Ends sessions when their time is up. It holds no state: on every tick it asks the database which sessions are
/// overdue, so after a restart (or a crash) it simply picks up where the timestamps say things are.
/// </summary>
public sealed class SessionExpiryWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<SessionExpiryWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var expired = await scope.ServiceProvider.GetRequiredService<SessionService>().ExpireDueSessionsAsync(stoppingToken);
                if (expired > 0)
                {
                    logger.LogInformation("Expired {Count} session(s)", expired);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Session expiry tick failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
