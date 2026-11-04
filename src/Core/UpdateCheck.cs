using System.Net.Http;
using System.Text.Json;

namespace NetworkAutoSwitch.Core;

public enum UpdateState { UpToDate, Available, Failed }

public record UpdateResult(UpdateState State, string Latest, string Url, string Message);

/// <summary>"Is there a newer version?" Asks GitHub for the latest release, ONLY when you click the button (the app never phones home by itself).
/// Parsing and comparing are pure and tested; only <see cref="CheckAsync"/> touches the network.</summary>
public static class UpdateCheck
{
    public const string ReleasesPage = "https://github.com/Sijan079/NetworkAutoSwitch/releases";
    const string Api = "https://api.github.com/repos/Sijan079/NetworkAutoSwitch/releases/latest";

    /// <summary>Reads GitHub's "latest release" answer. The version is the tag without the leading "v". Null if it does not look like a release.</summary>
    public static (string Version, string Url)? ParseLatest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!r.TryGetProperty("tag_name", out var tag) || tag.GetString() is not string t) return null;
            string v = t.TrimStart('v', 'V');
            if (!AppInfo.IsValid(v)) return null;
            string url = r.TryGetProperty("html_url", out var u) && u.GetString() is string s && s.StartsWith("https://") ? s : ReleasesPage;
            return (v, url);
        }
        catch { return null; }
    }

    /// <summary>Decides what to tell the user, given the running version and GitHub's answer.</summary>
    public static UpdateResult Decide(string current, string? json)
    {
        var latest = json is null ? null : ParseLatest(json);
        if (latest is null) return new(UpdateState.Failed, "", ReleasesPage, "Could not read the answer from GitHub.");
        return AppInfo.Compare(latest.Value.Version, current) > 0
            ? new(UpdateState.Available, latest.Value.Version, latest.Value.Url, $"Version {latest.Value.Version} is available.")
            : new(UpdateState.UpToDate, latest.Value.Version, latest.Value.Url, "You have the latest version.");
    }

    public static async Task<UpdateResult> CheckAsync(string current)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NetworkAutoSwitch-update-check");   // GitHub's API requires a User-Agent; nothing about you is sent
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return Decide(current, await http.GetStringAsync(Api).ConfigureAwait(false));
        }
        catch (Exception e) { return new(UpdateState.Failed, "", ReleasesPage, "Could not reach GitHub: " + e.GetBaseException().Message); }
    }
}
