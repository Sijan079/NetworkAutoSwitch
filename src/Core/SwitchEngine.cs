using System.Net.Sockets;

namespace NetworkAutoSwitch.Core;

public record EngineStatus(int Tick, Active Active, ProbeResult Primary, ProbeResult Backup, bool Changed, string Reason, bool Paused, string EtwInfo, ConnectionMode Mode = ConnectionMode.Auto, bool Degraded = false);

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
    // Detection speed: 3 failed checks in a row (each gives up after ~1.2 s, re-checked at once) = about 4 s.
    // Coming back is deliberately slower and is a SETTING (default 10 s of good checks), so a flickering Wi-Fi does not bounce you between connections.
    const int FailAfter = 3;
    // ---- idle-backup checks (every one costs a little mobile data) and the daily data cap ----
    ProbeResult? _lastBackupRaw; DateTime? _lastBackupAt; string _lastBackupProbedName = ""; bool _lastPrimaryOk = true;
    int _backupCheckSeconds = 10;
    readonly string _usageFile, _programsFile; DataBudget _budget; long _capMb; readonly Dictionary<string, long> _counters = new(StringComparer.OrdinalIgnoreCase); DateTime _budgetSavedAt = DateTime.UtcNow;
    /// <summary>Raised when the backup's data today reaches 80 % of the daily cap, and when it reaches the cap: (level, bytes today, cap in bytes). Any thread.</summary>
    public event Action<BudgetLevel, long, long>? BudgetAlert;
    /// <summary>Seconds between checks of the idle backup while the main connection is fine.</summary>
    public int BackupCheckSeconds => _backupCheckSeconds;
    public void SetBackupCheckSeconds(int seconds)
    {
        _backupCheckSeconds = Math.Clamp(seconds, 1, 60);
        AppSettings.Update(s => s.BackupCheckSeconds = _backupCheckSeconds, _settingsFile);
        Log($"settings: the idle backup is checked every {_backupCheckSeconds} s");
    }
    /// <summary>The daily limit for the backup connection in MB (0 = none).</summary>
    public int DailyCapMB => (int)_capMb;
    public void SetDailyCapMB(int mb)
    {
        _capMb = Math.Max(0, mb); _budget.CapChanged();
        AppSettings.Update(s => s.DailyCapMB = (int)_capMb, _settingsFile);
        Log(_capMb == 0 ? "settings: no daily data cap on the backup" : $"settings: daily data cap on the backup is {_capMb} MB");
    }
    /// <summary>Bytes that went through the backup adapter today (everything on it, not just this app's programs).</summary>
    public long BackupBytesToday => _budget.Today(DateTime.Now);

    VpnFallback.Backoff? _backoff; bool _degraded;
    /// <summary>True while a VPN refuses the per-connection internet check and the app works from what it can still know.</summary>
    public bool Degraded => _degraded;
    int _recoverSeconds = 10, _recoverAfter = 10;      // seconds the main connection must stay good / the same expressed as a number of checks
    FailoverMonitor _mon = new(FailAfter, 10);
    readonly SemaphoreSlim _nudge = new(0, int.MaxValue);   // "check now": released by network-change events and by mode changes
    readonly string? _settingsFile;                       // null = the real settings file (tests pass a temporary one)
    ConnectionMode _mode;
    Active _locked = Active.Primary;
    PolicyFile _policy = new(new());
    DownloadGuard _guard = new(Array.Empty<AppRule>());
    EtwNetSource? _etw;
    string _pName = "", _bName = "";                     // the adapters in use right now (found by detection, unless you or a command-line argument chose them)
    AdapterKind _pKind = AdapterKind.Other, _bKind = AdapterKind.Other;
    string _userPrimary = "", _userBackup = "";           // saved choice of the user; "" = detect automatically
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
    public AdapterKind PrimaryKind => _pKind;
    public AdapterKind BackupKind => _bKind;
    public string UserPrimary => _userPrimary;
    public string UserBackup => _userBackup;
    /// <summary>Raised when the main or backup adapter changes (a phone was plugged in, an adapter was renamed, or you chose different ones).</summary>
    public event Action? AdaptersChanged;
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
            var prot = BackupNetworks.ProtectedAdapters(_mon.Active, _pName, _bName, _mainProtected);
            bool onBackup = _enforce && _touched && prot.Count > 0 && !_paused;
            if (onBackup) { StopWatch(); BackupPolicy.Run(BackupPolicy.RemoveCommands(), !_execute, Log); }
            _policy = fresh; _guard = new DownloadGuard(_policy.BackupRules, _policy.Defaults);
            if (onBackup) { foreach (var ad in prot) BackupPolicy.Run(BackupPolicy.ApplyCommands(_policy, ad), !_execute, Log); StartWatch(prot); }
            Log("policy reloaded" + (onBackup ? " and re-applied on the backup" : ""));
        }
    }

    public SwitchEngine(string primary, string backup, string policyFile, bool enforce, bool execute, int intervalSec, string? settingsFile = null)
    { _primaryArg = primary; _backupArg = backup; _policyFile = policyFile; _enforce = enforce; _execute = execute; _interval = intervalSec; _settingsFile = settingsFile;
      var saved = AppSettings.Load(settingsFile); _mode = saved.ConnectionMode; _userPrimary = saved.PrimaryAdapter ?? ""; _userBackup = saved.BackupAdapter ?? ""; _pName = saved.LastPrimary ?? ""; _bName = saved.LastBackup ?? "";
      _tables = NetworkTables.From(saved);
      _backupCheckSeconds = Math.Clamp(saved.BackupCheckSeconds, 1, 60); _capMb = Math.Max(0, saved.DailyCapMB);
      _usageFile = settingsFile is null ? Paths.UsageFile : Path.ChangeExtension(settingsFile, ".usage.json"); _budget = DataBudget.Load(_usageFile, DateTime.Now);
      _programsFile = settingsFile is null ? Paths.ProgramsFile : Path.ChangeExtension(settingsFile, ".programs.json"); if (execute) Usage.LoadFrom(_programsFile, DateOnly.FromDateTime(DateTime.Now));       // a dry run (tests, snapshots with sample data) never reads or writes the real file
      _recoverSeconds = Math.Clamp(saved.RecoverSeconds, 3, 600); _recoverAfter = Math.Max(1, (int)Math.Round(_recoverSeconds / (double)Math.Max(1, intervalSec))); _mon.SetRecoverAfter(_recoverAfter);
      ResolveAdapters(initial: true); _ = Task.Run(RefreshNetworkNames); }

    string Mode => !_enforce ? "decide-only" : _execute ? "ENFORCE (live)" : "enforce DRY-RUN";

    static string? First(string? a, string? b) => !string.IsNullOrWhiteSpace(a) ? a : !string.IsNullOrWhiteSpace(b) ? b : null;

    /// <summary>How many seconds the main connection must work, without a single failed check, before the app moves back from the backup.</summary>
    public int RecoverSeconds => _recoverSeconds;

    /// <summary>Change that time (3 to 600 s). Remembered across restarts and applied at once, including to a count that is already running.</summary>
    public void SetRecoverSeconds(int seconds)
    {
        lock (_gate)
        {
            _recoverSeconds = Math.Clamp(seconds, 3, 600);
            _recoverAfter = Math.Max(1, (int)Math.Round(_recoverSeconds / (double)Math.Max(1, _interval)));
            _mon.SetRecoverAfter(_recoverAfter);
            AppSettings.Update(s => s.RecoverSeconds = _recoverSeconds, _settingsFile);
            Log($"settings: switch back to the main connection after {_recoverSeconds} s of good checks");
        }
    }

    // ---- "busy": true while the engine is changing the network (switching, applying a choice, restoring). The window shows placeholders meanwhile. ----
    int _busy;
    /// <summary>Raised with true when the engine starts changing the network and with false when it is done.</summary>
    public event Action<bool>? Busy;
    void BeginBusy() { if (Interlocked.Increment(ref _busy) == 1) Busy?.Invoke(true); }
    void EndBusy() { if (Interlocked.Decrement(ref _busy) == 0) Busy?.Invoke(false); }

    // ---- the name of the network each adapter is connected to (for display) ----
    Dictionary<string, string> _networks = new(StringComparer.OrdinalIgnoreCase);
    volatile bool _namesDirty = true; DateTime _namesAt = DateTime.MinValue;
    public string PrimaryNetwork => _networks.GetValueOrDefault(_pName, "");
    public string BackupNetwork => _bName.Length > 0 ? _networks.GetValueOrDefault(_bName, "") : "";
    /// <summary>Where the network names come from (the tests replace it).</summary>
    public Func<Dictionary<string, string>> NameSource { get; set; } = NetworkNames.Read;
    /// <summary>The same-router check: (adapters, main, backup, main network, backup network). The tests replace it.</summary>
    public Func<IReadOnlyList<AdapterInfo>, string, string, string, string, bool> SameNetworkCheck { get; set; } = (all, p, b, pnet, bnet) => SameNetwork.Check(all, p, b, pnet, bnet);
    volatile bool _nameUnknown; bool _namesBrokenLogged;
    // ---- what each program is doing on the network (one always-on session) ----
    volatile Dictionary<string, string> _ipRole = new();      // this PC's IPv4 addresses of the main and backup adapters -> "Main" / "Backup"
    volatile HashSet<string> _guardIps = new();               // addresses of the PROTECTED adapters: only downloads there can be blocked

    Dictionary<string, string> IpRoles(string pn, string bn)
    {
        var map = new Dictionary<string, string>();
        void Add(string name, string role)
        {
            if (name.Length == 0) return;
            try
            {
                var props = AdapterProber.Find(name)?.GetIPProperties();
                if (props is null) return;
                foreach (var u in props.UnicastAddresses) if (u.Address.AddressFamily == AddressFamily.InterNetwork) map[u.Address.ToString()] = role;
            }
            catch { }
        }
        Add(pn, "Main"); Add(bn, "Backup");
        return map;
    }

    void RefreshNetworkNames()
    {
        var n = NameSource();
        string pn, bn; AdapterKind bk; lock (_gate) { _networks = n; pn = _pName; bn = _bName; bk = _bKind; }
        var all = AdapterDiscovery.Detect();
        bool Up(string name) => name.Length > 0 && all.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && a.IsUp && a.HasGateway);
        string pnet = n.GetValueOrDefault(pn, ""), bnet = bn.Length > 0 ? n.GetValueOrDefault(bn, "") : "";
        _ipRole = IpRoles(pn, bn);
        bool same = bn.Length > 0 && SameNetworkCheck(all, pn, bn, pnet, bnet);
        SetSameNetwork(same);
        AskAbout(pnet, _pKind); AskAbout(bnet, bk);
        var t = _tables;
        bool backupOnMain = bnet.Length > 0 && !BackupNetworks.BackupAllowed(t.RoleOf(bnet));      // the backup adapter joined a network that may not be the backup
        if (backupOnMain != _backupOnMainNet)
        {
            _backupOnMainNet = backupOnMain;
            Log(backupOnMain ? $"WARNING: '{bn}' is on '{bnet}', which may not be a backup, so it is not used as the backup." : "the backup connection is on an allowed network again");
        }

        // Names. "Windows gave no name for ANY adapter" means the lookup itself is not working: nothing can be judged, so the main connection is left alone (said once).
        // An adapter that is up but has no name while the others do is simply not identified YET: protect the main connection until it is (fail closed), and look again every 2 s.
        bool lookupWorks = n.Count > 0;
        if (!lookupWorks && (Up(pn) || Up(bn)) && !_namesBrokenLogged) { _namesBrokenLogged = true; Log("WARNING: Windows gives no network names, so the main connection cannot be checked against your tables."); }
        if (lookupWorks) _namesBrokenLogged = false;
        bool mainUnidentified = lookupWorks && bn.Length > 0 && Up(pn) && pnet.Length == 0;
        _nameUnknown = lookupWorks && ((Up(pn) && pnet.Length == 0) || (Up(bn) && bnet.Length == 0));
        var mainRole = mainUnidentified ? NetworkRole.Unlabelled : (bn.Length == 0 || pnet.Length == 0 ? NetworkRole.Main : t.RoleOf(pnet));
        SetMainProtection(same, mainRole, pnet, identified: !mainUnidentified);
    }

    // ---- the two network tables. The role belongs to the NETWORK and follows it, whichever adapter it is on. ----
    volatile NetworkTables _tables = new(ListMode.Blacklist, new(), new(), new(), new());      // replaced as a whole, never edited in place (read from several threads)
    volatile bool _mainProtected, _backupOnMainNet; string _mainReason = "";
    bool _rulesLive;                                                        // our firewall rules may exist right now
    List<string> _watchAdapters = new();                                    // the adapters the running watchdog protects
    /// <summary>The mode and both tables, as saved.</summary>
    public NetworkTables Tables => _tables;
    /// <summary>The global watch trigger: megabytes per 10 seconds (or whatever window is set) a watched program may download before it is blocked.</summary>
    public double TriggerMb => _policy.Global.MbPerWindow;
    /// <summary>True while the MAIN connection gets the backup's protections (it is on a backup / unlabelled network, or on the same network as the backup).</summary>
    public bool MainProtected => _mainProtected;
    public string MainProtectedReason => _mainReason;
    public event Action<bool>? MainProtectionChanged;
    /// <summary>Raised ONCE per network that is on neither table: the window asks "main or backup?" and calls SetNetworkRole. Any thread.</summary>
    public event Action<string>? NetworkNeedsLabel;
    volatile List<string> _asked = new();
    void AskAbout(string network, AdapterKind kind)
    {
        var t = _tables;
        if (!BackupNetworks.ShouldAsk(network, kind, t.Mode, t.Main, t.Backup, _asked)) return;
        _asked = _asked.Append(network.Trim()).ToList();
        Log($"asking what the network '{network}' is (main or backup)");
        NetworkNeedsLabel?.Invoke(network);
    }
    public NetworkRole RoleOf(string? network) => _tables.RoleOf(network);

    /// <summary>Say that a network is Main or Backup (in the current mode). Remembered across restarts and applied at once.</summary>
    public void SetNetworkRole(string network, NetworkRole role)
    {
        if (string.IsNullOrWhiteSpace(network) || role is not (NetworkRole.Main or NetworkRole.Backup)) return;
        var t = _tables.WithRole(network, role);
        ApplyTables(t);
        Log(role == NetworkRole.Backup ? $"settings: '{network.Trim()}' is a BACKUP network (it gets the backup's protections on whichever adapter it is on)" : $"settings: '{network.Trim()}' is a MAIN network");
    }

    /// <summary>Replace the mode and all four lists (the Choose Connections window saves this).</summary>
    public void SetNetworkTables(NetworkTables t)
    {
        ApplyTables(t);
        Log($"settings: network tables saved ({t.Mode}: {t.Main.Count} in the main table, {t.Backup.Count} in the backup table)");
    }

    void ApplyTables(NetworkTables t)
    {
        _tables = t;
        AppSettings.Update(s => t.WriteTo(s), _settingsFile);
        _namesDirty = true; try { _nudge.Release(); } catch (SemaphoreFullException) { }
    }

    volatile bool _mainAlarm;
    /// <summary>True while the main connection is (probably) your phone, or both connections are one network: the window shows a red banner and a pop-up that ignores mute.</summary>
    public bool MainProtectionAlarm => _mainAlarm;

    void SetMainProtection(bool same, NetworkRole mainRole, string mainNet, bool identified = true)
    {
        string reason = BackupNetworks.MainProtectionReason(same, mainRole, identified);
        bool prot = reason.Length > 0, alarm = prot && BackupNetworks.IsAlarming(same, mainRole, identified), changed;
        lock (_gate) { changed = prot != _mainProtected || alarm != _mainAlarm; _mainProtected = prot; _mainAlarm = alarm; _mainReason = reason; if (changed) _applied = false; }   // not applied yet: the next tick puts the protections in place
        if (!changed) return;
        Log(prot ? $"PROTECTING the main connection '{_pName}'{(mainNet.Length > 0 ? $" ({mainNet})" : "")} like the backup: {reason}."
                 : $"the main connection '{_pName}' no longer needs the backup's protection");
        MainProtectionChanged?.Invoke(prot);
        try { _nudge.Release(); } catch (SemaphoreFullException) { }
    }

    // ---- guard: main and backup are really the same network, so a switch would change nothing ----
    volatile bool _sameNetwork;
    /// <summary>True while the main and the backup adapter are connected to the same network (same router). Switching between them cannot help then.</summary>
    public bool SameNetworkWarning => _sameNetwork;
    /// <summary>Raised when that changes: true = they are now the same network, false = fixed. Any thread.</summary>
    public event Action<bool>? SameNetworkChanged;
    void SetSameNetwork(bool same)
    {
        if (same == _sameNetwork) return;
        _sameNetwork = same;
        Log(same ? $"WARNING: '{_pName}' and '{_bName}' are on the same network (same router), so switching between them cannot keep you online. Connect one of them to a different network (for example your phone's hotspot)."
                 : "the main and backup connections are on different networks now");
        SameNetworkChanged?.Invoke(same);
    }

    /// <summary>Works out which adapters are the main and the backup connection right now. Priority: a name given on the command line,
    /// then your saved choice, then detection (which keeps what is already in use while it still exists).
    /// If the pair changes while the app is managing the network, the old pair is put back first, so nothing is left half-changed.</summary>
    public void ResolveAdapters(bool initial = false)
    {
        var all = AdapterDiscovery.Detect();
        var (sp, sb) = AdapterDiscovery.Suggest(all, _pName, _bName);
        string p = First(_primaryArg, _userPrimary) ?? sp ?? _pName;
        string b = First(_backupArg, _userBackup) ?? sb ?? "";
        if (b.Equals(p, StringComparison.OrdinalIgnoreCase)) b = "";
        // the labels beat the adapter choice: the adapter on a Main network is the main connection, the one on a Backup network is the backup, whichever adapter that is
        var snap = _networks;
        var byLabel = BackupNetworks.Assign(AdapterDiscovery.Candidates(all).Where(a => a.IsUp && a.HasGateway && snap.ContainsKey(a.Name)).Select(a => (a.Name, snap[a.Name])).ToList(), _tables.Mode, _tables.Main, _tables.Backup, p, b);
        if (byLabel is { } roles) { p = roles.Primary; b = roles.Backup; }
        // hard rule, whatever the adapter choice says: a network that may not be the backup is never used as the backup
        if (b.Length > 0 && snap.TryGetValue(b, out var bNet) && !BackupNetworks.BackupAllowed(_tables.RoleOf(bNet))) b = "";
        AdapterKind Kind(string n) => all.FirstOrDefault(a => a.Name.Equals(n, StringComparison.OrdinalIgnoreCase))?.Kind ?? AdapterKind.Other;

        bool changed;
        lock (_gate)
        {
            _pKind = Kind(p); _bKind = Kind(b);
            changed = p != _pName || b != _bName;
            if (changed)
            {
                if (!initial)
                {
                    BeginBusy();
                    try
                    {
                        Log($"adapters changed: main '{_pName}' -> '{p}', backup '{_bName}' -> '{(b.Length > 0 ? b : "none")}'");
                        Cleanup();                           // put the OLD pair back (their saved metrics) before switching to the new one
                        _mon = FreshMonitor();
                        _namesDirty = true;
                    }
                    finally { EndBusy(); }
                }
                _pName = p; _bName = b; _applied = false;
                AppSettings.Update(s => { s.LastPrimary = p; s.LastBackup = b; }, _settingsFile);
            }
        }
        if (changed && !initial) AdaptersChanged?.Invoke();
    }

    /// <summary>Choose the adapters yourself ("" or null = detect that one automatically). Remembered across restarts.</summary>
    public void SetAdapterChoice(string? primary, string? backup)
    {
        lock (_gate)
        {
            _userPrimary = primary ?? ""; _userBackup = backup ?? "";
            AppSettings.Update(s => { s.PrimaryAdapter = _userPrimary; s.BackupAdapter = _userBackup; }, _settingsFile);
        }
        ResolveAdapters();
        _nudge.Release();
    }

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

    FailoverMonitor FreshMonitor() { var m = new FailoverMonitor(FailAfter, _recoverAfter); if (_mode != ConnectionMode.Auto) m.Reset(_locked); return m; }

    void OnNetworkChanged(object? s, EventArgs e) { _namesDirty = true; try { _nudge.Release(); } catch (SemaphoreFullException) { } }

    /// <summary>Runs until ticks are used up (0 = forever) or the token is cancelled. Always restores on exit.</summary>
    public async Task RunAsync(int ticks, CancellationToken ct)
    {
        ResolveAdapters(initial: true);
        _policy = BackupPolicy.Load(_policyFile);
        _guard = new DownloadGuard(_policy.BackupRules, _policy.Defaults);
        if (_mode != ConnectionMode.Auto) { _locked = _mode == ConnectionMode.StayBackup ? Active.Backup : Active.Primary; _mon.Reset(_locked); }
        Log($"primary='{_pName}' backup='{_bName}' every {_interval}s ({(ticks == 0 ? "until stopped" : ticks + " ticks")}) mode={Mode}"
            + (_mode == ConnectionMode.Auto ? "" : $" LOCKED to {(_mode == ConnectionMode.StayBackup ? _bName : _pName)}"));

        StartEtw();
        await Task.Run(RefreshNetworkNames);      // know which networks we are on, and protect the main connection if needed, BEFORE the first switch decision

        // Windows tells us the moment a network link changes (Wi-Fi dropped, cable pulled): check straight away instead of waiting for the next tick
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        try
        {
            for (int t = 1; (ticks == 0 || t <= ticks) && !ct.IsCancellationRequested; t++)
            {
                ResolveAdapters();       // a phone plugged in, an adapter renamed or removed: noticed within a second
                if (_namesDirty || DateTime.UtcNow - _namesAt > TimeSpan.FromSeconds(_nameUnknown ? 2 : 30)) { _namesDirty = false; _namesAt = DateTime.UtcNow; _ = Task.Run(RefreshNetworkNames); }   // which network each adapter is on (about 50 ms, off the loop)
                string pn = _pName, bn = _bName;
                // the idle backup is not checked every second (each check costs a little mobile data): see ProbeSchedule
                bool activeIsBackup = (_mode == ConnectionMode.Auto ? _mon.Active : _locked) == Active.Backup;
                bool probeB = ProbeSchedule.ProbeBackupNow(DateTime.UtcNow, _lastBackupAt, _backupCheckSeconds, activeIsBackup, _lastPrimaryOk, _lastBackupRaw?.Healthy ?? false,
                    mustProbe: _lastBackupRaw is null || !bn.Equals(_lastBackupProbedName, StringComparison.OrdinalIgnoreCase));
                var p = Task.Run(() => AdapterProber.Probe(pn), CancellationToken.None);
                var b = probeB ? Task.Run(() => AdapterProber.Probe(bn), CancellationToken.None) : Task.FromResult(_lastBackupRaw!);
                var (pr, br) = (await p, await b);
                if (probeB) { _lastBackupRaw = br; _lastBackupAt = DateTime.UtcNow; _lastBackupProbedName = bn; }
                bool degraded = false;
                if (VpnFallback.IsBlocked(pr) || VpnFallback.IsBlocked(br))
                {
                    bool activeIsPrimary = (_mode == ConnectionMode.Auto ? _mon.Active : _locked) == Active.Primary;
                    var (pr0, br0) = (pr, br);
                    (pr, br, degraded) = await Task.Run(() => VpnFallback.Apply(pr0, br0, activeIsPrimary, () => AdapterProber.ProbeAny(), n => VpnFallback.PingGateway(n)));
                }
                SetDegraded(degraded); ReportUnnamed();
                _lastPrimaryOk = pr.Healthy; TrackBackupData();
                Decision d;
                lock (_gate)
                {
                    if (_mode == ConnectionMode.Auto)
                    {
                        d = _mon.Observe(pr.Healthy, br.Healthy, primaryLinkDown: AdapterProber.IsLinkDown(pr.Detail));
                        _backoff ??= new VpnFallback.Backoff(() => _recoverSeconds);
                        if (d.Changed) _backoff.OnSwitch(d.Active, DateTime.UtcNow, degraded);
                        _backoff.Settle(DateTime.UtcNow, d.Active);
                        _mon.SetRecoverAfter(Math.Max(1, (int)Math.Round(_backoff.Seconds / (double)Math.Max(1, _interval))));
                        if (_enforce && !_paused)
                        {
                            if (d.Changed) Enforce(d.Active, moveRunning: d.Active == Active.Primary);          // a real switch (back to main: also move running downloads)
                            else if (d.Active == Active.Primary && pr.Healthy && !_applied) Enforce(Active.Primary, moveRunning: true);   // main is healthy: make sure Windows really prefers it (and that downloads still on the backup move over)
                            else if (d.Active == Active.Backup && !_applied) Enforce(Active.Backup, moveRunning: false);                  // e.g. the main connection just turned out to be a backup network: put its protections in place
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
                    _etw is null ? "" : $"etw={_etw.EventsMatched}/{_etw.EventsSeen}", _mode, degraded);
                Stats.Record(status, DateTime.Now, backupFresh: probeB);
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
            _budget.Save(_usageFile); if (_execute) Usage.Save(_programsFile);
            Cleanup(); StopEtw();
        }
    }

    /// <summary>Adds what the backup adapter carried since the last look to today's total, and says so when a limit is reached.</summary>
    void TrackBackupData()
    {
        try
        {
            // the backup adapter, and the main one too while it is really a backup network (one counter per adapter)
            var counted = new List<string>(); if (_bName.Length > 0) counted.Add(_bName); if (_mainProtected && _pName.Length > 0 && _bName.Length > 0) counted.Add(_pName);
            foreach (var k in _counters.Keys.Where(k => !counted.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList()) _counters.Remove(k);
            long delta = 0;
            foreach (var name in counted)
            {
                var nic = AdapterProber.Find(name);
                if (nic is null) { _counters.Remove(name); continue; }
                var st = nic.GetIPv4Statistics(); long total = st.BytesReceived + st.BytesSent;
                delta += CounterDelta.Next(_counters.TryGetValue(nic.Name, out var prev) ? prev : null, total); _counters[nic.Name] = total;
            }
            long cap = _capMb * 1_000_000L;
            if (_budget.Add(delta, DateTime.Now, cap) is BudgetLevel level)
            {
                Log(level == BudgetLevel.Reached ? $"DATA CAP reached: {_budget.Bytes / 1e6:0} MB of {_capMb} MB used on {_bName} today" : $"data cap warning: {_budget.Bytes / 1e6:0} MB of {_capMb} MB used on {_bName} today");
                BudgetAlert?.Invoke(level, _budget.Bytes, cap);
            }
            if (DateTime.UtcNow - _budgetSavedAt > TimeSpan.FromSeconds(30)) { _budget.Save(_usageFile); if (_execute) Usage.Save(_programsFile); _budgetSavedAt = DateTime.UtcNow; }
        }
        catch { /* the adapter vanished between two lines: the next tick starts over */ }
    }

    long _unnamedBytes; DateTime _unnamedAt = DateTime.MinValue;
    /// <summary>The watchdog cannot block a program it cannot name. Say so (at most every 30 s) instead of letting that data pass unseen.</summary>
    void ReportUnnamed()
    {
        if (_etw?.TakeUnnamed() is not { } u) return;
        _unnamedBytes += u.Bytes;
        if (_unnamedBytes < 5_000_000 || DateTime.UtcNow - _unnamedAt < TimeSpan.FromSeconds(30)) return;
        Log($"  [watchdog] WARNING: {_unnamedBytes / 1e6:0} MB came in on a protected connection from programs Windows would not name ({u.Who}). They cannot be watched or blocked.");
        _unnamedBytes = 0; _unnamedAt = DateTime.UtcNow;
    }

    void SetDegraded(bool on)
    {
        if (on == _degraded) return;
        _degraded = on;
        Log(on ? "VPN: it refuses the check on a single connection (AccessDenied). Using the VPN's own path for the connection in use, and the gateway for the other one. Switching back is a guess until the main connection proves itself, so it waits longer after a failed attempt."
               : "VPN: per-connection checks work again.");
    }

    /// <summary>Pause: put Windows back to normal and stop switching until Resume.</summary>
    public void Pause() { lock (_gate) { if (_paused) return; _paused = true; BeginBusy(); try { Cleanup(); } finally { EndBusy(); } _mon = FreshMonitor(); Log("paused: original metrics restored"); } }
    public void Resume() { lock (_gate) { if (!_paused) return; _paused = false; _mon = FreshMonitor(); Log("resumed"); } _nudge.Release(); }

    /// <summary>Makes Windows prefer <paramref name="a"/>: the preferred adapter gets the lowest metric, the other the highest.
    /// Windows' own defaults rank the tether ABOVE Wi-Fi, so the main connection must be set explicitly too, not just "restored".
    /// The original metrics are saved first and put back when the app pauses or exits.</summary>
    void Enforce(Active a, bool moveRunning)
    {
        BeginBusy();                          // the window shows placeholders until this is done
        try { EnforceCore(a, moveRunning); } finally { EndBusy(); }
    }

    void EnforceCore(Active a, bool moveRunning)
    {
        if (a == Active.Primary) StopWatch();
        // With only ONE connection there is nothing to prefer over anything: the metric commands (and the backup rules) are skipped.
        bool both = _pName.Length > 0 && _bName.Length > 0;
        if (_execute && both && !_touched) { MetricSwitcher.SaveOriginals(_pName, _bName); _touched = true; }
        // Which adapters get the backup's protections: the backup while it is in use, and the main one whenever it is really a backup network.
        var prot = BackupNetworks.ProtectedAdapters(a, _pName, _bName, _mainProtected);
        var cmds = new List<string>();
        if (both) cmds.AddRange(a == Active.Backup ? MetricSwitcher.PreferCommands(_bName, _pName) : MetricSwitcher.PreferCommands(_pName, _bName));
        // Removing deletes the app's whole firewall group, so it must finish BEFORE new rules are added (they run in parallel with each other, not with the removal).
        if (prot.Count == 0 || _rulesLive) cmds.AddRange(BackupPolicy.RemoveCommands());
        BackupPolicy.RunParallel(cmds, !_execute, Log);      // independent commands: all at once, so the switch is as fast as one command
        var rules = new List<string>(); foreach (var ad in prot) rules.AddRange(BackupPolicy.ApplyCommands(_policy, ad));
        if (rules.Count > 0) BackupPolicy.RunParallel(rules, !_execute, Log);
        _rulesLive = prot.Count > 0;
        _applied = true;
        if (_execute && both) VerifyRoute(a == Active.Backup ? _bName : _pName, a == Active.Backup ? _pName : _bName);
        if (prot.Count > 0) StartWatch(prot);
        if (a == Active.Primary && moveRunning && !_mainProtected) MoveRunningToPrimary();    // when main is itself a backup network there is nothing to move off the backup
    }

    /// <summary>Don't trust the numbers: ask Windows which adapter a new connection really takes now. If it is the wrong one,
    /// push the other adapter's route further back and ask again, and say so in the log.</summary>
    void VerifyRoute(string expected, string other)
    {
        var chosen = MetricSwitcher.RouteChoice();
        if (chosen is null) return;
        if (chosen.Equals(expected, StringComparison.OrdinalIgnoreCase)) { Log($"  [route] new connections now use {chosen}"); return; }
        if (!chosen.Equals(other, StringComparison.OrdinalIgnoreCase))
        {   // neither of our two: a VPN tunnel (or another virtual adapter) sits on top. It sends its traffic over whichever of our adapters Windows prefers, so there is nothing to fix.
            Log($"  [route] new connections go through {chosen} (a tunnel), which follows {expected}");
            return;
        }
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

    void StartWatch(IReadOnlyList<string> adapters)
    {
        _watchAdapters = adapters.ToList();
        var watched = _policy.BackupRules.Where(r => r.Action.Equals("watch", StringComparison.OrdinalIgnoreCase)).ToList();
        if (watched.Count == 0) { _guardIps = new(); return; }
        string desc = string.Join(", ", watched.Select(r => $"{r.Name} >{_policy.MbFor(r)}MB/{_policy.WindowFor(r)}s" + (_policy.UsesGlobal(r) ? "" : " (custom)")));
        if (!_execute) { Log($"  # (would start download watchdog: {desc})"); return; }
        var ips = adapters.Select(AdapterProber.Find).Where(n => n is not null)
            .SelectMany(n => n!.GetIPProperties().UnicastAddresses).Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork).Select(u => u.Address).ToList();
        if (ips.Count == 0) { Log("  [watchdog] cannot start: the protected adapter is not there"); return; }
        if (_etw is null) { Log("  [watchdog] cannot start: per-program traffic is not available (see the warning at start-up)"); return; }
        _guardIps = ips.Select(i => i.ToString()).ToHashSet();       // the session is already running: the watchdog just starts looking at these addresses
        Log($"  [watchdog] started: {desc}");
    }

    /// <summary>The one network-event session of the app: it feeds the Network Activity table for the main connection and the backup, and the watchdog for the protected ones.</summary>
    void StartEtw()
    {
        if (!_execute || _etw is not null) return;
        try { _etw = new EtwNetSource(OnNetEvent, (x, y) => { var g = _guardIps; return g.Contains(x) || g.Contains(y); }); }
        catch (Exception e) { Log("  [activity] WARNING: per-program traffic cannot be read (" + e.Message + "). The Network Activity table and the download watchdog are off."); }
    }

    void StopEtw() { _guardIps = new(); _etw?.Dispose(); _etw = null; }

    void OnNetEvent(string path, long bytes, bool inbound, string a, string b)
    {
        var roles = _ipRole;
        string? conn = roles.GetValueOrDefault(a) ?? roles.GetValueOrDefault(b);
        if (conn is null) return;                      // not the main connection or the backup (a VPN tunnel, a virtual adapter): its bytes are counted on the real adapter
        var now = DateTime.UtcNow;
        if (!inbound) { Usage.AddUp(path, bytes, conn); return; }
        Usage.Add(path, bytes, now, conn);
        var g = _guardIps;
        if (g.Count > 0 && (g.Contains(a) || g.Contains(b)) && _guard.Record(path, bytes, now) is { } act) Task.Run(() => { Act(act); WatchdogBlocked?.Invoke(act); });
    }

    void Act(GuardAction a)
    {
        Log($"  [watchdog] {a.Kind.ToString().ToUpper()} {a.Rule} ({a.Path})");
        if (a.Kind != GuardKind.Block) { BackupPolicy.Run(new[] { DownloadGuard.UnblockCommand(a) }, false, Log); return; }
        var targets = _watchAdapters.Count > 0 ? _watchAdapters : new List<string> { _bName };       // one block rule per protected adapter
        BackupPolicy.Run(targets.Select(t => DownloadGuard.BlockCommand(a, t)), false, Log);
    }

    void StopWatch()
    {
        _guardIps = new();       // the session keeps counting for the table; only the blocking stops
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
                BackupPolicy.RunParallel(MetricSwitcher.RestoreFromState().Concat(BackupPolicy.RemoveCommands()), false, Log);   // every command runs; one failure cannot skip the others
                MetricSwitcher.ClearState(); _touched = false; _applied = false; _rulesLive = false;
            }
        }
    }
}
