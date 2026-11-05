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

/// <summary>One line of the Dashboard's "Network Activity" table.</summary>
public record UsageRow(string Name, long Down, long Up, long PeakDown, string On, long BackupBytes = 0)
{
    /// <summary>Everything the program moved today, in both directions.</summary>
    public long Total => Down + Up;
}

/// <summary>What each program downloaded and uploaded today, on the main connection and on the backup (the "Network Activity" view).
/// Peak = the fastest PeakWindowSec burst of downloading. The BACKUP's own peak is kept separately: it decides which running downloads are moved back to the main connection.</summary>
/// <summary>How the Network Activity table is ordered. Keys: "total" (down plus up, the default), "program", "backup" (how much went through the backup), "trigger", "watch".</summary>
public static class UsageSort
{
    public static readonly string[] Keys = { "total", "program", "backup", "trigger", "watch" };

    /// <summary>"program" starts A to Z, everything else starts with the biggest (for "watch": watched first).
    /// <paramref name="flip"/> reverses it. Ties are broken by name, so the table never jumps around.</summary>
    public static List<UsageRow> Apply(IEnumerable<UsageRow> rows, string key, bool flip = false, Func<UsageRow, bool>? watched = null)
    {
        bool descending = key != "program"; if (flip) descending = !descending;
        var list = rows.ToList();
        if (key == "program") return (descending ? list.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase) : list.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        Func<UsageRow, long> num = key switch
        {
            "backup" => r => r.BackupBytes, "trigger" => r => r.PeakDown,
            "watch" => r => watched?.Invoke(r) == true ? 1 : 0,
            _ => r => r.Total,
        };
        var ordered = descending ? list.OrderByDescending(num) : list.OrderBy(num);
        return ordered.ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

public sealed class NetUsage
{
    readonly object _l = new();
    public const int PeakWindowSec = 10;     // matches the usual watch window, so the peak can be compared with a trigger directly
    readonly Dictionary<string, long> _by = new(StringComparer.OrdinalIgnoreCase), _up = new(StringComparer.OrdinalIgnoreCase);     // downloaded / uploaded today
    readonly Dictionary<string, Queue<(DateTime At, long Bytes)>> _win = new(StringComparer.OrdinalIgnoreCase), _bwin = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, long> _winSum = new(StringComparer.OrdinalIgnoreCase), _peak = new(StringComparer.OrdinalIgnoreCase),
                                      _bwinSum = new(StringComparer.OrdinalIgnoreCase), _bpeak = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _onMain = new(StringComparer.OrdinalIgnoreCase), _onBackup = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, long> _bb = new(StringComparer.OrdinalIgnoreCase);     // bytes (down plus up) that went through the BACKUP, per program
    public long Total { get; private set; }     // everything downloaded today
    readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);   // program name -> where it was last seen running from

    DateOnly _day = DateOnly.FromDateTime(DateTime.Now);

    public void Add(string exePath, long bytes) => Add(exePath, bytes, DateTime.UtcNow);

    /// <summary>The totals are for one calendar day (local time): a new day starts them again, like "Backup Data Today".</summary>
    public DateOnly Day { get { lock (_l) return _day; } }
    public void RollTo(DateOnly today) { if (today != _day) { ResetCore(); _day = today; } }

    // ---- remembered across restarts (same day only) ----
    sealed record SavedProgram(string Name, string Path, long Bytes, long Peak, long Up = 0, bool Main = false, bool Backup = false, long BackupPeak = 0, long BackupBytes = -1);
    sealed record SavedUsage(string Day, List<SavedProgram> Programs);

    public void Save(string file)
    {
        try
        {
            lock (_l)
            {
                var list = Names().Select(k => new SavedProgram(k, _paths.GetValueOrDefault(k, k), _by.GetValueOrDefault(k), _peak.GetValueOrDefault(k), _up.GetValueOrDefault(k), _onMain.Contains(k), _onBackup.Contains(k), _bpeak.GetValueOrDefault(k), _bb.GetValueOrDefault(k))).ToList();
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
                    if (string.IsNullOrWhiteSpace(p.Name) || p.Bytes < 0 || p.Up < 0) continue;
                    _by[p.Name] = p.Bytes; _up[p.Name] = p.Up; _paths[p.Name] = p.Path ?? p.Name; _peak[p.Name] = Math.Max(0, p.Peak); _bpeak[p.Name] = Math.Max(0, p.BackupPeak > 0 ? p.BackupPeak : p.Peak);
                    // a file from before this was counted: when it knows nothing about Main or Backup, all of it was backup data (the table used to count only the backup)
                    _bb[p.Name] = p.BackupBytes >= 0 ? p.BackupBytes : (!p.Main && !p.Backup ? p.Bytes : 0);
                    if (p.Main) _onMain.Add(p.Name);
                    if (p.Backup) _onBackup.Add(p.Name);
                    Total += p.Bytes;
                }
            }
        }
        catch { }
    }

    IEnumerable<string> Names() => _by.Keys.Concat(_up.Keys.Where(k => !_by.ContainsKey(k)));

    /// <summary>Programs that used the network, with the full path (so a rule can be made in one click). The ones seen on the backup come first, then the biggest.</summary>
    public List<SeenProgram> Seen()
    {
        lock (_l) return Names().OrderByDescending(k => _onBackup.Contains(k)).ThenByDescending(k => _by.GetValueOrDefault(k))
            .Select(k => new SeenProgram(k, _paths.GetValueOrDefault(k, k), _by.GetValueOrDefault(k))).ToList();
    }

    public string? PathFor(string name) { lock (_l) return _paths.GetValueOrDefault(name); }

    /// <summary>The fastest burst (bytes within PeakWindowSec) this program has had downloading on the BACKUP; 0 if it never used it.</summary>
    public long PeakFor(string name) { lock (_l) return _bpeak.GetValueOrDefault(name); }

    static void Burst(Dictionary<string, Queue<(DateTime At, long Bytes)>> win, Dictionary<string, long> sums, Dictionary<string, long> peaks, string n, DateTime now, long bytes)
    {
        if (!win.TryGetValue(n, out var q)) win[n] = q = new();
        q.Enqueue((now, bytes)); long sum = sums.GetValueOrDefault(n) + bytes;
        while (q.Count > 0 && (now - q.Peek().At).TotalSeconds > PeakWindowSec) sum -= q.Dequeue().Bytes;
        sums[n] = sum;
        if (sum > peaks.GetValueOrDefault(n)) peaks[n] = sum;
    }

    void Mark(string n, string? connection) { if (connection == "Main") _onMain.Add(n); else if (connection == "Backup") _onBackup.Add(n); }

    /// <summary>Bytes a program DOWNLOADED. <paramref name="connection"/> = "Main" or "Backup" (null = unknown, counted like the backup, as before).</summary>
    public void Add(string exePath, long bytes, DateTime now, string? connection = null)
    {
        lock (_l)
        {
            RollTo(DateOnly.FromDateTime(DateTime.Now));
            var n = Path.GetFileName(exePath);
            _paths[n] = exePath; Mark(n, connection);
            _by[n] = _by.GetValueOrDefault(n) + bytes; Total += bytes;
            if (connection == "Backup") _bb[n] = _bb.GetValueOrDefault(n) + bytes;
            Burst(_win, _winSum, _peak, n, now, bytes);
            if (connection is null or "Backup") Burst(_bwin, _bwinSum, _bpeak, n, now, bytes);
        }
    }

    /// <summary>Bytes a program UPLOADED.</summary>
    public void AddUp(string exePath, long bytes, string? connection = null)
    {
        lock (_l)
        {
            RollTo(DateOnly.FromDateTime(DateTime.Now));
            var n = Path.GetFileName(exePath);
            _paths[n] = exePath; Mark(n, connection);
            _up[n] = _up.GetValueOrDefault(n) + bytes;
            if (connection == "Backup") _bb[n] = _bb.GetValueOrDefault(n) + bytes;
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

    /// <summary>The Network Activity table: the busiest programs by what they downloaded plus uploaded, with where they were seen ("Main", "Backup", "Both").</summary>
    public List<UsageRow> Rows(int n)
    {
        lock (_l) return Names().Select(k => new UsageRow(k, _by.GetValueOrDefault(k), _up.GetValueOrDefault(k), _peak.GetValueOrDefault(k),
                _onMain.Contains(k) && _onBackup.Contains(k) ? "Both" : _onMain.Contains(k) ? "Main" : _onBackup.Contains(k) ? "Backup" : "", _bb.GetValueOrDefault(k)))
            .OrderByDescending(r => r.Total).Take(n).ToList();
    }

    public void Reset() { lock (_l) ResetCore(); }
    void ResetCore() { _by.Clear(); _up.Clear(); _bb.Clear(); _paths.Clear(); _win.Clear(); _winSum.Clear(); _peak.Clear(); _bwin.Clear(); _bwinSum.Clear(); _bpeak.Clear(); _onMain.Clear(); _onBackup.Clear(); Total = 0; }
}
