namespace NetworkAutoSwitch.Core;

/// <summary>Builds a watch rule from programs the user picked, so nobody has to type file names or folders.</summary>
public static class PolicyBuilder
{
    /// <summary>Is this program already covered by a watch rule? Same test everywhere: file name listed AND folder matches (or the rule has no folder).</summary>
    public static bool IsCovered(string path, IEnumerable<AppRule> rules)
    {
        string file = Path.GetFileName(path);
        return rules.Any(r => r.Action.Equals("watch", StringComparison.OrdinalIgnoreCase)
            && (r.Exes ?? Array.Empty<string>()).Any(e => e.Equals(file, StringComparison.OrdinalIgnoreCase))
            && r.InFolder(path));
    }

    /// <summary>When the main connection returns, which programs get their backup connections reset so they reconnect over it?
    /// Watched programs, and anything that was pulling real data (at or above the threshold per 10 s). Games stay well below it, so they are left alone.</summary>
    public static bool ShouldMove(string path, long peakBytesPer10s, IEnumerable<AppRule> rules, long thresholdBytes = 1_000_000)
        => IsCovered(path, rules) || peakBytesPer10s >= thresholdBytes;

    /// <summary>One rule for the whole batch (e.g. a launcher and its helpers). Programs already covered by an existing
    /// watch rule are skipped. A program is "covered" when a rule lists its file name AND its folder matches (or the rule has no folder),
    /// so two different launchers that are both called launcher.exe are not confused.</summary>
    public static (AppRule? Rule, List<string> Skipped) WatchRuleFor(IEnumerable<string> paths, IEnumerable<AppRule> existing)
    {
        var rules = existing.ToList();
        var watch = rules.Where(r => r.Action.Equals("watch", StringComparison.OrdinalIgnoreCase)).ToList();
        var skipped = new List<string>(); var files = new List<string>(); var dirs = new List<string>();

        foreach (var p in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string file = Path.GetFileName(p), dir = Path.GetDirectoryName(p) ?? "";
            bool covered = IsCovered(p, watch);
            if (covered || files.Contains(file, StringComparer.OrdinalIgnoreCase)) { skipped.Add(file); continue; }
            files.Add(file); dirs.Add(dir);
        }
        if (files.Count == 0) return (null, skipped);

        string stem = Path.GetFileNameWithoutExtension(files[0]);
        string baseName = stem.Length > 0 ? char.ToUpperInvariant(stem[0]) + stem[1..] : "Rule", name = baseName;   // "steam" -> "Steam"
        for (int i = 2; rules.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++) name = $"{baseName} {i}";

        // the folders of the picked programs narrow the rule to those installs: a launcher and its game live in different folders, and both are kept (one bundle)
        var (folder, folders) = FromLines(string.Join("\n", dirs));
        return (new AppRule(name, null, "watch", Exes: files.ToArray(), Folder: folder, Folders: folders), skipped);
    }

    /// <summary>One folder per line (the Policy page's folder box) to the rule's fields: none = (null, null), one = "folder", several = "folders".</summary>
    public static (string? Folder, string[]? Folders) FromLines(string text)
    {
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return lines.Count == 0 ? (null, null) : lines.Count == 1 ? (lines[0], null) : (null, lines.ToArray());
    }

    /// <summary>The folder box text for a rule: one folder per line.</summary>
    public static string ToLines(AppRule r) => string.Join("\r\n", r.AllFolders());
}
