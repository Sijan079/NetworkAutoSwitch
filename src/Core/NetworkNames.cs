using System.Diagnostics;
using System.Text;

namespace NetworkAutoSwitch.Core;

/// <summary>The name of the network each adapter is connected to ("SNcial_5G" for a Wi-Fi, the phone's network for a tether).
/// Windows 11 blocks the usual Wi-Fi SSID lookup (netsh) unless Location access is on, so this uses the connection profiles, which need no permission.</summary>
public static class NetworkNames
{
    /// <summary>adapter name -> network name. Empty when nothing is connected or Windows cannot tell. Never throws.</summary>
    public static Dictionary<string, string> Read()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string raw = Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"Get-NetConnectionProfile | ForEach-Object { $_.InterfaceAlias + '|' + $_.Name }\"");
            if (string.IsNullOrWhiteSpace(raw)) return map;
            var saved = ParseSavedProfiles(Run("netsh.exe", "wlan show profiles"));
            foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int bar = line.IndexOf('|');
                if (bar <= 0) continue;
                string alias = line[..bar].Trim(), name = line[(bar + 1)..].Trim();
                if (alias.Length > 0 && name.Length > 0 && !IsPlaceholder(name)) map[alias] = Clean(name, saved);
            }
        }
        catch { /* the names are a nicety: never let them break anything */ }
        return map;
    }

    /// <summary>Windows shows a stand-in name while it is still working out which network it joined ("Identifying...", "Unidentified network"). That is not a network:
    /// it must never be shown, asked about or saved as a Main or Backup network. Any name that ends in "..." counts (so it works in every Windows language).</summary>
    public static bool IsPlaceholder(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        string n = name.Trim();
        return n.EndsWith("...", StringComparison.Ordinal) || n.EndsWith("\u2026", StringComparison.Ordinal) || n.Equals("Unidentified network", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Wi-Fi networks saved in Windows (so they can be sorted before ever connecting). Never throws; empty when Windows cannot tell.</summary>
    public static List<string> SavedWifi()
    {
        try { return ParseSavedProfiles(Run("netsh.exe", "wlan show profiles")).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); }
        catch { return new(); }
    }

    /// <summary>Windows adds a number to a profile name when a network was joined more than once ("SNcial 1684" for the network "SNcial").
    /// If removing that number gives a network you have saved, and the name with the number is not itself saved, the shorter name is the real one.</summary>
    public static string Clean(string name, IEnumerable<string> savedWifiNames)
    {
        var saved = new HashSet<string>(savedWifiNames, StringComparer.OrdinalIgnoreCase);
        if (saved.Contains(name)) return name;
        int sp = name.LastIndexOf(' ');
        if (sp > 0 && sp < name.Length - 1 && name[(sp + 1)..].All(char.IsDigit))
        {
            string baseName = name[..sp];
            if (saved.Contains(baseName)) return baseName;
        }
        return name;
    }

    /// <summary>Reads the list printed by "netsh wlan show profiles". Only the layout is used (an indented "label : name" line), not the
    /// label's wording, so it works in every Windows language.</summary>
    public static List<string> ParseSavedProfiles(string netshOutput)
    {
        var result = new List<string>();
        foreach (var raw in netshOutput.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || !char.IsWhiteSpace(line[0])) continue;      // headers and separators are not indented
            int colon = line.IndexOf(" : ", StringComparison.Ordinal);
            if (colon < 0) continue;
            string name = line[(colon + 3)..].Trim();
            if (name.Length > 0) result.Add(name);
        }
        return result;
    }

    static string Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return ""; }
        return o;
    }
}
