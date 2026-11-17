using Microsoft.AspNetCore.SignalR;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Server.Services;

namespace TelloGameRoom.Server.Realtime;

/// <summary>Pushes fresh state to the dashboards of a branch and to the affected machine. Call after the transaction commits.</summary>
public sealed class RealtimeNotifier(
    IHubContext<DashboardHub, IDashboardClient> dashboards,
    IHubContext<MachineHub, IMachineClient> machines,
    StateService state,
    ILogger<RealtimeNotifier> logger)
{
    public async Task MachineChangedAsync(Guid machineId, CancellationToken ct = default)
    {
        try
        {
            var dto = await state.GetMachineStateAsync(machineId, ct);
            if (dto is null)
            {
                return;
            }

            await Task.WhenAll(
                dashboards.Clients.Group(Groups.Branch(dto.BranchId)).MachineChanged(dto),
                machines.Clients.Group(Groups.Machine(machineId)).SessionStateChanged(await state.GetMachineSessionStateAsync(machineId, ct)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The change is already committed. Clients that missed the push resync on their own.
            logger.LogWarning(ex, "Failed to push state for machine {MachineId}", machineId);
        }
    }

    public Task BranchChangedAsync(Guid branchId) => dashboards.Clients.Group(Groups.Branch(branchId)).BranchChanged(branchId);

    public Task MachineRemovedAsync(Guid branchId, Guid machineId) =>
        dashboards.Clients.Group(Groups.Branch(branchId)).MachineRemoved(branchId, machineId);

    /// <summary>Re-sends branding and settings to one machine.</summary>
    public async Task MachineConfigChangedAsync(Guid machineId, CancellationToken ct = default)
    {
        if (await state.GetMachineConfigAsync(machineId, ct) is { } config)
        {
            await machines.Clients.Group(Groups.Machine(machineId)).ConfigChanged(config);
        }
    }
}
