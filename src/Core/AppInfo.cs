using System.Reflection;
using System.Text.RegularExpressions;

namespace NetworkAutoSwitch.Core;

/// <summary>What this build is: its version (from Directory.Build.props, the single source of truth) and a helper to compare versions
/// by the SemVer 2.0 rules, which the installer's "never downgrade" rule and the tests use.</summary>
public static class AppInfo
{
    public const string Name = "Network Auto Switch";

    static readonly string Info = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>"0.1.0" or "0.2.0-beta.1" (without the "+commit" part).</summary>
    public static string Version => Info.Split('+')[0];

    /// <summary>"0.1.0+abc1234" when built from git: put this in bug reports.</summary>
    public static string FullVersion { get { var p = Info.Split('+'); return p.Length > 1 && p[1].Length > 7 ? $"{p[0]}+{p[1][..7]}" : Info; } }

    static readonly Regex SemVer = new(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.Compiled);

    public static bool IsValid(string v) => SemVer.IsMatch(v);

    /// <summary>SemVer 2.0 precedence: numbers first; a version WITH a prerelease tag is lower than the same number without
    /// (1.0.0-beta &lt; 1.0.0); prerelease parts compare one by one (numbers as numbers, words as text, numbers before words).
    /// Build metadata (+abc) is ignored. Returns negative, 0 or positive, like CompareTo.</summary>
    public static int Compare(string a, string b)
    {
        var ma = SemVer.Match(a); var mb = SemVer.Match(b);
        if (!ma.Success || !mb.Success) throw new FormatException($"not a valid version: '{(ma.Success ? b : a)}'");
        for (int i = 1; i <= 3; i++)
        {
            int c = long.Parse(ma.Groups[i].Value).CompareTo(long.Parse(mb.Groups[i].Value));
            if (c != 0) return c;
        }
        string pa = ma.Groups[4].Value, pb = mb.Groups[4].Value;
        if (pa.Length == 0 && pb.Length == 0) return 0;
        if (pa.Length == 0) return 1;      // release > prerelease
        if (pb.Length == 0) return -1;
        var xa = pa.Split('.'); var xb = pb.Split('.');
        for (int i = 0; i < Math.Min(xa.Length, xb.Length); i++)
        {
            bool na = long.TryParse(xa[i], out var la), nb = long.TryParse(xb[i], out var lb);
            int c = na && nb ? la.CompareTo(lb) : na ? -1 : nb ? 1 : string.CompareOrdinal(xa[i], xb[i]);
            if (c != 0) return c;
        }
        return xa.Length.CompareTo(xb.Length);
    }
}
