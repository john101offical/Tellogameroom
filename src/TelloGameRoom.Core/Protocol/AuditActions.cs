namespace TelloGameRoom.Core.Protocol;

/// <summary>Stable identifiers for audited actions. Clients map them to localized labels.</summary>
public static class AuditActions
{
    public const string SetupCompleted = "setup.completed";
    public const string LoginSucceeded = "auth.login";
    public const string LoginFailed = "auth.login_failed";
    public const string AccountLockedOut = "auth.lockout";
    public const string Logout = "auth.logout";
    public const string RefreshTokenReuse = "auth.refresh_reuse";
    public const string PasswordChanged = "auth.password_changed";
    public const string PairingCodeCreated = "machine.pairing_code_created";
    public const string MachinePaired = "machine.paired";
    public const string MachineUpdated = "machine.updated";
    public const string MachineDeleted = "machine.deleted";
    public const string MachineTokenReset = "machine.token_reset";
    public const string SessionStarted = "session.started";
    public const string SessionFreeStarted = "session.free_started";
    public const string SessionTimeAdded = "session.time_added";
    public const string SessionTimeRemoved = "session.time_removed";
    public const string SessionPaused = "session.paused";
    public const string SessionResumed = "session.resumed";
    public const string SessionStopped = "session.stopped";
    public const string SessionCancelled = "session.cancelled";
    public const string SessionTransferred = "session.transferred";
    public const string SessionExpired = "session.expired";
    public const string RemoteCommand = "remote.command";
    public const string RemoteCommandAll = "remote.command_all";
    public const string RemoteMessage = "remote.message";
    public const string RemoteMessageAll = "remote.message_all";
    public const string RemoteScreenshot = "remote.screenshot";
    public const string EmployeeCreated = "employee.created";
    public const string EmployeeUpdated = "employee.updated";
    public const string EmployeeDeactivated = "employee.deactivated";
    public const string EmployeeActivated = "employee.activated";
    public const string EmployeePasswordReset = "employee.password_reset";
    public const string PermissionsChanged = "settings.permissions_changed";
    public const string ShiftOpened = "shift.opened";
    public const string ShiftClosed = "shift.closed";
    public const string PriceChanged = "settings.price_changed";
    public const string SettingsChanged = "settings.changed";
    public const string AdminPinChanged = "settings.admin_pin_changed";
    public const string BranchCreated = "branch.created";
    public const string BranchUpdated = "branch.updated";
    public const string GroupChanged = "machine_group.changed";
    public const string BackupCreated = "backup.created";
    public const string BackupRestored = "backup.restored";
    public const string ReportExported = "report.exported";
    public const string AuditExported = "audit.exported";
}
