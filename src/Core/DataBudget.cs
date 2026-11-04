using System.Text.Json;

namespace NetworkAutoSwitch.Core;

public enum BudgetLevel { Ok, Warning, Reached }

/// <summary>How much data went through the backup connection today, and whether that crossed the daily cap you set. Pure logic: bytes and times in, a level out.
/// The day is the local calendar day; the count starts again at midnight. Each level (80 % of the cap, then the cap) is announced once per day.</summary>
public sealed class DataBudget
{
    public const double WarnAt = 0.8;
    readonly object _l = new();
    BudgetLevel _announced = BudgetLevel.Ok;

    public DateOnly Day { get; private set; }
    public long Bytes { get; private set; }

    public DataBudget(DateOnly day, long bytes = 0) { Day = day; Bytes = Math.Max(0, bytes); }

    public static BudgetLevel LevelFor(long bytes, long capBytes) =>
        capBytes <= 0 ? BudgetLevel.Ok : bytes >= capBytes ? BudgetLevel.Reached : bytes >= capBytes * WarnAt ? BudgetLevel.Warning : BudgetLevel.Ok;

    /// <summary>Adds data and returns the new level ONLY when a higher level than before was just reached today (so you are told once, not every second).</summary>
    public BudgetLevel? Add(long bytes, DateTime now, long capBytes)
    {
        lock (_l)
        {
            Rollover(now);
            Bytes += Math.Max(0, bytes);
            var level = LevelFor(Bytes, capBytes);
            if (level <= _announced) return null;
            _announced = level; return level;
        }
    }

    /// <summary>The cap was changed: levels are judged again from scratch (a lowered cap that is already exceeded is announced at the next check).</summary>
    public void CapChanged() { lock (_l) _announced = BudgetLevel.Ok; }

    /// <summary>Bytes used today (0 if the day has changed since the last count).</summary>
    public long Today(DateTime now) { lock (_l) { Rollover(now); return Bytes; } }

    void Rollover(DateTime now)
    {
        var d = DateOnly.FromDateTime(now);
        if (d != Day) { Day = d; Bytes = 0; _announced = BudgetLevel.Ok; }
    }

    // ---- remembered across restarts, so restarting the app does not reset today's count ----
    sealed record Saved(string Day, long Bytes);

    public void Save(string file)
    {
        try { lock (_l) File.WriteAllText(file, JsonSerializer.Serialize(new Saved(Day.ToString("yyyy-MM-dd"), Bytes))); } catch { }
    }

    /// <summary>Never throws: a missing or damaged file gives a fresh count for today.</summary>
    public static DataBudget Load(string file, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        try
        {
            var s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(file));
            if (s is not null && DateOnly.TryParseExact(s.Day, "yyyy-MM-dd", out var d) && d == today) return new DataBudget(d, s.Bytes);
        }
        catch { }
        return new DataBudget(today);
    }
}

/// <summary>Turns a network adapter's running byte counter into "bytes since last time".</summary>
public static class CounterDelta
{
    /// <summary>The first reading, or a counter that went DOWN (the adapter was reset, replaced or re-plugged), adds nothing: it only sets the new starting point.</summary>
    public static long Next(long? last, long current) => last is null || current < last ? 0 : current - last.Value;
}

/// <summary>Decides how often the idle backup connection is checked. Every check on a phone tether uses a little mobile data,
/// and the backup only matters at the moment the main connection fails, so while the main one is fine the backup is checked every few seconds instead of every second.</summary>
public static class ProbeSchedule
{
    /// <param name="lastProbe">When the backup was last really checked (null = never).</param>
    /// <param name="activeIsBackup">The backup is the connection in use: then every check counts.</param>
    /// <param name="primaryHealthy">The main connection's latest check. When it starts failing the backup is checked at once, so a switch is never based on stale news.</param>
    /// <param name="lastBackupHealthy">A backup that is down costs no data to check, so it is checked every time (it is noticed the moment it comes up).</param>
    /// <param name="mustProbe">Something changed (a different adapter was chosen, the app just started).</param>
    public static bool ProbeBackupNow(DateTime now, DateTime? lastProbe, int idleSeconds, bool activeIsBackup, bool primaryHealthy, bool lastBackupHealthy, bool mustProbe)
    {
        if (mustProbe || lastProbe is null || activeIsBackup || !primaryHealthy || !lastBackupHealthy) return true;
        return (now - lastProbe.Value).TotalSeconds >= Math.Max(1, idleSeconds) - 0.05;
    }
}
