using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Security;

namespace TelloGameRoom.Core.Dtos;

// ---- Shifts ----------------------------------------------------------------------------------------------------

public sealed record OpenShiftRequest(long OpeningCashCents);

public sealed record CloseShiftRequest(long CountedCashCents, string? Notes = null);

/// <summary>
/// A shift with its cash reconciliation. Expected cash = opening cash + sales - refunds.
/// <see cref="DifferenceCents"/> = counted - expected: negative means cash is missing.
/// </summary>
public sealed record ShiftDto(
    Guid Id,
    Guid BranchId,
    Guid UserId,
    string UserDisplayName,
    DateTime OpenedAtUtc,
    DateTime? ClosedAtUtc,
    long OpeningCashCents,
    long SalesCents,
    long RefundsCents,
    long ExpectedCashCents,
    long? CountedCashCents,
    long? DifferenceCents,
    int SessionsStarted,
    string? Notes);

// ---- Employees -------------------------------------------------------------------------------------------------

public sealed record EmployeeDto(
    Guid Id,
    string Username,
    string DisplayName,
    UserRole Role,
    bool IsActive,
    bool IsLockedOut,
    IReadOnlyList<Guid> BranchIds,
    DateTime CreatedAtUtc,
    DateTime? LastLoginAtUtc);

public sealed record CreateEmployeeRequest(string Username, string DisplayName, string Password, UserRole Role, IReadOnlyList<Guid> BranchIds);

public sealed record UpdateEmployeeRequest(string DisplayName, UserRole Role, bool IsActive, IReadOnlyList<Guid> BranchIds);

public sealed record ResetPasswordRequest(string NewPassword);

/// <param name="Allowed">Whether employees currently have the permission.</param>
/// <param name="DefaultAllowed">Whether they have it out of the box.</param>
public sealed record PermissionSetting(Permission Permission, bool Allowed, bool DefaultAllowed);

public sealed record UpdateEmployeePermissionsRequest(IReadOnlyDictionary<Permission, bool> Permissions);

// ---- Audit -----------------------------------------------------------------------------------------------------

public sealed record AuditEntryDto(
    long Id,
    DateTime AtUtc,
    string Action,
    Guid? BranchId,
    Guid? UserId,
    string? Username,
    Guid? MachineId,
    string? MachineName,
    Guid? SessionId,
    string? OldValue,
    string? NewValue,
    string? Reason,
    string? IpAddress);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

// ---- Reports ---------------------------------------------------------------------------------------------------

public sealed record DayPoint(DateOnly Date, long IncomeCents, int Sessions, long SecondsSold);

public sealed record NamedTotal(Guid? Id, string Name, long IncomeCents, int Sessions, long SecondsSold);

public sealed record HourPoint(int Hour, int Sessions, long IncomeCents);

/// <summary>Income report for a branch between two local dates (inclusive), in the branch's time zone.</summary>
public sealed record ReportDto(
    Guid BranchId,
    string BranchName,
    DateOnly From,
    DateOnly To,
    string TimeZoneId,
    long IncomeCents,
    long SalesCents,
    long RefundsCents,
    int Sessions,
    int FreeSessions,
    long SecondsSold,
    IReadOnlyList<DayPoint> ByDay,
    IReadOnlyList<NamedTotal> ByEmployee,
    IReadOnlyList<NamedTotal> ByMachine,
    IReadOnlyList<HourPoint> ByHour);

// ---- Settings --------------------------------------------------------------------------------------------------

public sealed record ShopSettingsDto(string ShopName, string DefaultLanguage, string? LogoVersion);

public sealed record UpdateShopSettingsRequest(string ShopName, string DefaultLanguage);

public sealed record BranchSettingsDto(
    Guid Id,
    string Name,
    bool IsActive,
    string TimeZoneId,
    long HourlyRateCents,
    string CurrencyCode,
    string CurrencySymbol,
    int CurrencyDecimals,
    RoundingMode RoundingMode,
    long RoundingIncrementCents,
    int WarningThresholdSeconds,
    bool HasAdminPin);

/// <param name="NewAdminPin">Null leaves the PIN unchanged; an empty string removes it; otherwise 4 to 12 digits.</param>
public sealed record UpdateBranchSettingsRequest(
    string Name,
    bool IsActive,
    string TimeZoneId,
    long HourlyRateCents,
    string CurrencyCode,
    string CurrencySymbol,
    int CurrencyDecimals,
    RoundingMode RoundingMode,
    long RoundingIncrementCents,
    int WarningThresholdSeconds,
    string? NewAdminPin = null);

public sealed record CreateBranchRequest(string Name);

public sealed record MachineGroupDto(Guid Id, string Name, long? HourlyRateCents);

public sealed record UpsertMachineGroupRequest(string Name, long? HourlyRateCents);

public sealed record UpdateMachineRequest(string Name, Guid? GroupId, bool IsEnabled);
