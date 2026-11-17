using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Security;

namespace TelloGameRoom.Server.Data.Entities;

/// <summary>Shop-wide settings. Exactly one row (Id = 1) exists after setup.</summary>
public sealed class ShopSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public required string ShopName { get; set; }
    public byte[]? Logo { get; set; }
    public string? LogoContentType { get; set; }
    public string DefaultLanguage { get; set; } = "en";
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>A physical shop location. Machines, sessions, shifts and pricing all belong to a branch.</summary>
public sealed class Branch
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>IANA time zone used for "business day" boundaries in reports.</summary>
    public string TimeZoneId { get; set; } = "America/Bogota";

    public long HourlyRateCents { get; set; }
    public string CurrencyCode { get; set; } = "COP";
    public string CurrencySymbol { get; set; } = "$";
    public int CurrencyDecimals { get; set; }
    public RoundingMode RoundingMode { get; set; } = RoundingMode.Nearest;

    /// <summary>Rounding step in cents. Default 10000 = round to the nearest COP 100.</summary>
    public long RoundingIncrementCents { get; set; } = 10_000;

    public int WarningThresholdSeconds { get; set; } = 300;

    /// <summary>Argon2id hash of the PIN that lets the owner close the Machine app.</summary>
    public string? AdminPinHash { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Owner-configured grant or revocation of a permission for the Employee role.</summary>
public sealed class EmployeePermissionOverride
{
    public Permission Permission { get; set; }
    public bool Allowed { get; set; }
}
