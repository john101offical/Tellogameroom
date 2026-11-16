using TelloGameRoom.Core.Enums;

namespace TelloGameRoom.Server.Data.Entities;

public sealed class User
{
    public Guid Id { get; set; }
    public required string Username { get; set; }

    /// <summary>Upper-invariant username; unique. Logins are case-insensitive.</summary>
    public required string NormalizedUsername { get; set; }

    public required string DisplayName { get; set; }
    public required string PasswordHash { get; set; }
    public UserRole Role { get; set; }
    public bool IsActive { get; set; } = true;
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }

    public List<UserBranch> Branches { get; } = [];
}

/// <summary>Assigns an employee to a branch. Owners implicitly have access to every branch.</summary>
public sealed class UserBranch
{
    public Guid UserId { get; set; }
    public Guid BranchId { get; set; }
    public User? User { get; set; }
    public Branch? Branch { get; set; }
}

/// <summary>
/// A rotating refresh token. Only the SHA-256 hash is stored. Tokens issued from the same login share a
/// family; presenting an already-used token revokes the whole family (theft detection).
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public Guid FamilyId { get; set; }
    public required string TokenHash { get; set; }
    public string? DeviceName { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
}
