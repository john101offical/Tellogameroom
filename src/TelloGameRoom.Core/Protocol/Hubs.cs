using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;

namespace TelloGameRoom.Core.Protocol;

/// <summary>Messages the server pushes to a Machine app over <c>/hubs/machine</c>.</summary>
public interface IMachineClient
{
    Task SessionStateChanged(MachineSessionState state);

    Task ConfigChanged(MachineConfigDto config);

    Task ShowMessage(MachineMessageDto message);

    Task ExecuteCommand(MachineCommand command);

    /// <summary>Asks the machine to capture its screen and upload it to <c>POST /machines/me/screenshots/{requestId}</c>.</summary>
    Task CaptureScreenshot(string requestId);
}

/// <summary>Methods a Machine app can call on <c>/hubs/machine</c>.</summary>
public interface IMachineHub
{
    /// <summary>Returns the authoritative session state. Called after every (re)connect and periodically to correct drift.</summary>
    Task<MachineSessionState> Sync();

    Task<MachineConfigDto> GetConfig();
}

/// <summary>Messages the server pushes to dashboards over <c>/hubs/dashboard</c>.</summary>
public interface IDashboardClient
{
    Task MachineChanged(MachineStateDto machine);

    Task MachineRemoved(Guid branchId, Guid machineId);

    /// <summary>Pricing, settings or the on-duty list changed: fetch the branch state again.</summary>
    Task BranchChanged(Guid branchId);
}

/// <summary>Methods a dashboard can call on <c>/hubs/dashboard</c>.</summary>
public interface IDashboardHub
{
    /// <summary>Starts receiving updates for a branch and returns its current state.</summary>
    Task<BranchStateDto> Subscribe(Guid branchId);

    Task Unsubscribe(Guid branchId);
}
