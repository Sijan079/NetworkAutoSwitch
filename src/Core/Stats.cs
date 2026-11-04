namespace NetworkAutoSwitch.Core;

public record Sample(DateTime At, int? Ms);   // Ms == null means the probe failed
public record SeenProgram(string Name, string Path, long Bytes);

/// <summary>Numbers for the dashboard: latency history, uptime, switches, time on backup. Thread-safe, no OS calls.</summary>
public sealed class StatsTracker
{
    const int MaxSamples = 360;
    readonly object _l = new();
    readonly List<Sample> _p = new(), _b = new();
    DateTime? _lastAt; Active _lastActive = Active.Primary;

    public int Switches { get; private set; }
    public DateTime? LastSwitchAt { get; private set; }
    public string LastSwitchReason { get; private set; } = "";
    public double SecondsOnBackup { get; private set; }
    public int ProbesPrimary { get; private set; }
    public int FailPrimary { get; private set; }
    public int ProbesBackup { get; private set; }
    public int FailBackup { get; private set; }

    /// <param name="backupFresh">False when the backup was not really checked this time (its last result was reused): the chart repeats the last value,
    /// but the uptime numbers only count real checks.</param>
    public void Record(EngineStatus s, DateTime now, bool backupFresh = true)
    {
        lock (_l)
        {
            if (_lastAt is DateTime prev && _lastActive == Active.Backup) SecondsOnBackup += (now - prev).TotalSeconds;
            _lastAt = now; _lastActive = s.Active;

            Add(_p, new Sample(now, s.Primary.Healthy ? (int)s.Primary.Ms : null)); ProbesPrimary++; if (!s.Primary.Healthy) FailPrimary++;
            Add(_b, new Sample(now, s.Backup.Healthy ? (int)s.Backup.Ms : null));
            if (backupFresh) { ProbesBackup++; if (!s.Backup.Healthy) FailBackup++; }
            if (s.Changed) { Switches++; LastSwitchAt = now; LastSwitchReason = s.Reason; }
        }
    }

    static void Add(List<Sample> l, Sample s) { l.Add(s); if (l.Count > MaxSamples) l.RemoveAt(0); }

    public (Sample[] primary, Sample[] backup) Snapshot() { lock (_l) return (_p.ToArray(), _b.ToArray()); }

    public static double UptimePct(int probes, int fails) => probes == 0 ? 0 : 100.0 * (probes - fails) / probes;

    /// <summary>Average of the successful probes in the last <paramref name="n"/> samples, or null if none succeeded.</summary>
    public int? AvgMs(bool primary, int n = 60)
    {
        lock (_l)
        {
            var ok = (primary ? _p : _b).TakeLast(n).Where(s => s.Ms.HasValue).Select(s => s.Ms!.Value).ToList();
            return ok.Count == 0 ? null : (int)ok.Average();
        }
    }
}

/// <summary>Inbound bytes seen on the backup adapter, grouped by program. This is the "who is using my tether data" view.</summary>
public sealed class NetUsage
{
    readonly object _l = new();
    public const int PeakWindowSec = 10;     // matches the usual watch window, so the peak can be compared with a trigger directly
    readonly Dictionary<string, long> _by = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Queue<(DateTime At, long Bytes)>> _win = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, long> _winSum = new(StringComparer.OrdinalIgnoreCase), _peak = new(StringComparer.OrdinalIgnoreCase);
    public long Total { get; private set; }
    readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);   // program name -> where it was last seen running from

    DateOnly _day = DateOnly.FromDateTime(DateTime.Now);

    public void Add(string exePath, long bytes) => Add(exePath, bytes, DateTime.UtcNow);

    /// <summary>The totals are for one calendar day (local time): a new day starts them again, like "Backup Data Today".</summary>
    public DateOnly Day { get { lock (_l) return _day; } }
    public void RollTo(DateOnly today) { if (today != _day) { ResetCore(); _day = today; } }

    // ---- remembered across restarts (same day only) ----
    sealed record SavedProgram(string Name, string Path, long Bytes, long Peak);
    sealed record SavedUsage(string Day, List<SavedProgram> Programs);

    public void Save(string file)
    {
        try
        {
            lock (_l)
            {
                var list = _by.Select(kv => new SavedProgram(kv.Key, _paths.GetValueOrDefault(kv.Key, kv.Key), kv.Value, _peak.GetValueOrDefault(kv.Key))).ToList();
                File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new SavedUsage(_day.ToString("yyyy-MM-dd"), list)));
            }
        }
        catch { }
    }

    /// <summary>Loads what was saved earlier TODAY (a file from another day is ignored). Never throws.</summary>
    public void LoadFrom(string file, DateOnly today)
    {
        try
        {
            var s = System.Text.Json.JsonSerializer.Deserialize<SavedUsage>(File.ReadAllText(file));
            if (s is null || !DateOnly.TryParseExact(s.Day, "yyyy-MM-dd", out var d) || d != today) return;
            lock (_l)
            {
                ResetCore(); _day = d;
                foreach (var p in s.Programs ?? new())
                {
                    if (string.IsNullOrWhiteSpace(p.Name) || p.Bytes < 0) continue;
                    _by[p.Name] = p.Bytes; _paths[p.Name] = p.Path ?? p.Name; _peak[p.Name] = Math.Max(0, p.Peak); Total += p.Bytes;
                }
            }
        }
        catch { }
    }

    /// <summary>Programs that used data on the backup, biggest first, with the full path (so a rule can be made in one click).</summary>
    public List<SeenProgram> Seen()
    {
        lock (_l) return _by.OrderByDescending(kv => kv.Value).Select(kv => new SeenProgram(kv.Key, _paths.GetValueOrDefault(kv.Key, kv.Key), kv.Value)).ToList();
    }

    public string? PathFor(string name) { lock (_l) return _paths.GetValueOrDefault(name); }

    /// <summary>The fastest burst (bytes within PeakWindowSec) this program has had on the backup; 0 if it never used it.</summary>
    public long PeakFor(string name) { lock (_l) return _peak.GetValueOrDefault(name); }

    public void Add(string exePath, long bytes, DateTime now)
    {
        lock (_l)
        {
            RollTo(DateOnly.FromDateTime(DateTime.Now));
            var n = Path.GetFileName(exePath);
            _paths[n] = exePath;
            _by[n] = _by.GetValueOrDefault(n) + bytes; Total += bytes;
            if (!_win.TryGetValue(n, out var q)) _win[n] = q = new();
            q.Enqueue((now, bytes)); long sum = _winSum.GetValueOrDefault(n) + bytes;
            while (q.Count > 0 && (now - q.Peek().At).TotalSeconds > PeakWindowSec) sum -= q.Dequeue().Bytes;
            _winSum[n] = sum;
            if (sum > _peak.GetValueOrDefault(n)) _peak[n] = sum;
        }
    }

    public List<(string Name, long Bytes)> Top(int n)
    {
        lock (_l) return _by.OrderByDescending(kv => kv.Value).Take(n).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>Like Top, plus each program's fastest PeakWindowSec burst (compare it with your trigger).</summary>
    public List<(string Name, long Bytes, long Peak)> TopWithPeak(int n)
    {
        lock (_l) return _by.OrderByDescending(kv => kv.Value).Take(n).Select(kv => (kv.Key, kv.Value, _peak.GetValueOrDefault(kv.Key))).ToList();
    }

    public void Reset() { lock (_l) ResetCore(); }
    void ResetCore() { _by.Clear(); _paths.Clear(); _win.Clear(); _winSum.Clear(); _peak.Clear(); Total = 0; }
}
