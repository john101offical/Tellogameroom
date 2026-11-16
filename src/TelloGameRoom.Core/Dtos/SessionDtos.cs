using TelloGameRoom.Core.Enums;

namespace TelloGameRoom.Core.Dtos;

/// <summary>A session as dashboards see it. <see cref="RemainingSeconds"/> is the server's value at the time of sending.</summary>
public sealed record SessionDto(
    Guid Id,
    Guid MachineId,
    SessionStatus Status,
    DateTime StartedAtUtc,
    DateTime? EndsAtUtc,
    long RemainingSeconds,
    long PurchasedSeconds,
    long AmountCents,
    long HourlyRateCents,
    bool IsFree,
    string StartedBy,
    int Version);

/// <summary>Everything a dashboard card needs for one machine.</summary>
public sealed record MachineStateDto(
    Guid Id,
    Guid BranchId,
    string Name,
    Guid? GroupId,
    string? GroupName,
    bool IsEnabled,
    bool IsOnline,
    MachineStatus Status,
    long HourlyRateCents,
    SessionDto? Session,
    DateTime? LastSeenAtUtc,
    string? AppVersion);

public sealed record BranchPricingDto(
    long HourlyRateCents,
    string CurrencyCode,
    string CurrencySymbol,
    int CurrencyDecimals,
    RoundingMode RoundingMode,
    long RoundingIncrementCents,
    int WarningThresholdSeconds);

public sealed record OnDutyDto(Guid ShiftId, Guid UserId, string DisplayName, DateTime SinceUtc);

/// <summary>Full snapshot of a branch, returned when a dashboard subscribes.</summary>
public sealed record BranchStateDto(
    Guid BranchId,
    string BranchName,
    string ShopName,
    BranchPricingDto Pricing,
    IReadOnlyList<MachineStateDto> Machines,
    IReadOnlyList<OnDutyDto> OnDuty,
    DateTime ServerTimeUtc);

/// <param name="DurationSeconds">Time to sell.</param>
/// <param name="ExpectedAmountCents">The amount the staff member was shown. If the server computes a different amount the request is rejected, so nobody collects the wrong cash.</param>
/// <param name="IsFree">Owner-only free session. Requires <paramref name="Reason"/>.</param>
/// <param name="AllowOffline">Start even though the machine is not connected right now.</param>
public sealed record StartSessionRequest(long DurationSeconds, long? ExpectedAmountCents = null, bool IsFree = false, string? Reason = null, bool AllowOffline = false);

public sealed record AdjustTimeRequest(long Seconds, long? ExpectedAmountCents = null, string? Reason = null);

/// <param name="RefundCents">Only for cancel: overrides the default pro-rata refund.</param>
public sealed record EndSessionRequest(string? Reason = null, long? RefundCents = null);

public sealed record TransferSessionRequest(Guid TargetMachineId, string? Reason = null, bool AllowOffline = false);

public sealed record QuoteResponse(long Seconds, long HourlyRateCents, long AmountCents);

public enum MachineLockState
{
    Locked = 0,
    Active = 1,
    Paused = 2,
}

/// <summary>
/// What the server tells a machine. The machine counts down from <see cref="RemainingSeconds"/> on its own monotonic
/// clock; it never needs its wall clock to agree with the server's.
/// </summary>
public sealed record MachineSessionState(
    MachineLockState State,
    Guid? SessionId,
    long RemainingSeconds,
    int WarningThresholdSeconds,
    int Version,
    DateTime ServerTimeUtc);

/// <summary>Branding and behaviour settings pushed to a machine.</summary>
public sealed record MachineConfigDto(
    string ShopName,
    string BranchName,
    string MachineName,
    string Language,
    string? AdminPinHash,
    int WarningThresholdSeconds,
    string? LogoVersion);

public sealed record MachineMessageDto(string Text, DateTime SentAtUtc);

public sealed record SendMessageRequest(string Text);

public sealed record MachineCommandRequest(MachineCommand Command);

public sealed record BulkResult(int Sent, int Offline);
