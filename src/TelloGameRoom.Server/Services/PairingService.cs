using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Server.Data;
using TelloGameRoom.Server.Data.Entities;
using TelloGameRoom.Server.Infrastructure;
using TelloGameRoom.Server.Security;

namespace TelloGameRoom.Server.Services;

public enum PairingError
{
    None = 0,
    NameTaken,
    GroupNotFound,
    InvalidCode,
}

/// <summary>Issues one-time pairing codes and turns them into machine identities.</summary>
public sealed class PairingService(AppDbContext db, AuditService audit, TimeProvider time, IOptions<SecurityOptions> security)
{
    public async Task<(PairingCodeResponse? Response, PairingError Error)> CreateCodeAsync(
        Guid branchId, CreatePairingCodeRequest request, CurrentUser user, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var name = request.MachineName.Trim();

        if (await db.Machines.AnyAsync(m => m.BranchId == branchId && m.Name == name, ct))
        {
            return (null, PairingError.NameTaken);
        }

        if (request.GroupId is { } groupId && !await db.MachineGroups.AnyAsync(g => g.Id == groupId && g.BranchId == branchId, ct))
        {
            return (null, PairingError.GroupNotFound);
        }

        // A newer code for the same name replaces any older unused one.
        await db.PairingCodes
            .Where(c => c.BranchId == branchId && c.MachineName == name && c.UsedAtUtc == null)
            .ExecuteDeleteAsync(ct);

        var code = SecretTokens.NewPairingCode();
        var expires = now.AddMinutes(security.Value.PairingCodeMinutes);
        db.PairingCodes.Add(new PairingCode
        {
            Id = Guid.CreateVersion7(),
            BranchId = branchId,
            CodeHash = SecretTokens.Hash(code),
            MachineName = name,
            GroupId = request.GroupId,
            CreatedByUserId = user.Id,
            CreatedAtUtc = now,
            ExpiresAtUtc = expires,
        });
        audit.Record(AuditActions.PairingCodeCreated, user.Id, user.Username, branchId, machineName: name);
        await db.SaveChangesAsync(ct);
        return (new PairingCodeResponse(SecretTokens.FormatPairingCode(code), expires, name), PairingError.None);
    }

    public async Task<(PairMachineResponse? Response, PairingError Error)> PairAsync(PairMachineRequest request, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var normalized = SecretTokens.NormalizePairingCode(request.Code ?? string.Empty);
        if (normalized.Length != SecretTokens.PairingCodeLength)
        {
            return (null, PairingError.InvalidCode);
        }

        var hash = SecretTokens.Hash(normalized);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Claim the code atomically so two machines racing with the same code cannot both win.
        var claimed = await db.PairingCodes
            .Where(c => c.CodeHash == hash && c.UsedAtUtc == null && c.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAtUtc, now), ct);
        if (claimed != 1)
        {
            return (null, PairingError.InvalidCode);
        }

        var code = await db.PairingCodes.FirstAsync(c => c.CodeHash == hash, ct);
        var branch = await db.Branches.AsNoTracking().FirstAsync(b => b.Id == code.BranchId, ct);
        if (await db.Machines.AnyAsync(m => m.BranchId == code.BranchId && m.Name == code.MachineName, ct))
        {
            // Rolls back the claim as well.
            return (null, PairingError.NameTaken);
        }

        var secret = SecretTokens.NewToken();
        var machine = new Machine
        {
            Id = Guid.CreateVersion7(),
            BranchId = code.BranchId,
            Name = code.MachineName,
            GroupId = code.GroupId,
            TokenHash = SecretTokens.Hash(secret),
            HostName = Trim(request.HostName, 100),
            OsVersion = Trim(request.OsVersion, 100),
            AppVersion = Trim(request.AppVersion, 32),
            CreatedAtUtc = now,
        };
        db.Machines.Add(machine);
        code.MachineId = machine.Id;
        audit.Record(AuditActions.MachinePaired, code.CreatedByUserId, branchId: code.BranchId, machineId: machine.Id,
            machineName: machine.Name, newValue: machine.HostName);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var shopName = await db.ShopSettings.Select(s => s.ShopName).FirstAsync(ct);
        return (new PairMachineResponse(machine.Id, machine.Name, branch.Id, branch.Name, shopName, $"{machine.Id}.{secret}"), PairingError.None);
    }

    private static string? Trim(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim() is { } v && v.Length > max ? v[..max] : value.Trim();
}
