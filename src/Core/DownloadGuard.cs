using System.Diagnostics;
using System.Net;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetworkAutoSwitch.Core;

public enum GuardKind { Block, Unblock }
public record GuardAction(GuardKind Kind, string Rule, string Path, string DisplayName);

/// <summary>Pure logic (no OS calls): per watched program, sums inbound bytes in a sliding window.
/// Over the limit -> Block that exe path. A block never expires by itself: only the user lifts it (Allow). Easy to unit-test.</summary>
public class DownloadGuard
{
    record Sample(DateTime At, long Bytes);
    class State { public Queue<Sample> Samples = new(); public long Sum; }

    readonly List<AppRule> _rules;
    readonly Dictionary<string, State> _state = new();                 // key: rule|exePath
    readonly Dictionary<string, (GuardAction act, DateTime since)> _blocked = new();
    readonly object _lock = new();

    readonly WatchDefaults _def;
    readonly Dictionary<string, (DateTime until, GuardAction act)> _allowed = new();     // programs you overrode: not counted until "until"

    public DownloadGuard(IEnumerable<AppRule> rules, WatchDefaults? defaults = null)
    {
        _rules = rules.Where(r => r.Action.Equals("watch", StringComparison.OrdinalIgnoreCase)).ToList();
        _def = defaults ?? new WatchDefaults();
    }

    double Mb(AppRule r) => r.MbPerWindow ?? _def.MbPerWindow;
    int Win(AppRule r) => r.WindowSec ?? _def.WindowSec;

    /// <summary>Matches by exe file name, and by install folder when the rule sets one
    /// (so two launchers that are both called launcher.exe don't get mixed up).</summary>
    AppRule? FindRule(string exePath) =>
        _rules.FirstOrDefault(r => r.Exes!.Any(e => e.Equals(System.IO.Path.GetFileName(exePath), StringComparison.OrdinalIgnoreCase))
                                && r.InFolder(exePath));

    public static string DisplayNameFor(string rule, string path) =>
        $"{BackupPolicy.Prefix}DL-{rule}-{(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(path):x8}";

    /// <summary>Called for every inbound packet of a program. Returns a Block action when the limit is crossed.</summary>
    public GuardAction? Record(string exePath, long bytes, DateTime now)
    {
        var rule = FindRule(exePath);
        if (rule is null) return null;
        lock (_lock)
        {
            string key = rule.Name + "|" + exePath.ToLowerInvariant();
            if (_allowed.TryGetValue(key, out var al)) { if (now < al.until) return null; _allowed.Remove(key); }
            if (_blocked.ContainsKey(key)) return null;
            if (!_state.TryGetValue(key, out var st)) _state[key] = st = new State();
            st.Samples.Enqueue(new(now, bytes)); st.Sum += bytes;
            while (st.Samples.Count > 0 && (now - st.Samples.Peek().At).TotalSeconds > Win(rule))
                st.Sum -= st.Samples.Dequeue().Bytes;
            if (st.Sum <= Mb(rule) * 1_000_000) return null;

            var act = new GuardAction(GuardKind.Block, rule.Name, exePath, DisplayNameFor(rule.Name, exePath));
            _blocked[key] = (act, now);
            st.Samples.Clear(); st.Sum = 0;
            return act;
        }
    }

    public record BlockedInfo(string Rule, string Path, string DisplayName, DateTime Since);

    /// <summary>What is blocked right now and since when. Nothing here ends by itself.</summary>
    public List<BlockedInfo> BlockedNow()
    {
        lock (_lock) return _blocked.Values.Select(v => new BlockedInfo(v.act.Rule, v.act.Path, v.act.DisplayName, v.since)).OrderBy(b => b.Since).ToList();
    }

    /// <summary>Manual override: lifts the block now and stops counting that program for <paramref name="graceMinutes"/>,
    /// so it isn't blocked again a few seconds later. Returns the Unblock action to run, or null if it wasn't blocked.</summary>
    public GuardAction? Allow(string displayName, DateTime now, int graceMinutes)
    {
        lock (_lock)
        {
            var hit = _blocked.FirstOrDefault(kv => kv.Value.act.DisplayName == displayName);
            if (hit.Key is null) return null;
            _blocked.Remove(hit.Key);
            _state.Remove(hit.Key);
            _allowed[hit.Key] = (now.AddMinutes(graceMinutes), hit.Value.act);
            return hit.Value.act with { Kind = GuardKind.Unblock };
        }
    }

    public record AllowedInfo(string Rule, string Path, string DisplayName, DateTime Until);

    /// <summary>Programs you unblocked, and when the watchdog starts watching them again.</summary>
    public List<AllowedInfo> AllowedNow(DateTime now)
    {
        lock (_lock)
        {
            foreach (var k in _allowed.Where(kv => now >= kv.Value.until).Select(kv => kv.Key).ToList()) _allowed.Remove(k);   // expired: back to normal
            return _allowed.Values.Select(v => new AllowedInfo(v.act.Rule, v.act.Path, v.act.DisplayName, v.until)).OrderBy(a => a.Until).ToList();
        }
    }

    /// <summary>"Watch again": ends the allowed time now. The program is then blocked automatically if it goes over the trigger.</summary>
    public bool ResumeWatch(string displayName)
    {
        lock (_lock)
        {
            var hit = _allowed.FirstOrDefault(kv => kv.Value.act.DisplayName == displayName);
            if (hit.Key is null) return false;
            _allowed.Remove(hit.Key); _state.Remove(hit.Key);
            return true;
        }
    }

    /// <summary>"Block now": blocks a program you had allowed, immediately. Returns the Block action to run.</summary>
    public GuardAction? BlockNow(string displayName, DateTime now)
    {
        lock (_lock)
        {
            var hit = _allowed.FirstOrDefault(kv => kv.Value.act.DisplayName == displayName);
            if (hit.Key is null) return null;
            _allowed.Remove(hit.Key); _state.Remove(hit.Key);
            _blocked[hit.Key] = (hit.Value.act, now);
            return hit.Value.act with { Kind = GuardKind.Block };
        }
    }

    /// <summary>Everything currently blocked, so shutdown can unblock it all.</summary>
    public List<GuardAction> DrainAll()
    {
        lock (_lock)
        {
            var all = _blocked.Values.Select(v => v.act with { Kind = GuardKind.Unblock }).ToList();
            _blocked.Clear(); _state.Clear(); _allowed.Clear();
            return all;
        }
    }

    // Every value goes through BackupPolicy.Q: the display name is built from the rule name in policy.json and the
    // path is whatever exe was seen, so either can hold a quote. Unquoted, "Bob's Game" breaks the command and a
    // crafted rule name could run its own PowerShell as admin (issue #6).
    public static string BlockCommand(GuardAction a, string backupAdapter) =>
        $"New-NetFirewallRule -DisplayName {BackupPolicy.Q(a.DisplayName)} -Group {BackupPolicy.Q("NetAutoSwitch")} -Direction Outbound -Action Block " +
        $"-Program {BackupPolicy.Q(a.Path)} -InterfaceAlias {BackupPolicy.Q(backupAdapter)} | Out-Null";

    public static string UnblockCommand(GuardAction a) =>
        $"$r = Get-NetFirewallRule -DisplayName {BackupPolicy.Q(a.DisplayName)} -ErrorAction SilentlyContinue; if ($r) {{ $r | Remove-NetFirewallRule -ErrorAction Stop }}; exit 0";
}

/// <summary>Reads per-process network bytes (down AND up) from Windows' own network events (ETW, no driver). Needs admin.
/// It reports every IPv4 packet batch with both addresses; the engine decides which connection it belongs to and what to do with it.</summary>
public sealed class EtwNetSource : IDisposable
{
    readonly TraceEventSession _session;
    readonly Dictionary<int, (string? Path, DateTime At)> _pidPath = new();
    readonly Dictionary<int, long> _unnamed = new();      // bytes from programs whose name Windows would not give us (pid -> bytes)
    public long EventsSeen, EventsMatched, EventsNoPath;

    /// <param name="onEvent">(program path, bytes, inbound?, address 1, address 2): the two addresses of the packet; one of them is this PC.</param>
    /// <param name="unnamedFilter">Which events matter when a program cannot be named (the protected connections). null = all.</param>
    public EtwNetSource(Action<string, long, bool, string, string> onEvent, Func<string, string, bool>? unnamedFilter = null)
    {
        _session = new TraceEventSession("NetAutoSwitchWatch");
        _session.EnableProvider("Microsoft-Windows-Kernel-Network");
        _session.Source.Dynamic.All += data =>
        {
            // Kernel-Network IPv4: 10 = TCP data sent, 11 = TCP data received, 42 = UDP sent, 43 = UDP received (field names: PID, size, daddr, saddr)
            int id = (int)data.ID;
            bool inbound = id is 11 or 43;
            if (!inbound && id is not (10 or 42)) return;
            Interlocked.Increment(ref EventsSeen);
            try
            {
                string a = ToIp(data.PayloadByName("daddr")), b = ToIp(data.PayloadByName("saddr"));
                if (a.StartsWith("127.") && b.StartsWith("127.")) return;       // this PC talking to itself
                int pid = Convert.ToInt32(data.PayloadByName("PID"));
                long size = Convert.ToInt64(data.PayloadByName("size"));
                string? path = PathOf(pid);
                if (path is null)
                {   // do not drop it silently: a program we cannot name cannot be watched or blocked, and the user must be able to see that
                    Interlocked.Increment(ref EventsNoPath);
                    if (inbound && (unnamedFilter?.Invoke(a, b) ?? true)) lock (_pidPath) _unnamed[pid] = _unnamed.GetValueOrDefault(pid) + size;
                    return;
                }
                Interlocked.Increment(ref EventsMatched);
                onEvent(path, size, inbound, a, b);
            }
            catch { /* never let a bad event kill the session */ }
        };
        new Thread(() => { try { _session.Source.Process(); } catch { } }) { IsBackground = true }.Start();
    }

    /// <summary>An address from an event as text. Events deliver IPv4 addresses as a signed Int32 in little-endian order
    /// (observed live: 2138643372 = 172.27.121.127). Unsigned and IPAddress forms are accepted too.</summary>
    public static string ToIp(object? v) => v switch
    {
        null => "",
        IPAddress ip => ip.ToString(),
        int i => new IPAddress(BitConverter.GetBytes(unchecked((uint)i))).ToString(),
        uint u => new IPAddress(BitConverter.GetBytes(u)).ToString(),
        _ => v.ToString() ?? "",
    };

    public static bool Matches(object? v, HashSet<string> localIps)
    {
        if (v is null) return false;
        if (v is IPAddress ip) return localIps.Contains(ip.ToString());
        if (v is int or uint)
        {
            uint u = v is int i ? unchecked((uint)i) : (uint)v;
            var le = new IPAddress(BitConverter.GetBytes(u));
            var be = new IPAddress(BitConverter.GetBytes(u).Reverse().ToArray());
            return localIps.Contains(le.ToString()) || localIps.Contains(be.ToString());
        }
        return localIps.Contains(v.ToString() ?? "");
    }

    /// <summary>Where the program of this process lives. QueryFullProcessImageName works for protected processes too (anti-cheat, other users);
    /// MainModule is only the second try. A failure is remembered for 5 seconds only (a process that is just starting must not be ignored for good).</summary>
    string? PathOf(int pid)
    {
        lock (_pidPath)
        {
            if (_pidPath.TryGetValue(pid, out var c) && (c.Path is not null || DateTime.UtcNow - c.At < TimeSpan.FromSeconds(5))) return c.Path;
            string? p = ProcPath.Of(pid);
            if (p is null) { try { p = Process.GetProcessById(pid).MainModule?.FileName; } catch { } }
            _pidPath[pid] = (p, DateTime.UtcNow);
            return p;
        }
    }

    /// <summary>Bytes that came in from programs we could not name since the last call (and who they were), or null when there were none.</summary>
    public (long Bytes, string Who)? TakeUnnamed()
    {
        lock (_pidPath)
        {
            if (_unnamed.Count == 0) return null;
            var top = _unnamed.OrderByDescending(kv => kv.Value).Take(3).Select(kv => { try { return $"{Process.GetProcessById(kv.Key).ProcessName} (pid {kv.Key})"; } catch { return $"pid {kv.Key}"; } }).ToList();
            long total = _unnamed.Values.Sum(); _unnamed.Clear();
            return (total, string.Join(", ", top));
        }
    }

    public void Dispose() { try { _session.Dispose(); } catch { } }
}
