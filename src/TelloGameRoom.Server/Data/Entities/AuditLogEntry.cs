namespace TelloGameRoom.Server.Data.Entities;

/// <summary>
/// Append-only record of a sensitive action. Names are denormalized on purpose so the log stays readable
/// even after a user or machine is renamed or removed. Rows can never be updated or deleted.
/// </summary>
public sealed class AuditLogEntry
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; }
    public required string Action { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? UserId { get; set; }
    public string? Username { get; set; }
    public Guid? MachineId { get; set; }
    public string? MachineName { get; set; }
    public Guid? SessionId { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? Reason { get; set; }
    public string? IpAddress { get; set; }
}
