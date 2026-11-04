namespace NetworkAutoSwitch.Core;

public enum Active { Primary, Backup }

/// <summary>How the app decides: Auto fails over and back by itself; the other two lock it to one connection (no automatic switching).</summary>
public enum ConnectionMode { Auto, StayPrimary, StayBackup }

public record Decision(Active Active, bool Changed, string Reason);

/// <summary>Pure state machine (no network calls, easy to test).
/// Fails over after N consecutive primary failures (only if backup is healthy), or at once when Windows says the primary link is down;
/// fails back after M consecutive primary successes, so one flaky probe never flips anything.</summary>
public class FailoverMonitor
{
    readonly int _failAfter, _recoverAfter;
    int _bad, _good;
    public Active Active { get; private set; } = Active.Primary;

    public FailoverMonitor(int failAfter = 3, int recoverAfter = 3) { _failAfter = failAfter; _recoverAfter = recoverAfter; }

    /// <summary>Forget the counters and start from the given state (used when leaving a locked mode).</summary>
    public void Reset(Active a) { Active = a; _bad = _good = 0; }

    /// <param name="primaryLinkDown">The adapter itself reports "down": that is certain, so there is nothing to wait for.</param>
    public Decision Observe(bool primaryHealthy, bool backupHealthy, bool primaryLinkDown = false)
    {
        if (primaryHealthy) { _good++; _bad = 0; }
        else { _bad = primaryLinkDown ? Math.Max(_bad + 1, _failAfter) : _bad + 1; _good = 0; }

        if (Active == Active.Primary && _bad >= _failAfter)
        {
            if (!backupHealthy) return new(Active, false, $"primary down {_bad}x but backup is also down, staying put");
            Active = Active.Backup; _good = 0;
            return new(Active, true, primaryLinkDown ? "primary link is down, switching to backup" : $"primary failed {_bad} probes in a row, switching to backup");
        }
        if (Active == Active.Backup && _good >= _recoverAfter)
        {
            Active = Active.Primary; _bad = 0;
            return new(Active, true, $"primary healthy {_good} probes in a row, switching back");
        }
        if (Active == Active.Backup && !backupHealthy && primaryHealthy)
        {   // backup died while primary is back: don't wait for the recovery count
            Active = Active.Primary; _bad = 0;
            return new(Active, true, "backup failed and primary is up, switching to primary");
        }
        return new(Active, false, primaryHealthy ? "primary ok" : $"primary bad ({_bad}/{_failAfter})");
    }
}
