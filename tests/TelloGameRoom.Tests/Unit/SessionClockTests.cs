using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Sessions;

namespace TelloGameRoom.Tests.Unit;

public class SessionClockTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Active_session_counts_down_from_its_end_time()
    {
        Assert.Equal(600, SessionClock.RemainingSeconds(SessionStatus.Active, Now.AddMinutes(10), null, Now));
        Assert.Equal(1, SessionClock.RemainingSeconds(SessionStatus.Active, Now.AddMilliseconds(200), null, Now));
        Assert.Equal(0, SessionClock.RemainingSeconds(SessionStatus.Active, Now.AddMinutes(-1), null, Now));
    }

    [Fact]
    public void Paused_session_keeps_its_frozen_time_no_matter_how_long_it_waits()
    {
        Assert.Equal(1234, SessionClock.RemainingSeconds(SessionStatus.Paused, null, 1234, Now));
        Assert.Equal(1234, SessionClock.RemainingSeconds(SessionStatus.Paused, null, 1234, Now.AddDays(3)));
    }

    [Fact]
    public void Ended_session_has_no_time() =>
        Assert.Equal(0, SessionClock.RemainingSeconds(SessionStatus.Ended, Now.AddHours(1), 500, Now));
}

public class MachineSessionTrackerTests
{
    private TimeSpan _clock = TimeSpan.FromHours(5);
    private readonly Guid _session = Guid.NewGuid();

    private MachineSessionTracker NewTracker() => new(() => _clock);

    private MachineSessionState Active(long remaining, int version = 1, Guid? session = null, int threshold = 300) =>
        new(MachineLockState.Active, session ?? _session, remaining, threshold, version, DateTime.UtcNow);

    private static MachineSessionState Locked() => new(MachineLockState.Locked, null, 0, 300, 0, DateTime.UtcNow);

    private void Advance(double seconds) => _clock += TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Starts_locked() => Assert.Equal(MachineLockState.Locked, NewTracker().Tick().State);

    [Fact]
    public void Counts_down_locally_without_any_further_server_contact()
    {
        var tracker = NewTracker();
        tracker.ApplyServerState(Active(3600));
        Advance(600);
        Assert.Equal(new MachineView(MachineLockState.Active, 3000, false), tracker.Tick());
    }

    [Fact]
    public void Locks_itself_at_zero_while_offline_and_stays_locked()
    {
        var tracker = NewTracker();
        tracker.ApplyServerState(Active(60));
        Advance(61);
        Assert.Equal(MachineLockState.Locked, tracker.Tick().State);

        // Still offline much later: nothing unlocks it.
        Advance(10_000);
        Assert.Equal(MachineLockState.Locked, tracker.Tick().State);
    }

    [Fact]
    public void After_locking_offline_only_the_server_can_unlock()
    {
        var tracker = NewTracker();
        tracker.ApplyServerState(Active(60));
        Advance(120);
        tracker.Tick();

        // Reconnect: staff added time while the machine was offline. The server's value wins.
        tracker.ApplyServerState(Active(900, version: 2));
        Assert.Equal(new MachineView(MachineLockState.Active, 900, false), tracker.Tick());
    }

    [Fact]
    public void Server_value_wins_over_the_local_countdown_on_reconnect()
    {
        var tracker = NewTracker();
        tracker.ApplyServerState(Active(3600));
        Advance(100);

        tracker.ApplyServerState(Active(1200, version: 2)); // staff removed time meanwhile
        Assert.Equal(1200, tracker.Tick().RemainingSeconds);

        tracker.ApplyServerState(Locked()); // session stopped
        Assert.Equal(MachineLockState.Locked, tracker.Tick().State);
    }

    [Fact]
    public void Stale_messages_for_the_same_session_are_ignored()
    {
        var tracker = NewTracker();
        tracker.ApplyServerState(Active(600, version: 5));
        Assert.False(tracker.ApplyServerState(Active(3600, version: 4)));
        Assert.Equal(600, tracker.Tick().RemainingSeconds);
    }

    [Fact]
    public void A_new_session_is_accepted_even_with_a_lower_version()
    {
        var tracker = NewTracker();
        tracker.ApplyServerState(Active(600, version: 9));
        Assert.True(tracker.ApplyServerState(Active(1800, version: 1, session: Guid.NewGuid())));
        Assert.Equal(1800, tracker.Tick().RemainingSeconds);
    }

    [Fact]
    public void Paused_session_shows_frozen_time()
    {
        var tracker = NewTracker();
        tracker.ApplyServerState(new MachineSessionState(MachineLockState.Paused, _session, 777, 300, 2, DateTime.UtcNow));
        Advance(5000);
        Assert.Equal(new MachineView(MachineLockState.Paused, 777, false), tracker.Tick());
    }

    [Fact]
    public void Warning_shows_at_the_threshold_once_and_again_after_more_time_is_added()
    {
        var tracker = NewTracker();
        tracker.ApplyServerState(Active(310));
        Assert.False(tracker.Tick().WarningActive);
        Advance(11);
        Assert.True(tracker.Tick().WarningActive);

        tracker.AcknowledgeWarning();
        Assert.False(tracker.Tick().WarningActive);

        tracker.ApplyServerState(Active(1000, version: 2));
        Advance(701);
        Assert.True(tracker.Tick().WarningActive);
    }

    [Fact]
    public void Changing_the_windows_clock_does_not_matter_because_only_the_monotonic_clock_is_used()
    {
        var tracker = NewTracker();
        tracker.ApplyServerState(Active(600) with { ServerTimeUtc = DateTime.UtcNow.AddYears(-10) });
        Advance(100);
        Assert.Equal(500, tracker.Tick().RemainingSeconds);
    }

    [Fact]
    public void Snapshot_survives_an_app_restart_and_never_gives_extra_time()
    {
        var wall = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var tracker = NewTracker();
        tracker.ApplyServerState(Active(600));
        Advance(100);
        var saved = tracker.Snapshot(wall)!;
        Assert.Equal(500, saved.RemainingSecondsAtSave);

        // App restarts 200 seconds later.
        var restored = NewTracker();
        restored.Restore(saved, wall.AddSeconds(200));
        Assert.Equal(300, restored.Tick().RemainingSeconds);

        // Customer set the Windows clock back a day before restarting: capped at what was left when saved.
        var cheated = NewTracker();
        cheated.Restore(saved, wall.AddDays(-1));
        Assert.Equal(500, cheated.Tick().RemainingSeconds);

        // Restart after the time ran out: locked.
        var late = NewTracker();
        late.Restore(saved, wall.AddHours(1));
        Assert.Equal(MachineLockState.Locked, late.Tick().State);
    }

    [Fact]
    public void Locked_tracker_has_nothing_to_persist()
    {
        Assert.Null(NewTracker().Snapshot(DateTime.UtcNow));
    }
}
