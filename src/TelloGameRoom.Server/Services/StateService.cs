using Microsoft.EntityFrameworkCore;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Sessions;
using TelloGameRoom.Server.Data;
using TelloGameRoom.Server.Data.Entities;
using TelloGameRoom.Server.Realtime;

namespace TelloGameRoom.Server.Services;

/// <summary>Builds the read models that dashboards and machines display.</summary>
public sealed class StateService(AppDbContext db, MachinePresence presence, TimeProvider time)
{
    public async Task<BranchStateDto?> GetBranchStateAsync(Guid branchId, CancellationToken ct)
    {
        var branch = await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == branchId, ct);
        if (branch is null)
        {
            return null;
        }

        var shopName = await db.ShopSettings.Select(s => s.ShopName).FirstOrDefaultAsync(ct) ?? string.Empty;
        var machines = await QueryMachines(db.Machines.Where(m => m.BranchId == branchId), branch.HourlyRateCents, ct);
        var onDuty = await db.Shifts.AsNoTracking()
            .Where(s => s.BranchId == branchId && s.ClosedAtUtc == null)
            .OrderBy(s => s.OpenedAtUtc)
            .Select(s => new OnDutyDto(s.Id, s.UserId, s.User!.DisplayName, s.OpenedAtUtc))
            .ToListAsync(ct);

        return new BranchStateDto(branch.Id, branch.Name, shopName, ToPricing(branch), machines, onDuty, time.GetUtcNow().UtcDateTime);
    }

    public async Task<MachineStateDto?> GetMachineStateAsync(Guid machineId, CancellationToken ct)
    {
        var rate = await db.Machines.Where(m => m.Id == machineId).Select(m => (long?)m.Branch!.HourlyRateCents).FirstOrDefaultAsync(ct);
        return rate is null ? null : (await QueryMachines(db.Machines.Where(m => m.Id == machineId), rate.Value, ct)).FirstOrDefault();
    }

    /// <summary>The state a Machine app must enforce.</summary>
    public async Task<MachineSessionState> GetMachineSessionStateAsync(Guid machineId, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var threshold = await db.Machines.Where(m => m.Id == machineId).Select(m => m.Branch!.WarningThresholdSeconds).FirstOrDefaultAsync(ct);
        var session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.MachineId == machineId && s.Status != SessionStatus.Ended, ct);
        if (session is null)
        {
            return new MachineSessionState(MachineLockState.Locked, null, 0, threshold, 0, now);
        }

        var remaining = SessionClock.RemainingSeconds(session.Status, session.EndsAtUtc, session.RemainingSecondsAtPause, now);
        var state = session.Status == SessionStatus.Paused ? MachineLockState.Paused
            : remaining > 0 ? MachineLockState.Active : MachineLockState.Locked;
        return new MachineSessionState(state, session.Id, remaining, threshold, session.Version, now);
    }

    public async Task<MachineConfigDto?> GetMachineConfigAsync(Guid machineId, CancellationToken ct)
    {
        var shop = await db.ShopSettings.AsNoTracking()
            .Select(s => new { s.ShopName, s.DefaultLanguage, HasLogo = s.Logo != null, s.UpdatedAtUtc })
            .FirstOrDefaultAsync(ct);
        var machine = await db.Machines.AsNoTracking()
            .Where(m => m.Id == machineId)
            .Select(m => new { m.Name, BranchName = m.Branch!.Name, m.Branch.AdminPinHash, m.Branch.WarningThresholdSeconds })
            .FirstOrDefaultAsync(ct);
        return machine is null
            ? null
            : new MachineConfigDto(
                shop?.ShopName ?? string.Empty,
                machine.BranchName,
                machine.Name,
                shop?.DefaultLanguage ?? "en",
                machine.AdminPinHash,
                machine.WarningThresholdSeconds,
                shop is { HasLogo: true } ? shop.UpdatedAtUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    public static BranchPricingDto ToPricing(Branch b) => new(
        b.HourlyRateCents, b.CurrencyCode, b.CurrencySymbol, b.CurrencyDecimals, b.RoundingMode, b.RoundingIncrementCents, b.WarningThresholdSeconds);

    private async Task<List<MachineStateDto>> QueryMachines(IQueryable<Machine> query, long branchRate, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var rows = await query.AsNoTracking()
            .OrderBy(m => m.Name)
            .Select(m => new
            {
                Machine = m,
                GroupName = m.Group == null ? null : m.Group.Name,
                GroupRate = m.Group == null ? null : m.Group.HourlyRateCents,
                Session = db.Sessions.Where(s => s.MachineId == m.Id && s.Status != SessionStatus.Ended)
                    .Select(s => new { S = s, StartedBy = db.Users.Where(u => u.Id == s.StartedByUserId).Select(u => u.DisplayName).FirstOrDefault() })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return rows.ConvertAll(r =>
        {
            var online = presence.IsOnline(r.Machine.Id);
            SessionDto? session = null;
            if (r.Session is { S: var s })
            {
                var remaining = SessionClock.RemainingSeconds(s.Status, s.EndsAtUtc, s.RemainingSecondsAtPause, now);
                session = new SessionDto(s.Id, s.MachineId, s.Status, s.StartedAtUtc, s.EndsAtUtc, remaining, s.PurchasedSeconds,
                    s.AmountCents, s.HourlyRateCents, s.IsFree, r.Session.StartedBy ?? string.Empty, s.Version);
            }

            var status = session switch
            {
                { Status: SessionStatus.Paused } => MachineStatus.Paused,
                not null => MachineStatus.InUse,
                null when online => MachineStatus.Free,
                _ => MachineStatus.Offline,
            };
            return new MachineStateDto(r.Machine.Id, r.Machine.BranchId, r.Machine.Name, r.Machine.GroupId, r.GroupName, r.Machine.IsEnabled,
                online, status, r.GroupRate ?? branchRate, session, r.Machine.LastSeenAtUtc, r.Machine.AppVersion);
        });
    }
}
