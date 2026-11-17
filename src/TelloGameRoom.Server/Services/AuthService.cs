using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Server.Data;
using TelloGameRoom.Server.Data.Entities;
using TelloGameRoom.Server.Infrastructure;
using TelloGameRoom.Server.Security;

namespace TelloGameRoom.Server.Services;

public enum AuthFailure
{
    None = 0,
    InvalidCredentials,
    AccountLocked,
    AccountDisabled,
    InvalidRefreshToken,
}

public sealed record AuthOutcome(AuthResponse? Response, AuthFailure Failure, DateTime? LockedUntilUtc = null)
{
    public bool Succeeded => Response is not null;

    public static AuthOutcome Fail(AuthFailure failure, DateTime? lockedUntil = null) => new(null, failure, lockedUntil);
}

public sealed class AuthService(
    AppDbContext db,
    IPasswordHasher hasher,
    TokenService tokens,
    PermissionService permissions,
    AuditService audit,
    TimeProvider time,
    IOptions<SecurityOptions> security,
    IOptions<JwtOptions> jwt)
{
    public static string Normalize(string username) => username.Trim().ToUpperInvariant();

    public async Task<AuthOutcome> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var normalized = Normalize(request.Username ?? string.Empty);
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, ct);

        if (user is null)
        {
            hasher.VerifyDummy(request.Password ?? string.Empty);
            audit.Record(AuditActions.LoginFailed, username: Truncate(request.Username, 32), reason: "unknown_user");
            await db.SaveChangesAsync(ct);
            return AuthOutcome.Fail(AuthFailure.InvalidCredentials);
        }

        if (user.LockoutEndUtc is { } lockedUntil && lockedUntil > now)
        {
            audit.Record(AuditActions.LoginFailed, user.Id, user.Username, reason: "locked_out");
            await db.SaveChangesAsync(ct);
            return AuthOutcome.Fail(AuthFailure.AccountLocked, lockedUntil);
        }

        if (!hasher.Verify(request.Password ?? string.Empty, user.PasswordHash, out var needsRehash))
        {
            user.FailedLoginCount++;
            audit.Record(AuditActions.LoginFailed, user.Id, user.Username, reason: "wrong_password");
            if (user.FailedLoginCount >= security.Value.MaxFailedLogins)
            {
                user.FailedLoginCount = 0;
                user.LockoutEndUtc = now.AddMinutes(security.Value.LockoutMinutes);
                audit.Record(AuditActions.AccountLockedOut, user.Id, user.Username);
                await db.SaveChangesAsync(ct);
                return AuthOutcome.Fail(AuthFailure.AccountLocked, user.LockoutEndUtc);
            }

            await db.SaveChangesAsync(ct);
            return AuthOutcome.Fail(AuthFailure.InvalidCredentials);
        }

        if (!user.IsActive)
        {
            audit.Record(AuditActions.LoginFailed, user.Id, user.Username, reason: "disabled");
            await db.SaveChangesAsync(ct);
            return AuthOutcome.Fail(AuthFailure.AccountDisabled);
        }

        if (needsRehash)
        {
            user.PasswordHash = hasher.Hash(request.Password!);
        }

        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        user.LastLoginAtUtc = now;
        audit.Record(AuditActions.LoginSucceeded, user.Id, user.Username, newValue: Truncate(request.DeviceName, 100));

        var response = await IssueAsync(user, Guid.CreateVersion7(), Truncate(request.DeviceName, 100), ct);
        await db.SaveChangesAsync(ct);
        return new AuthOutcome(response, AuthFailure.None);
    }

    /// <summary>Rotates a refresh token. Presenting a token that was already used revokes its whole family.</summary>
    public async Task<AuthOutcome> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var hash = SecretTokens.Hash(refreshToken ?? string.Empty);
        var stored = await db.RefreshTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored?.User is null)
        {
            return AuthOutcome.Fail(AuthFailure.InvalidRefreshToken);
        }

        if (stored.RevokedAtUtc is not null)
        {
            // A rotated token came back: either a replay by an attacker or by the legitimate client. Kill the family.
            await db.RefreshTokens
                .Where(t => t.FamilyId == stored.FamilyId && t.RevokedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, now), ct);
            audit.Record(AuditActions.RefreshTokenReuse, stored.UserId, stored.User.Username);
            await db.SaveChangesAsync(ct);
            return AuthOutcome.Fail(AuthFailure.InvalidRefreshToken);
        }

        if (stored.ExpiresAtUtc <= now)
        {
            return AuthOutcome.Fail(AuthFailure.InvalidRefreshToken);
        }

        if (!stored.User.IsActive)
        {
            return AuthOutcome.Fail(AuthFailure.AccountDisabled);
        }

        stored.RevokedAtUtc = now;
        var response = await IssueAsync(stored.User, stored.FamilyId, stored.DeviceName, ct);
        await db.SaveChangesAsync(ct);
        return new AuthOutcome(response, AuthFailure.None);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var hash = SecretTokens.Hash(refreshToken ?? string.Empty);
        var stored = await db.RefreshTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null)
        {
            return;
        }

        await db.RefreshTokens
            .Where(t => t.FamilyId == stored.FamilyId && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, now), ct);
        audit.Record(AuditActions.Logout, stored.UserId, stored.User?.Username);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Changes the caller's password and signs out every other device.</summary>
    public async Task<bool> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct)
    {
        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        if (!hasher.Verify(request.CurrentPassword ?? string.Empty, user.PasswordHash, out _))
        {
            return false;
        }

        var now = time.GetUtcNow().UtcDateTime;
        user.PasswordHash = hasher.Hash(request.NewPassword);
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, now), ct);
        audit.Record(AuditActions.PasswordChanged, user.Id, user.Username);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<UserInfo> BuildUserInfoAsync(User user, CancellationToken ct)
    {
        var branches = user.Role == UserRole.Owner
            ? db.Branches.Where(b => b.IsActive)
            : db.UserBranches.Where(ub => ub.UserId == user.Id && ub.Branch!.IsActive).Select(ub => ub.Branch!);
        var branchList = await branches.OrderBy(b => b.Name).Select(b => new BranchSummary(b.Id, b.Name)).ToListAsync(ct);
        var effective = await permissions.GetEffectiveAsync(user.Role, ct);
        return new UserInfo(user.Id, user.Username, user.DisplayName, user.Role, [.. effective.Order()], branchList);
    }

    /// <summary>Queues a new refresh token and builds the response. The caller saves.</summary>
    internal async Task<AuthResponse> IssueAsync(User user, Guid familyId, string? deviceName, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var refresh = SecretTokens.NewToken();
        var refreshExpires = now.AddDays(jwt.Value.RefreshTokenDays);
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            FamilyId = familyId,
            TokenHash = SecretTokens.Hash(refresh),
            DeviceName = deviceName,
            CreatedAtUtc = now,
            ExpiresAtUtc = refreshExpires,
        });

        var (access, accessExpires) = tokens.CreateAccessToken(user);
        return new AuthResponse(access, accessExpires, refresh, refreshExpires, await BuildUserInfoAsync(user, ct));
    }

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
