using TelloGameRoom.Core.Enums;

namespace TelloGameRoom.Server.Data.Entities;

/// <summary>
/// A block of time sold on a machine. Remaining time is never stored as a ticking counter: while Active it is
/// <c>EndsAtUtc - now</c>, while Paused it is <c>RemainingSecondsAtPause</c>. That is what makes timers survive restarts.
/// </summary>
public sealed class Session
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public Guid MachineId { get; set; }
    public Machine? Machine { get; set; }
    public Guid StartedByUserId { get; set; }
    public SessionStatus Status { get; set; }
    public DateTime StartedAtUtc { get; set; }

    /// <summary>When the session runs out. Only meaningful while Active.</summary>
    public DateTime? EndsAtUtc { get; set; }

    public DateTime? PausedAtUtc { get; set; }
    public long? RemainingSecondsAtPause { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public SessionEndReason? EndReason { get; set; }

    /// <summary>Net time sold (start + added - removed), in seconds.</summary>
    public long PurchasedSeconds { get; set; }

    /// <summary>Net amount collected, in cents. Always equals the sum of the session's event amounts.</summary>
    public long AmountCents { get; set; }

    /// <summary>Hourly rate at the moment the session started. Later price changes do not affect it.</summary>
    public long HourlyRateCents { get; set; }

    public bool IsFree { get; set; }
    public string? FreeReason { get; set; }

    /// <summary>Optimistic concurrency token, incremented on every change.</summary>
    public int Version { get; set; }

    public List<SessionEvent> Events { get; } = [];
}

/// <summary>Immutable ledger entry for a session. Cash expected in a shift is the sum of its events' amounts.</summary>
public sealed class SessionEvent
{
    public long Id { get; set; }
    public Guid SessionId { get; set; }
    public Session? Session { get; set; }
    public Guid BranchId { get; set; }
    public SessionEventType Type { get; set; }
    public DateTime AtUtc { get; set; }

    /// <summary>Staff member responsible; null for system events such as expiry.</summary>
    public Guid? UserId { get; set; }

    public Guid? ShiftId { get; set; }
    public Guid MachineId { get; set; }
    public long DeltaSeconds { get; set; }

    /// <summary>Cash collected (positive) or refunded (negative), in cents.</summary>
    public long AmountDeltaCents { get; set; }

    public Guid? FromMachineId { get; set; }
    public string? Reason { get; set; }
}

/// <summary>An employee's work period with its cash count.</summary>
public sealed class Shift
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public DateTime OpenedAtUtc { get; set; }
    public long OpeningCashCents { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public long? ClosingCashCountedCents { get; set; }

    /// <summary>Opening cash + net sales during the shift, frozen at close.</summary>
    public long? ExpectedCashCents { get; set; }

    public string? Notes { get; set; }
}
