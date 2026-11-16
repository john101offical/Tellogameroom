using Microsoft.EntityFrameworkCore;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Core.Security;
using TelloGameRoom.Server.Data;
using TelloGameRoom.Server.Data.Entities;
using TelloGameRoom.Server.Infrastructure;
using TelloGameRoom.Server.Realtime;
using TelloGameRoom.Server.Security;

namespace TelloGameRoom.Server.Services;

/// <summary>Cash totals of one shift, derived from the session ledger.</summary>
public readonly record struct ShiftTotals(long SalesCents, long RefundsCents, int SessionsStarted)
{
    public long NetCents => SalesCents - RefundsCents;

    /// <summary>What should be in the drawer.</summary>
    public long Expected(long openingCashCents) => openingCashCents + NetCents;

    /// <summary>Counted minus expected. Negative: cash is missing. Positive: there is too much.</summary>
    public static long Difference(long countedCents, long expectedCents) => countedCents - expectedCents;
}

public sealed class ShiftService(AppDbContext db, AuditService audit, TimeProvider time, RealtimeNotifier notifier)
{
    public async Task<Result<ShiftDto>> OpenAsync(Guid branchId, OpenShiftRequest request, CurrentUser user, CancellationToken ct)
    {
        if (request.OpeningCashCents is < 0 or > 100_000_000_000)
        {
            return ApiError.Invalid("Opening cash cannot be negative.");
        }

        if (await db.Shifts.AnyAsync(s => s.UserId == user.Id && s.ClosedAtUtc == null, ct))
        {
            return ApiError.Conflict(ErrorCodes.ShiftAlreadyOpen, "You already have an open shift. Close it first.");
        }

        var shift = new Shift
        {
            Id = Guid.CreateVersion7(),
            BranchId = branchId,
            UserId = user.Id,
            OpenedAtUtc = time.GetUtcNow().UtcDateTime,
            OpeningCashCents = request.OpeningCashCents,
        };
        db.Shifts.Add(shift);
        audit.Record(AuditActions.ShiftOpened, user.Id, user.Username, branchId, newValue: Cents(request.OpeningCashCents));
        await db.SaveChangesAsync(ct);
        await notifier.BranchChangedAsync(branchId);
        return (await GetAsync(shift.Id, ct))!;
    }

    /// <summary>Closes the caller's own open shift in this branch.</summary>
    public async Task<Result<ShiftDto>> CloseOwnAsync(Guid branchId, CloseShiftRequest request, CurrentUser user, CancellationToken ct)
    {
        var shift = await db.Shifts.FirstOrDefaultAsync(s => s.UserId == user.Id && s.BranchId == branchId && s.ClosedAtUtc == null, ct);
        return shift is null
            ? ApiError.Conflict(ErrorCodes.NoOpenShift, "You have no open shift in this branch.")
            : await CloseAsync(shift, request, user, ct);
    }

    /// <summary>Closes any shift (owner closing a shift an employee forgot).</summary>
    public async Task<Result<ShiftDto>> CloseByIdAsync(Guid branchId, Guid shiftId, CloseShiftRequest request, CurrentUser user, CancellationToken ct)
    {
        var shift = await db.Shifts.FirstOrDefaultAsync(s => s.Id == shiftId && s.BranchId == branchId, ct);
        if (shift is null)
        {
            return ApiError.NotFound("The shift does not exist.");
        }

        return shift.ClosedAtUtc is not null
            ? ApiError.Conflict(ErrorCodes.NoOpenShift, "This shift is already closed.")
            : await CloseAsync(shift, request, user, ct);
    }

    public async Task<ShiftDto?> GetAsync(Guid shiftId, CancellationToken ct) =>
        (await QueryAsync(db.Shifts.Where(s => s.Id == shiftId), ct)).FirstOrDefault();

    public async Task<ShiftDto?> GetCurrentAsync(Guid branchId, Guid userId, CancellationToken ct) =>
        (await QueryAsync(db.Shifts.Where(s => s.UserId == userId && s.BranchId == branchId && s.ClosedAtUtc == null), ct)).FirstOrDefault();

    /// <summary>Lists shifts opened in a period. Callers without <see cref="Permission.ShiftsViewAll"/> only see their own.</summary>
    public Task<List<ShiftDto>> ListAsync(Guid branchId, DateTime fromUtc, DateTime toUtc, Guid? userId, CurrentUser caller, CancellationToken ct)
    {
        var query = db.Shifts.Where(s => s.BranchId == branchId && s.OpenedAtUtc >= fromUtc && s.OpenedAtUtc < toUtc);
        if (!caller.Has(Permission.ShiftsViewAll))
        {
            query = query.Where(s => s.UserId == caller.Id);
        }
        else if (userId is not null)
        {
            query = query.Where(s => s.UserId == userId);
        }

        return QueryAsync(query, ct);
    }

    private async Task<Result<ShiftDto>> CloseAsync(Shift shift, CloseShiftRequest request, CurrentUser user, CancellationToken ct)
    {
        if (request.CountedCashCents is < 0 or > 100_000_000_000)
        {
            return ApiError.Invalid("Counted cash cannot be negative.");
        }

        var totals = (await TotalsAsync([shift.Id], ct)).GetValueOrDefault(shift.Id);
        var expected = totals.Expected(shift.OpeningCashCents);
        shift.ClosedAtUtc = time.GetUtcNow().UtcDateTime;
        shift.ClosingCashCountedCents = request.CountedCashCents;
        shift.ExpectedCashCents = expected;
        shift.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim() is { Length: > 1000 } n ? n[..1000] : request.Notes.Trim();

        var owner = await db.Users.Where(u => u.Id == shift.UserId).Select(u => u.Username).FirstAsync(ct);
        audit.Record(AuditActions.ShiftClosed, user.Id, user.Username, shift.BranchId,
            oldValue: $"expected {Cents(expected)}",
            newValue: $"counted {Cents(request.CountedCashCents)}; difference {Cents(ShiftTotals.Difference(request.CountedCashCents, expected))}",
            reason: user.Id == shift.UserId ? null : $"closed for {owner}");
        await db.SaveChangesAsync(ct);
        await notifier.BranchChangedAsync(shift.BranchId);
        return (await GetAsync(shift.Id, ct))!;
    }

    private async Task<List<ShiftDto>> QueryAsync(IQueryable<Shift> query, CancellationToken ct)
    {
        var shifts = await query.AsNoTracking().OrderByDescending(s => s.OpenedAtUtc)
            .Select(s => new { Shift = s, s.User!.DisplayName })
            .Take(1000)
            .ToListAsync(ct);
        var totals = await TotalsAsync(shifts.ConvertAll(s => s.Shift.Id), ct);

        return shifts.ConvertAll(row =>
        {
            var s = row.Shift;
            var t = totals.GetValueOrDefault(s.Id);
            // A closed shift reports the expected amount frozen at closing time.
            var expected = s.ExpectedCashCents ?? t.Expected(s.OpeningCashCents);
            long? difference = s.ClosingCashCountedCents is { } counted ? ShiftTotals.Difference(counted, expected) : null;
            return new ShiftDto(s.Id, s.BranchId, s.UserId, row.DisplayName, s.OpenedAtUtc, s.ClosedAtUtc, s.OpeningCashCents,
                t.SalesCents, t.RefundsCents, expected, s.ClosingCashCountedCents, difference, t.SessionsStarted, s.Notes);
        });
    }

    private async Task<Dictionary<Guid, ShiftTotals>> TotalsAsync(IReadOnlyCollection<Guid> shiftIds, CancellationToken ct)
    {
        if (shiftIds.Count == 0)
        {
            return [];
        }

        var rows = await db.SessionEvents.AsNoTracking()
            .Where(e => e.ShiftId != null && shiftIds.Contains(e.ShiftId.Value))
            .GroupBy(e => e.ShiftId!.Value)
            .Select(g => new
            {
                ShiftId = g.Key,
                Sales = g.Sum(e => e.AmountDeltaCents > 0 ? e.AmountDeltaCents : 0),
                Refunds = g.Sum(e => e.AmountDeltaCents < 0 ? -e.AmountDeltaCents : 0),
                Started = g.Count(e => e.Type == SessionEventType.Started),
            })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.ShiftId, r => new ShiftTotals(r.Sales, r.Refunds, r.Started));
    }

    private static string Cents(long cents) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{cents}c");
}
