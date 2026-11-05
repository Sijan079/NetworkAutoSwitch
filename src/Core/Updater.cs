using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetworkAutoSwitch.Core;

/// <summary>Installs a newer version from the GitHub release: download the installer, check it against the release's
/// SHA-256 file, run it quietly. The parsing and checking are pure (tested); only Download and Launch touch the machine.
/// The checksum catches a damaged download; it is not a signature (both files come from the same release).</summary>
public static class Updater
{
    const string DownloadPrefix = "https://github.com/Sijan079/NetworkAutoSwitch/releases/download/";

    /// <summary>The installer and checksum links of a release, only if both exist, have the expected names and point at this project's own downloads.</summary>
    public static (string Setup, string Sha)? ParseAssets(string json, string version)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
            string setupName = $"NetworkAutoSwitch-Setup-{version}.exe", shaName = setupName + ".sha256";
            string? setup = null, sha = null;
            foreach (var a in assets.EnumerateArray())
            {
                string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                string? url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (name is null || url is null || !url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) continue;
                if (name == setupName) setup = url; else if (name == shaName) sha = url;
            }
            return setup is not null && sha is not null ? (setup, sha) : null;
        }
        catch { return null; }
    }

    /// <summary>Reads a ".sha256" file ("hash  filename" or just the hash). Null if it is not a SHA-256 hash.</summary>
    public static string? ParseHash(string text)
    {
        var m = Regex.Match(text ?? "", @"\b[0-9a-fA-F]{64}\b");
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    public static string HashOf(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>The quiet-install options. The sign-in task is kept as it is now (the installer would otherwise fall back to its default).</summary>
    public static string InstallerArgs(bool startAtSignIn) =>
        "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- " + (startAtSignIn ? "/MERGETASKS=\"startup\"" : "/MERGETASKS=\"!startup\"");

    /// <summary>Downloads and checks the installer. Returns (path, null) when it is ready, or (null, what went wrong). A file that fails the check is deleted.</summary>
    public static async Task<(string? Path, string? Error)> DownloadAsync(UpdateResult u, Action<string>? progress = null)
    {
        if (u.State != UpdateState.Available || u.SetupUrl.Length == 0 || u.ShaUrl.Length == 0) return (null, "This release has no installer to download.");
        string dir = Path.Combine(Path.GetTempPath(), "NetworkAutoSwitch-update");
        string file = Path.Combine(dir, $"NetworkAutoSwitch-Setup-{u.Latest}.exe");
        try
        {
            Directory.CreateDirectory(dir);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NetworkAutoSwitch-update");
            progress?.Invoke("Reading the checksum...");
            string? expected = ParseHash(await http.GetStringAsync(u.ShaUrl).ConfigureAwait(false));
            if (expected is null) return (null, "The checksum file of this release is not valid.");
            progress?.Invoke("Downloading " + u.Latest + "...");
            using (var resp = await http.GetAsync(u.SetupUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var dst = File.Create(file);
                await src.CopyToAsync(dst).ConfigureAwait(false);
            }
            progress?.Invoke("Checking the download...");
            if (HashOf(file) != expected) { try { File.Delete(file); } catch { } return (null, "The download does not match its checksum, so it was thrown away."); }
            return (file, null);
        }
        catch (Exception e) { try { File.Delete(file); } catch { } return (null, "Download failed: " + e.GetBaseException().Message); }
    }

    /// <summary>Starts the installer. It closes this app the normal way (network settings put back), replaces it and starts the new one.</summary>
    public static bool Launch(string installer, bool startAtSignIn, Action<string> log)
    {
        try
        {
            Process.Start(new ProcessStartInfo(installer, InstallerArgs(startAtSignIn)) { UseShellExecute = true });
            log("update: installer started (" + Path.GetFileName(installer) + ")");
            return true;
        }
        catch (Exception e) { log("update: could not start the installer: " + e.Message); return false; }
    }
}
