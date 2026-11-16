using Microsoft.EntityFrameworkCore;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Server.Data;
using TelloGameRoom.Server.Infrastructure;

namespace TelloGameRoom.Server.Reports;

public sealed class ReportService(AppDbContext db)
{
    public const int MaxRangeDays = 366;

    /// <summary>Resolves an IANA zone id, falling back to UTC when the host does not know it.</summary>
    public static TimeZoneInfo Zone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>UTC instants covering the local days <paramref name="from"/> through <paramref name="to"/> inclusive.</summary>
    public static (DateTime FromUtc, DateTime ToUtc) UtcRange(DateOnly from, DateOnly to, TimeZoneInfo zone) => (
        TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone),
        TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone));

    public async Task<Result<ReportDto>> BuildAsync(Guid branchId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (to < from || to.DayNumber - from.DayNumber >= MaxRangeDays)
        {
            return ApiError.Invalid($"Choose a period of at most {MaxRangeDays} days.");
        }

        var branch = await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == branchId, ct);
        if (branch is null)
        {
            return ApiError.NotFound();
        }

        var zone = Zone(branch.TimeZoneId);
        var (fromUtc, toUtc) = UtcRange(from, to, zone);

        var events = await db.SessionEvents.AsNoTracking()
            .Where(e => e.BranchId == branchId && e.AtUtc >= fromUtc && e.AtUtc < toUtc)
            .Select(e => new { e.AtUtc, e.Type, e.UserId, e.MachineId, e.DeltaSeconds, e.AmountDeltaCents, e.Session!.IsFree })
            .ToListAsync(ct);

        var userNames = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var machineNames = await db.Machines.AsNoTracking().Where(m => m.BranchId == branchId).ToDictionaryAsync(m => m.Id, m => m.Name, ct);

        static bool CountsAsTimeSold(SessionEventType t) =>
            t is SessionEventType.Started or SessionEventType.TimeAdded or SessionEventType.TimeRemoved or SessionEventType.Cancelled;

        var rows = events.ConvertAll(e => new
        {
            Local = TimeZoneInfo.ConvertTimeFromUtc(e.AtUtc, zone),
            e.Type,
            e.UserId,
            e.MachineId,
            Amount = e.AmountDeltaCents,
            Seconds = CountsAsTimeSold(e.Type) ? e.DeltaSeconds : 0,
            Started = e.Type == SessionEventType.Started ? 1 : 0,
            Free = e.Type == SessionEventType.Started && e.IsFree ? 1 : 0,
        });

        var perDay = rows.GroupBy(r => DateOnly.FromDateTime(r.Local)).ToDictionary(g => g.Key);
        var byDay = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1)
            .Select(i => from.AddDays(i))
            .Select(d => perDay.TryGetValue(d, out var g)
                ? new DayPoint(d, g.Sum(r => r.Amount), g.Sum(r => r.Started), g.Sum(r => r.Seconds))
                : new DayPoint(d, 0, 0, 0))
            .ToList();

        var byEmployee = rows.Where(r => r.Amount != 0 || r.Started > 0)
            .GroupBy(r => r.UserId)
            .Select(g => new NamedTotal(g.Key, g.Key is { } id && userNames.TryGetValue(id, out var n) ? n : "-",
                g.Sum(r => r.Amount), g.Sum(r => r.Started), g.Sum(r => r.Seconds)))
            .OrderByDescending(t => t.IncomeCents)
            .ToList();

        var byMachine = rows.Where(r => r.Amount != 0 || r.Started > 0)
            .GroupBy(r => r.MachineId)
            .Select(g => new NamedTotal(g.Key, machineNames.TryGetValue(g.Key, out var n) ? n : "-",
                g.Sum(r => r.Amount), g.Sum(r => r.Started), g.Sum(r => r.Seconds)))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var perHour = rows.GroupBy(r => r.Local.Hour).ToDictionary(g => g.Key);
        var byHour = Enumerable.Range(0, 24)
            .Select(h => perHour.TryGetValue(h, out var g) ? new HourPoint(h, g.Sum(r => r.Started), g.Sum(r => r.Amount)) : new HourPoint(h, 0, 0))
            .ToList();

        return new ReportDto(
            branch.Id, branch.Name, from, to, branch.TimeZoneId,
            IncomeCents: rows.Sum(r => r.Amount),
            SalesCents: rows.Where(r => r.Amount > 0).Sum(r => r.Amount),
            RefundsCents: rows.Where(r => r.Amount < 0).Sum(r => -r.Amount),
            Sessions: rows.Sum(r => r.Started),
            FreeSessions: rows.Sum(r => r.Free),
            SecondsSold: rows.Sum(r => r.Seconds),
            byDay, byEmployee, byMachine, byHour);
    }
}
