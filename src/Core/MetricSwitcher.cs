using System.Diagnostics;
using System.Text.Json;

namespace NetworkAutoSwitch.Core;

public record SavedMetric(string Adapter, bool Automatic, int Metric);

/// <summary>Makes Windows prefer one adapter by lowering its interface metric.
/// The original metrics are saved to state.json first so Restore() can always undo it (also after a crash).</summary>
public static class MetricSwitcher
{
    public const int PreferredMetric = 5, DemotedMetric = 500;
    public static string StateFile => Paths.StateFile;

    static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    public static SavedMetric? Read(string adapter)
    {
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add($"Get-NetIPInterface -InterfaceAlias {Q(adapter)} -AddressFamily IPv4 | ForEach-Object {{ \"$($_.AutomaticMetric)|$($_.InterfaceMetric)\" }}");
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit();
        var parts = o.Split('|');
        return parts.Length == 2 && int.TryParse(parts[1], out int m) ? new(adapter, parts[0] == "Enabled", m) : null;
    }

    /// <summary>Saves originals once (never overwrites an existing save, so a second run can't lose the real values).</summary>
    public static void SaveOriginals(params string[] adapters)
    {
        if (File.Exists(StateFile)) return;
        var list = adapters.Select(Read).Where(x => x is not null).ToList();
        File.WriteAllText(StateFile, JsonSerializer.Serialize(list));
    }

    /// <summary>Total cost of a route = the adapter's interface metric + the route's OWN metric. A route created while an adapter was demoted
    /// can keep a big route metric of its own (seen live: Wi-Fi interface 5 + route 500 lost to tether 500 + 0). So besides the interface metrics
    /// we also normalize the preferred adapter's default route to 0.</summary>
    public static List<string> PreferCommands(string preferred, string other) => new()
    {
        $"Set-NetIPInterface -InterfaceAlias {Q(preferred)} -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric {PreferredMetric}",
        $"Set-NetIPInterface -InterfaceAlias {Q(other)} -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric {DemotedMetric}",
        SetDefaultRouteMetric(preferred, 0),
    };

    /// <summary>Second pass when Windows still picks the wrong adapter: push the other adapter's default route far back.</summary>
    public static List<string> StrongDemote(string other) => new() { SetDefaultRouteMetric(other, 5000) };

    /// <summary>Sets the metric of an adapter's IPv4 default route. A missing route (adapter down) is not an error, and never stops the other commands.</summary>
    public static string SetDefaultRouteMetric(string adapter, int metric) =>
        $"try {{ Get-NetRoute -InterfaceAlias {Q(adapter)} -DestinationPrefix '0.0.0.0/0' -ErrorAction Stop | Where-Object {{ $_.RouteMetric -ne {metric} }} | Set-NetRoute -RouteMetric {metric} -ErrorAction Stop }} catch {{ }}; exit 0";

    /// <summary>Asks Windows which adapter a NEW connection to the internet would use right now (null if it can't tell).</summary>
    public static string? RouteChoice()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("(Find-NetRoute -RemoteIPAddress 8.8.8.8)[0].InterfaceAlias");
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit();
            return o.Length > 0 ? o : null;
        }
        catch { return null; }
    }

    public static List<string> RestoreCommands(IEnumerable<SavedMetric> saved)
    {
        var list = saved.ToList();
        // An adapter that is gone right now (a phone that was unplugged) cannot be restored and does not need to be: that one case is not an error.
        // Any OTHER failure still fails loudly. Every command is independent, so one missing adapter can never stop the others.
        static string Guard(string cmd) => $"try {{ {cmd} -ErrorAction Stop }} catch {{ if ($_.CategoryInfo.Category -ne 'ObjectNotFound') {{ throw }} }}; exit 0";
        var cmds = list.Select(s => Guard(s.Automatic
            ? $"Set-NetIPInterface -InterfaceAlias {Q(s.Adapter)} -AddressFamily IPv4 -AutomaticMetric Enabled"
            : $"Set-NetIPInterface -InterfaceAlias {Q(s.Adapter)} -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric {s.Metric}")).ToList();
        cmds.AddRange(list.Select(s => SetDefaultRouteMetric(s.Adapter, 0)));    // leave no route metric of ours behind
        return cmds;
    }

    public static List<string> RestoreFromState() =>
        File.Exists(StateFile) ? RestoreCommands(JsonSerializer.Deserialize<List<SavedMetric>>(File.ReadAllText(StateFile))!) : new();

    public static void ClearState() { if (File.Exists(StateFile)) File.Delete(StateFile); }
}
