namespace TelloGameRoom.Server.Realtime;

/// <summary>SignalR group names.</summary>
public static class Groups
{
    public static string Branch(Guid branchId) => $"branch:{branchId:N}";

    public static string Machine(Guid machineId) => $"machine:{machineId:N}";

    public static string BranchMachines(Guid branchId) => $"branch-machines:{branchId:N}";
}
