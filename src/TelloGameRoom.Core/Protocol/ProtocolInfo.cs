namespace TelloGameRoom.Core.Protocol;

/// <summary>
/// Wire-protocol constants shared by the server and every client. See docs/PROTOCOL.md.
/// </summary>
public static class ProtocolInfo
{
    /// <summary>
    /// Protocol version. Bump on any breaking change to REST payloads or hub messages.
    /// Clients send it in the <see cref="VersionHeader"/> header; the server rejects incompatible majors.
    /// </summary>
    public const int Version = 1;

    public const string VersionHeader = "X-Tello-Protocol";
    public const string ApiPrefix = "/api/v1";
    public const string DashboardHubPath = "/hubs/dashboard";
    public const string MachineHubPath = "/hubs/machine";

    /// <summary>Authorization scheme used by machine apps: <c>Authorization: Machine {machineId}.{secret}</c>.</summary>
    public const string MachineAuthScheme = "Machine";
}

/// <summary>Stable machine-readable error codes returned in problem responses (the <c>code</c> extension).</summary>
public static class ErrorCodes
{
    public const string Validation = "validation_failed";
    public const string InvalidCredentials = "invalid_credentials";
    public const string AccountLocked = "account_locked";
    public const string AccountDisabled = "account_disabled";
    public const string InvalidRefreshToken = "invalid_refresh_token";
    public const string SetupAlreadyCompleted = "setup_already_completed";
    public const string SetupRequired = "setup_required";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string InvalidPairingCode = "invalid_pairing_code";
    public const string ProtocolMismatch = "protocol_mismatch";
    public const string RateLimited = "rate_limited";
}
