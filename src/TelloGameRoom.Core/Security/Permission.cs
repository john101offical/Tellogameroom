namespace TelloGameRoom.Core.Security;

/// <summary>
/// Every action the server authorizes. Numeric values are persisted in permission overrides; never renumber.
/// </summary>
public enum Permission
{
    // Machines and sessions
    MachinesView = 100,
    MachinesManage = 101,
    SessionsStart = 110,
    SessionsAddTime = 111,
    SessionsRemoveTime = 112,
    SessionsPauseResume = 113,
    SessionsStop = 114,
    SessionsCancelPaid = 115,
    SessionsTransfer = 116,
    SessionsFreeTime = 117,

    // Remote control
    RemoteMessage = 200,
    RemoteLock = 201,
    RemotePower = 202,
    RemoteScreenshot = 203,

    // Shifts
    ShiftsOwn = 300,
    ShiftsViewAll = 301,

    // Back office
    ReportsView = 400,
    ReportsExport = 401,
    AuditView = 410,
    EmployeesManage = 420,
    SettingsManage = 430,
    PricingManage = 431,
    BranchesManage = 440,
    BackupManage = 450,
}
