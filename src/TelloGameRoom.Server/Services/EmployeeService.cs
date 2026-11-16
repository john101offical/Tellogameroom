using Microsoft.EntityFrameworkCore;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Core.Security;
using TelloGameRoom.Server.Data;
using TelloGameRoom.Server.Data.Entities;
using TelloGameRoom.Server.Endpoints;
using TelloGameRoom.Server.Infrastructure;
using TelloGameRoom.Server.Security;

namespace TelloGameRoom.Server.Services;

public sealed class EmployeeService(AppDbContext db, IPasswordHasher hasher, AuditService audit, PermissionService permissions, TimeProvider time)
{
    public async Task<List<EmployeeDto>> ListAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var users = await db.Users.AsNoTracking().Include(u => u.Branches).OrderBy(u => u.DisplayName).ToListAsync(ct);
        return users.ConvertAll(u => ToDto(u, now));
    }

    public async Task<Result<EmployeeDto>> CreateAsync(CreateEmployeeRequest request, CurrentUser actor, CancellationToken ct)
    {
        var v = new Validator()
            .Check(request.Username is not null && SetupEndpoints.UsernamePattern().IsMatch(request.Username), nameof(request.Username),
                "Use 3 to 32 letters, digits, dots, dashes or underscores.")
            .Length(request.DisplayName, nameof(request.DisplayName), 1, 100)
            .Check(SetupEndpoints.IsAcceptablePassword(request.Password), nameof(request.Password),
                $"Must be between {SetupEndpoints.MinPasswordLength} and {SetupEndpoints.MaxPasswordLength} characters.")
            .Check(Enum.IsDefined(request.Role), nameof(request.Role), "Unknown role.");
        if (!v.IsValid)
        {
            return ApiError.Invalid("Check the username, name, password and role.");
        }

        var normalized = AuthService.Normalize(request.Username!);
        if (await db.Users.AnyAsync(u => u.NormalizedUsername == normalized, ct))
        {
            return ApiError.Conflict(ErrorCodes.Conflict, "This username is already taken.");
        }

        var branchIds = await ValidBranchIdsAsync(request.BranchIds, ct);
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Username = request.Username!.Trim(),
            NormalizedUsername = normalized,
            DisplayName = request.DisplayName.Trim(),
            PasswordHash = hasher.Hash(request.Password),
            Role = request.Role,
            CreatedAtUtc = time.GetUtcNow().UtcDateTime,
        };
        foreach (var branchId in branchIds)
        {
            user.Branches.Add(new UserBranch { UserId = user.Id, BranchId = branchId });
        }

        db.Users.Add(user);
        audit.Record(AuditActions.EmployeeCreated, actor.Id, actor.Username, newValue: $"{user.Username} ({user.Role})");
        await db.SaveChangesAsync(ct);
        return ToDto(user, user.CreatedAtUtc);
    }

    public async Task<Result<EmployeeDto>> UpdateAsync(Guid id, UpdateEmployeeRequest request, CurrentUser actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 100 || !Enum.IsDefined(request.Role))
        {
            return ApiError.Invalid("Check the name and role.");
        }

        var user = await db.Users.Include(u => u.Branches).FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return ApiError.NotFound("The employee does not exist.");
        }

        var losesOwnership = user.Role == UserRole.Owner && user.IsActive && (request.Role != UserRole.Owner || !request.IsActive);
        if (losesOwnership)
        {
            if (user.Id == actor.Id)
            {
                return ApiError.Conflict(ErrorCodes.LastOwner, "You cannot deactivate or demote your own account.");
            }

            if (await db.Users.CountAsync(u => u.Role == UserRole.Owner && u.IsActive, ct) <= 1)
            {
                return ApiError.Conflict(ErrorCodes.LastOwner, "The shop needs at least one active owner.");
            }
        }

        var before = $"{user.DisplayName}; {user.Role}; active={user.IsActive}";
        var wasActive = user.IsActive;
        user.DisplayName = request.DisplayName.Trim();
        user.Role = request.Role;
        user.IsActive = request.IsActive;

        var branchIds = await ValidBranchIdsAsync(request.BranchIds, ct);
        user.Branches.RemoveAll(b => !branchIds.Contains(b.BranchId));
        foreach (var branchId in branchIds.Where(b => user.Branches.All(x => x.BranchId != b)))
        {
            user.Branches.Add(new UserBranch { UserId = user.Id, BranchId = branchId });
        }

        var now = time.GetUtcNow().UtcDateTime;
        if (wasActive && !user.IsActive)
        {
            // Sign the user out everywhere. Their access token dies on the next request because users are re-read each time.
            await db.RefreshTokens.Where(t => t.UserId == id && t.RevokedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, now), ct);
            audit.Record(AuditActions.EmployeeDeactivated, actor.Id, actor.Username, newValue: user.Username);
        }
        else if (!wasActive && user.IsActive)
        {
            user.FailedLoginCount = 0;
            user.LockoutEndUtc = null;
            audit.Record(AuditActions.EmployeeActivated, actor.Id, actor.Username, newValue: user.Username);
        }

        audit.Record(AuditActions.EmployeeUpdated, actor.Id, actor.Username, oldValue: before,
            newValue: $"{user.DisplayName}; {user.Role}; active={user.IsActive}", reason: user.Username);
        await db.SaveChangesAsync(ct);
        return ToDto(user, now);
    }

    /// <summary>Sets a new password, clears any lockout and signs the employee out everywhere.</summary>
    public async Task<Result<bool>> ResetPasswordAsync(Guid id, ResetPasswordRequest request, CurrentUser actor, CancellationToken ct)
    {
        if (!SetupEndpoints.IsAcceptablePassword(request.NewPassword))
        {
            return ApiError.Invalid($"The password must be between {SetupEndpoints.MinPasswordLength} and {SetupEndpoints.MaxPasswordLength} characters.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return ApiError.NotFound("The employee does not exist.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        user.PasswordHash = hasher.Hash(request.NewPassword);
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        await db.RefreshTokens.Where(t => t.UserId == id && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, now), ct);
        audit.Record(AuditActions.EmployeePasswordReset, actor.Id, actor.Username, newValue: user.Username);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<List<PermissionSetting>> GetPermissionsAsync(CancellationToken ct)
    {
        var effective = await permissions.GetEffectiveAsync(UserRole.Employee, ct);
        return [.. PermissionPolicy.EmployeeConfigurable.Order()
            .Select(p => new PermissionSetting(p, effective.Contains(p), PermissionPolicy.EmployeeDefaults.Contains(p)))];
    }

    public async Task<Result<List<PermissionSetting>>> UpdatePermissionsAsync(UpdateEmployeePermissionsRequest request, CurrentUser actor, CancellationToken ct)
    {
        if (request.Permissions is null || request.Permissions.Keys.Any(p => !PermissionPolicy.EmployeeConfigurable.Contains(p)))
        {
            return ApiError.Invalid("Only the configurable employee permissions can be changed.");
        }

        var existing = await db.EmployeePermissionOverrides.ToDictionaryAsync(o => o.Permission, ct);
        var changes = new List<string>();
        foreach (var (permission, allowed) in request.Permissions)
        {
            var isDefault = PermissionPolicy.EmployeeDefaults.Contains(permission) == allowed;
            var current = existing.TryGetValue(permission, out var row) ? row.Allowed : PermissionPolicy.EmployeeDefaults.Contains(permission);
            if (current != allowed)
            {
                changes.Add($"{permission}={(allowed ? "on" : "off")}");
            }

            if (isDefault && row is not null)
            {
                db.EmployeePermissionOverrides.Remove(row);
            }
            else if (!isDefault && row is null)
            {
                db.EmployeePermissionOverrides.Add(new EmployeePermissionOverride { Permission = permission, Allowed = allowed });
            }
            else if (!isDefault)
            {
                row!.Allowed = allowed;
            }
        }

        if (changes.Count > 0)
        {
            audit.Record(AuditActions.PermissionsChanged, actor.Id, actor.Username, newValue: string.Join(", ", changes));
        }

        await db.SaveChangesAsync(ct);
        permissions.Invalidate();
        return await GetPermissionsAsync(ct);
    }

    private async Task<List<Guid>> ValidBranchIdsAsync(IReadOnlyList<Guid>? requested, CancellationToken ct)
    {
        var ids = (requested ?? []).Distinct().ToList();
        return ids.Count == 0 ? ids : await db.Branches.Where(b => ids.Contains(b.Id)).Select(b => b.Id).ToListAsync(ct);
    }

    private static EmployeeDto ToDto(User u, DateTime now) => new(
        u.Id, u.Username, u.DisplayName, u.Role, u.IsActive, u.LockoutEndUtc > now,
        [.. u.Branches.Select(b => b.BranchId)], u.CreatedAtUtc, u.LastLoginAtUtc);
}
