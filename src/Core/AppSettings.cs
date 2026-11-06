using System.Text.Json;

namespace NetworkAutoSwitch.Core;

/// <summary>Choices that survive a restart: the theme and the connection mode (automatic, or locked to one connection).
/// One file, one owner, so the two settings can never overwrite each other.</summary>
public sealed class AppSettings
{
    public string Theme { get; set; } = "System";
    public string Mode { get; set; } = "Auto";
    /// <summary>Adapter names chosen by the user. Empty = detect automatically.</summary>
    public string PrimaryAdapter { get; set; } = "";
    public string BackupAdapter { get; set; } = "";
    /// <summary>What detection chose last time (used only to keep the choice stable across restarts).</summary>
    public string LastPrimary { get; set; } = "";
    public string LastBackup { get; set; } = "";
    /// <summary>How long the main connection must work, without a single failed check, before the app moves back from the backup.</summary>
    public int RecoverSeconds { get; set; } = 10;
    /// <summary>When true the tray shows no pop-up notifications (everything still goes to the log).</summary>
    public bool MuteNotifications { get; set; } = false;
    /// <summary>First-run welcome: false = still to be shown (set on a brand-new install), true = shown, missing = installed before the welcome existed (never shown).</summary>
    public bool? Welcomed { get; set; }
    /// <summary>While the main connection is fine, the idle backup is checked this often (seconds, 1 to 60). Every check on a phone tether uses a little mobile data.</summary>
    /// <summary>How many lines the Log page keeps on screen (100 to 20000). Older lines drop off the bottom; the log FILE is not shortened (it rotates at 1 MB).</summary>
    public int LogLines { get; set; } = 2000;
    public int BackupCheckSeconds { get; set; } = 10;
    /// <summary>Daily limit for data through the backup connection, in MB. 0 = no limit. You are told at 80 % and when it is reached.</summary>
    public int DailyCapMB { get; set; } = 0;

    /// <summary>Look for a newer release at start and once a day. Only reads GitHub's public release information; installing always needs your click.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>"Blacklist" or "Whitelist": how the two network tables are read (see ListMode). Each mode keeps its own pair of tables.</summary>
    public string NetworkListMode { get; set; } = "Blacklist";
    public List<string> BlacklistMainNetworks { get; set; } = new();       // networks barred from being main (so always backup)
    public List<string> BlacklistBackupNetworks { get; set; } = new();     // networks barred from being backup (so always main)
    public List<string> WhitelistMainNetworks { get; set; } = new();       // the only networks allowed to be main
    public List<string> WhitelistBackupNetworks { get; set; } = new();     // the only networks allowed to be backup

    public ConnectionMode ConnectionMode => Enum.TryParse<ConnectionMode>(Mode, true, out var m) ? m : ConnectionMode.Auto;

    public static string DefaultFile => Path.Combine(Paths.DataDir, "settings.json");

    // The UI thread and the engine thread both write this file. One lock for the whole process makes every
    // read-change-write a single step, so neither can read the other's half-written file or undo its change.
    static readonly object _lock = new();
    // The last copy known to be good, per file (as JSON so callers never share one mutable object).
    // If the file and its .bak are both unreadable, this stops a save from putting the defaults over real settings.
    static readonly Dictionary<string, string> _lastGood = new(StringComparer.OrdinalIgnoreCase);
    static readonly JsonSerializerOptions _readOpts = new() { PropertyNameCaseInsensitive = true };
    static readonly JsonSerializerOptions _writeOpts = new() { WriteIndented = true };

    /// <summary>Never throws. Missing file = defaults. A damaged file falls back to its .bak, then to the last good copy
    /// this process saw, and only then to the defaults; each fallback is logged.</summary>
    public static AppSettings Load(string? file = null)
    {
        var path = Path.GetFullPath(file ?? DefaultFile);
        lock (_lock)
        {
            if (!File.Exists(path)) return new();
            if (TryRead(path, out var s, out var why)) { _lastGood[path] = JsonSerializer.Serialize(s, _writeOpts); return s; }
            Paths.AppendLog($"settings: {path} could not be read ({why})");
            if (TryRead(path + ".bak", out var b, out _))
            {
                Paths.AppendLog("settings: using the backup copy (.bak)");
                _lastGood[path] = JsonSerializer.Serialize(b, _writeOpts);
                return b;
            }
            if (_lastGood.TryGetValue(path, out var json) && JsonSerializer.Deserialize<AppSettings>(json, _readOpts) is { } mem)
            {
                Paths.AppendLog("settings: no usable backup, keeping the settings already in memory");
                return mem;
            }
            Paths.AppendLog("settings: no usable backup, using the defaults");
            return new();
        }
    }

    static bool TryRead(string path, out AppSettings s, out string why)
    {
        s = new(); why = "";
        try
        {
            if (!File.Exists(path)) { why = "missing"; return false; }
            var r = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), _readOpts);
            if (r is null) { why = "empty"; return false; }
            s = r; return true;
        }
        catch (Exception e) { why = e.Message; return false; }
    }

    /// <summary>Never throws (a failed save is logged). The file is replaced in one step, so a reader sees the old
    /// or the new settings, never half of each; the previous good file is kept as .bak.</summary>
    public void Save(string? file = null)
    {
        var path = Path.GetFullPath(file ?? DefaultFile);
        lock (_lock)
        {
            var json = JsonSerializer.Serialize(this, _writeOpts);
            // Unique name: the CLI and the tray are separate processes and do not share the lock.
            var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(tmp, json);
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        if (!File.Exists(path)) File.Move(tmp, path);
                        // A damaged file must not become the backup, or it would push out the good one.
                        else File.Replace(tmp, path, TryRead(path, out _, out _) ? path + ".bak" : null, true);
                        break;
                    }
                    // Antivirus or the other process can hold the file for a moment; that is worth a short retry.
                    catch (IOException) when (attempt < 5) { Thread.Sleep(50 * attempt); }
                }
                _lastGood[path] = json;
            }
            catch (Exception e) { Paths.AppendLog($"settings: could not save {path} ({e.Message})"); }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }
    }

    /// <summary>Read the file, change one thing, write it back: other settings are kept, even when two threads do this at once.</summary>
    public static void Update(Action<AppSettings> change, string? file = null)
    {
        lock (_lock) { var s = Load(file); change(s); s.Save(file); }
    }
}
