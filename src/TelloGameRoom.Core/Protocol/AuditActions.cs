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
}
