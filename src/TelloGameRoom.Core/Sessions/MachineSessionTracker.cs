using TelloGameRoom.Core.Dtos;

namespace TelloGameRoom.Core.Sessions;

/// <summary>What the machine should be showing right now.</summary>
public readonly record struct MachineView(MachineLockState State, long RemainingSeconds, bool WarningActive);

/// <summary>
/// The Machine app's local copy of the session, built to keep working without a connection.
/// <para>
/// It counts down against a monotonic clock (immune to someone changing the Windows clock), locks at zero on its own,
/// and once it has locked it stays locked until the <em>server</em> says otherwise. Whatever the server sends replaces
/// local state, except that stale messages (lower version for the same session) are ignored.
/// </para>
/// </summary>
public sealed class MachineSessionTracker(Func<TimeSpan> monotonicNow)
{
    private readonly Lock _sync = new();
    private MachineLockState _state = MachineLockState.Locked;
    private Guid? _sessionId;
    private int _version = -1;
    private TimeSpan _deadline;
    private long _pausedRemaining;
    private int _warningThreshold = 300;
    private bool _warningAcknowledged;

    public Guid? SessionId
    {
        get
        {
            lock (_sync)
            {
                return _sessionId;
            }
        }
    }

    /// <summary>Applies an authoritative state from the server.</summary>
    /// <returns>False when the message was stale and ignored.</returns>
    public bool ApplyServerState(MachineSessionState server)
    {
        lock (_sync)
        {
            if (server.SessionId is not null && server.SessionId == _sessionId && server.Version < _version)
            {
                return false;
            }

            if (server.SessionId != _sessionId)
            {
                _warningAcknowledged = false;
            }

            _sessionId = server.SessionId;
            _version = server.Version;
            _warningThreshold = server.WarningThresholdSeconds;
            _state = server.State;
            switch (server.State)
            {
                case MachineLockState.Active when server.RemainingSeconds > 0:
                    _deadline = monotonicNow() + TimeSpan.FromSeconds(server.RemainingSeconds);
                    if (server.RemainingSeconds > _warningThreshold)
                    {
                        // Time was added past the threshold: warn again when it runs low next time.
                        _warningAcknowledged = false;
                    }

                    break;
                case MachineLockState.Paused:
                    _pausedRemaining = server.RemainingSeconds;
                    break;
                default:
                    _state = MachineLockState.Locked;
                    break;
            }

            return true;
        }
    }

    /// <summary>Call about once per second. Locks locally when the countdown reaches zero.</summary>
    public MachineView Tick()
    {
        lock (_sync)
        {
            switch (_state)
            {
                case MachineLockState.Active:
                    var remaining = (long)Math.Ceiling((_deadline - monotonicNow()).TotalSeconds);
                    if (remaining <= 0)
                    {
                        // Out of time. From here only the server can unlock, even if the connection is down.
                        _state = MachineLockState.Locked;
                        return new MachineView(MachineLockState.Locked, 0, false);
                    }

                    return new MachineView(MachineLockState.Active, remaining, remaining <= _warningThreshold && !_warningAcknowledged);
                case MachineLockState.Paused:
                    return new MachineView(MachineLockState.Paused, _pausedRemaining, false);
                default:
                    return new MachineView(MachineLockState.Locked, 0, false);
            }
        }
    }

    /// <summary>The customer dismissed the "time is running out" popup.</summary>
    public void AcknowledgeWarning()
    {
        lock (_sync)
        {
            _warningAcknowledged = true;
        }
    }

    /// <summary>A snapshot that can be saved to disk so a crashed or restarted Machine app does not hand out free time.</summary>
    public PersistedMachineSession? Snapshot(DateTime utcNow)
    {
        lock (_sync)
        {
            return _state switch
            {
                MachineLockState.Active => new PersistedMachineSession(_sessionId, _version, MachineLockState.Active,
                    utcNow + (_deadline - monotonicNow()), 0, _warningThreshold)
                {
                    RemainingSecondsAtSave = Math.Max(0, (long)Math.Ceiling((_deadline - monotonicNow()).TotalSeconds)),
                },
                MachineLockState.Paused => new PersistedMachineSession(_sessionId, _version, MachineLockState.Paused, null, _pausedRemaining, _warningThreshold),
                _ => null,
            };
        }
    }

    /// <summary>
    /// Restores a snapshot after an app restart while offline. Wall-clock time is only trusted in the direction that
    /// shortens the session: the restored remaining time can never exceed what was left when the snapshot was taken.
    /// </summary>
    public void Restore(PersistedMachineSession saved, DateTime utcNow)
    {
        var remaining = saved.State == MachineLockState.Active && saved.EndsAtUtc is { } end
            ? Math.Min(saved.RemainingSecondsAtSave, (long)Math.Ceiling((end - utcNow).TotalSeconds))
            : saved.PausedRemainingSeconds;
        ApplyServerState(new MachineSessionState(
            remaining > 0 ? saved.State : MachineLockState.Locked, saved.SessionId, Math.Max(0, remaining), saved.WarningThresholdSeconds, saved.Version, utcNow));
    }
}

/// <summary>On-disk form of the tracker state.</summary>
public sealed record PersistedMachineSession(
    Guid? SessionId, int Version, MachineLockState State, DateTime? EndsAtUtc, long PausedRemainingSeconds, int WarningThresholdSeconds)
{
    /// <summary>Remaining seconds at the moment of saving, used as the upper bound on restore.</summary>
    public long RemainingSecondsAtSave { get; init; }
}
