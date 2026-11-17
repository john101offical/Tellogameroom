using System.Collections.Concurrent;

namespace TelloGameRoom.Server.Realtime;

/// <summary>Tracks which machines currently have a live hub connection.</summary>
public sealed class MachinePresence
{
    private readonly ConcurrentDictionary<Guid, HashSet<string>> _connections = new();

    /// <returns>True when the machine just came online (first connection).</returns>
    public bool Connected(Guid machineId, string connectionId)
    {
        var set = _connections.GetOrAdd(machineId, _ => []);
        lock (set)
        {
            set.Add(connectionId);
            return set.Count == 1;
        }
    }

    /// <returns>True when the machine just went offline (last connection closed).</returns>
    public bool Disconnected(Guid machineId, string connectionId)
    {
        if (!_connections.TryGetValue(machineId, out var set))
        {
            return false;
        }

        lock (set)
        {
            return set.Remove(connectionId) && set.Count == 0;
        }
    }

    public bool IsOnline(Guid machineId)
    {
        if (!_connections.TryGetValue(machineId, out var set))
        {
            return false;
        }

        lock (set)
        {
            return set.Count > 0;
        }
    }
}
