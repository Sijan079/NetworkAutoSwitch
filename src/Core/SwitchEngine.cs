using System.Net.Sockets;

namespace NetworkAutoSwitch.Core;

public record EngineStatus(int Tick, Active Active, ProbeResult Primary, ProbeResult Backup, bool Changed, string Reason, bool Paused, string EtwInfo, ConnectionMode Mode = ConnectionMode.Auto);

/// <summary>The whole app in one class: probe both adapters, decide, switch metrics, apply backup rules,
/// run the download watchdog, and always restore on stop. Used by both the CLI and the tray app.</summary>
public sealed class SwitchEngine
{
    readonly string _primaryArg, _backupArg, _policyFile;
    readonly bool _enforce, _execute;
    readonly int _interval;
    readonly object _gate = new();

    // Detection speed: 3 failed checks in a row (each gives up after ~1.2 s, re-checked at once) = about 4 s. Coming back is deliberately slower (10 good checks)
    // so a flickering Wi-Fi does not bounce you between connections.
    const int FailAfter = 3, RecoverAfter = 10;
    FailoverMonitor _mon = new(FailAfter, RecoverAfter);
    readonly SemaphoreSlim _nudge = new(0, int.MaxValue);   // "check now": released by network-change events and by mode changes
    readonly string? _settingsFile;                       // null = the real settings file (tests pass a temporary one)
    ConnectionMode _mode;
    Active _locked = Active.Primary;
    PolicyFile _policy = new(new());
    DownloadGuard _guard = new(Array.Empty<AppRule>());
    EtwNetSource? _etw;
    string _pName = "", _bName = "";
    bool _touched, _applied;   // _touched: originals saved. _applied: our metrics are in place right now
    volatile bool _paused;

    public Action<string> Log { get; set; } = Console.WriteLine;
    public event Action<EngineStatus>? Status;
    /// <summary>Raised when the watchdog blocks a program. Blocks never expire, so the UI should tell the user.</summary>
    public event Action<GuardAction>? WatchdogBlocked;
    public bool Paused => _paused;
    public StatsTracker Stats { get; } = new();
    public NetUsage Usage { get; } = new();
    public string PrimaryName => _pName;
    public string BackupName => _bName;
    public bool Live => _execute;
    public Active CurrentActive => _mon.Active;

    public List<DownloadGuard.BlockedInfo> Blocked => _guard.BlockedNow();

    /// <summary>Is this program already on the watch list (covered by a watch rule in the policy that is running)?</summary>
    public bool IsWatched(string path) => PolicyBuilder.IsCovered(path, _policy.BackupRules);

    public List<DownloadGuard.AllowedInfo> Allowed => _guard.AllowedNow(DateTime.UtcNow);

    /// <summary>"Block now": re-block a program you had allowed, right away.</summary>
    public bool BlockAgain(string displayName)
    {
        var a = _guard.BlockNow(displayName, DateTime.UtcNow);
        if (a is null) return false;
        Log($"  [watchdog] BLOCKED AGAIN by you: {a.Rule}");
        Act(a);
        return true;
    }

    /// <summary>"Watch again": end the allowed time so the watchdog treats the program normally.</summary>
    public bool WatchAgain(string displayName)
    {
        bool ok = _guard.ResumeWatch(displayName);
        if (ok) Log("  [watchdog] watching again (you ended the allowed time)");
        return ok;
    }

    /// <summary>Dashboard override: lift a watchdog block now and leave that program alone for a while.</summary>
    public bool OverrideBlock(string displayName, int graceMinutes)
    {
        var a = _guard.Allow(displayName, DateTime.UtcNow, graceMinutes);
        if (a is null) return false;
        Log($"  [watchdog] OVERRIDE: {a.Rule} unblocked by you, not watched for {graceMinutes} min");
        Act(a);
        return true;
    }

    /// <summary>Re-reads the policy file and applies it right away (rules are rebuilt if we are on the backup).
    /// Throws InvalidDataException if the file is invalid, leaving the old policy running.</summary>
    public void ReloadPolicy()
    {
        var fresh = BackupPolicy.Load(_policyFile);
        lock (_gate)
        {
            bool onBackup = _enforce && _touched && _mon.Active == Active.Backup && !_paused;
            if (onBackup) { StopWatch(); BackupPolicy.Run(BackupPolicy.RemoveCommands(), !_execute, Log); }
            _policy = fresh; _guard = new DownloadGuard(_policy.BackupRules, _policy.Defaults);
            if (onBackup) { BackupPolicy.Run(BackupPolicy.ApplyCommands(_policy, _bName), !_execute, Log); StartWatch(); }
            Log("policy reloaded" + (onBackup ? " and re-applied on the backup" : ""));
        }
    }

    public SwitchEngine(string primary, string backup, string policyFile, bool enforce, bool execute, int intervalSec, string? settingsFile = null)
    { _primaryArg = primary; _backupArg = backup; _policyFile = policyFile; _enforce = enforce; _execute = execute; _interval = intervalSec; _settingsFile = settingsFile; _mode = AppSettings.Load(settingsFile).ConnectionMode; }

    string Mode => !_enforce ? "decide-only" : _execute ? "ENFORCE (live)" : "enforce DRY-RUN";

    /// <summary>Automatic = the app switches by itself. Otherwise it is locked to one connection and never switches on its own.</summary>
    public ConnectionMode ConnMode => _mode;

    /// <summary>Lock the app to one connection (or give control back to automatic switching). Remembered across restarts.
    /// The chosen connection is applied within a moment, whether or not it is healthy: it is your decision.</summary>
    public void SetConnectionMode(ConnectionMode m)
    {
        lock (_gate)
        {
            if (m == _mode) return;
            _mode = m;
            AppSettings.Update(s => s.Mode = m.ToString(), _settingsFile);
            if (m != ConnectionMode.Auto) { _locked = m == ConnectionMode.StayBackup ? Active.Backup : Active.Primary; _applied = false; }
            _mon.Reset(m == ConnectionMode.Auto ? _mon.Active : _locked);   // automatic mode carries on from where we are now
            Log(m == ConnectionMode.Auto ? "mode: automatic switching is on"
                                         : $"mode: locked to {(m == ConnectionMode.StayBackup ? _bName : _pName)}; no automatic switching");
        }
        _nudge.Release();     // act on it right away
    }

    FailoverMonitor FreshMonitor() { var m = new FailoverMonitor(FailAfter, RecoverAfter); if (_mode != ConnectionMode.Auto) m.Reset(_locked); return m; }

    void OnNetworkChanged(object? s, EventArgs e) { try { _nudge.Release(); } catch (SemaphoreFullException) { } }

    /// <summary>Runs until ticks are used up (0 = forever) or the token is cancelled. Always restores on exit.</summary>
    public async Task RunAsync(int ticks, CancellationToken ct)
    {
        _pName = AdapterProber.Find(_primaryArg)?.Name ?? _primaryArg;
        _bName = AdapterProber.Find(_backupArg)?.Name ?? _backupArg;
        _policy = BackupPolicy.Load(_policyFile);
        _guard = new DownloadGuard(_policy.BackupRules, _policy.Defaults);
        if (_mode != ConnectionMode.Auto) { _locked = _mode == ConnectionMode.StayBackup ? Active.Backup : Active.Primary; _mon.Reset(_locked); }
        Log($"primary='{_pName}' backup='{_bName}' every {_interval}s ({(ticks == 0 ? "until stopped" : ticks + " ticks")}) mode={Mode}"
            + (_mode == ConnectionMode.Auto ? "" : $" LOCKED to {(_mode == ConnectionMode.StayBackup ? _bName : _pName)}"));

        // Windows tells us the moment a network link changes (Wi-Fi dropped, cable pulled): check straight away instead of waiting for the next tick
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        try
        {
            for (int t = 1; (ticks == 0 || t <= ticks) && !ct.IsCancellationRequested; t++)
            {
                var p = Task.Run(() => AdapterProber.Probe(_primaryArg), CancellationToken.None);
                var b = Task.Run(() => AdapterProber.Probe(_backupArg), CancellationToken.None);
                var (pr, br) = (await p, await b);
                Decision d;
                lock (_gate)
                {
                    if (_mode == ConnectionMode.Auto)
                    {
                        d = _mon.Observe(pr.Healthy, br.Healthy, primaryLinkDown: pr.Detail == AdapterProber.AdapterDown);
                        if (_enforce && !_paused)
                        {
                            if (d.Changed) Enforce(d.Active, moveRunning: d.Active == Active.Primary);          // a real switch (back to main: also move running downloads)
                            else if (d.Active == Active.Primary && pr.Healthy && !_applied) Enforce(Active.Primary, moveRunning: true);   // main is healthy: make sure Windows really prefers it (and that downloads still on the backup move over)
                        }
                    }
                    else
                    {
                        // locked: no deciding, no switching. Just keep the chosen connection in place (and keep measuring, for the dashboard).
                        d = new Decision(_locked, false, $"locked: staying on {(_locked == Active.Backup ? _bName : _pName)}");
                        if (_enforce && !_paused && !_applied) Enforce(_locked, moveRunning: _locked == Active.Primary);
                    }
                }
                var status = new EngineStatus(t, d.Active, pr, br, d.Changed, d.Reason, _paused,
                    _etw is null ? "" : $"etw={_etw.EventsMatched}/{_etw.EventsSeen}", _mode);
                Stats.Record(status, DateTime.Now);
                Status?.Invoke(status);
                if (ticks == 0 || t < ticks)
                {
                    // Right after a failed check on the main connection, check again almost at once: that is what makes an outage get noticed in seconds.
                    int wait = _mode == ConnectionMode.Auto && d.Active == Active.Primary && !pr.Healthy ? 150 : _interval * 1000;
                    try { if (await _nudge.WaitAsync(wait, ct)) while (_nudge.CurrentCount > 0) _nudge.Wait(0); } catch (OperationCanceledException) { break; }
                }
            }
        }
        finally
        {
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
            Cleanup();
        }
    }

    /// <summary>Pause: put Windows back to normal and stop switching until Resume.</summary>
    public void Pause() { lock (_gate) { if (_paused) return; _paused = true; Cleanup(); _mon = FreshMonitor(); Log("paused: original metrics restored"); } }
    public void Resume() { lock (_gate) { if (!_paused) return; _paused = false; _mon = FreshMonitor(); Log("resumed"); } _nudge.Release(); }

    /// <summary>Makes Windows prefer <paramref name="a"/>: the preferred adapter gets the lowest metric, the other the highest.
    /// Windows' own defaults rank the tether ABOVE Wi-Fi, so the main connection must be set explicitly too, not just "restored".
    /// The original metrics are saved first and put back when the app pauses or exits.</summary>
    void Enforce(Active a, bool moveRunning)
    {
        if (a == Active.Primary) StopWatch();
        if (_execute && !_touched) { MetricSwitcher.SaveOriginals(_pName, _bName); _touched = true; }
        var cmds = a == Active.Backup ? MetricSwitcher.PreferCommands(_bName, _pName).Concat(BackupPolicy.ApplyCommands(_policy, _bName)).ToList()
                                      : MetricSwitcher.PreferCommands(_pName, _bName).Concat(BackupPolicy.RemoveCommands()).ToList();
        BackupPolicy.RunParallel(cmds, !_execute, Log);      // independent commands: all at once, so the switch is as fast as one command
        _applied = true;
        if (_execute) VerifyRoute(a == Active.Backup ? _bName : _pName, a == Active.Backup ? _pName : _bName);
        if (a == Active.Backup) StartWatch();
        else if (moveRunning) MoveRunningToPrimary();
    }

    /// <summary>Don't trust the numbers: ask Windows which adapter a new connection really takes now. If it is the wrong one,
    /// push the other adapter's route further back and ask again, and say so in the log.</summary>
    void VerifyRoute(string expected, string other)
    {
        var chosen = MetricSwitcher.RouteChoice();
        if (chosen is null) return;
        if (chosen.Equals(expected, StringComparison.OrdinalIgnoreCase)) { Log($"  [route] new connections now use {chosen}"); return; }
        Log($"  [route] WARNING: new connections still use {chosen}, not {expected}. Pushing {other} further back.");
        BackupPolicy.Run(MetricSwitcher.StrongDemote(other), false, Log);
        chosen = MetricSwitcher.RouteChoice();
        Log(chosen is not null && chosen.Equals(expected, StringComparison.OrdinalIgnoreCase)
            ? $"  [route] fixed: new connections now use {chosen}"
            : $"  [route] FAILED: new connections still use {chosen ?? "an unknown adapter"} instead of {expected}");
    }

    /// <summary>Main connection is back: new connections already use it, but a download that started on the backup would keep trying to use
    /// the backup. Reset those connections so the program reconnects (and resumes) over the main connection.
    /// Only watched programs and anything pulling heavy data are touched; games and light traffic are left alone.</summary>
    void MoveRunningToPrimary()
    {
        if (!_execute) { Log($"  # (would move running downloads from {_bName} onto {_pName}: watched programs and anything above 1 MB per 10 s)"); return; }
        string pn = _pName, bn = _bName; var rules = _policy.BackupRules;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2500);   // let the new routing settle first, so the reconnect lands on the main connection
                var ips = AdapterProber.Find(bn)?.GetIPProperties().UnicastAddresses.Select(u => u.Address).ToList() ?? new();
                var move = new List<(TcpMigrator.Conn Conn, string Name)>();
                foreach (var c in TcpMigrator.Established(ips))
                {
                    var path = ProcPath.Of(c.Pid);
                    if (path is not null && PolicyBuilder.ShouldMove(path, Usage.PeakFor(System.IO.Path.GetFileName(path)), rules)) move.Add((c, System.IO.Path.GetFileName(path)));
                }
                if (move.Count == 0) { Log("  [migrate] no running downloads to move"); return; }
                int n = TcpMigrator.Reset(move.Select(m => m.Conn));
                Log($"  [migrate] moved {n} connection(s) of {string.Join(", ", move.Select(m => m.Name).Distinct())} from {bn} onto {pn}; they reconnect on {pn}");
            }
            catch (Exception e) { Log("  [migrate] FAILED: " + e.Message); }
        });
    }

    void StartWatch()
    {
        var watched = _policy.BackupRules.Where(r => r.Action.Equals("watch", StringComparison.OrdinalIgnoreCase)).ToList();
        if (watched.Count == 0) return;
        string desc = string.Join(", ", watched.Select(r => $"{r.Name} >{_policy.MbFor(r)}MB/{_policy.WindowFor(r)}s" + (_policy.UsesGlobal(r) ? "" : " (custom)")));
        if (!_execute) { Log($"  # (would start download watchdog: {desc})"); return; }
        var ips = AdapterProber.Find(_bName)!.GetIPProperties().UnicastAddresses
            .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork).Select(u => u.Address).ToList();
        _etw = new EtwNetSource(ips, (path, bytes) => { Usage.Add(path, bytes); var a = _guard.Record(path, bytes, DateTime.UtcNow); if (a is not null) Task.Run(() => { Act(a); WatchdogBlocked?.Invoke(a); }); });
        Log($"  [watchdog] started: {desc}");
    }

    void Act(GuardAction a)
    {
        Log($"  [watchdog] {a.Kind.ToString().ToUpper()} {a.Rule} ({a.Path})");
        BackupPolicy.Run(new[] { a.Kind == GuardKind.Block ? DownloadGuard.BlockCommand(a, _bName) : DownloadGuard.UnblockCommand(a) }, false, Log);
    }

    void StopWatch()
    {
        _etw?.Dispose(); _etw = null;
        foreach (var u in _guard.DrainAll()) Act(u);
    }

    void Cleanup()
    {
        lock (_gate)
        {
            if (!_enforce) return;
            StopWatch();
            if (_touched)
            {
                Log("Restoring original metrics and removing rules...");
                BackupPolicy.Run(MetricSwitcher.RestoreFromState().Concat(BackupPolicy.RemoveCommands()), false, Log);
                MetricSwitcher.ClearState(); _touched = false; _applied = false;
            }
        }
    }
}
