using TelloGameRoom.Server.Data;
using TelloGameRoom.Server.Data.Entities;

namespace TelloGameRoom.Server.Services;

/// <summary>
/// Queues audit entries on the current <see cref="AppDbContext"/>. Entries are written by the caller's own
/// <c>SaveChanges</c>, so an action and its audit record always commit (or fail) together.
/// </summary>
public sealed class AuditService(AppDbContext db, TimeProvider time, IHttpContextAccessor http)
{
    public void Record(
        string action,
        Guid? userId = null,
        string? username = null,
        Guid? branchId = null,
        Guid? machineId = null,
        string? machineName = null,
        Guid? sessionId = null,
        string? oldValue = null,
        string? newValue = null,
        string? reason = null)
    {
        db.AuditLog.Add(new AuditLogEntry
        {
            AtUtc = time.GetUtcNow().UtcDateTime,
            Action = action,
            UserId = userId,
            Username = username,
            BranchId = branchId,
            MachineId = machineId,
            MachineName = machineName,
            SessionId = sessionId,
            OldValue = oldValue,
            NewValue = newValue,
            Reason = reason,
            IpAddress = http.HttpContext?.Connection.RemoteIpAddress?.ToString(),
        });
    }
}
