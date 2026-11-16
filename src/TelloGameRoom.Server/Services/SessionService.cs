using Microsoft.EntityFrameworkCore;
using TelloGameRoom.Core.Billing;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Core.Security;
using TelloGameRoom.Core.Sessions;
using TelloGameRoom.Server.Data;
using TelloGameRoom.Server.Data.Entities;
using TelloGameRoom.Server.Infrastructure;
using TelloGameRoom.Server.Realtime;
using TelloGameRoom.Server.Security;

namespace TelloGameRoom.Server.Services;

/// <summary>
/// Serializes every change to sessions. The shop does a handful of session operations per minute, so one lock for
/// the whole process is the simplest way to make "check then write" sequences race-free. The database's unique
/// index on open sessions is the backstop.
/// </summary>
public sealed class SessionGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        return new Releaser(_semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}

/// <summary>
/// The session and timer engine. Every operation: takes the gate, validates against the database, writes the session
/// change + a ledger event + an audit entry in one transaction, then pushes the new state to dashboards and the machine.
/// </summary>
public sealed class SessionService(
    AppDbContext db,
    TimeProvider time,
    AuditService audit,
    SessionGate gate,
    MachinePresence presence,
    StateService state,
    RealtimeNotifier notifier)
{
    private const int MaxReasonLength = 500;

    public async Task<Result<QuoteResponse>> QuoteAsync(Guid branchId, Guid machineId, long seconds, CancellationToken ct)
    {
        if (seconds is < SessionClock.MinDurationSeconds or > SessionClock.MaxDurationSeconds)
        {
            return DurationError();
        }

        var ctx = await LoadMachineAsync(branchId, machineId, ct);
        if (ctx is null)
        {
            return ApiError.NotFound("The machine does not exist.");
        }

        return new QuoteResponse(seconds, ctx.Rate, Price(ctx.Rate, seconds, ctx.Branch));
    }

    public async Task<Result<MachineStateDto>> StartAsync(Guid branchId, Guid machineId, StartSessionRequest request, CurrentUser user, CancellationToken ct)
    {
        if (request.DurationSeconds is < SessionClock.MinDurationSeconds or > SessionClock.MaxDurationSeconds)
        {
            return DurationError();
        }

        var reason = Clean(request.Reason);
        if (request.IsFree)
        {
            if (!user.Has(Permission.SessionsFreeTime))
            {
                return ApiError.Forbidden("Only the owner can give free time.");
            }

            if (reason is null)
            {
                return ReasonRequired();
            }
        }

        using var _ = await gate.EnterAsync(ct);
        var ctx = await LoadMachineAsync(branchId, machineId, ct);
        if (ctx is null)
        {
            return ApiError.NotFound("The machine does not exist.");
        }

        if (!ctx.Machine.IsEnabled)
        {
            return ApiError.Conflict(ErrorCodes.MachineDisabled, "This machine is disabled.");
        }

        if (await db.Sessions.AnyAsync(s => s.MachineId == machineId && s.Status != SessionStatus.Ended, ct))
        {
            return ApiError.Conflict(ErrorCodes.MachineBusy, "This machine already has a session.");
        }

        if (!request.AllowOffline && !presence.IsOnline(machineId))
        {
            return ApiError.Conflict(ErrorCodes.MachineOffline, "This machine is offline.");
        }

        var amount = request.IsFree ? 0 : Price(ctx.Rate, request.DurationSeconds, ctx.Branch);
        if (request.ExpectedAmountCents is { } expected && expected != amount)
        {
            return PriceChanged();
        }

        var shift = await ResolveShiftAsync(branchId, user, requireForEmployees: true, ct);
        if (shift.Error is not null)
        {
            return shift.Error;
        }

        var now = Now;
        var session = new Session
        {
            Id = Guid.CreateVersion7(),
            BranchId = branchId,
            MachineId = machineId,
            StartedByUserId = user.Id,
            Status = SessionStatus.Active,
            StartedAtUtc = now,
            EndsAtUtc = now.AddSeconds(request.DurationSeconds),
            PurchasedSeconds = request.DurationSeconds,
            AmountCents = amount,
            HourlyRateCents = ctx.Rate,
            IsFree = request.IsFree,
            FreeReason = request.IsFree ? reason : null,
            Version = 1,
        };
        db.Sessions.Add(session);
        AddEvent(session, SessionEventType.Started, user.Id, shift.Value, request.DurationSeconds, amount, reason);
        audit.Record(request.IsFree ? AuditActions.SessionFreeStarted : AuditActions.SessionStarted, user.Id, user.Username, branchId,
            machineId, ctx.Machine.Name, session.Id, newValue: Describe(request.DurationSeconds, amount), reason: reason);
        await db.SaveChangesAsync(ct);
        return await PublishAsync(machineId, ct);
    }

    public async Task<Result<MachineStateDto>> AddTimeAsync(Guid branchId, Guid machineId, AdjustTimeRequest request, CurrentUser user, CancellationToken ct)
    {
        if (request.Seconds is < SessionClock.MinDurationSeconds or > SessionClock.MaxDurationSeconds)
        {
            return DurationError();
        }

        using var _ = await gate.EnterAsync(ct);
        var (session, ctx, error) = await LoadOpenSessionAsync(branchId, machineId, ct);
        if (error is not null)
        {
            return error;
        }

        if (session!.IsFree && !user.Has(Permission.SessionsFreeTime))
        {
            return ApiError.Forbidden("Only the owner can extend a free session.");
        }

        var now = Now;
        var remaining = Remaining(session, now);
        if (remaining + request.Seconds > SessionClock.MaxDurationSeconds)
        {
            return DurationError();
        }

        var amount = session.IsFree ? 0 : Price(session.HourlyRateCents, request.Seconds, ctx!.Branch);
        if (request.ExpectedAmountCents is { } expected && expected != amount)
        {
            return PriceChanged();
        }

        var shift = await ResolveShiftAsync(branchId, user, requireForEmployees: true, ct);
        if (shift.Error is not null)
        {
            return shift.Error;
        }

        ShiftRemaining(session, request.Seconds, now);
        session.PurchasedSeconds += request.Seconds;
        session.AmountCents += amount;
        session.Version++;
        var reason = Clean(request.Reason);
        AddEvent(session, SessionEventType.TimeAdded, user.Id, shift.Value, request.Seconds, amount, reason);
        audit.Record(AuditActions.SessionTimeAdded, user.Id, user.Username, branchId, machineId, ctx!.Machine.Name, session.Id,
            oldValue: Describe(remaining, null), newValue: Describe(remaining + request.Seconds, amount), reason: reason);
        await db.SaveChangesAsync(ct);
        return await PublishAsync(machineId, ct);
    }

    public async Task<Result<MachineStateDto>> RemoveTimeAsync(Guid branchId, Guid machineId, AdjustTimeRequest request, CurrentUser user, CancellationToken ct)
    {
        var reason = Clean(request.Reason);
        if (reason is null)
        {
            return ReasonRequired();
        }

        if (request.Seconds < 1)
        {
            return DurationError();
        }

        using var _ = await gate.EnterAsync(ct);
        var (session, ctx, error) = await LoadOpenSessionAsync(branchId, machineId, ct);
        if (error is not null)
        {
            return error;
        }

        var now = Now;
        var remaining = Remaining(session!, now);
        if (request.Seconds >= remaining)
        {
            return ApiError.Conflict(ErrorCodes.InvalidSessionState, "Cannot remove all the remaining time. Stop or cancel the session instead.");
        }

        // Removing paid time gives cash back, never more than was collected.
        var refund = session!.IsFree ? 0 : Math.Min(session.AmountCents, Price(session.HourlyRateCents, request.Seconds, ctx!.Branch));
        if (request.ExpectedAmountCents is { } expected && expected != refund)
        {
            return PriceChanged();
        }

        var shift = await ResolveShiftAsync(branchId, user, requireForEmployees: true, ct);
        if (shift.Error is not null)
        {
            return shift.Error;
        }

        ShiftRemaining(session, -request.Seconds, now);
        session.PurchasedSeconds -= request.Seconds;
        session.AmountCents -= refund;
        session.Version++;
        AddEvent(session, SessionEventType.TimeRemoved, user.Id, shift.Value, -request.Seconds, -refund, reason);
        audit.Record(AuditActions.SessionTimeRemoved, user.Id, user.Username, branchId, machineId, ctx!.Machine.Name, session.Id,
            oldValue: Describe(remaining, null), newValue: Describe(remaining - request.Seconds, -refund), reason: reason);
        await db.SaveChangesAsync(ct);
        return await PublishAsync(machineId, ct);
    }

    public async Task<Result<MachineStateDto>> PauseAsync(Guid branchId, Guid machineId, CurrentUser user, CancellationToken ct, string? auditReason = null)
    {
        using var _ = await gate.EnterAsync(ct);
        var (session, ctx, error) = await LoadOpenSessionAsync(branchId, machineId, ct);
        if (error is not null)
        {
            return error;
        }

        if (session!.Status != SessionStatus.Active)
        {
            return ApiError.Conflict(ErrorCodes.InvalidSessionState, "The session is not running.");
        }

        var now = Now;
        var remaining = Remaining(session, now);
        if (remaining <= 0)
        {
            return ApiError.Conflict(ErrorCodes.InvalidSessionState, "The session has just run out.");
        }

        session.Status = SessionStatus.Paused;
        session.RemainingSecondsAtPause = remaining;
        session.PausedAtUtc = now;
        session.EndsAtUtc = null;
        session.Version++;
        AddEvent(session, SessionEventType.Paused, user.Id, null, 0, 0, auditReason);
        audit.Record(AuditActions.SessionPaused, user.Id, user.Username, branchId, machineId, ctx!.Machine.Name, session.Id,
            newValue: Describe(remaining, null), reason: auditReason);
        await db.SaveChangesAsync(ct);
        return await PublishAsync(machineId, ct);
    }

    public async Task<Result<MachineStateDto>> ResumeAsync(Guid branchId, Guid machineId, CurrentUser user, CancellationToken ct)
    {
        using var _ = await gate.EnterAsync(ct);
        var (session, ctx, error) = await LoadOpenSessionAsync(branchId, machineId, ct);
        if (error is not null)
        {
            return error;
        }

        if (session!.Status != SessionStatus.Paused)
        {
            return ApiError.Conflict(ErrorCodes.InvalidSessionState, "The session is not paused.");
        }

        var now = Now;
        var remaining = session.RemainingSecondsAtPause ?? 0;
        session.Status = SessionStatus.Active;
        session.EndsAtUtc = now.AddSeconds(remaining);
        session.RemainingSecondsAtPause = null;
        session.PausedAtUtc = null;
        session.Version++;
        AddEvent(session, SessionEventType.Resumed, user.Id, null, 0, 0, null);
        audit.Record(AuditActions.SessionResumed, user.Id, user.Username, branchId, machineId, ctx!.Machine.Name, session.Id,
            newValue: Describe(remaining, null));
        await db.SaveChangesAsync(ct);
        return await PublishAsync(machineId, ct);
    }

    /// <summary>Ends the session early without giving money back (the customer left).</summary>
    public async Task<Result<MachineStateDto>> StopAsync(Guid branchId, Guid machineId, EndSessionRequest request, CurrentUser user, CancellationToken ct)
    {
        using var _ = await gate.EnterAsync(ct);
        var (session, ctx, error) = await LoadOpenSessionAsync(branchId, machineId, ct);
        if (error is not null)
        {
            return error;
        }

        var now = Now;
        var remaining = Remaining(session!, now);
        var reason = Clean(request.Reason);
        End(session!, SessionEndReason.StoppedByStaff, now);
        AddEvent(session!, SessionEventType.Stopped, user.Id, null, -remaining, 0, reason);
        audit.Record(AuditActions.SessionStopped, user.Id, user.Username, branchId, machineId, ctx!.Machine.Name, session!.Id,
            oldValue: Describe(remaining, null), reason: reason);
        await db.SaveChangesAsync(ct);
        return await PublishAsync(machineId, ct);
    }

    /// <summary>Ends the session and refunds the unused time (or an explicit amount). Requires a reason.</summary>
    public async Task<Result<MachineStateDto>> CancelAsync(Guid branchId, Guid machineId, EndSessionRequest request, CurrentUser user, CancellationToken ct)
    {
        var reason = Clean(request.Reason);
        if (reason is null)
        {
            return ReasonRequired();
        }

        using var _ = await gate.EnterAsync(ct);
        var (session, ctx, error) = await LoadOpenSessionAsync(branchId, machineId, ct);
        if (error is not null)
        {
            return error;
        }

        var now = Now;
        var remaining = Remaining(session!, now);
        var proRata = session!.IsFree ? 0 : Math.Min(session.AmountCents, Price(session.HourlyRateCents, remaining, ctx!.Branch));
        var refund = request.RefundCents ?? proRata;
        if (refund < 0 || refund > session.AmountCents)
        {
            return ApiError.Invalid("The refund cannot be negative or more than what was collected.");
        }

        var shift = await ResolveShiftAsync(branchId, user, requireForEmployees: true, ct);
        if (shift.Error is not null)
        {
            return shift.Error;
        }

        session.AmountCents -= refund;
        session.PurchasedSeconds -= remaining;
        End(session, SessionEndReason.Cancelled, now);
        AddEvent(session, SessionEventType.Cancelled, user.Id, shift.Value, -remaining, -refund, reason);
        audit.Record(AuditActions.SessionCancelled, user.Id, user.Username, branchId, machineId, ctx!.Machine.Name, session.Id,
            oldValue: Describe(remaining, null), newValue: Describe(0, -refund), reason: reason);
        await db.SaveChangesAsync(ct);
        return await PublishAsync(machineId, ct);
    }

    /// <summary>Moves the open session to another free machine in the same branch, keeping its remaining time.</summary>
    public async Task<Result<MachineStateDto>> TransferAsync(Guid branchId, Guid machineId, TransferSessionRequest request, CurrentUser user, CancellationToken ct)
    {
        if (request.TargetMachineId == machineId)
        {
            return ApiError.Invalid("Choose a different machine.");
        }

        using var _ = await gate.EnterAsync(ct);
        var (session, ctx, error) = await LoadOpenSessionAsync(branchId, machineId, ct);
        if (error is not null)
        {
            return error;
        }

        var target = await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == request.TargetMachineId && m.BranchId == branchId, ct);
        if (target is null)
        {
            return ApiError.NotFound("The target machine does not exist.");
        }

        if (!target.IsEnabled)
        {
            return ApiError.Conflict(ErrorCodes.MachineDisabled, "The target machine is disabled.");
        }

        if (await db.Sessions.AnyAsync(s => s.MachineId == target.Id && s.Status != SessionStatus.Ended, ct))
        {
            return ApiError.Conflict(ErrorCodes.MachineBusy, "The target machine already has a session.");
        }

        if (!request.AllowOffline && !presence.IsOnline(target.Id))
        {
            return ApiError.Conflict(ErrorCodes.MachineOffline, "The target machine is offline.");
        }

        var reason = Clean(request.Reason);
        session!.MachineId = target.Id;
        session.Version++;
        db.SessionEvents.Add(new SessionEvent
        {
            SessionId = session.Id,
            BranchId = branchId,
            Type = SessionEventType.Transferred,
            AtUtc = Now,
            UserId = user.Id,
            MachineId = target.Id,
            FromMachineId = machineId,
            Reason = reason,
        });
        audit.Record(AuditActions.SessionTransferred, user.Id, user.Username, branchId, target.Id, target.Name, session.Id,
            oldValue: ctx!.Machine.Name, newValue: target.Name, reason: reason);
        await db.SaveChangesAsync(ct);

        await notifier.MachineChangedAsync(machineId, ct);
        return await PublishAsync(target.Id, ct);
    }

    /// <summary>Ends every session whose time has run out. Called by the background worker.</summary>
    /// <returns>Number of sessions expired.</returns>
    public async Task<int> ExpireDueSessionsAsync(CancellationToken ct)
    {
        var now = Now;
        if (!await db.Sessions.AnyAsync(s => s.Status == SessionStatus.Active && s.EndsAtUtc <= now, ct))
        {
            return 0;
        }

        List<Guid> machineIds;
        using (await gate.EnterAsync(ct))
        {
            now = Now;
            var due = await db.Sessions.Include(s => s.Machine)
                .Where(s => s.Status == SessionStatus.Active && s.EndsAtUtc <= now)
                .ToListAsync(ct);
            foreach (var session in due)
            {
                // The session ended when its time ran out, even if the server was down at that moment.
                End(session, SessionEndReason.TimeExpired, session.EndsAtUtc ?? now);
                AddEvent(session, SessionEventType.Expired, null, null, 0, 0, null);
                audit.Record(AuditActions.SessionExpired, branchId: session.BranchId, machineId: session.MachineId,
                    machineName: session.Machine?.Name, sessionId: session.Id);
            }

            await db.SaveChangesAsync(ct);
            machineIds = due.ConvertAll(s => s.MachineId);
        }

        foreach (var machineId in machineIds)
        {
            await notifier.MachineChangedAsync(machineId, ct);
        }

        return machineIds.Count;
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private static long Price(long rate, long seconds, Branch branch) =>
        PriceCalculator.AmountDue(rate, seconds, branch.RoundingMode, branch.RoundingIncrementCents);

    private static long Remaining(Session s, DateTime now) =>
        SessionClock.RemainingSeconds(s.Status, s.EndsAtUtc, s.RemainingSecondsAtPause, now);

    private static void ShiftRemaining(Session s, long deltaSeconds, DateTime now)
    {
        if (s.Status == SessionStatus.Paused)
        {
            s.RemainingSecondsAtPause = (s.RemainingSecondsAtPause ?? 0) + deltaSeconds;
        }
        else
        {
            // If the timer hit zero a moment ago but the worker has not expired it yet, extend from now, not from the past.
            var basis = s.EndsAtUtc is { } end && end > now ? end : now;
            s.EndsAtUtc = basis.AddSeconds(deltaSeconds);
        }
    }

    private static void End(Session s, SessionEndReason reason, DateTime at)
    {
        s.Status = SessionStatus.Ended;
        s.EndReason = reason;
        s.EndedAtUtc = at;
        s.EndsAtUtc = null;
        s.RemainingSecondsAtPause = null;
        s.PausedAtUtc = null;
        s.Version++;
    }

    private void AddEvent(Session s, SessionEventType type, Guid? userId, Guid? shiftId, long deltaSeconds, long amountCents, string? reason) =>
        db.SessionEvents.Add(new SessionEvent
        {
            SessionId = s.Id,
            BranchId = s.BranchId,
            Type = type,
            AtUtc = Now,
            UserId = userId,
            ShiftId = shiftId,
            MachineId = s.MachineId,
            DeltaSeconds = deltaSeconds,
            AmountDeltaCents = amountCents,
            Reason = reason,
        });

    private async Task<Result<MachineStateDto>> PublishAsync(Guid machineId, CancellationToken ct)
    {
        await notifier.MachineChangedAsync(machineId, ct);
        return (await state.GetMachineStateAsync(machineId, ct))!;
    }

    private sealed record MachineContext(Machine Machine, Branch Branch, long Rate);

    private async Task<MachineContext?> LoadMachineAsync(Guid branchId, Guid machineId, CancellationToken ct)
    {
        var machine = await db.Machines.AsNoTracking().Include(m => m.Branch).Include(m => m.Group)
            .FirstOrDefaultAsync(m => m.Id == machineId && m.BranchId == branchId, ct);
        return machine?.Branch is null ? null : new MachineContext(machine, machine.Branch, machine.Group?.HourlyRateCents ?? machine.Branch.HourlyRateCents);
    }

    private async Task<(Session? Session, MachineContext? Context, ApiError? Error)> LoadOpenSessionAsync(Guid branchId, Guid machineId, CancellationToken ct)
    {
        var ctx = await LoadMachineAsync(branchId, machineId, ct);
        if (ctx is null)
        {
            return (null, null, ApiError.NotFound("The machine does not exist."));
        }

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.MachineId == machineId && s.Status != SessionStatus.Ended, ct);
        return session is null
            ? (null, ctx, ApiError.Conflict(ErrorCodes.NoOpenSession, "This machine has no session."))
            : (session, ctx, null);
    }

    /// <summary>
    /// Finds the shift that money should be booked to. Employees must be clocked in at this branch to handle cash;
    /// the owner may act without a shift.
    /// </summary>
    private async Task<(Guid? Value, ApiError? Error)> ResolveShiftAsync(Guid branchId, CurrentUser user, bool requireForEmployees, CancellationToken ct)
    {
        var shiftId = await db.Shifts
            .Where(s => s.UserId == user.Id && s.BranchId == branchId && s.ClosedAtUtc == null)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);
        if (shiftId is null && requireForEmployees && user.Role == UserRole.Employee)
        {
            return (null, ApiError.Conflict(ErrorCodes.ShiftRequired, "Clock in before handling sessions."));
        }

        return (shiftId, null);
    }

    private static string? Clean(string? reason)
    {
        var trimmed = reason?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > MaxReasonLength ? trimmed[..MaxReasonLength] : trimmed;
    }

    private static string Describe(long seconds, long? amountCents) =>
        amountCents is null
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{seconds}s")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{seconds}s; {amountCents}c");

    private static ApiError DurationError() =>
        ApiError.Invalid($"Time must be between {SessionClock.MinDurationSeconds / 60} minute and {SessionClock.MaxDurationSeconds / 3600} hours.");

    private static ApiError ReasonRequired() => new(400, ErrorCodes.ReasonRequired, "A reason is required for this action.");

    private static ApiError PriceChanged() =>
        ApiError.Conflict(ErrorCodes.PriceChanged, "The price has changed. Check the amount and confirm again.");
}
