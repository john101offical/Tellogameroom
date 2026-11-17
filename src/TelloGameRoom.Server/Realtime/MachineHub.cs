using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Server.Data;
using TelloGameRoom.Server.Security;
using TelloGameRoom.Server.Services;

namespace TelloGameRoom.Server.Realtime;

[Authorize(Policy = Policies.Machine)]
public sealed class MachineHub(
    MachinePresence presence,
    StateService state,
    AppDbContext db,
    TimeProvider time,
    IHubContext<DashboardHub, IDashboardClient> dashboards) : Hub<IMachineClient>, IMachineHub
{
    private Guid MachineId => Context.User!.GetMachineId()!.Value;

    private Guid BranchId => Guid.Parse(Context.User!.FindFirst(TelloClaims.BranchId)!.Value);

    public override async Task OnConnectedAsync()
    {
        var machineId = MachineId;
        await Groups.AddToGroupAsync(Context.ConnectionId, Realtime.Groups.Machine(machineId));
        await Groups.AddToGroupAsync(Context.ConnectionId, Realtime.Groups.BranchMachines(BranchId));
        presence.Connected(machineId, Context.ConnectionId);

        var http = Context.GetHttpContext();
        var appVersion = http?.Request.Query["appVersion"].ToString();
        var now = time.GetUtcNow().UtcDateTime;
        await db.Machines.Where(m => m.Id == machineId).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.LastSeenAtUtc, now)
            .SetProperty(m => m.AppVersion, m => string.IsNullOrEmpty(appVersion) ? m.AppVersion : appVersion.Length > 32 ? appVersion.Substring(0, 32) : appVersion));

        await PushToDashboardsAsync(machineId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var machineId = MachineId;
        if (presence.Disconnected(machineId, Context.ConnectionId))
        {
            var now = time.GetUtcNow().UtcDateTime;
            await db.Machines.Where(m => m.Id == machineId).ExecuteUpdateAsync(s => s.SetProperty(m => m.LastSeenAtUtc, now));
            await PushToDashboardsAsync(machineId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public Task<MachineSessionState> Sync() => state.GetMachineSessionStateAsync(MachineId, Context.ConnectionAborted);

    public async Task<MachineConfigDto> GetConfig() =>
        await state.GetMachineConfigAsync(MachineId, Context.ConnectionAborted) ?? throw new HubException("Machine not found.");

    private async Task PushToDashboardsAsync(Guid machineId)
    {
        if (await state.GetMachineStateAsync(machineId, CancellationToken.None) is { } dto)
        {
            await dashboards.Clients.Group(Realtime.Groups.Branch(dto.BranchId)).MachineChanged(dto);
        }
    }
}
