using System.Windows.Threading;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray.Fluent;

/// <summary>Everything the Fluent pages need from the tray: the engine, the theme, mute, the log feed and policy saving. Pages get this instead of reaching into the tray.</summary>
sealed class FluentContext
{
    public SwitchEngine Engine { get; }
    public Action TogglePause { get; }
    public Func<ThemeMode> GetTheme { get; }
    public Action<ThemeMode> SetTheme { get; }
    public Func<bool> GetMute { get; }
    public Action<bool> SetMute { get; }
    /// <summary>Raised (on any thread) for every new log line.</summary>
    public event Action<string>? LogLine;
    public void FeedLog(string line) => LogLine?.Invoke(line);
    /// <summary>Raised after the policy file was written (so other pages can reload).</summary>
    public event Action? PolicyChanged;

    public FluentContext(SwitchEngine engine, Action togglePause, Func<ThemeMode> getTheme, Action<ThemeMode> setTheme, Func<bool> getMute, Action<bool> setMute)
    { Engine = engine; TogglePause = togglePause; GetTheme = getTheme; SetTheme = setTheme; GetMute = getMute; SetMute = setMute; }

    public static string PolicyPath => Paths.DefaultPolicy;

    /// <summary>The policy on disk (never throws: a missing or damaged file gives an empty policy).</summary>
    public PolicyFile ReadPolicy()
    {
        try { return BackupPolicy.Parse(File.ReadAllText(PolicyPath)); } catch { return new PolicyFile(new List<AppRule>()); }
    }

    /// <summary>Checks, saves (keeping a .bak) and applies a policy. <paramref name="say"/> is called on the UI thread with progress and the result.</summary>
    public void WritePolicy(PolicyFile policy, Action<string> say)
    {
        string json = BackupPolicy.ToJson(policy);
        BackupPolicy.Parse(json);                                  // a typo or incomplete rule must never reach the running engine
        if (File.Exists(PolicyPath)) File.Copy(PolicyPath, PolicyPath + ".bak", true);
        File.WriteAllText(PolicyPath, json);
        say("Saving...");
        PolicyChanged?.Invoke();
        var d = Dispatcher.CurrentDispatcher;
        Task.Run(() =>
        {
            try { Engine.ReloadPolicy(); d.BeginInvoke(() => say("Saved and applied.")); }
            catch (Exception e) { d.BeginInvoke(() => say("Saved, but applying failed: " + e.Message)); }
        });
    }
}
