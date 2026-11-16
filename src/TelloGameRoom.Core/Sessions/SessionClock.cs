using TelloGameRoom.Core.Enums;

namespace TelloGameRoom.Core.Sessions;

/// <summary>
/// The one formula for "how much time is left". Remaining time is always derived from stored timestamps,
/// never from a counter that ticks, so it is correct after restarts and identical on every node.
/// </summary>
public static class SessionClock
{
    public const long MinDurationSeconds = 60;
    public const long MaxDurationSeconds = 24 * 3600;

    /// <returns>Whole seconds left, rounded up, never negative.</returns>
    public static long RemainingSeconds(SessionStatus status, DateTime? endsAtUtc, long? remainingSecondsAtPause, DateTime nowUtc) =>
        status switch
        {
            SessionStatus.Active when endsAtUtc is { } end => Math.Max(0, (long)Math.Ceiling((end - nowUtc).TotalSeconds)),
            SessionStatus.Paused => Math.Max(0, remainingSecondsAtPause ?? 0),
            _ => 0,
        };
}
