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
    var nets = NetworkNames.Read();
    Console.WriteLine($"{"Name",-34}{"Kind",-14}{"Up",-5}{"Gateway",-9}{"IPv4",-17}{"Network",-18}Description");
    foreach (var a in all.OrderBy(a => a.Kind)) Console.WriteLine($"{a.Name,-34}{AdapterDiscovery.KindLabel(a.Kind),-14}{(a.IsUp ? "yes" : "no"),-5}{(a.HasGateway ? "yes" : "no"),-9}{a.Ipv4,-17}{nets.GetValueOrDefault(a.Name, ""),-18}{a.Description}");
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
    // a STABLE backup: three checks in a row must pass (one lucky check is not enough; a flaky USB dongle would make the test fail for the wrong reason)
    string? testBackup = AdapterDiscovery.Candidates(AdapterDiscovery.Detect()).Select(a => a.Name).FirstOrDefault(n => Enumerable.Range(0, 3).All(_ => AdapterProber.Probe(n).Healthy));
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
    bool backupStillGood = AdapterProber.Probe(testBackup).Healthy;   // the network can change while the test runs: if the backup died, say so instead of failing for the wrong reason
    if (!backupStillGood) Console.WriteLine($"SKIP  automatic failover checks: the backup '{testBackup}' stopped working during the test");
    else Check("back to automatic: it fails over by itself again (main has no internet)", await Until(() => last is { Active: Active.Backup, Mode: ConnectionMode.Auto }, 12));
    if (backupStillGood) Check("automatic: the switch is logged", logLines.Any(l => l.StartsWith("SWITCH") || l.Contains("switching to backup")) || last!.Changed || last.Active == Active.Backup);
    Check("the chosen mode is remembered in the settings file", AppSettings.Load(tmp).ConnectionMode == ConnectionMode.Auto);

    cts0.Cancel(); try { await run; } catch { }

    // adapters are detected, not hard-coded; a choice made while running takes effect and is announced
    var eng2 = new SwitchEngine("", "", Paths.DefaultPolicy, enforce: true, execute: false, intervalSec: 1, settingsFile: tmp);
    var real = AdapterDiscovery.Candidates(AdapterDiscovery.Detect());
    int busyOn = 0, busyOff = 0;
    eng2.SetRecoverSeconds(2);
    Check("switch-back time: the engine keeps it between 3 and 600 s (2 becomes 3) and remembers it", eng2.RecoverSeconds == 3 && AppSettings.Load(tmp).RecoverSeconds == 3);
    eng2.SetRecoverSeconds(45);
    Check("switch-back time: 45 s is kept as 45", eng2.RecoverSeconds == 45 && AppSettings.Load(tmp).RecoverSeconds == 45);
    Check("auto-detect: with no names given, a main connection is found by itself (" + eng2.PrimaryName + ")", eng2.PrimaryName.Length > 0 && real.Any(a => a.Name == eng2.PrimaryName));
    Check("auto-detect: the main connection is never a virtual adapter", AdapterDiscovery.Detect().All(a => a.Name != eng2.PrimaryName || a.Kind != AdapterKind.Virtual));
    if (real.Count >= 2)
    {
        int changes = 0; eng2.AdaptersChanged += () => changes++;
        eng2.Busy += b => { if (b) busyOn++; else busyOff++; };
        string pick1 = real[0].Name, pick2 = real[1].Name;
        eng2.SetAdapterChoice(pick2, pick1);
        Check($"choice: choosing '{pick2}' as main and '{pick1}' as backup takes effect at once", eng2.PrimaryName == pick2 && eng2.BackupName == pick1);
        Check("choice: the engine announces the change (so the window and menu update)", changes >= 1);
        Check("choice: it is remembered in the settings file", AppSettings.Load(tmp).PrimaryAdapter == pick2 && AppSettings.Load(tmp).BackupAdapter == pick1);
        eng2.SetAdapterChoice(pick1, pick1);
        Check("choice: the same adapter for both is refused (the backup is left empty)", eng2.PrimaryName == pick1 && eng2.BackupName == "");
        eng2.SetAdapterChoice(null, null);
        Check("choice: back to Automatic detects again", eng2.PrimaryName.Length > 0 && AppSettings.Load(tmp).PrimaryAdapter == "");
        Check("busy: the window is told while the engine changes the network (so it can show placeholders), and told when done", busyOn >= 1 && busyOff == busyOn);
    }
    else Console.WriteLine("SKIP  choice tests (this PC has fewer than two real adapters)");
    // the main connection can be a backup network (the 6.9 GB case). Names and the router check are injected, so the result does not depend on this PC's Wi-Fi.
    var realNow = AdapterDiscovery.Candidates(AdapterDiscovery.Detect());
    if (testBackup is not null && realNow.Any(a => a.Name != testBackup))
    {
        string mainAd = testBackup, otherAd = realNow.First(a => a.Name != testBackup).Name;
        async Task<List<string>> Scenario(Dictionary<string, string> names, Action<AppSettings>? tables)
        {
            string f = Path.Combine(Path.GetTempPath(), "nas-engine-prot-" + Guid.NewGuid().ToString("N") + ".json");
            if (tables is not null) AppSettings.Update(tables, f);
            var lines = new List<string>();
            var e = new SwitchEngine(mainAd, otherAd, Paths.DefaultPolicy, enforce: true, execute: false, intervalSec: 1, settingsFile: f);
            e.NameSource = () => names; e.SameNetworkCheck = (_, _, _, _, _) => false;
            e.Log = l => { lock (lines) lines.Add(l); };
            using var c = new CancellationTokenSource(); var r = e.RunAsync(0, c.Token);
            await Task.Delay(5000);
            c.Cancel(); try { await r; } catch { }
            File.Delete(f);
            lock (lines) return lines.ToList();
        }
        var protectedMain = await Scenario(new() { [mainAd] = "TestPhone", [otherAd] = "TestHome" }, s => s.BlacklistMainNetworks = new() { "TestPhone" });
        Check("protected main: a main connection on a backup network is announced as protected", protectedMain.Any(l => l.Contains("PROTECTING the main connection") && l.Contains("labelled as backup")));
        Check("protected main: the watchdog starts while the MAIN connection is in use", protectedMain.Any(l => l.Contains("would start download watchdog")));
        var trustedMain = await Scenario(new() { [mainAd] = "TestHome", [otherAd] = "TestPhone" }, s => { s.BlacklistMainNetworks = new() { "TestPhone" }; s.BlacklistBackupNetworks = new() { "TestHome" }; });
        Check("trusted main: a main network is NOT protected and no watchdog runs on it", !trustedMain.Any(l => l.Contains("PROTECTING")) && !trustedMain.Any(l => l.Contains("would start download watchdog")));
        var unidentified = await Scenario(new() { [otherAd] = "TestHome" }, null);
        Check("unidentified main: an adapter that is up but has no network name yet is protected (fail closed)", unidentified.Any(l => l.Contains("PROTECTING the main connection") && l.Contains("not identified")));
        var noNames = await Scenario(new(), null);
        Check("no names at all: the lookup is broken, so nothing is blocked, and it is said once", !noNames.Any(l => l.Contains("PROTECTING")) && noNames.Count(l => l.Contains("Windows gives no network names")) == 1);
    }
    else Console.WriteLine("SKIP  protected-main tests (no second adapter to pair with a healthy one)");
    File.Delete(tmp); File.Delete(tmp + ".bak");
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
        // Issue #9: an adapter with no IPv4 binding made Probe throw and end the engine loop.
        Check("no IPv4: a throwing index lookup gives null instead of an exception", AdapterProber.IPv4Index(() => throw new System.Net.NetworkInformation.NetworkInformationException()) is null);
        Check("no IPv4: a null IPv4 properties object gives null too", AdapterProber.IPv4Index(() => ((System.Net.NetworkInformation.IPv4InterfaceProperties?)null)!.Index) is null);
        Check("no IPv4: a normal lookup returns the index", AdapterProber.IPv4Index(() => 7) == 7);
        var n = new FailoverMonitor(3, 3);
        for (int i = 0; i < 5; i++) n.Observe(false, false);
        Check("both down: never switches", n.Active == Active.Primary);
        var q = new FailoverMonitor(3, 3);
        for (int i = 0; i < 3; i++) q.Observe(false, true);
        q.Observe(true, false);
        Check("backup dies while primary is back: return immediately", q.Active == Active.Primary);
        // ---- data cap and idle-backup checks ----
        {
            var day1 = new DateTime(2026, 5, 1, 10, 0, 0); long cap = 1000;
            var bud = new DataBudget(DateOnly.FromDateTime(day1));
            Check("budget: below 80 % -> no alert", bud.Add(700, day1, cap) is null && bud.Bytes == 700);
            Check("budget: crossing 80 % -> Warning, once", bud.Add(150, day1, cap) == BudgetLevel.Warning && bud.Add(10, day1, cap) is null);
            Check("budget: reaching the cap -> Reached, once", bud.Add(200, day1, cap) == BudgetLevel.Reached && bud.Add(500, day1, cap) is null && bud.Bytes == 1560);
            Check("budget: a jump straight past the cap gives Reached (not Warning first)", new DataBudget(DateOnly.FromDateTime(day1)).Add(5000, day1, cap) == BudgetLevel.Reached);
            Check("budget: cap 0 means no limit, never alerts", new DataBudget(DateOnly.FromDateTime(day1)).Add(99_999_999, day1, 0) is null);
            Check("budget: the count starts again the next day, and alerts again", bud.Add(900, day1.AddDays(1), cap) == BudgetLevel.Warning && bud.Bytes == 900);
            Check("budget: yesterday's data is not today's", bud.Today(day1.AddDays(2)) == 0);
            bud.Add(500, day1.AddDays(2), cap); bud.CapChanged();
            Check("budget: lowering the cap below usage announces at the next check", bud.Add(0, day1.AddDays(2), 400) == BudgetLevel.Reached);
            Check("budget: negative bytes never reduce the total", new DataBudget(DateOnly.FromDateTime(day1), 100).Add(-50, day1, cap) is null);
            string f = Path.Combine(Path.GetTempPath(), "nas-usage-test.json");
            new DataBudget(DateOnly.FromDateTime(day1), 1234).Save(f);
            Check("budget: today's count survives a restart", DataBudget.Load(f, day1.AddHours(3)).Bytes == 1234);
            Check("budget: a saved count from another day is not used", DataBudget.Load(f, day1.AddDays(1)).Bytes == 0);
            File.WriteAllText(f, "not json");
            Check("budget: a damaged file gives a fresh count", DataBudget.Load(f, day1).Bytes == 0);
            File.Delete(f);
            // ---- settings file: lock, atomic save, recovery (temp folder, never the real ProgramData file) ----
            {
                var dir = Path.Combine(Path.GetTempPath(), "nas-settings-test-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var sf = Path.Combine(dir, "settings.json");
                try
                {
                    Check("settings: a missing file gives the defaults", AppSettings.Load(sf).Theme == "System");
                    AppSettings.Update(s => { s.Theme = "Dark"; s.RecoverSeconds = 42; }, sf);
                    Check("settings: a change is saved and read back", AppSettings.Load(sf) is { Theme: "Dark", RecoverSeconds: 42 });
                    AppSettings.Update(s => s.Mode = "StayPrimary", sf);
                    Check("settings: the previous file is kept as .bak", File.Exists(sf + ".bak") && AppSettings.Load(sf + ".bak").Mode == "Auto" && AppSettings.Load(sf + ".bak").Theme == "Dark");

                    const int jobs = 64;
                    Parallel.For(0, jobs, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
                    {
                        if (i % 2 == 0) AppSettings.Update(s => s.BlacklistMainNetworks.Add("net" + i), sf);
                        else AppSettings.Update(s => { s.LastPrimary = "p" + i; s.BlacklistBackupNetworks.Add("net" + i); }, sf);
                    });
                    var after = AppSettings.Load(sf);
                    Check($"settings: {jobs} parallel updates all survive (none lost to another thread)",
                        after.BlacklistMainNetworks.Count == jobs / 2 && after.BlacklistBackupNetworks.Count == jobs / 2
                        && after.Theme == "Dark" && after.RecoverSeconds == 42 && after.Mode == "StayPrimary");
                    Check("settings: no temp files are left behind", Directory.GetFiles(dir, "*.tmp").Length == 0);

                    File.WriteAllText(sf, "{ \"Theme\": \"Li");   // what a half-written file looks like
                    Check("settings: a damaged file falls back to the .bak, not the defaults", AppSettings.Load(sf).Theme == "Dark");
                    AppSettings.Update(s => s.MuteNotifications = true, sf);
                    var fixedUp = AppSettings.Load(sf);
                    Check("settings: an update after damage keeps the user's settings", fixedUp.MuteNotifications && fixedUp.Theme == "Dark" && fixedUp.RecoverSeconds == 42);
                    Check("settings: the damaged file never becomes the .bak", AppSettings.Load(sf + ".bak").Theme == "Dark");

                    AppSettings.Update(s => s.Theme = "Light", sf);
                    File.WriteAllText(sf, "not json"); File.Delete(sf + ".bak");
                    Check("settings: damaged file and no .bak keeps the last good copy in memory", AppSettings.Load(sf).Theme == "Light");
                    AppSettings.Update(s => s.DailyCapMB = 500, sf);
                    Check("settings: saving then writes the good copy back, not the defaults", AppSettings.Load(sf) is { Theme: "Light", DailyCapMB: 500, RecoverSeconds: 42 });
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            }
            Check("counter: first reading adds nothing", CounterDelta.Next(null, 5000) == 0);
            Check("counter: normal growth", CounterDelta.Next(5000, 5600) == 600);
            Check("counter: a counter that went down (adapter reset) adds nothing", CounterDelta.Next(5000, 40) == 0);

            var t = new DateTime(2026, 5, 1, 12, 0, 0);
            bool Due(double secAgo, bool activeBackup = false, bool primaryOk = true, bool backupOk = true, bool must = false) => ProbeSchedule.ProbeBackupNow(t, t.AddSeconds(-secAgo), 10, activeBackup, primaryOk, backupOk, must);
            Check("schedule: idle backup checked 3 s ago -> skip", !Due(3));
            Check("schedule: idle backup checked 10 s ago -> check", Due(10));
            Check("schedule: never checked -> check", ProbeSchedule.ProbeBackupNow(t, null, 10, false, true, true, false));
            Check("schedule: backup in use -> check every time", Due(1, activeBackup: true));
            Check("schedule: main connection failing -> check the backup now", Due(1, primaryOk: false));
            Check("schedule: backup is down -> check every time (costs no data)", Due(1, backupOk: false));
            Check("schedule: a different adapter was chosen -> check now", Due(1, must: true));
            Check("schedule: 1 s setting behaves like before", ProbeSchedule.ProbeBackupNow(t, t.AddSeconds(-1), 1, false, true, true, false));
        }
        // ---- per-program data survives a restart (same day) ----
        {
            var today = DateOnly.FromDateTime(DateTime.Now); string pf = Path.Combine(Path.GetTempPath(), "nas-programs-test.json");
            var u1 = new NetUsage(); u1.Add(@"C:\Games\steam.exe", 5_000_000); u1.Add(@"C:\Games\steam.exe", 7_000_000); u1.Add(@"C:\Tools\x.exe", 100);
            u1.Save(pf);
            var u2 = new NetUsage(); u2.LoadFrom(pf, today);
            Check("programs: totals, peak and path come back after a restart", u2.Total == 12_000_100 && u2.TopWithPeak(5)[0].Name == "steam.exe" && u2.TopWithPeak(5)[0].Bytes == 12_000_000 && u2.PeakFor("steam.exe") == 12_000_000 && u2.PathFor("steam.exe") == @"C:\Games\steam.exe");
            var u3 = new NetUsage(); u3.LoadFrom(pf, today.AddDays(1));
            Check("programs: a file from another day is ignored", u3.Total == 0 && u3.Seen().Count == 0);
            File.WriteAllText(pf, "{ nope"); var u4 = new NetUsage(); u4.LoadFrom(pf, today);
            Check("programs: a damaged file is ignored", u4.Total == 0);
            u1.RollTo(today.AddDays(1));
            Check("programs: a new day starts the totals again", u1.Total == 0 && u1.Seen().Count == 0);
            File.Delete(pf);
        }
        // ---- check for updates (the parsing and the decision; the network call itself is not tested here) ----
        {
            const string rel = "{\"tag_name\":\"v0.2.0\",\"html_url\":\"https://github.com/Sijan079/NetworkAutoSwitch/releases/tag/v0.2.0\",\"prerelease\":false}";
            Check("update: reads the version and link from GitHub's answer", UpdateCheck.ParseLatest(rel) is ("0.2.0", var link) && link.EndsWith("v0.2.0"));
            Check("update: a newer release is offered", UpdateCheck.Decide("0.1.0", rel).State == UpdateState.Available && UpdateCheck.Decide("0.1.0", rel).Latest == "0.2.0");
            Check("update: the same version is up to date (build number ignored)", UpdateCheck.Decide("0.2.0+abc1234", rel).State == UpdateState.UpToDate);
            Check("update: a newer installed version is up to date", UpdateCheck.Decide("0.3.0", rel).State == UpdateState.UpToDate);
            Check("update: a prerelease of the same number is older than the release", UpdateCheck.Decide("0.2.0-rc.1", rel).State == UpdateState.Available);
            Check("update: 0.10.0 is newer than 0.9.0", UpdateCheck.Decide("0.9.0", rel.Replace("0.2.0", "0.10.0")).State == UpdateState.Available);
            Check("update: nonsense is a failure, not a crash", UpdateCheck.Decide("0.1.0", "not json").State == UpdateState.Failed && UpdateCheck.Decide("0.1.0", "{\"tag_name\":\"latest\"}").State == UpdateState.Failed && UpdateCheck.Decide("0.1.0", null).State == UpdateState.Failed);
            Check("update: a link that is not https is replaced by the releases page", UpdateCheck.ParseLatest("{\"tag_name\":\"v1.0.0\",\"html_url\":\"javascript:alert(1)\"}") is ("1.0.0", var safe) && safe == UpdateCheck.ReleasesPage);

            // ---- installing it: only the real installer + checksum of this project's release are accepted ----
            string D = "https://github.com/Sijan079/NetworkAutoSwitch/releases/download/v0.4.0/";
            string full = "{\"tag_name\":\"v0.4.0\",\"assets\":[{\"name\":\"NetworkAutoSwitch-Setup-0.4.0.exe\",\"browser_download_url\":\"" + D + "NetworkAutoSwitch-Setup-0.4.0.exe\"},{\"name\":\"NetworkAutoSwitch-Setup-0.4.0.exe.sha256\",\"browser_download_url\":\"" + D + "NetworkAutoSwitch-Setup-0.4.0.exe.sha256\"}]}";
            var up = UpdateCheck.Decide("0.3.0", full);
            Check("update: a release with installer and checksum can be installed", up.State == UpdateState.Available && up.SetupUrl.EndsWith("Setup-0.4.0.exe") && up.ShaUrl.EndsWith(".sha256"));
            Check("update: without the checksum file it cannot be installed (only offered)", UpdateCheck.Decide("0.3.0", full.Replace("Setup-0.4.0.exe.sha256\",\"browser", "other.txt\",\"browser")).SetupUrl.Length == 0);
            Check("update: a download link outside this project's releases is ignored", UpdateCheck.Decide("0.3.0", full.Replace("github.com/Sijan079", "evil.example/Sijan079")).SetupUrl.Length == 0);
            Check("update: an installer of another version is ignored", UpdateCheck.Decide("0.3.0", full.Replace("\"name\":\"NetworkAutoSwitch-Setup-0.4.0.exe\"", "\"name\":\"NetworkAutoSwitch-Setup-0.3.9.exe\"")).SetupUrl.Length == 0);
            string h = new string('a', 64);
            Check("update: reads the hash from 'hash  filename'", Updater.ParseHash(h.ToUpper() + "  NetworkAutoSwitch-Setup-0.4.0.exe\r\n") == h && Updater.ParseHash("not a hash") is null && Updater.ParseHash(h + "ff") is null);
            string tf = Path.GetTempFileName(); File.WriteAllText(tf, "hello");
            Check("update: the file hash is SHA-256", Updater.HashOf(tf) == "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824");
            File.Delete(tf);
            Check("update: the quiet install keeps the sign-in setting as it is", Updater.InstallerArgs(true).Contains("/MERGETASKS=\"startup\"") && Updater.InstallerArgs(false).Contains("/MERGETASKS=\"!startup\"") && Updater.InstallerArgs(true).Contains("/VERYSILENT"));
        }
        // ---- start when I sign in ----
        {
            string enable = StartupTask.EnableCommand(@"C:\Program Files\Network Auto Switch\NetworkAutoSwitchTray.exe");
            Check("startup: the task runs the program from its own folder, 30 s after sign-in, with the highest rights", enable.Contains(@"-Execute 'C:\Program Files\Network Auto Switch\NetworkAutoSwitchTray.exe'")
                && enable.Contains(@"-WorkingDirectory 'C:\Program Files\Network Auto Switch'") && enable.Contains("PT30S") && enable.Contains("-RunLevel Highest") && enable.Contains("-TaskName NetworkAutoSwitchTray"));
            Check("startup: a quote in the path cannot break out of the command", StartupTask.EnableCommand(@"C:\Users\O'Brien\app.exe").Contains(@"-Execute 'C:\Users\O''Brien\app.exe'"));
            Check("startup: turning it off removes the task and ignores 'not found'", StartupTask.DisableCommand.Contains("Unregister-ScheduledTask -TaskName NetworkAutoSwitchTray") && StartupTask.DisableCommand.Contains("SilentlyContinue"));
            Check("startup: the program is read from the task's XML", StartupTask.ParseTarget("<Task><Actions><Exec><Command>\"C:\\a &amp; b\\x.exe\"</Command></Exec></Actions></Task>") == @"C:\a & b\x.exe");
            Check("startup: no command in the XML -> off", StartupTask.ParseTarget("<Task/>") is null);
        }
        // ---- VPN fallback: a VPN that refuses the per-adapter check must not look like an outage ----
        {
            ProbeResult Den(string n) => new(n, 1, false, 0, "AccessDenied");
            ProbeResult Ok(string n) => new(n, 1, true, 40, "connected");
            int calls = 0; ProbeResult Net(bool ok) { calls++; return ok ? new("(active)", 0, true, 70, "x") : new("(active)", 0, false, 0, "timeout"); }
            var r0 = VpnFallback.Apply(Ok("W"), Ok("P"), true, () => Net(true), _ => (true, 1));
            Check("VPN fallback: nothing blocked -> results untouched, not degraded", !r0.Degraded && r0.Primary.Ms == 40 && calls == 0);
            var r1 = VpnFallback.Apply(Den("W"), Den("P"), true, () => Net(true), _ => (true, 5));
            Check("VPN fallback: both denied, internet works -> both healthy, degraded", r1.Degraded && r1.Primary.Healthy && r1.Backup.Healthy && r1.Primary.Ms == 70 && r1.Backup.Ms == 5 && calls == 1);
            Check("VPN fallback: the active connection says 'through the VPN', the idle one 'gateway'", r1.Primary.Detail == VpnFallback.ViaVpn && r1.Backup.Detail == VpnFallback.GatewayOnly);
            var r2 = VpnFallback.Apply(Den("W"), Den("P"), true, () => Net(false), _ => (true, 5));
            Check("VPN fallback: active has no internet -> main counts as down, backup (gateway ok) as up", !r2.Primary.Healthy && r2.Backup.Healthy);
            var mm = new FailoverMonitor(3, 3); for (int i = 0; i < 3; i++) mm.Observe(r2.Primary.Healthy, r2.Backup.Healthy);
            Check("VPN fallback: a real outage behind a VPN still fails over", mm.Active == Active.Backup);
            var r3 = VpnFallback.Apply(Den("W"), Den("P"), false, () => Net(true), _ => (false, 0));
            Check("VPN fallback: on backup, main's gateway unreachable -> main stays down", r3.Primary.Healthy == false && r3.Backup.Healthy);
            calls = 0; var r4 = VpnFallback.Apply(Ok("W"), Den("P"), true, () => Net(true), _ => (true, 3));
            Check("VPN fallback: only the idle connection denied -> no internet check needed", calls == 0 && r4.Degraded && r4.Backup.Healthy && r4.Primary.Ms == 40);
            int baseSec = 10; var bo = new VpnFallback.Backoff(() => baseSec); var tv0 = new DateTime(2026, 1, 1, 12, 0, 0);
            bo.OnSwitch(Active.Primary, tv0, true); bo.OnSwitch(Active.Backup, tv0.AddSeconds(20), true);
            Check("backoff: failing 20 s after moving back doubles the wait", bo.Seconds == 20);
            bo.OnSwitch(Active.Primary, tv0.AddSeconds(60), true); bo.OnSwitch(Active.Backup, tv0.AddSeconds(80), true);
            Check("backoff: doubles again, then is capped at 5 minutes", bo.Seconds == 40);
            for (int i = 0; i < 6; i++) { bo.OnSwitch(Active.Primary, tv0.AddMinutes(10 + i), true); bo.OnSwitch(Active.Backup, tv0.AddMinutes(10 + i).AddSeconds(5), true); }
            Check("backoff: never above 300 s", bo.Seconds == 300);
            bo.OnSwitch(Active.Primary, tv0.AddMinutes(30), true); bo.Settle(tv0.AddMinutes(36), Active.Primary);
            Check("backoff: 5 quiet minutes on the main connection -> back to the normal wait", bo.Seconds == 10);
            var bo2 = new VpnFallback.Backoff(() => 600); bo2.OnSwitch(Active.Primary, tv0, true); bo2.OnSwitch(Active.Backup, tv0.AddSeconds(5), true);
            Check("backoff: never goes below what you set (600 s stays 600 s)", bo2.Seconds == 600);
            var bo3 = new VpnFallback.Backoff(() => 10); bo3.OnSwitch(Active.Primary, tv0, false); bo3.OnSwitch(Active.Backup, tv0.AddSeconds(5), false);
            Check("backoff: a real internet check failing is not punished (not degraded)", bo3.Seconds == 10);
        }
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
        // Issue #6: the rule name reaches an admin PowerShell command. A quote in it must be doubled, never end the string.
        var qa = new GuardAction(GuardKind.Block, "Bob's Game", @"C:\G\x.exe", DownloadGuard.DisplayNameFor("Bob's Game", @"C:\G\x.exe"));
        Check("quoting: a rule name with ' is doubled in the block and unblock commands",
            DownloadGuard.BlockCommand(qa, "Ethernet 2").Contains("-DisplayName 'NetAutoSwitch-DL-Bob''s Game-") &&
            DownloadGuard.UnblockCommand(qa).Contains("-DisplayName 'NetAutoSwitch-DL-Bob''s Game-"));
        var evil = "x'; Remove-Item C:\\Windows -Recurse; '";
        var qe = new GuardAction(GuardKind.Block, evil, @"C:\G\it's.exe", DownloadGuard.DisplayNameFor(evil, @"C:\G\x.exe"));
        string eb = DownloadGuard.BlockCommand(qe, "Wi-Fi 'b'"), eu = DownloadGuard.UnblockCommand(qe);
        // The injected text must stay inside one quoted value: only doubled quotes after the opening one, up to its closing quote.
        static bool OneLiteral(string cmd, string flag)
        {
            int i = cmd.IndexOf(flag + " '", StringComparison.Ordinal); if (i < 0) return false;
            for (int j = i + flag.Length + 2; j < cmd.Length; j++)
                if (cmd[j] == '\'') { if (j + 1 < cmd.Length && cmd[j + 1] == '\'') j++; else return j + 1 == cmd.Length || cmd[j + 1] == ' '; }
            return false;
        }
        Check("quoting: a rule name with '; Remove-Item ... cannot break out of its quotes",
            eb.Contains("x''; Remove-Item C:\\Windows -Recurse; ''") && OneLiteral(eb, "-DisplayName") && OneLiteral(eu, "-DisplayName")
            && OneLiteral(eb, "-Program") && OneLiteral(eb, "-InterfaceAlias") && eb.Contains("-Program 'C:\\G\\it''s.exe'"));
        Check("quoting: curly single quotes are doubled too, so they cannot end the string",
            DownloadGuard.BlockCommand(new GuardAction(GuardKind.Block, "Game", "C:\\G\\a\u2019; calc; \u2018b.exe", DownloadGuard.DisplayNameFor("Game", @"C:\G\x.exe")), "Ethernet 2")
                .Contains("-Program 'C:\\G\\a\u2019\u2019; calc; \u2018\u2018b.exe'"));
        Check("names: plain names (also ones PolicyBuilder makes) are valid", BackupPolicy.IsValidName("Steam") && BackupPolicy.IsValidName("HoYoPlay 2") && BackupPolicy.IsValidName("Game (x86) v1.2_beta-3 & co+"));
        Check("names: quotes, $, ;, backticks, empty and over-long names are invalid",
            !BackupPolicy.IsValidName("Bob's Game") && !BackupPolicy.IsValidName("a;b") && !BackupPolicy.IsValidName("$x") && !BackupPolicy.IsValidName("a`b")
            && !BackupPolicy.IsValidName("") && !BackupPolicy.IsValidName(new string('a', BackupPolicy.MaxNameLength + 1)) && BackupPolicy.IsValidName(new string('a', BackupPolicy.MaxNameLength)));
        var nlog = new List<string>();
        var np = BackupPolicy.Parse("""
            { "backupRules": [
              { "name": "Steam", "action": "watch", "exes": ["steam.exe"] },
              { "name": "Bob's Game", "action": "watch", "exes": ["bob.exe"] },
              { "name": "x'; Remove-Item C:\\Windows -Recurse; '", "action": "watch", "exes": ["x.exe"] },
              { "name": "Steam;", "action": "watch", "exes": ["s2.exe"] },
              { "name": "'''", "action": "watch", "exes": ["q.exe"] } ] }
            """, nlog.Add);
        Check("names on load: valid names are kept, bad ones are sanitised and logged",
            np.BackupRules.Select(r => r.Name).SequenceEqual(new[] { "Steam", "Bobs Game", "x Remove-Item CWindows -Recurse", "Steam 2", "Rule" })
            && nlog.Count == 4 && np.BackupRules.All(r => BackupPolicy.IsValidName(r.Name)));
        Check("names on load: the shipped policy loads unchanged with nothing logged",
            new Func<bool>(() => { var l = new List<string>(); var sp = BackupPolicy.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "policy.json")), l.Add); return l.Count == 0 && sp.BackupRules.Count > 0; })());
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
        Check("add: programs from different folders are kept as ONE rule limited to both folders (a launcher and its game)", PolicyBuilder.WatchRuleFor(new[] { @"D:\a\x.exe", @"E:\b\y.exe" }, existing).Rule is { Folder: null, Folders: [@"D:\a", @"E:\b"] });
        var dup = PolicyBuilder.WatchRuleFor(new[] { @"C:\Program Files (x86)\Steam\steam.exe" }, existing);
        Check("add: a program already watched is skipped, not duplicated", dup.Rule is null && dup.Skipped.SequenceEqual(new[] { "steam.exe" }));
        Check("add: same file name in ANOTHER launcher's folder is not treated as a duplicate", PolicyBuilder.WatchRuleFor(new[] { @"C:\Program Files\GRYPHLINK\launcher.exe" }, existing).Rule is not null);
        var clash = PolicyBuilder.WatchRuleFor(new[] { @"D:\other\steam.exe" }, existing);
        Check("add: a clashing rule name gets a number (Steam 2)", clash.Rule!.Name == "Steam 2");
        Check("watched: a program listed in a rule (and in its folder) is covered", PolicyBuilder.IsCovered(@"C:\Program Files (x86)\Steam\steam.exe", existing));
        Check("watched: same name in another folder, or an unlisted program, is not covered", !PolicyBuilder.IsCovered(@"D:\other\steam.exe", existing) && !PolicyBuilder.IsCovered(@"C:\x\notepad.exe", existing));
        // network names ("SNcial_5G"): Windows adds a number to a repeated network name; the saved Wi-Fi list tells us which name is real
        var savedWifi = NetworkNames.ParseSavedProfiles("Profiles on interface Wi-Fi:\r\n\r\nGroup policy profiles (read only)\r\n---------------------------------\r\n    <None>\r\n\r\nUser profiles\r\n-------------\r\n    All User Profile     : SNcial\r\n    All User Profile     : SNcial_5G\r\n    All User Profile     : Starbucks Customer\r\n");
        Check("names: the saved Wi-Fi list is read by layout, not by wording (works in any language)", savedWifi.SequenceEqual(new[] { "SNcial", "SNcial_5G", "Starbucks Customer" }));
        Check("names: Windows' added number is removed when the shorter name is a saved network ('SNcial 1684' -> 'SNcial')", NetworkNames.Clean("SNcial 1684", savedWifi) == "SNcial");
        Check("names: a name that is itself saved is kept as it is", NetworkNames.Clean("SNcial_5G", savedWifi) == "SNcial_5G");
        Check("names: a name with a number is NOT shortened when the short name is unknown ('Network 30', 'Cafe 2')", NetworkNames.Clean("Network 30", savedWifi) == "Network 30" && NetworkNames.Clean("Cafe 2", savedWifi) == "Cafe 2");
        Check("names: a number that is really part of a saved name stays ('Hotel 5' saved)", NetworkNames.Clean("Hotel 5", new[] { "Hotel 5", "Hotel" }) == "Hotel 5");
        var rn = new FailoverMonitor(3, 10); rn.Observe(false, true, primaryLinkDown: true);
        for (int i = 0; i < 4; i++) rn.Observe(true, true);
        rn.SetRecoverAfter(5);
        Check("switch-back time: lowering it applies to a count that is already running (4 good + 1 more = 5)", rn.Observe(true, true).Changed);
        Check("switch-back time: the setting is stored and defaults to 10 s", new AppSettings().RecoverSeconds == 10);

        // restoring must be best-effort: an unplugged phone must never stop the firewall rules from being removed
        var gone = MetricSwitcher.RestoreCommands(new[] { new SavedMetric("No Such Adapter 9", true, 25) });
        Check("restore: an adapter that no longer exists is NOT an error (real PowerShell run)", BackupPolicy.RunParallel(gone, false, _ => { }) == 0);
        string marker = Path.Combine(Path.GetTempPath(), "nas-restore-marker-" + Guid.NewGuid().ToString("N"));
        int worst = BackupPolicy.RunParallel(new[] { "exit 1", $"Set-Content -LiteralPath '{marker}' ok; exit 0" }, false, _ => { });
        Check("restore: when one command fails, the others STILL run (and the failure is reported)", worst != 0 && File.Exists(marker));
        if (File.Exists(marker)) File.Delete(marker);

        // the PowerShell runner (#8): harmless commands only, nothing on the PC is changed
        var runLog = new List<string>();
        Check("runner: a command printing ~1 MB to stdout does not hang (both pipes are drained)",
              BackupPolicy.Run(new[] { "Write-Output ('x' * 1048576); exit 0" }, false, runLog.Add, TimeSpan.FromSeconds(20)) == 0);
        Check("runner: a non-zero exit code is a failure", BackupPolicy.Run(new[] { "exit 1" }, false, _ => { }) == 1);
        runLog.Clear();
        Check("runner: text on stderr with exit code 0 is a success, logged as a warning",
              BackupPolicy.Run(new[] { "[Console]::Error.WriteLine('just a note'); exit 0" }, false, runLog.Add) == 0 && runLog.Any(l => l.Contains("WARNING") && l.Contains("just a note")));
        runLog.Clear();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int slow = BackupPolicy.Run(new[] { "Start-Sleep -Seconds 30" }, false, runLog.Add, TimeSpan.FromSeconds(2));
        sw.Stop();
        Check($"runner: a hung command is killed at the timeout and reported as failed ({sw.Elapsed.TotalSeconds:0.0} s)",
              slow == 1 && sw.Elapsed < TimeSpan.FromSeconds(15) && runLog.Any(l => l.Contains("timed out")));
        Check("runner: dry run only prints, nothing is started", BackupPolicy.Run(new[] { "exit 1" }, true, _ => { }) == 0);

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
        // guard: both connections on the SAME network (this PC once had both Wi-Fi adapters on one router, so a "switch" changed nothing)
        Check("same network: same router address and same router hardware address = same network", SameNetwork.Judge("10.22.141.235", "AA-BB-CC-00-11-22", "10.22.141.235", "aa-bb-cc-00-11-22"));
        Check("same network: different router addresses = different networks", !SameNetwork.Judge("192.168.1.1", "AA-BB-CC-00-11-22", "172.20.10.1", "AA-BB-CC-00-11-22"));
        Check("same network: two DIFFERENT routers that both use 192.168.1.1 are NOT the same network", !SameNetwork.Judge("192.168.1.1", "AA-BB-CC-00-11-22", "192.168.1.1", "DD-EE-FF-33-44-55"));
        Check("same network: the hardware address could not be read: the matching address alone counts as same", SameNetwork.Judge("10.0.0.1", "", "10.0.0.1", "AA-BB-CC-00-11-22"));
        Check("same network: a connection with no gateway is never 'same'", !SameNetwork.Judge("", "", "", ""));
        Check("same network: reading an unreachable router returns empty, never throws", SameNetwork.GatewayMac("not an ip", "also not") == "");
        string[] noNets = Array.Empty<string>();
        // backup networks: the label belongs to the network, so it protects whichever adapter that network ends up on
        Check("backup nets: names match ignoring case and spaces; unknown never matches", BackupNetworks.Matches(new[] { "SNcial" }, " sncial ") && !BackupNetworks.Matches(new[] { "SNcial" }, "SNcial_5G") && !BackupNetworks.Matches(new[] { "SNcial" }, ""));
        // mode + two tables. Blacklist: Main table = barred from main, Backup table = barred from backup. Whitelist: Main table = only these may be main, Backup table = only these may be backup.
        Check("names: Windows' stand-in names ('Identifying...', 'Unidentified network') are not networks", NetworkNames.IsPlaceholder("Identifying...") && NetworkNames.IsPlaceholder("Identifizieren\u2026") && NetworkNames.IsPlaceholder("Unidentified network") && NetworkNames.IsPlaceholder("  ") && !NetworkNames.IsPlaceholder("SNcial") && !NetworkNames.IsPlaceholder("Cafe 2"));
        Check("names: a saved stand-in name is dropped when the tables load", NetworkTables.From(new AppSettings { BlacklistMainNetworks = new() { "SNcial", "Identifying..." } }).BlacklistMain.SequenceEqual(new[] { "SNcial" }));
        var BL = ListMode.Blacklist; var WL = ListMode.Whitelist;
        Check("blacklist: barred from main = always backup, barred from backup = always main, not listed = unlabelled",
            BackupNetworks.RoleOf("SNcial", BL, new[] { "SNcial" }, new[] { "SNcial_5G" }) == NetworkRole.Backup && BackupNetworks.RoleOf("SNcial_5G", BL, new[] { "SNcial" }, new[] { "SNcial_5G" }) == NetworkRole.Main
            && BackupNetworks.RoleOf("Hotel", BL, new[] { "SNcial" }, new[] { "SNcial_5G" }) == NetworkRole.Unlabelled);
        Check("blacklist: barred from BOTH roles = barred (not used, treated as backup)", BackupNetworks.RoleOf("X", BL, new[] { "X" }, new[] { "X" }) == NetworkRole.Barred);
        Check("whitelist: only listed networks may be that role; on both tables = either; on neither = unlabelled",
            BackupNetworks.RoleOf("SNcial_5G", WL, new[] { "SNcial_5G" }, new[] { "SNcial" }) == NetworkRole.Main && BackupNetworks.RoleOf("SNcial", WL, new[] { "SNcial_5G" }, new[] { "SNcial" }) == NetworkRole.Backup
            && BackupNetworks.RoleOf("Phone", WL, new[] { "Phone" }, new[] { "Phone" }) == NetworkRole.Either && BackupNetworks.RoleOf("Hotel", WL, new[] { "SNcial_5G" }, new[] { "SNcial" }) == NetworkRole.Unlabelled);
        Check("tables: saying 'this is Main / Backup' puts the network on the right table in EITHER mode (and takes it off the other)",
            new[] { BL, WL }.All(md => { var (m1, b1) = BackupNetworks.WithRole("N", NetworkRole.Main, md, new[] { "N" }, new[] { "N" }); var (m2, b2) = BackupNetworks.WithRole("N", NetworkRole.Backup, md, noNets, noNets);
                return BackupNetworks.RoleOf("N", md, m1, b1) == NetworkRole.Main && BackupNetworks.RoleOf("N", md, m2, b2) == NetworkRole.Backup; }));
        Check("protect: unlabelled, backup and barred networks are protected when they are the main connection; main and either are not",
            BackupNetworks.NeedsProtection(NetworkRole.Unlabelled) && BackupNetworks.NeedsProtection(NetworkRole.Backup) && BackupNetworks.NeedsProtection(NetworkRole.Barred) && !BackupNetworks.NeedsProtection(NetworkRole.Main) && !BackupNetworks.NeedsProtection(NetworkRole.Either));
        Check("ask once: an unlabelled Wi-Fi or Ethernet network is asked about", BackupNetworks.ShouldAsk("Hotel", AdapterKind.WiFi, BL, noNets, noNets, noNets) && BackupNetworks.ShouldAsk("Office", AdapterKind.Ethernet, WL, noNets, noNets, noNets));
        Check("ask once: not again after it was asked, not when it is on a table, not when the name is unknown", !BackupNetworks.ShouldAsk("Hotel", AdapterKind.WiFi, BL, noNets, noNets, new[] { "hotel" }) && !BackupNetworks.ShouldAsk("SNcial", AdapterKind.WiFi, BL, new[] { "SNcial" }, noNets, noNets) && !BackupNetworks.ShouldAsk("", AdapterKind.WiFi, BL, noNets, noNets, noNets));
        Check("ask once: a phone/modem adapter is protected by kind, its generic name is never asked about", !BackupNetworks.ShouldAsk("Network 3", AdapterKind.Tether, BL, noNets, noNets, noNets));
        Check("backup rule: a Main or barred network may never be the backup; backup, either and unlabelled may", !BackupNetworks.BackupAllowed(NetworkRole.Main) && !BackupNetworks.BackupAllowed(NetworkRole.Barred) && BackupNetworks.BackupAllowed(NetworkRole.Backup) && BackupNetworks.BackupAllowed(NetworkRole.Either) && BackupNetworks.BackupAllowed(NetworkRole.Unlabelled));
        Check("same network: a hardware address that could not be read, with two DIFFERENT known names, is not 'same' (no false alarm)", !SameNetwork.Judge("192.168.1.1", "", "192.168.1.1", "AA-BB-CC-00-11-22", "Home", "Hotspot"));
        Check("same network: a hardware address that could not be read, with equal or unknown names, still counts as same", SameNetwork.Judge("192.168.1.1", "", "192.168.1.1", "", "Home", "home") && SameNetwork.Judge("192.168.1.1", "", "192.168.1.1", "", "", "Hotspot"));
        Check("same network: when both hardware addresses are known the names do not matter (SNcial and SNcial_5G on one router)", SameNetwork.Judge("10.0.0.1", "AA-BB-CC-00-11-22", "10.0.0.1", "aa-bb-cc-00-11-22", "SNcial", "SNcial_5G"));
        Check("protect: a network Windows has not identified yet is protected, and the reason says so", BackupNetworks.MainProtectionReason(false, NetworkRole.Unlabelled, identified: false).Contains("not identified") && BackupNetworks.MainProtectionReason(false, NetworkRole.Main, identified: true) == "");
        Check("alarm: the main connection on a backup network, or both on one network, is an alarm; unsorted or not yet identified is not",
            BackupNetworks.IsAlarming(true, NetworkRole.Main, true) && BackupNetworks.IsAlarming(false, NetworkRole.Backup, true) && BackupNetworks.IsAlarming(false, NetworkRole.Barred, true)
            && !BackupNetworks.IsAlarming(false, NetworkRole.Unlabelled, true) && !BackupNetworks.IsAlarming(false, NetworkRole.Unlabelled, false) && !BackupNetworks.IsAlarming(false, NetworkRole.Main, true));
        Check("watchdog: the path of a running program is found by its process id (this very program)", ProcPath.Of(Environment.ProcessId) is string pp && pp.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || ProcPath.Of(Environment.ProcessId) is string pp2 && pp2.Length > 0);
        Check("watchdog: a process that does not exist has no path (and no crash)", ProcPath.Of(int.MaxValue - 7) is null);
        // a game bundle: one rule for the launcher AND the game itself, which live in different folders
        var bundle = new AppRule("Endfield", null, "watch", Exes: new[] { "Launcher.exe", "Endfield.exe" }, MbPerWindow: 1, Folders: new[] { @"C:\Program Files\GRYPHLINK", @"D:\Sijan\EndField Game" });
        var bg = new DownloadGuard(new[] { bundle });
        Check("bundle: the launcher in its folder is watched", bg.Record(@"C:\Program Files\GRYPHLINK\1.6.0\Launcher.exe", 2_000_000, DateTime.UtcNow) is { Kind: GuardKind.Block });
        Check("bundle: the game in ITS folder is watched by the same rule", new DownloadGuard(new[] { bundle }).Record(@"D:\Sijan\EndField Game\Endfield.exe", 2_000_000, DateTime.UtcNow) is { Kind: GuardKind.Block, Rule: "Endfield" });
        Check("bundle: the same file name in a folder that is not part of the bundle is left alone", new DownloadGuard(new[] { bundle }).Record(@"E:\Elsewhere\Endfield.exe", 2_000_000, DateTime.UtcNow) is null);
        Check("bundle: both folders count as covered, an unrelated folder does not", PolicyBuilder.IsCovered(@"D:\Sijan\EndField Game\Endfield.exe", new[] { bundle }) && PolicyBuilder.IsCovered(@"C:\Program Files\GRYPHLINK\Launcher.exe", new[] { bundle }) && !PolicyBuilder.IsCovered(@"E:\x\Endfield.exe", new[] { bundle }));
        Check("bundle: the folder box text maps to folder / folders and back", PolicyBuilder.FromLines("") is (null, null) && PolicyBuilder.FromLines("C:\\a") is ("C:\\a", null) && PolicyBuilder.FromLines("C:\\a\r\nD:\\b") is (null, ["C:\\a", "D:\\b"]) && PolicyBuilder.ToLines(bundle).Split("\r\n").Length == 2);
        var bundleBack = BackupPolicy.Parse(BackupPolicy.ToJson(new PolicyFile(new List<AppRule> { bundle })));
        Check("bundle: the policy file round-trips both folders", bundleBack.BackupRules[0].AllFolders().Count == 2 && bundleBack.BackupRules[0].Exes!.Length == 2);
        // Network Activity: down AND up, on the main connection AND the backup
        var act = new NetUsage(); var tAct = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        act.Add(@"C:\g\game.exe", 50_000_000, tAct, "Main"); act.Add(@"C:\g\game.exe", 3_000_000, tAct, "Backup"); act.AddUp(@"C:\g\game.exe", 2_000_000, "Main");
        act.AddUp(@"C:\u\backup.exe", 9_000_000, "Backup");
        var actRows = act.Rows(5);
        Check("activity: a program that uses both connections is 'Both'; down and up are counted separately", actRows.First(r => r.Name == "game.exe") is { Down: 53_000_000, Up: 2_000_000, On: "Both" });
        Check("activity: an upload-only program still shows, with 'Backup' and no download", actRows.First(r => r.Name == "backup.exe") is { Down: 0, Up: 9_000_000, On: "Backup" });
        Check("activity: the busiest program (down plus up) comes first", actRows[0].Name == "game.exe");
        Check("activity: the BACKUP peak only counts downloads on the backup (a big download on main does not make a program 'heavy on the backup')", act.PeakFor("game.exe") == 3_000_000);
        Check("activity: the table peak counts every download", actRows.First(r => r.Name == "game.exe").PeakDown == 53_000_000);
        Check("activity: programs seen on the backup come first in the picker", act.Seen()[0].Name is "backup.exe" or "game.exe" && act.Seen().Take(2).All(p => p.Name is "backup.exe" or "game.exe"));
        string actFile = Path.Combine(Path.GetTempPath(), "nas-activity-" + Guid.NewGuid().ToString("N") + ".json"); act.Save(actFile);
        var act2 = new NetUsage(); act2.LoadFrom(actFile, DateOnly.FromDateTime(DateTime.Now)); File.Delete(actFile);
        Check("activity: up, 'On' and the backup peak survive a restart", act2.Rows(5).First(r => r.Name == "game.exe") is { Up: 2_000_000, On: "Both" } && act2.PeakFor("game.exe") == 3_000_000);
        Check("activity: an older programs.json (without up / on) still loads", new Func<bool>(() => { string f = Path.Combine(Path.GetTempPath(), "nas-old-" + Guid.NewGuid().ToString("N") + ".json"); File.WriteAllText(f, "{\"Day\":\"" + DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd") + "\",\"Programs\":[{\"Name\":\"x.exe\",\"Path\":\"C:\\\\x.exe\",\"Bytes\":5,\"Peak\":5}]}"); var u = new NetUsage(); u.LoadFrom(f, DateOnly.FromDateTime(DateTime.Now)); File.Delete(f); return u.Rows(5) is [{ Name: "x.exe", Down: 5, Up: 0 }]; })());
        Check("activity: event addresses are read as little-endian text", EtwNetSource.ToIp(2138643372) == "172.27.121.127" && EtwNetSource.ToIp(System.Net.IPAddress.Parse("10.0.0.2")) == "10.0.0.2" && EtwNetSource.ToIp(null) == "");
        var srt = new List<UsageRow> { new("beta.exe", 5_000_000, 100, 4_000_000, "Main", 0), new("Alpha.exe", 1_000_000, 9_000_000, 900_000, "Backup", 9_500_000), new("gamma.exe", 3_000_000, 3_000_000, 8_000_000, "Both", 1_000_000), new("delta.exe", 5_000_000, 100, 1_000_000, "", 0) };
        Check("sort: Program goes A to Z ignoring case; pressing again flips it", UsageSort.Apply(srt, "program").Select(r => r.Name).SequenceEqual(new[] { "Alpha.exe", "beta.exe", "delta.exe", "gamma.exe" }) && UsageSort.Apply(srt, "program", flip: true).First().Name == "gamma.exe");
        Check("sort: Total (down plus up, the default) puts the busiest first, ties by name", UsageSort.Apply(srt, "total").Select(r => r.Name).SequenceEqual(new[] { "Alpha.exe", "gamma.exe", "beta.exe", "delta.exe" }) && UsageSort.Apply(srt, "total", flip: true).First().Name == "beta.exe");
        Check("sort: On Backup puts the programs that used most of your phone's data first", UsageSort.Apply(srt, "backup").Select(r => r.Name).SequenceEqual(new[] { "Alpha.exe", "gamma.exe", "beta.exe", "delta.exe" }));
        Check("sort: Trigger puts the fastest 10 seconds first", UsageSort.Apply(srt, "trigger").First().Name == "gamma.exe" && UsageSort.Apply(srt, "trigger", flip: true).First().Name == "Alpha.exe");
        Check("sort: Watch List puts watched programs first", UsageSort.Apply(srt, "watch", watched: r => r.Name == "delta.exe").First().Name == "delta.exe" && UsageSort.Apply(srt, "watch", flip: true, watched: r => r.Name == "delta.exe").Last().Name == "delta.exe");
        Check("usage row: Total is down plus up", srt[1].Total == 10_000_000);
        Check("activity: 'On Backup' counts down AND up that went through the backup, and nothing from the main connection", actRows.First(r => r.Name == "game.exe").BackupBytes == 3_000_000 && actRows.First(r => r.Name == "backup.exe").BackupBytes == 9_000_000);
        Check("activity: the backup amount survives a restart; an old file that knew nothing about Main/Backup counts as all backup", act2.Rows(5).First(r => r.Name == "game.exe").BackupBytes == 3_000_000);
        var two = new List<(string, string)> { ("Wi-Fi", "SNcial"), ("Wi-Fi 2", "SNcial_5G") };
        var swapped = new List<(string, string)> { ("Wi-Fi", "SNcial_5G"), ("Wi-Fi 2", "SNcial") };
        Check("roles: the adapter on the Main network is main, the one on the Backup network is backup", BackupNetworks.Assign(two, BL, new[] { "SNcial" }, new[] { "SNcial_5G" }, "", "") is ("Wi-Fi 2", "Wi-Fi"));
        Check("roles: adapters swapped by mistake: the roles follow the NETWORKS, not the adapter names", BackupNetworks.Assign(swapped, BL, new[] { "SNcial" }, new[] { "SNcial_5G" }, "Wi-Fi 2", "Wi-Fi") is ("Wi-Fi", "Wi-Fi 2"));
        Check("roles: the same result in whitelist mode", BackupNetworks.Assign(swapped, WL, new[] { "SNcial_5G" }, new[] { "SNcial" }, "Wi-Fi 2", "Wi-Fi") is ("Wi-Fi", "Wi-Fi 2"));
        Check("roles: both adapters on the backup network (the 6.9 GB case): no adapter may be main, so nothing is invented", BackupNetworks.Assign(new List<(string, string)> { ("Wi-Fi", "SNcial"), ("Wi-Fi 2", "SNcial") }, BL, new[] { "SNcial" }, new[] { "SNcial_5G" }, "Wi-Fi 2", "Wi-Fi") is null);
        Check("roles: a main network plus an UNLABELLED one: the unlabelled one is the backup", BackupNetworks.Assign(new List<(string, string)> { ("Wi-Fi", "Hotel"), ("Wi-Fi 2", "SNcial_5G") }, BL, new[] { "SNcial" }, new[] { "SNcial_5G" }, "", "") is ("Wi-Fi 2", "Wi-Fi"));
        Check("roles: an unlabelled network never becomes main by itself", BackupNetworks.Assign(new List<(string, string)> { ("Wi-Fi", "SNcial"), ("Wi-Fi 2", "Hotel") }, BL, new[] { "SNcial" }, new[] { "SNcial_5G" }, "Wi-Fi 2", "Wi-Fi") is null);
        Check("protect: on the main connection and nothing special: nothing protected", BackupNetworks.ProtectedAdapters(Active.Primary, "Wi-Fi 2", "Wi-Fi", false).Count == 0);
        Check("protect: on the backup: the backup is protected", BackupNetworks.ProtectedAdapters(Active.Backup, "Wi-Fi 2", "Wi-Fi", false).SequenceEqual(new[] { "Wi-Fi" }));
        Check("protect: the main connection is on a backup network: the MAIN adapter is protected while in use (the 6.9 GB case)", BackupNetworks.ProtectedAdapters(Active.Primary, "Wi-Fi 2", "Wi-Fi", true).SequenceEqual(new[] { "Wi-Fi 2" }));
        Check("protect: on the backup AND main is a backup network: both adapters are protected", BackupNetworks.ProtectedAdapters(Active.Backup, "Wi-Fi 2", "Wi-Fi", true).OrderBy(x => x).SequenceEqual(new[] { "Wi-Fi", "Wi-Fi 2" }));
        Check("protect: with only one connection there is nothing to compare, nothing is protected", BackupNetworks.ProtectedAdapters(Active.Primary, "Wi-Fi", "", true).Count == 0);
        Check("protect: the reason says why", BackupNetworks.MainProtectionReason(true, NetworkRole.Main).Contains("same network") && BackupNetworks.MainProtectionReason(false, NetworkRole.Backup).Contains("labelled as backup") && BackupNetworks.MainProtectionReason(false, NetworkRole.Unlabelled).Contains("not labelled") && BackupNetworks.MainProtectionReason(false, NetworkRole.Main) == "");
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
        File.WriteAllText(tmp, "{ not json"); var bad = AppSettings.Load(tmp); File.Delete(tmp); File.Delete(tmp + ".bak");
        Check("settings: a damaged file gives the backup copy instead of an error (and a missing one the defaults)", bad.Theme == "Dark" && AppSettings.Load(tmp).ConnectionMode == ConnectionMode.Auto && AppSettings.Load(tmp).Theme == "System");

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
