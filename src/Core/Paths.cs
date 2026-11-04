namespace NetworkAutoSwitch.Core;

/// <summary>Shared locations so the command-line tool and the tray app see the same state and log.</summary>
public static class Paths
{
    public static string DataDir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetworkAutoSwitch");
            Directory.CreateDirectory(d);
            return d;
        }
    }
    public static string ProgramsFile => Path.Combine(DataDir, "programs.json");
    public static string UsageFile => Path.Combine(DataDir, "usage.json");
    public static string StateFile => Path.Combine(DataDir, "state.json");
    public static string LogFile => Path.Combine(DataDir, "log.txt");
    /// <summary>The one editable policy file, shared by the tray, the window and the CLI.
    /// On first use it is seeded from the policy.json shipped next to the program.</summary>
    public static string DefaultPolicy
    {
        get
        {
            var f = Path.Combine(DataDir, "policy.json");
            var seed = Path.Combine(AppContext.BaseDirectory, "policy.json");
            if (!File.Exists(f) && File.Exists(seed)) File.Copy(seed, f);
            return f;
        }
    }

    static readonly object _logLock = new();
    public static void AppendLog(string line)
    {
        try
        {
            lock (_logLock)
            {
                var fi = new FileInfo(LogFile);
                if (fi.Exists && fi.Length > 1_000_000) File.Move(LogFile, LogFile + ".old", true);
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break switching */ }
    }
}
