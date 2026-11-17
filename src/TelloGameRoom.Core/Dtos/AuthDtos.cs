using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Security;

namespace TelloGameRoom.Core.Dtos;

/// <summary>Whether the server still needs its first-run setup.</summary>
public sealed record SetupStatusResponse(bool SetupRequired, int ProtocolVersion, string ServerVersion);

/// <summary>First-run setup: creates the owner account, the shop and its first branch.</summary>
public sealed record SetupRequest(
    string ShopName,
    string BranchName,
    string OwnerUsername,
    string OwnerDisplayName,
    string OwnerPassword,
    decimal? HourlyRate = null);

public sealed record LoginRequest(string Username, string Password, string? DeviceName = null);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LogoutRequest(string RefreshToken);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record BranchSummary(Guid Id, string Name);

public sealed record UserInfo(
    Guid Id,
    string Username,
    string DisplayName,
    UserRole Role,
    IReadOnlyList<Permission> Permissions,
    IReadOnlyList<BranchSummary> Branches);

/// <summary>Returned by setup, login and refresh.</summary>
public sealed record AuthResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc,
    UserInfo User);
