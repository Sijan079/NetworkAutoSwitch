using System.Diagnostics;
using System.Text.Json;

namespace NetworkAutoSwitch.Core;

// action "block"    -> always blocked on the backup adapter (needs Path)
// action "throttle" -> upload-only QoS cap (needs Path + Kbps)
// action "watch"    -> download watchdog: blocks the program only while it pulls > MbPerWindow in WindowSec (needs Exes)
// A watch rule is limited to "folder" (one) or "folders" (several, for a game bundle: its launcher AND the game itself). No folder = the file name matches anywhere.
// For "watch" rules, MbPerWindow / WindowSec are OPTIONAL: left out, the rule uses the global Defaults below.
// A watch block never expires by itself; the user lifts it from the dashboard. (Old files with "cooldownSec" still load; it is ignored.)
public record AppRule(string Name, string? Path, string Action, int Kbps = 0,
                      string[]? Exes = null, double? MbPerWindow = null, int? WindowSec = null, string? Folder = null, string[]? Folders = null)
{
    /// <summary>Every folder the rule is limited to: "folder" plus "folders". Empty = the rule matches the file name anywhere.</summary>
    public List<string> AllFolders() => (Folders ?? Array.Empty<string>()).Concat(Folder is null ? Array.Empty<string>() : new[] { Folder })
        .Select(f => f.Trim()).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Does the program's location satisfy the rule? No folders = anywhere; otherwise it must be inside ANY of them.
    /// A game bundle lists the launcher's folder and the game's folder in one rule.</summary>
    public bool InFolder(string exePath)
    {
        var all = AllFolders();
        return all.Count == 0 || all.Any(f => exePath.StartsWith(f, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>The global trigger shared by every watch rule that doesn't set its own.</summary>
public record WatchDefaults(double MbPerWindow = 2, int WindowSec = 10);

public record PolicyFile(List<AppRule> BackupRules, WatchDefaults? Defaults = null)
{
    public WatchDefaults Global => Defaults ?? new WatchDefaults();
    public double MbFor(AppRule r) => r.MbPerWindow ?? Global.MbPerWindow;
    public int WindowFor(AppRule r) => r.WindowSec ?? Global.WindowSec;
    public bool UsesGlobal(AppRule r) => r.MbPerWindow is null && r.WindowSec is null;
}

/// <summary>Rules that apply ONLY while on the backup network.
/// block    -> outbound firewall rule scoped to the backup adapter (blocks both directions of the connection).
/// throttle -> QoS policy capping the app's OUTBOUND rate (uploads). Windows QoS cannot cap downloads.
/// Every object is named with Prefix so Remove() can clean up exactly what we created.</summary>
public static class BackupPolicy
{
    public const string Prefix = "NetAutoSwitch-";

    public static PolicyFile Load(string file) => Parse(File.ReadAllText(file));

    /// <summary>Writes the policy as readable JSON (camelCase, zero/empty fields left out).</summary>
    public static string ToJson(PolicyFile p) => JsonSerializer.Serialize(p, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>Parses AND validates (unknown actions, missing fields). Throws with a readable message.</summary>
    public static PolicyFile Parse(string json, Action<string>? log = null)
    {
        PolicyFile p;
        try { p = JsonSerializer.Deserialize<PolicyFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                  ?? throw new InvalidDataException("empty policy"); }
        catch (JsonException e) { throw new InvalidDataException("Not valid JSON: " + e.Message); }
        if (p.BackupRules is null) throw new InvalidDataException("missing \"backupRules\" list");
        p = p with { BackupRules = CleanNames(p.BackupRules, log ?? Paths.AppendLog) };
        ApplyCommands(p, "validate");   // throws on bad rules
        return p;
    }

    public const int MaxNameLength = 64;

    /// <summary>Characters a rule name may use. The name ends up in firewall commands that run as admin, so only plain
    /// name characters are let through: letters, digits, space and - _ . ( ) + &amp;. Quotes, $, ; and backticks are not.</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= MaxNameLength && name.All(NameChar);

    static bool NameChar(char c) => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '(' or ')' or '+' or '&';

    /// <summary>Sanitises a bad name instead of rejecting the whole file, so one odd rule does not stop the user's policy
    /// from loading. Each change is logged. A cleaned name that collides gets " 2", " 3"... because rule names are keys;
    /// names that were already valid are never touched, so existing policies load exactly as before.</summary>
    static List<AppRule> CleanNames(List<AppRule> rules, Action<string> log)
    {
        var taken = new HashSet<string>(rules.Where(r => IsValidName(r.Name)).Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        var result = new List<AppRule>(rules.Count);
        foreach (var r in rules)
        {
            if (IsValidName(r.Name)) { result.Add(r); continue; }
            string clean = new string((r.Name ?? "").Where(NameChar).ToArray()).Trim();
            if (clean.Length > MaxNameLength) clean = clean[..MaxNameLength].TrimEnd();
            if (clean.Length == 0) clean = "Rule";
            string name = clean;
            for (int i = 2; !taken.Add(name); i++)
            {
                string suffix = " " + i;
                name = (clean.Length + suffix.Length > MaxNameLength ? clean[..(MaxNameLength - suffix.Length)] : clean) + suffix;
            }
            log($"policy: rule name \"{r.Name}\" is not allowed (letters, digits, space and - _ . ( ) + & only, max {MaxNameLength}); using \"{name}\"");
            result.Add(r with { Name = name });
        }
        return result;
    }

    // PowerShell also ends a single-quoted string on the curly quotes U+2018..U+201B, so a path
    // containing one could break out; doubling any of them escapes it the same way as '.
    internal static string Q(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 2).Append('\'');
        foreach (var c in s)
        {
            if (c == '\'' || (c >= '‘' && c <= '‛')) sb.Append(c);
            sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    public static List<string> ApplyCommands(PolicyFile p, string backupAdapter)
    {
        var cmds = new List<string>();
        foreach (var r in p.BackupRules)
        {
            string id = Prefix + r.Name;
            if (r.Action.Equals("watch", StringComparison.OrdinalIgnoreCase))
            {
                if (r.Exes is not { Length: > 0 }) throw new InvalidDataException($"rule '{r.Name}': watch needs exes");
                continue; // handled live by DownloadGuard, nothing to create up front
            }
            if (string.IsNullOrWhiteSpace(r.Path)) throw new InvalidDataException($"rule '{r.Name}': path is required");
            switch (r.Action.ToLowerInvariant())
            {
                case "block":
                    cmds.Add($"New-NetFirewallRule -DisplayName {Q(id)} -Group {Q("NetAutoSwitch")} -Direction Outbound -Action Block " +
                             $"-Program {Q(r.Path!)} -InterfaceAlias {Q(backupAdapter)} | Out-Null");
                    break;
                case "throttle":
                    if (r.Kbps <= 0) throw new InvalidDataException($"rule '{r.Name}': throttle needs kbps > 0");
                    cmds.Add($"New-NetQosPolicy -Name {Q(id)} -AppPathNameMatchCondition {Q(System.IO.Path.GetFileName(r.Path!))} " +
                             $"-ThrottleRateActionBitsPerSecond {(long)r.Kbps * 1000} | Out-Null");
                    break;
                default:
                    throw new InvalidDataException($"rule '{r.Name}': unknown action '{r.Action}' (use block or throttle)");
            }
        }
        return cmds;
    }

    /// <summary>Removes everything this app created, even leftovers from a crash.</summary>
    public static List<string> RemoveCommands() => new()
    {
        // "if ($x)" guards make "nothing to remove" a success instead of a failure exit code.
        // A real removal error still throws (exit 1); the trailing "exit 0" only runs if nothing threw.
        "$r = Get-NetFirewallRule -Group 'NetAutoSwitch' -ErrorAction SilentlyContinue; if ($r) { $r | Remove-NetFirewallRule -ErrorAction Stop }; exit 0",
        $"$q = Get-NetQosPolicy -ErrorAction SilentlyContinue | Where-Object {{ $_.Name -like '{Prefix}*' }}; if ($q) {{ $q | Remove-NetQosPolicy -Confirm:$false -ErrorAction Stop }}; exit 0",
    };

    public static bool IsAdmin() =>
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    /// <summary>Runs independent commands AT THE SAME TIME (each in its own PowerShell). A switch used to take several seconds because
    /// each command waited for the one before it; the commands here don't depend on each other, so the switch now costs about one command.
    /// Returns 0 if all succeeded.</summary>
    public static int RunParallel(IEnumerable<string> cmds, bool dryRun, Action<string>? log = null)
    {
        var list = cmds.ToList();
        if (dryRun || list.Count <= 1) return Run(list, dryRun, log);
        int failed = 0;
        Parallel.ForEach(list, c => { if (Run(new[] { c }, false, log) != 0) Interlocked.Increment(ref failed); });
        return failed == 0 ? 0 : 1;
    }

    /// <summary>How long one PowerShell command may run before it is killed and counted as failed.
    /// Our commands (firewall, QoS, interface metric, scheduled task) normally finish in well under 5 s, even on a slow PC
    /// where PowerShell itself takes a few seconds to start. 30 s leaves plenty of room for that, but a hung command
    /// (a stuck WMI/CIM provider, a prompt nobody can answer) can no longer block the caller forever, which mattered
    /// because the switch engine calls this while holding its lock.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>dryRun=true only prints. Real execution needs admin.</summary>
    public static int Run(IEnumerable<string> cmds, bool dryRun, Action<string>? log = null) => Run(cmds, dryRun, log, DefaultTimeout);

    /// <summary>Same as Run, with a chosen per-command timeout (the self-test uses a short one).
    /// Success is decided by the exit code. Text on stderr with exit code 0 is only logged as a warning.
    /// Returns 0 if all succeeded; stops at the first failure and returns 1.</summary>
    public static int Run(IEnumerable<string> cmds, bool dryRun, Action<string>? log, TimeSpan timeout)
    {
        log ??= Console.WriteLine;
        foreach (var c in cmds)
        {
            log((dryRun ? "[dry-run] " : "[run]     ") + c);
            if (dryRun) continue;
            var r = RunOne(c, timeout);
            if (r.TimedOut) { log($"  FAILED: timed out after {timeout.TotalSeconds:0.#} s (killed)"); return 1; }
            if (r.ExitCode != 0) { log($"  FAILED (exit {r.ExitCode}): " + r.Err.Trim()); return 1; }
            if (r.Err.Trim().Length > 0) log("  WARNING: " + r.Err.Trim());
        }
        return 0;
    }

    record RunResult(int ExitCode, string Err, bool TimedOut);

    /// <summary>Runs one command. Both stdout and stderr are read at the same time, so a command that prints a lot
    /// cannot fill a pipe and hang waiting for us to read it.</summary>
    static RunResult RunOne(string c, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-NonInteractive"); psi.ArgumentList.Add("-Command"); psi.ArgumentList.Add(c);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeout))
        {
            try { proc.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
            proc.WaitForExit(TimeSpan.FromSeconds(5));
            return new RunResult(-1, "", true);
        }
        // The process has exited; its pipes close with it. Bounded wait in case a child it started still holds them open.
        Task.WaitAll(new Task[] { outTask, errTask }, TimeSpan.FromSeconds(5));
        string err = errTask.IsCompletedSuccessfully ? errTask.Result : "";
        return new RunResult(proc.ExitCode, err, false);
    }
}
