namespace TelloGameRoom.Core.Dtos;

/// <summary>Asks the server for a one-time pairing code for a new machine in a branch.</summary>
public sealed record CreatePairingCodeRequest(string MachineName, Guid? GroupId = null);

public sealed record PairingCodeResponse(string Code, DateTime ExpiresAtUtc, string MachineName);

/// <summary>Sent by a machine app that has not been paired yet.</summary>
public sealed record PairMachineRequest(string Code, string? HostName = null, string? OsVersion = null, string? AppVersion = null);

/// <summary>
/// The machine's identity. <see cref="Token"/> is shown exactly once and must be stored protected on the machine.
/// </summary>
public sealed record PairMachineResponse(Guid MachineId, string MachineName, Guid BranchId, string BranchName, string ShopName, string Token);

public sealed record MachineSummary(
    Guid Id,
    Guid BranchId,
    string Name,
    Guid? GroupId,
    bool IsEnabled,
    DateTime CreatedAtUtc,
    DateTime? LastSeenAtUtc);

/// <summary>What a machine app learns about itself when it authenticates.</summary>
public sealed record MachineIdentity(Guid MachineId, string MachineName, Guid BranchId, string BranchName, string ShopName, DateTime ServerTimeUtc);
