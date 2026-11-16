using Microsoft.EntityFrameworkCore;
using TelloGameRoom.Core.Billing;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Server.Data;
using TelloGameRoom.Server.Data.Entities;
using TelloGameRoom.Server.Security;

namespace TelloGameRoom.Server.Services;

/// <summary>First-run setup: creates the shop, its first branch and the owner account, exactly once.</summary>
public sealed class SetupService(AppDbContext db, IPasswordHasher hasher, AuthService auth, AuditService audit, TimeProvider time)
{
    // Serializes concurrent setup attempts inside this process; the unique indexes cover everything else.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<bool> IsRequiredAsync(CancellationToken ct) => !await db.Users.AnyAsync(ct);

    /// <returns>Null when setup has already been completed.</returns>
    public async Task<AuthResponse?> RunAsync(SetupRequest request, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (await db.Users.AnyAsync(ct))
            {
                return null;
            }

            var now = time.GetUtcNow().UtcDateTime;
            db.ShopSettings.Add(new ShopSettings { ShopName = request.ShopName.Trim(), UpdatedAtUtc = now });

            var branch = new Branch
            {
                Id = Guid.CreateVersion7(),
                Name = request.BranchName.Trim(),
                HourlyRateCents = PriceCalculator.ToCents(request.HourlyRate ?? 0m),
                CreatedAtUtc = now,
            };
            db.Branches.Add(branch);

            var owner = new User
            {
                Id = Guid.CreateVersion7(),
                Username = request.OwnerUsername.Trim(),
                NormalizedUsername = AuthService.Normalize(request.OwnerUsername),
                DisplayName = request.OwnerDisplayName.Trim(),
                PasswordHash = hasher.Hash(request.OwnerPassword),
                Role = UserRole.Owner,
                CreatedAtUtc = now,
                LastLoginAtUtc = now,
            };
            db.Users.Add(owner);
            audit.Record(AuditActions.SetupCompleted, owner.Id, owner.Username, branch.Id, newValue: request.ShopName.Trim());

            // Save first so the branch exists when the user info is built.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            var response = await auth.IssueAsync(owner, Guid.CreateVersion7(), "setup", ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return response;
        }
        finally
        {
            Gate.Release();
        }
    }
}
