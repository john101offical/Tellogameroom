namespace TelloGameRoom.Server.Data.Entities;

/// <summary>Optional pricing group (e.g. "VIP"). A null rate means the branch rate applies.</summary>
public sealed class MachineGroup
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public Branch? Branch { get; set; }
    public required string Name { get; set; }
    public long? HourlyRateCents { get; set; }
}

public sealed class Machine
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public Branch? Branch { get; set; }
    public required string Name { get; set; }
    public Guid? GroupId { get; set; }
    public MachineGroup? Group { get; set; }

    /// <summary>SHA-256 hash of the machine's 256-bit secret.</summary>
    public required string TokenHash { get; set; }

    public bool IsEnabled { get; set; } = true;
    public string? HostName { get; set; }
    public string? OsVersion { get; set; }
    public string? AppVersion { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
}

/// <summary>One-time code that lets a new machine obtain its identity. Only the hash is stored.</summary>
public sealed class PairingCode
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public required string CodeHash { get; set; }
    public required string MachineName { get; set; }
    public Guid? GroupId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? UsedAtUtc { get; set; }
    public Guid? MachineId { get; set; }
}
