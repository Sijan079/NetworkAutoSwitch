namespace NetworkAutoSwitch.Core;

/// <summary>Builds a watch rule from programs the user picked, so nobody has to type file names or folders.</summary>
public static class PolicyBuilder
{
    /// <summary>Is this program already covered by a watch rule? Same test everywhere: file name listed AND folder matches (or the rule has no folder).</summary>
    public static bool IsCovered(string path, IEnumerable<AppRule> rules)
    {
        string file = Path.GetFileName(path), dir = Path.GetDirectoryName(path) ?? "";
        return rules.Any(r => r.Action.Equals("watch", StringComparison.OrdinalIgnoreCase)
            && (r.Exes ?? Array.Empty<string>()).Any(e => e.Equals(file, StringComparison.OrdinalIgnoreCase))
            && (r.Folder is null || dir.StartsWith(r.Folder, StringComparison.OrdinalIgnoreCase)));
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

        // one shared install folder narrows the rule to that install; programs from several folders get no folder filter
        string? folder = dirs.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 && dirs[0].Length > 0 ? dirs[0] : null;
        return (new AppRule(name, null, "watch", Exes: files.ToArray(), Folder: folder), skipped);
    }
}
