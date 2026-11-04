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

    public ConnectionMode ConnectionMode => Enum.TryParse<ConnectionMode>(Mode, true, out var m) ? m : ConnectionMode.Auto;

    public static string DefaultFile => Path.Combine(Paths.DataDir, "settings.json");

    /// <summary>Never throws: a missing or damaged file just gives the defaults.</summary>
    public static AppSettings Load(string? file = null)
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file ?? DefaultFile), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
        catch { return new(); }
    }

    public void Save(string? file = null)
    {
        try { File.WriteAllText(file ?? DefaultFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    /// <summary>Read the file, change one thing, write it back: other settings are kept.</summary>
    public static void Update(Action<AppSettings> change, string? file = null) { var s = Load(file); change(s); s.Save(file); }
}
