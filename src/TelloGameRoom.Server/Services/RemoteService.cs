using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Server.Data;
using TelloGameRoom.Server.Infrastructure;
using TelloGameRoom.Server.Realtime;
using TelloGameRoom.Server.Security;

namespace TelloGameRoom.Server.Services;

/// <summary>Matches screenshot requests sent to machines with the images they upload.</summary>
public sealed class ScreenshotBroker
{
    private readonly ConcurrentDictionary<string, (Guid MachineId, TaskCompletionSource<byte[]> Completion)> _pending = new();

    public (string RequestId, Task<byte[]> Image) Create(Guid machineId)
    {
        var id = SecretTokens.NewToken();
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = (machineId, tcs);
        return (id, tcs.Task);
    }

    /// <returns>False when the request is unknown, expired, or belongs to another machine.</returns>
    public bool Complete(string requestId, Guid machineId, byte[] image)
    {
        if (_pending.TryGetValue(requestId, out var entry) && entry.MachineId == machineId && _pending.TryRemove(requestId, out _))
        {
            return entry.Completion.TrySetResult(image);
        }

        return false;
    }

    public void Abandon(string requestId) => _pending.TryRemove(requestId, out _);
}

/// <summary>Remote control: messages, lock and power commands, screenshots. Everything here is audited.</summary>
public sealed class RemoteService(
    AppDbContext db,
    AuditService audit,
    MachinePresence presence,
    ScreenshotBroker screenshots,
    SessionService sessions,
    IHubContext<MachineHub, IMachineClient> hub,
    TimeProvider time)
{
    public const int MaxMessageLength = 300;
    private static readonly TimeSpan ScreenshotTimeout = TimeSpan.FromSeconds(15);

    public async Task<Result<bool>> SendMessageAsync(Guid branchId, Guid machineId, string text, CurrentUser user, CancellationToken ct)
    {
        var machine = await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == machineId && m.BranchId == branchId, ct);
        if (machine is null)
        {
            return ApiError.NotFound("The machine does not exist.");
        }

        if (!presence.IsOnline(machineId))
        {
            return Offline();
        }

        await hub.Clients.Group(Groups.Machine(machineId)).ShowMessage(new MachineMessageDto(text, time.GetUtcNow().UtcDateTime));
        audit.Record(AuditActions.RemoteMessage, user.Id, user.Username, branchId, machineId, machine.Name, newValue: text);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<BulkResult> SendMessageToAllAsync(Guid branchId, string text, CurrentUser user, CancellationToken ct)
    {
        var counts = await CountAsync(branchId, ct);
        await hub.Clients.Group(Groups.BranchMachines(branchId)).ShowMessage(new MachineMessageDto(text, time.GetUtcNow().UtcDateTime));
        audit.Record(AuditActions.RemoteMessageAll, user.Id, user.Username, branchId, newValue: text);
        await db.SaveChangesAsync(ct);
        return counts;
    }

    public async Task<Result<bool>> SendCommandAsync(Guid branchId, Guid machineId, MachineCommand command, CurrentUser user, CancellationToken ct)
    {
        var machine = await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == machineId && m.BranchId == branchId, ct);
        if (machine is null)
        {
            return ApiError.NotFound("The machine does not exist.");
        }

        if (!presence.IsOnline(machineId))
        {
            return Offline();
        }

        if (command == MachineCommand.Lock)
        {
            await PauseIfRunningAsync(branchId, machineId, user, ct);
        }

        await hub.Clients.Group(Groups.Machine(machineId)).ExecuteCommand(command);
        audit.Record(AuditActions.RemoteCommand, user.Id, user.Username, branchId, machineId, machine.Name, newValue: command.ToString());
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<BulkResult> SendCommandToAllAsync(Guid branchId, MachineCommand command, CurrentUser user, CancellationToken ct)
    {
        var counts = await CountAsync(branchId, ct);
        if (command == MachineCommand.Lock)
        {
            var running = await db.Sessions.Where(s => s.BranchId == branchId && s.Status == SessionStatus.Active).Select(s => s.MachineId).ToListAsync(ct);
            foreach (var machineId in running)
            {
                await PauseIfRunningAsync(branchId, machineId, user, ct);
            }
        }

        await hub.Clients.Group(Groups.BranchMachines(branchId)).ExecuteCommand(command);
        audit.Record(AuditActions.RemoteCommandAll, user.Id, user.Username, branchId, newValue: command.ToString());
        await db.SaveChangesAsync(ct);
        return counts;
    }

    /// <summary>Asks the machine for a screenshot and waits for the upload.</summary>
    public async Task<Result<byte[]>> CaptureScreenshotAsync(Guid branchId, Guid machineId, CurrentUser user, CancellationToken ct)
    {
        var machine = await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == machineId && m.BranchId == branchId, ct);
        if (machine is null)
        {
            return ApiError.NotFound("The machine does not exist.");
        }

        if (!presence.IsOnline(machineId))
        {
            return Offline();
        }

        // Logged before the capture: an attempt is recorded even if the machine never answers.
        audit.Record(AuditActions.RemoteScreenshot, user.Id, user.Username, branchId, machineId, machine.Name);
        await db.SaveChangesAsync(ct);

        var (requestId, image) = screenshots.Create(machineId);
        try
        {
            await hub.Clients.Group(Groups.Machine(machineId)).CaptureScreenshot(requestId);
            return await image.WaitAsync(ScreenshotTimeout, time, ct);
        }
        catch (TimeoutException)
        {
            return new ApiError(StatusCodes.Status504GatewayTimeout, ErrorCodes.Timeout, "The machine did not send a screenshot in time.");
        }
        finally
        {
            screenshots.Abandon(requestId);
        }
    }

    private async Task PauseIfRunningAsync(Guid branchId, Guid machineId, CurrentUser user, CancellationToken ct)
    {
        // A remote lock on a running session pauses it so the customer does not lose paid time.
        if (await db.Sessions.AnyAsync(s => s.MachineId == machineId && s.Status == SessionStatus.Active, ct))
        {
            await sessions.PauseAsync(branchId, machineId, user, ct, "remote_lock");
        }
    }

    private async Task<BulkResult> CountAsync(Guid branchId, CancellationToken ct)
    {
        var ids = await db.Machines.Where(m => m.BranchId == branchId && m.IsEnabled).Select(m => m.Id).ToListAsync(ct);
        var online = ids.Count(presence.IsOnline);
        return new BulkResult(online, ids.Count - online);
    }

    private static ApiError Offline() => ApiError.Conflict(ErrorCodes.MachineOffline, "This machine is offline.");
}
