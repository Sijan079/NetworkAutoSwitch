using System.Net.NetworkInformation;
using NetworkAutoSwitch.Core;

// Usage: nas-cli [--primary "Wi-Fi"] [--backup "Ethernet 2"] [--interval 5] [--ticks 6] [--selftest]
// Decision-only: it reports which adapter SHOULD be active. It never changes routes or metrics.
string Arg(string k, string d) { int i = Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }

if (args.Contains("--selftest")) return SelfTest.Run();

// --etw-debug: print what the network events really look like (admin). Read-only, changes nothing.
if (args.Contains("--etw-debug"))
{
    using var s = new Microsoft.Diagnostics.Tracing.Session.TraceEventSession("NetAutoSwitchDebug");
    s.EnableProvider("Microsoft-Windows-Kernel-Network");
    var ids = new Dictionary<int, int>(); int shown = 0;
    s.Source.Dynamic.All += d =>
    {
        int id = (int)d.ID; ids[id] = ids.GetValueOrDefault(id) + 1;
        if (shown < 12 && (id == 11 || id == 43 || d.EventName.Contains("Recv", StringComparison.OrdinalIgnoreCase) || d.EventName.Contains("Receive", StringComparison.OrdinalIgnoreCase)))
        {
            shown++;
            Console.WriteLine($"id={id} name={d.EventName} " + string.Join("  ", d.PayloadNames.Select(p => { var v = d.PayloadByName(p); return $"{p}={v}<{v?.GetType().Name}>"; })));
        }
    };
    _ = Task.Run(async () => { await Task.Delay(15000); s.Source.StopProcessing(); });
    Console.WriteLine("Listening 15 s... (open a web page or run a download on the backup network now)");
    s.Source.Process();
    Console.WriteLine("event ids seen: " + string.Join(", ", ids.OrderBy(k => k.Key).Select(k => $"{k.Key}x{k.Value}")));
    Console.WriteLine("local IPs of Ethernet 2: " + string.Join(", ", (AdapterProber.Find("Ethernet 2")?.GetIPProperties().UnicastAddresses.Select(u => u.Address.ToString()) ?? new string[0])));
    return 0;
}

string primary = Arg("--primary", ""), backup = Arg("--backup", "");   // "" = detect automatically
var found = AdapterDiscovery.Suggest(AdapterDiscovery.Detect());
string backupName = backup.Length > 0 ? backup : found.Backup ?? "";

// --list-adapters: READ-ONLY. Shows every adapter, what kind Windows says it is, and which two the app would use.
if (args.Contains("--list-adapters"))
{
    var all = AdapterDiscovery.Detect();
    Console.WriteLine($"{"Name",-34}{"Kind",-14}{"Up",-5}{"Gateway",-9}{"IPv4",-17}Description");
    foreach (var a in all.OrderBy(a => a.Kind)) Console.WriteLine($"{a.Name,-34}{AdapterDiscovery.KindLabel(a.Kind),-14}{(a.IsUp ? "yes" : "no"),-5}{(a.HasGateway ? "yes" : "no"),-9}{a.Ipv4,-17}{a.Description}");
    Console.WriteLine($"\nmain connection:   {(primary.Length > 0 ? primary + " (chosen)" : found.Primary ?? "none found")}");
    Console.WriteLine($"backup connection: {(backup.Length > 0 ? backup + " (chosen)" : found.Backup ?? "none found")}");
    return 0;
}

// --engine-test: runs the real engine in dry-run (no changes to Windows) with a "main connection" that never has internet, to prove
// that Automatic mode fails over and that a locked mode does NOT. Uses a temporary settings file.
if (args.Contains("--engine-test"))
{
    string tmp = Path.Combine(Path.GetTempPath(), "nas-engine-test-" + Guid.NewGuid().ToString("N") + ".json");
    var logLines = new List<string>(); int fails = 0;
    void Check(string name, bool ok) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}"); if (!ok) fails++; }
    // the test needs a HEALTHY adapter to fail over to: use whichever real adapter has working internet right now (whatever it is called)
    string? testBackup = AdapterDiscovery.Candidates(AdapterDiscovery.Detect()).Select(a => a.Name).FirstOrDefault(n => AdapterProber.Probe(n).Healthy);
    if (testBackup is null) { Console.WriteLine("SKIP  failover and lock tests: no real adapter has working internet right now"); testBackup = "Ethernet 2"; }
    else Console.WriteLine($"(using '{testBackup}' as the healthy backup for this test)");
    var eng = new SwitchEngine("ZeroTier", testBackup, Paths.DefaultPolicy, enforce: true, execute: false, intervalSec: 1, settingsFile: tmp);
    eng.Log = l => { lock (logLines) logLines.Add(l); };
    EngineStatus? last = null; eng.Status += s => last = s;
    using var cts0 = new CancellationTokenSource();
    var run = eng.RunAsync(0, cts0.Token);
    async Task<bool> Until(Func<bool> cond, int seconds) { for (int i = 0; i < seconds * 10; i++) { if (cond()) return true; await Task.Delay(100); } return cond(); }

    eng.SetConnectionMode(ConnectionMode.StayPrimary);
    await Task.Delay(7000);   // long enough for 3 failed checks twice over: an Automatic engine would have switched by now
    Check("locked to main: stays on main although main has no internet (7 s)", last is { Active: Active.Primary, Mode: ConnectionMode.StayPrimary } && !logLines.Any(l => l.StartsWith("SWITCH")));
    Check("locked: the dashboard is told it is locked, with a reason", last is not null && last.Reason.StartsWith("locked"));

    eng.SetConnectionMode(ConnectionMode.StayBackup);
    Check("locked to backup: moves onto the backup when asked, even though main never failed a check in this mode", await Until(() => last is { Active: Active.Backup }, 5));
    Check("locked to backup: the backup rules are applied", logLines.Any(l => l.Contains("would start download watchdog")) || logLines.Any(l => l.Contains("Set-NetIPInterface")));

    eng.SetConnectionMode(ConnectionMode.StayPrimary);
    Check("locked to main again: moves back immediately (no waiting for 10 good checks)", await Until(() => last is { Active: Active.Primary }, 5));

    eng.SetConnectionMode(ConnectionMode.Auto);
    Check("back to automatic: it fails over by itself again (main has no internet)", await Until(() => last is { Active: Active.Backup, Mode: ConnectionMode.Auto }, 12));
    Check("automatic: the switch is logged", logLines.Any(l => l.StartsWith("SWITCH") || l.Contains("switching to backup")) || last!.Changed || last.Active == Active.Backup);
    Check("the chosen mode is remembered in the settings file", AppSettings.Load(tmp).ConnectionMode == ConnectionMode.Auto);

    cts0.Cancel(); try { await run; } catch { }

    // adapters are detected, not hard-coded; a choice made while running takes effect and is announced
    var eng2 = new SwitchEngine("", "", Paths.DefaultPolicy, enforce: true, execute: false, intervalSec: 1, settingsFile: tmp);
    var real = AdapterDiscovery.Candidates(AdapterDiscovery.Detect());
    Check("auto-detect: with no names given, a main connection is found by itself (" + eng2.PrimaryName + ")", eng2.PrimaryName.Length > 0 && real.Any(a => a.Name == eng2.PrimaryName));
    Check("auto-detect: the main connection is never a virtual adapter", AdapterDiscovery.Detect().All(a => a.Name != eng2.PrimaryName || a.Kind != AdapterKind.Virtual));
    if (real.Count >= 2)
    {
        int changes = 0; eng2.AdaptersChanged += () => changes++;
        string pick1 = real[0].Name, pick2 = real[1].Name;
        eng2.SetAdapterChoice(pick2, pick1);
        Check($"choice: choosing '{pick2}' as main and '{pick1}' as backup takes effect at once", eng2.PrimaryName == pick2 && eng2.BackupName == pick1);
        Check("choice: the engine announces the change (so the window and menu update)", changes >= 1);
        Check("choice: it is remembered in the settings file", AppSettings.Load(tmp).PrimaryAdapter == pick2 && AppSettings.Load(tmp).BackupAdapter == pick1);
        eng2.SetAdapterChoice(pick1, pick1);
        Check("choice: the same adapter for both is refused (the backup is left empty)", eng2.PrimaryName == pick1 && eng2.BackupName == "");
        eng2.SetAdapterChoice(null, null);
        Check("choice: back to Automatic detects again", eng2.PrimaryName.Length > 0 && AppSettings.Load(tmp).PrimaryAdapter == "");
    }
    else Console.WriteLine("SKIP  choice tests (this PC has fewer than two real adapters)");
    File.Delete(tmp);
    Console.WriteLine(fails == 0 ? "ENGINE TEST PASSED" : $"{fails} FAILED");
    return fails;
}

// --list-conns: READ-ONLY. Shows which programs have connections open over the backup adapter, and whether they would be moved
// onto the main connection when it returns. Nothing is reset or changed.
if (args.Contains("--list-conns"))
{
    var ips = AdapterProber.Find(backupName)?.GetIPProperties().UnicastAddresses.Select(u => u.Address).ToList() ?? new();
    var pol = BackupPolicy.Load(Arg("--policy", Paths.DefaultPolicy));
    var conns = TcpMigrator.Established(ips);
    Console.WriteLine($"{conns.Count} established connection(s) over '{backupName}' ({string.Join(", ", ips.Where(i => i.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))})");
    foreach (var g in conns.GroupBy(c => ProcPath.Of(c.Pid) ?? $"pid {c.Pid}").OrderByDescending(g => g.Count()))
    {
        string name = Path.GetFileName(g.Key);
        bool watched = PolicyBuilder.IsCovered(g.Key, pol.BackupRules);
        Console.WriteLine($"  {g.Count(),3} x {name,-28} watched={watched,-5}  would be moved on switch-back: {(PolicyBuilder.ShouldMove(g.Key, 0, pol.BackupRules) ? "yes (watched)" : "only if it was pulling heavy data")}");
    }
    return 0;
}
string policyFile = Arg("--policy", Paths.DefaultPolicy);
bool execute = args.Contains("--execute"), enforce = args.Contains("--enforce");

// Policy commands print by default (dry-run). Add --execute to really run them (admin shell required).
if (execute && !BackupPolicy.IsAdmin())
{ Console.WriteLine("Not admin: re-run in an elevated shell, or drop --execute for a dry run."); return 2; }

if (args.Contains("--policy-apply") || args.Contains("--policy-remove"))
{
    var cmds = args.Contains("--policy-apply")
        ? BackupPolicy.ApplyCommands(BackupPolicy.Load(policyFile), AdapterProber.Find(backupName)?.Name ?? backupName)
        : BackupPolicy.RemoveCommands();
    return BackupPolicy.Run(cmds, !execute);
}
// --restore: put the original metrics back and remove our rules (use after a crash). Dry-run unless --execute.
if (args.Contains("--restore"))
{
    int rc = BackupPolicy.Run(MetricSwitcher.RestoreFromState().Concat(BackupPolicy.RemoveCommands()), !execute);
    if (execute && rc == 0) MetricSwitcher.ClearState();
    return rc;
}

int interval = int.Parse(Arg("--interval", "5")), ticks = int.Parse(Arg("--ticks", "6"));
var engine = new SwitchEngine(primary, backup, policyFile, enforce, execute, interval);
engine.Status += s =>
{
    string ps = s.Primary.Healthy ? $"OK {s.Primary.Ms}ms" : $"FAIL {s.Primary.Detail}";
    string bs = s.Backup.Healthy ? $"OK {s.Backup.Ms}ms" : $"FAIL {s.Backup.Detail}";
    Console.WriteLine($"#{s.Tick} primary={ps,-30} backup={bs,-30} -> {s.Active}{(s.Changed ? " [SWITCH]" : "")}  {s.Reason}  {s.EtwInfo}");
};
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };   // Ctrl+C -> clean stop, engine restores everything
await engine.RunAsync(ticks, cts.Token);
return 0;

static class SelfTest
{
    static int fails;
    static void Check(string name, bool cond) { Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}"); if (!cond) fails++; }
    public static int Run()
    {
        var m = new FailoverMonitor(3, 3);
        m.Observe(false, true); m.Observe(false, true);
        Check("2 failures: no switch yet", m.Active == Active.Primary);
        m.Observe(true, true); m.Observe(false, true); m.Observe(false, true);
        Check("a good probe resets the failure count", m.Active == Active.Primary);
        var d = m.Observe(false, true);
        Check("3rd consecutive failure switches to backup", d.Changed && m.Active == Active.Backup);
        m.Observe(true, true); m.Observe(true, true);
        Check("2 good probes: stay on backup", m.Active == Active.Backup);
        d = m.Observe(true, true);
        Check("3rd good probe switches back", d.Changed && m.Active == Active.Primary);
        var n = new FailoverMonitor(3, 3);
        for (int i = 0; i < 5; i++) n.Observe(false, false);
        Check("both down: never switches", n.Active == Active.Primary);
        var q = new FailoverMonitor(3, 3);
        for (int i = 0; i < 3; i++) q.Observe(false, true);
        q.Observe(true, false);
        Check("backup dies while primary is back: return immediately", q.Active == Active.Primary);
        var pol = new PolicyFile(new() { new("A'b", @"C:\x\a.exe", "block"), new("T", @"C:\x\t.exe", "throttle", 500) });
        var cmds = BackupPolicy.ApplyCommands(pol, "Ethernet 2");
        Check("block rule is scoped to backup adapter and quotes are escaped", cmds[0].Contains("-InterfaceAlias 'Ethernet 2'") && cmds[0].Contains("'NetAutoSwitch-A''b'"));
        Check("throttle converts kbps to bits/sec using exe name", cmds[1].Contains("-ThrottleRateActionBitsPerSecond 500000") && cmds[1].Contains("'t.exe'"));
        bool threw = false;
        try { BackupPolicy.ApplyCommands(new PolicyFile(new() { new("X", "x.exe", "throttle") }), "E"); } catch (InvalidDataException) { threw = true; }
        Check("throttle without kbps is rejected", threw);
        Check("remove only targets our prefix/group", BackupPolicy.RemoveCommands().All(c => c.Contains("NetAutoSwitch")));
        var pc = MetricSwitcher.PreferCommands("Ethernet 2", "Wi-Fi");
        Check("prefer: backup gets low metric, primary demoted", pc[0].Contains("'Ethernet 2'") && pc[0].Contains("-InterfaceMetric 5") && pc[1].Contains("'Wi-Fi'") && pc[1].Contains("-InterfaceMetric 500"));
        var rc = MetricSwitcher.RestoreCommands(new[] { new SavedMetric("Wi-Fi", true, 45), new SavedMetric("X", false, 77) });
        Check("restore: automatic re-enabled, manual value kept", rc[0].Contains("-AutomaticMetric Enabled") && rc[1].Contains("-InterfaceMetric 77"));
        var wr = new AppRule("T", null, "watch", Exes: new[] { "hyp.exe" }, MbPerWindow: 10, WindowSec: 10);
        var g = new DownloadGuard(new[] { wr });
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        const string hyp = @"C:\Program Files\HoYoPlay\1.19.0.382\HYP.exe";
        Check("unwatched program (a game) is never blocked", g.Record(@"C:\g\GenshinImpact.exe", 500_000_000, t0) is null);
        Check("small steady traffic (login/news) is allowed", g.Record(hyp, 2_000_000, t0) is null && g.Record(hyp, 2_000_000, t0.AddSeconds(5)) is null);
        var blk = g.Record(hyp, 8_000_000, t0.AddSeconds(8));
        Check("crossing 10 MB inside the window blocks that exe path", blk is { Kind: GuardKind.Block } && blk.Path == hyp);
        Check("while blocked, no repeat block", g.Record(hyp, 50_000_000, t0.AddSeconds(9)) is null);
        Check("a block NEVER expires on its own (still blocked after 3 hours)", g.BlockedNow().Count == 1 && g.Record(hyp, 1, t0.AddHours(3)) is null && g.BlockedNow().Count == 1);
        var ub = g.Allow(blk!.DisplayName, t0.AddHours(3), 10);
        Check("only the user lifts it: Allow returns the Unblock action", ub is { Kind: GuardKind.Unblock } && ub.DisplayName == blk!.DisplayName && g.BlockedNow().Count == 0);
        var g2 = new DownloadGuard(new[] { wr });
        g2.Record(hyp, 9_000_000, t0);
        Check("old bytes fall out of the window", g2.Record(hyp, 9_000_000, t0.AddSeconds(11)) is null);
        var g3 = new DownloadGuard(new[] { wr }); g3.Record(hyp, 99_000_000, t0);
        Check("shutdown drains all blocks", g3.DrainAll().Count == 1);
        Check("block command is scoped to backup adapter and named in our group", DownloadGuard.BlockCommand(blk!, "Ethernet 2").Contains("-InterfaceAlias 'Ethernet 2'") && blk!.DisplayName.StartsWith("NetAutoSwitch-DL-"));
        var f1 = new AppRule("A", null, "watch", Exes: new[] { "launcher.exe" }, MbPerWindow: 1, Folder: @"C:\Program Files\HoYoPlay");
        var f2 = new AppRule("B", null, "watch", Exes: new[] { "launcher.exe" }, MbPerWindow: 1, Folder: @"C:\Program Files\GRYPHLINK");
        var gf = new DownloadGuard(new[] { f1, f2 });
        var bl = gf.Record(@"C:\Program Files\GRYPHLINK\1.6.0\Launcher.exe", 5_000_000, t0);
        Check("same exe name in two folders is matched to the right launcher", bl is { Rule: "B" });
        Check("same exe name outside both folders is ignored", gf.Record(@"D:\other\launcher.exe", 99_000_000, t0) is null);
        var st = new StatsTracker(); var t1 = new DateTime(2026, 1, 1, 12, 0, 0);
        EngineStatus Mk(Active a, bool pOk, bool bOk, bool ch = false) =>
            new(0, a, new ProbeResult("W", 1, pOk, pOk ? 100 : 0, ""), new ProbeResult("E", 2, bOk, bOk ? 200 : 0, ""), ch, "r", false, "");
        st.Record(Mk(Active.Primary, true, true), t1);
        st.Record(Mk(Active.Backup, false, true, ch: true), t1.AddSeconds(5));
        st.Record(Mk(Active.Backup, false, true), t1.AddSeconds(15));
        Check("stats: time on backup counts only backup intervals (10 s)", Math.Abs(st.SecondsOnBackup - 10) < 0.01);
        Check("stats: 1 switch recorded with its reason", st.Switches == 1 && st.LastSwitchReason == "r");
        Check("stats: uptime 33% primary, 100% backup", Math.Round(StatsTracker.UptimePct(st.ProbesPrimary, st.FailPrimary)) == 33 && StatsTracker.UptimePct(st.ProbesBackup, st.FailBackup) == 100);
        Check("stats: average latency ignores failed probes", st.AvgMs(true) == 100 && st.AvgMs(false) == 200);
        Check("stats: history keeps failures as null", st.Snapshot().primary.Count(x => x.Ms is null) == 2);
        var nu = new NetUsage(); nu.Add(@"C:\a\Steam.exe", 5_000_000); nu.Add(@"D:\b\steam.exe", 1_000_000); nu.Add(@"C:\c\x.exe", 100);
        Check("usage: same program name is grouped, biggest first", nu.Top(5)[0] is ("Steam.exe", 6_000_000) && nu.Total == 6_000_100);
        var pk = new NetUsage(); var u0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        pk.Add(@"C:\s\steam.exe", 1_000_000, u0); pk.Add(@"C:\s\steam.exe", 2_000_000, u0.AddSeconds(5));     // burst of 3 MB inside 10 s
        pk.Add(@"C:\s\steam.exe", 500_000, u0.AddSeconds(30));                                               // later, quiet
        Check("usage: peak is the biggest 10 s burst (3 MB), not the total", pk.TopWithPeak(1)[0] is ("steam.exe", 3_500_000, 3_000_000));
        pk.Add(@"C:\s\steam.exe", 4_000_000, u0.AddSeconds(33));
        Check("usage: a faster later burst raises the peak (4.5 MB)", pk.TopWithPeak(1)[0].Peak == 4_500_000);
        Check("usage: reset clears everything", Reset(nu));
        bool Reset(NetUsage u) { u.Reset(); return u.Total == 0 && u.Top(5).Count == 0; }
        string good = "{ \"backupRules\": [ { \"name\": \"X\", \"action\": \"watch\", \"exes\": [\"a.exe\"] } ] }";
        Check("policy: valid JSON accepted", BackupPolicy.Parse(good).BackupRules.Count == 1);
        bool Bad(string j) { try { BackupPolicy.Parse(j); return false; } catch (InvalidDataException) { return true; } }
        Check("policy: broken JSON rejected with a message", Bad("{ nope"));
        Check("policy: unknown action rejected", Bad("{ \"backupRules\": [ { \"name\": \"X\", \"path\": \"c:\\\\a.exe\", \"action\": \"explode\" } ] }"));
        Check("policy: missing backupRules rejected", Bad("{ }"));
        var rt = new PolicyFile(new() {
            new("Steam", null, "watch", Exes: new[] { "steam.exe" }, MbPerWindow: 2.5, WindowSec: 10, Folder: @"C:\Program Files (x86)\Steam"),
            new("Old", @"C:\x\old.exe", "block"), new("Up", @"C:\x\up.exe", "throttle", Kbps: 300) });
        var back = BackupPolicy.Parse(BackupPolicy.ToJson(rt));
        Check("policy: GUI save round-trips (watch rule keeps MB, folder)", back.BackupRules[0] is { MbPerWindow: 2.5, Folder: @"C:\Program Files (x86)\Steam" } && back.BackupRules[0].Exes![0] == "steam.exe");
        Check("policy: GUI save round-trips block and throttle rules", back.BackupRules[1].Path == @"C:\x\old.exe" && back.BackupRules[2].Kbps == 300 && back.BackupRules.Count == 3);
        // global trigger: rules without their own numbers follow it, custom rules override it
        var gdef = new WatchDefaults(MbPerWindow: 2, WindowSec: 10);
        var follow = new AppRule("F", null, "watch", Exes: new[] { "f.exe" });                                   // no numbers of its own
        var custom = new AppRule("C", null, "watch", Exes: new[] { "c.exe" }, MbPerWindow: 8);  // own MB, window from global
        var gg = new DownloadGuard(new[] { follow, custom }, gdef);
        Check("global: a rule with no numbers trips at the global 2 MB", gg.Record(@"C:\x\f.exe", 2_500_000, t0) is { Kind: GuardKind.Block });
        Check("global: a custom rule ignores the global 2 MB (8 MB needed)", gg.Record(@"C:\x\c.exe", 5_000_000, t0) is null && gg.Record(@"C:\x\c.exe", 4_000_000, t0.AddSeconds(1)) is { Kind: GuardKind.Block });
        var gpol = new PolicyFile(new() { follow, custom }, gdef);
        var gback = BackupPolicy.Parse(BackupPolicy.ToJson(gpol));
        Check("global: saved policy keeps defaults and leaves followers without numbers", gback.Global is { MbPerWindow: 2, WindowSec: 10 } && gback.UsesGlobal(gback.BackupRules[0]) && !gback.UsesGlobal(gback.BackupRules[1]) && gback.BackupRules[1].MbPerWindow == 8);
        Check("global: an old policy file with no defaults still loads (built-in defaults)", BackupPolicy.Parse(good).Global.MbPerWindow == 2);
        // manual override from the dashboard
        var og = new DownloadGuard(new[] { new AppRule("S", null, "watch", Exes: new[] { "s.exe" }, MbPerWindow: 1) });
        var ob = og.Record(@"C:\x\s.exe", 2_000_000, t0);
        Check("override: the block shows up in the blocked list", ob is not null && og.BlockedNow().Count == 1 && og.BlockedNow()[0].Path == @"C:\x\s.exe");
        var oa = og.Allow(ob!.DisplayName, t0.AddSeconds(5), 10);
        Check("override: allow lifts the block and returns an Unblock action", oa is { Kind: GuardKind.Unblock } && og.BlockedNow().Count == 0);
        Check("override: not re-blocked while the grace time lasts", og.Record(@"C:\x\s.exe", 50_000_000, t0.AddSeconds(30)) is null);
        Check("override: watching resumes after the grace time", og.Record(@"C:\x\s.exe", 5_000_000, t0.AddMinutes(11)) is { Kind: GuardKind.Block });
        Check("override: unknown name does nothing", og.Allow("NetAutoSwitch-DL-nope", t0, 10) is null);
        // "Allowed for now" list: countdown, Block now, Watch again
        var ag = new DownloadGuard(new[] { new AppRule("S", null, "watch", Exes: new[] { "s.exe" }, MbPerWindow: 1) });
        var ab = ag.Record(@"C:\x\s.exe", 2_000_000, t0)!;
        ag.Allow(ab.DisplayName, t0, 10);
        var al = ag.AllowedNow(t0.AddMinutes(4));
        Check("allowed: listed with its end time (10 min after the override)", al.Count == 1 && al[0].Until == t0.AddMinutes(10) && al[0].Path == @"C:\x\s.exe");
        Check("allowed: disappears from the list when the time is up", ag.AllowedNow(t0.AddMinutes(11)).Count == 0);
        ag.Allow(ab.DisplayName, t0, 10);   // (not blocked any more, so this does nothing)
        var ag2 = new DownloadGuard(new[] { new AppRule("S", null, "watch", Exes: new[] { "s.exe" }, MbPerWindow: 1) });
        var b2 = ag2.Record(@"C:\x\s.exe", 2_000_000, t0)!; ag2.Allow(b2.DisplayName, t0, 30);
        var again = ag2.BlockNow(b2.DisplayName, t0.AddMinutes(1));
        Check("allowed: Block now blocks it again at once and clears the allowed entry", again is { Kind: GuardKind.Block } && ag2.BlockedNow().Count == 1 && ag2.AllowedNow(t0.AddMinutes(1)).Count == 0);
        var ag3 = new DownloadGuard(new[] { new AppRule("S", null, "watch", Exes: new[] { "s.exe" }, MbPerWindow: 1) });
        var b3 = ag3.Record(@"C:\x\s.exe", 2_000_000, t0)!; ag3.Allow(b3.DisplayName, t0, 30);
        Check("allowed: Watch again ends the allowed time (it is then blocked normally if it downloads)", ag3.ResumeWatch(b3.DisplayName) && ag3.Record(@"C:\x\s.exe", 2_000_000, t0.AddMinutes(1)) is { Kind: GuardKind.Block });
        // easy "add a rule" from picked programs
        var existing = new List<AppRule> { new("Steam", null, "watch", Exes: new[] { "steam.exe" }, Folder: @"C:\Program Files (x86)\Steam"),
                                           new("HoYoPlay", null, "watch", Exes: new[] { "launcher.exe" }, Folder: @"C:\Program Files\HoYoPlay") };
        var one1 = PolicyBuilder.WatchRuleFor(new[] { @"D:\Games\Epic\EpicGamesLauncher.exe" }, existing);
        Check("add: one program makes a watch rule named after it, limited to its folder", one1.Rule is { Name: "EpicGamesLauncher", Action: "watch", Folder: @"D:\Games\Epic" } && one1.Rule.Exes!.SequenceEqual(new[] { "EpicGamesLauncher.exe" }));
        var many = PolicyBuilder.WatchRuleFor(new[] { @"D:\L\HYP.exe", @"D:\L\HYPWorker.exe", @"D:\L\HYP.exe" }, existing);
        Check("add: several programs from one folder become ONE rule (duplicates dropped)", many.Rule!.Exes!.Length == 2 && many.Rule.Folder == @"D:\L");
        Check("add: programs from different folders get no folder filter", PolicyBuilder.WatchRuleFor(new[] { @"D:\a\x.exe", @"E:\b\y.exe" }, existing).Rule!.Folder is null);
        var dup = PolicyBuilder.WatchRuleFor(new[] { @"C:\Program Files (x86)\Steam\steam.exe" }, existing);
        Check("add: a program already watched is skipped, not duplicated", dup.Rule is null && dup.Skipped.SequenceEqual(new[] { "steam.exe" }));
        Check("add: same file name in ANOTHER launcher's folder is not treated as a duplicate", PolicyBuilder.WatchRuleFor(new[] { @"C:\Program Files\GRYPHLINK\launcher.exe" }, existing).Rule is not null);
        var clash = PolicyBuilder.WatchRuleFor(new[] { @"D:\other\steam.exe" }, existing);
        Check("add: a clashing rule name gets a number (Steam 2)", clash.Rule!.Name == "Steam 2");
        Check("watched: a program listed in a rule (and in its folder) is covered", PolicyBuilder.IsCovered(@"C:\Program Files (x86)\Steam\steam.exe", existing));
        Check("watched: same name in another folder, or an unlisted program, is not covered", !PolicyBuilder.IsCovered(@"D:\other\steam.exe", existing) && !PolicyBuilder.IsCovered(@"C:\x\notepad.exe", existing));
        // restoring must be best-effort: an unplugged phone must never stop the firewall rules from being removed
        var gone = MetricSwitcher.RestoreCommands(new[] { new SavedMetric("No Such Adapter 9", true, 25) });
        Check("restore: an adapter that no longer exists is NOT an error (real PowerShell run)", BackupPolicy.RunParallel(gone, false, _ => { }) == 0);
        string marker = Path.Combine(Path.GetTempPath(), "nas-restore-marker-" + Guid.NewGuid().ToString("N"));
        int worst = BackupPolicy.RunParallel(new[] { "exit 1", $"Set-Content -LiteralPath '{marker}' ok; exit 0" }, false, _ => { });
        Check("restore: when one command fails, the others STILL run (and the failure is reported)", worst != 0 && File.Exists(marker));
        if (File.Exists(marker)) File.Delete(marker);

        // adapter detection, using the REAL descriptions seen on a PC with two Wi-Fi adapters, a phone tether and lots of driver layers
        AdapterKind K2(string d, NetworkInterfaceType t = NetworkInterfaceType.Ethernet) => AdapterDiscovery.Classify(d, t);
        Check("detect: a Wi-Fi card is Wi-Fi", K2("MediaTek Wi-Fi 6 MT7921 Wireless LAN Card", NetworkInterfaceType.Wireless80211) == AdapterKind.WiFi);
        Check("detect: a phone's USB tether (RNDIS) is a tether", K2("Remote NDIS based Internet Sharing Device") == AdapterKind.Tether && K2("Apple Mobile Device Ethernet") == AdapterKind.Tether && K2("Bluetooth Device (Personal Area Network)") == AdapterKind.Tether);
        Check("detect: driver layers listed next to a real adapter are NOT adapters (and 'Filter' does not look like 'LTE')",
            K2("Realtek 8811CU Wireless LAN 802.11ac USB NIC-WFP Native MAC Layer LightWeight Filter-0000", NetworkInterfaceType.Wireless80211) == AdapterKind.Virtual
            && K2("MediaTek Wi-Fi 6 MT7921 Wireless LAN Card-QoS Packet Scheduler-0000", NetworkInterfaceType.Wireless80211) == AdapterKind.Virtual
            && K2("ZeroTier Virtual Port-Npcap Packet Driver (NPCAP)-0000") == AdapterKind.Virtual);
        Check("detect: VPNs, virtual machines, WARP, kernel debugger and Wi-Fi Direct are virtual",
            K2("ZeroTier Virtual Port") == AdapterKind.Virtual && K2("Cloudflare WARP Interface Tunnel", NetworkInterfaceType.Tunnel) == AdapterKind.Virtual && K2("Hyper-V Virtual Ethernet Adapter") == AdapterKind.Virtual
            && K2("Microsoft Kernel Debug Network Adapter") == AdapterKind.Virtual && K2("Microsoft Wi-Fi Direct Virtual Adapter", NetworkInterfaceType.Wireless80211) == AdapterKind.Virtual);
        Check("detect: cellular modems are tethers, wired cards are Ethernet", K2("Sierra Wireless EM7455", NetworkInterfaceType.Wwanpp2) == AdapterKind.Tether && K2("Realtek PCIe GbE Family Controller", NetworkInterfaceType.GigabitEthernet) == AdapterKind.Ethernet);
        AdapterInfo A(string n, string d, AdapterKind k, bool up, bool gw) => new(n, d, k, up, gw, up ? "10.0.0.2" : "");
        var thisPc = new List<AdapterInfo> {   // this PC: built-in Wi-Fi + USB Wi-Fi dongle, tether unplugged, Bluetooth, virtual things
            A("Wi-Fi 2", "Realtek 8811CU Wireless LAN 802.11ac USB NIC", AdapterKind.WiFi, true, true),
            A("Wi-Fi", "MediaTek Wi-Fi 6 MT7921 Wireless LAN Card", AdapterKind.WiFi, true, true),
            A("Ethernet 2", "Remote NDIS based Internet Sharing Device", AdapterKind.Tether, false, false),
            A("Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)", AdapterKind.Tether, false, false),
            A("ZeroTier One", "ZeroTier Virtual Port", AdapterKind.Virtual, true, true) };
        Check("suggest: with two connected Wi-Fi adapters the built-in one is main, the USB dongle is not", AdapterDiscovery.Suggest(thisPc).Primary == "Wi-Fi");
        Check("suggest: the USB/RNDIS tether is preferred over Bluetooth for the backup, even when unplugged", AdapterDiscovery.Suggest(thisPc).Backup == "Ethernet 2");
        Check("suggest: virtual adapters (ZeroTier) are never chosen", !new[] { AdapterDiscovery.Suggest(thisPc).Primary, AdapterDiscovery.Suggest(thisPc).Backup }.Contains("ZeroTier One"));
        Check("suggest: a choice already in use is kept (the dongle stays main if it was main)", AdapterDiscovery.Suggest(thisPc, "Wi-Fi 2", "Ethernet 2").Primary == "Wi-Fi 2");
        Check("suggest: if the remembered adapter is gone, it falls back to detection", AdapterDiscovery.Suggest(thisPc.Where(a => a.Name != "Wi-Fi").ToList(), "Wi-Fi", "Ethernet 2").Primary == "Wi-Fi 2");
        var wired = new List<AdapterInfo> { A("Wi-Fi", "Intel Wi-Fi 6", AdapterKind.WiFi, true, true), A("Ethernet", "Realtek PCIe GbE", AdapterKind.Ethernet, true, true) };
        Check("suggest: no phone: wired is main, Wi-Fi is the backup", AdapterDiscovery.Suggest(wired) is ("Ethernet", "Wi-Fi"));
        Check("suggest: only one connection: no backup (null), not an invented one", AdapterDiscovery.Suggest(new List<AdapterInfo> { A("Wi-Fi", "Intel Wi-Fi 6", AdapterKind.WiFi, true, true) }) is ("Wi-Fi", null));
        Check("suggest: nothing at all: nothing chosen", AdapterDiscovery.Suggest(new List<AdapterInfo>()) is (null, null));
        var plugged = AdapterDiscovery.Suggest(thisPc.Append(A("Ethernet 3", "Samsung Mobile USB RNDIS", AdapterKind.Tether, true, true)).ToList(), "Wi-Fi", null);
        Check("suggest: a phone is plugged in LATER: the connected one becomes the backup and main stays put", plugged.Backup == "Ethernet 3" && plugged.Primary == "Wi-Fi");

        // versioning: the build's own version is valid SemVer, and precedence follows the SemVer 2.0 spec
        Check("version: this build has a valid SemVer version (" + AppInfo.FullVersion + ")", AppInfo.IsValid(AppInfo.Version) && AppInfo.Version != "0.0.0");
        var order = new[] { "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0" };   // the example order from semver.org
        Check("version: prerelease order matches the SemVer 2.0 spec example", Enumerable.Range(0, order.Length - 1).All(i => AppInfo.Compare(order[i], order[i + 1]) < 0 && AppInfo.Compare(order[i + 1], order[i]) > 0));
        Check("version: numbers compare as numbers (0.9.0 < 0.10.0 < 1.0.0), not as text", AppInfo.Compare("0.9.0", "0.10.0") < 0 && AppInfo.Compare("0.10.0", "1.0.0") < 0);
        Check("version: build metadata (+abc) is ignored, equal versions are equal", AppInfo.Compare("1.2.3+abc", "1.2.3+def") == 0 && AppInfo.Compare("1.2.3", "1.2.3") == 0);
        Check("version: junk is rejected", !AppInfo.IsValid("1.2") && !AppInfo.IsValid("v1.2.3") && !AppInfo.IsValid("01.2.3") && !AppInfo.IsValid("1.2.3-") && AppInfo.IsValid("2.0.0-rc.1+build.5"));
        Check("version: an unusable one throws instead of guessing", ThrowsFormat(() => AppInfo.Compare("1.2", "1.2.3")));
        bool ThrowsFormat(Action a) { try { a(); return false; } catch (FormatException) { return true; } }

        // fast outage detection + locking to one connection
        var fm = new FailoverMonitor(3, 10);
        Check("fast: a dead LINK switches at once (no waiting for 3 failures)", fm.Observe(false, true, primaryLinkDown: true) is { Changed: true, Active: Active.Backup });
        var fm2 = new FailoverMonitor(3, 10);
        fm2.Observe(false, true); fm2.Observe(false, true);
        Check("fast: a merely failing check still needs 3 in a row", fm2.Active == Active.Primary && fm2.Observe(false, true).Changed);
        var fm3 = new FailoverMonitor(3, 10); fm3.Observe(false, true, primaryLinkDown: true);
        for (int i = 0; i < 9; i++) fm3.Observe(true, true);
        Check("fast: coming back takes 10 good checks (a flickering Wi-Fi must not bounce you)", fm3.Active == Active.Backup && fm3.Observe(true, true).Changed);
        var fm4 = new FailoverMonitor(3, 10); fm4.Observe(false, false, primaryLinkDown: true);
        Check("fast: link down but backup unusable -> stays put (never switches to nothing)", fm4.Active == Active.Primary && fm4.Observe(false, true).Changed);
        var fm5 = new FailoverMonitor(3, 10); fm5.Observe(false, true, primaryLinkDown: true); fm5.Reset(Active.Primary);
        Check("lock: Reset starts clean from the given connection", fm5.Active == Active.Primary && !fm5.Observe(true, true).Changed);
        string tmp = Path.Combine(Path.GetTempPath(), "nas-settings-test-" + Guid.NewGuid().ToString("N") + ".json");
        AppSettings.Update(s => s.Theme = "Dark", tmp); AppSettings.Update(s => s.Mode = "StayBackup", tmp);
        var ls = AppSettings.Load(tmp);
        Check("settings: theme and mode are saved together and neither wipes the other", ls.Theme == "Dark" && ls.ConnectionMode == ConnectionMode.StayBackup);
        File.WriteAllText(tmp, "{ not json"); var bad = AppSettings.Load(tmp); File.Delete(tmp);
        Check("settings: a damaged file gives the defaults instead of an error", bad.Theme == "System" && bad.ConnectionMode == ConnectionMode.Auto && AppSettings.Load(tmp).ConnectionMode == ConnectionMode.Auto);

        // route metrics: the preferred adapter's default route is normalized so a poisoned route metric cannot beat it
        var prefer = MetricSwitcher.PreferCommands("Wi-Fi", "Ethernet 2");
        Check("route: preferring an adapter also normalizes ITS default route to 0 (and never stops the chain: ends with exit 0)", prefer.Count == 3 && prefer[2].Contains("'Wi-Fi'") && prefer[2].Contains("-RouteMetric 0") && prefer[2].EndsWith("exit 0"));
        Check("route: restore also clears route metrics for every saved adapter", MetricSwitcher.RestoreCommands(new[] { new SavedMetric("Wi-Fi", true, 45), new SavedMetric("Ethernet 2", true, 25) }).Count(c => c.Contains("-RouteMetric 0")) == 2);
        Check("route: strong demote pushes only the other adapter's route far back", MetricSwitcher.StrongDemote("Ethernet 2")[0] is var sd && sd.Contains("'Ethernet 2'") && sd.Contains("-RouteMetric 5000"));
        Check("log: route warnings show as errors, route info as info", LogClassifier.Classify("[route] WARNING: new connections still use Ethernet 2") == LogKind.Error && LogClassifier.Classify("  [route] new connections now use Wi-Fi") == LogKind.Info);

        // when the main connection returns: which running programs get moved over
        Check("move: a watched program is moved even if it is quiet", PolicyBuilder.ShouldMove(@"C:\Program Files (x86)\Steam\steam.exe", 0, existing));
        Check("move: an unwatched program pulling 5 MB per 10 s (a browser download) is moved", PolicyBuilder.ShouldMove(@"C:\x\chrome.exe", 5_000_000, existing));
        Check("move: a game with light traffic is NOT moved", !PolicyBuilder.ShouldMove(@"C:\Games\GenshinImpact.exe", 80_000, existing));
        Check("log: migrate lines are info (blue)", LogClassifier.Classify("[migrate] moved 3 connection(s) of steam.exe") == LogKind.Info);
        var pk2 = new NetUsage(); pk2.Add(@"C:\x\chrome.exe", 2_000_000, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Check("move: peak lookup by file name ignores case", pk2.PeakFor("CHROME.EXE") == 2_000_000 && pk2.PeakFor("nothing.exe") == 0);
        var seen = new NetUsage(); seen.Add(@"C:\s\steam.exe", 3_000_000); seen.Add(@"C:\s\other.exe", 100);
        Check("usage: seen programs carry their full path, biggest first", seen.Seen()[0] is ("steam.exe", @"C:\s\steam.exe", 3_000_000) && seen.PathFor("OTHER.EXE") == @"C:\s\other.exe");

        // log coloring
        LogKind K(string s) => LogClassifier.Classify(LogClassifier.Split(s).Message);
        Check("log: switch line", K("2026-11-04 14:26:56  SWITCH -> Backup: primary failed 3 probes in a row") == LogKind.Switch);
        Check("log: block line", K("2026-11-04 14:35:22    [watchdog] BLOCK Steam (C:\\Steam\\steam.exe)") == LogKind.Block);
        Check("log: UNBLOCK is not mistaken for BLOCK", K("2026-11-04 14:34:47    [watchdog] UNBLOCK Steam (C:\\x.exe)") == LogKind.Unblock);
        Check("log: override and watch-again count as unblock", K("  [watchdog] OVERRIDE: Steam unblocked by you") == LogKind.Unblock && K("  [watchdog] watching again (you ended the allowed time)") == LogKind.Unblock);
        Check("log: blocked-again-by-you is a block", K("  [watchdog] BLOCKED AGAIN by you: Steam") == LogKind.Block);
        Check("log: failures show as errors", K("2026-11-04 10:00:00    FAILED: access denied") == LogKind.Error && K("ENGINE CRASHED: boom") == LogKind.Error);
        Check("log: powershell commands are dimmed (command)", K("2026-11-04 14:35:22  [run]     New-NetFirewallRule -DisplayName 'x'") == LogKind.Command && K("[dry-run] Set-NetIPInterface") == LogKind.Command);
        Check("log: restore/pause/started are info", K("Restoring original metrics and removing rules...") == LogKind.Info && K("  [watchdog] started: Steam >2MB/10s") == LogKind.Info && K("paused: original metrics restored") == LogKind.Info);
        Check("log: ordinary line stays normal", K("something else") == LogKind.Normal);
        Check("log: timestamp is split off", LogClassifier.Split("2026-11-04 14:26:56  hello") is ("2026-11-04 14:26:56", "hello") && LogClassifier.Split("no time here") is ("", "no time here"));
        var mine = new HashSet<string> { "172.27.121.127" };
        Check("real event value 2138643372 (Int32) matches the tether IP", EtwNetSource.Matches(2138643372, mine));
        Check("other address (1119539976) does not match", !EtwNetSource.Matches(1119539976, mine));
        Check("negative Int32 address does not crash or match", !EtwNetSource.Matches(-1850175431, mine));
        Check("IPAddress form also matches", EtwNetSource.Matches(System.Net.IPAddress.Parse("172.27.121.127"), mine));
        Console.WriteLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        return fails;
    }
}
