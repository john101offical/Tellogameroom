namespace TelloGameRoom.Core.Enums;

/// <summary>Staff roles. Numeric values are persisted; never renumber.</summary>
public enum UserRole
{
    Employee = 1,
    Owner = 2,
}

/// <summary>Lifecycle of a paid (or free) session on a machine.</summary>
public enum SessionStatus
{
    Active = 1,
    Paused = 2,
    Ended = 3,
}

/// <summary>Why a session ended.</summary>
public enum SessionEndReason
{
    TimeExpired = 1,
    StoppedByStaff = 2,
    Cancelled = 3,
}

/// <summary>Ledger entry types. Income is always the sum of the amount deltas of these events.</summary>
public enum SessionEventType
{
    Started = 1,
    TimeAdded = 2,
    TimeRemoved = 3,
    Paused = 4,
    Resumed = 5,
    Stopped = 6,
    Expired = 7,
    Transferred = 8,
    Cancelled = 9,
}

/// <summary>What the dashboard shows for a machine. Derived by the server, never stored.</summary>
public enum MachineStatus
{
    Offline = 0,
    Free = 1,
    InUse = 2,
    Paused = 3,
}

/// <summary>How the amount due is rounded before it is shown to staff.</summary>
public enum RoundingMode
{
    None = 0,
    Nearest = 1,
    Up = 2,
    Down = 3,
}

/// <summary>Remote power and lock commands a dashboard can send to a machine.</summary>
public enum MachineCommand
{
    Lock = 1,
    Restart = 2,
    Shutdown = 3,
}
