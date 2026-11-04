using System.Diagnostics;
using System.Text.RegularExpressions;

namespace NetworkAutoSwitch.Core;

/// <summary>"Start when I sign in": a scheduled task that runs the app with the highest rights 30 s after sign-in (so a slow Wi-Fi connect is not taken for an outage).
/// Running as a task is what lets the app start already elevated, with no UAC prompt at every sign-in. The installer creates the very same task, so the checkbox in Settings
/// and the installer's option are one thing.</summary>
public static class StartupTask
{
    public const string TaskName = "NetworkAutoSwitchTray";

    static string Q(string path) => "'" + path.Replace("'", "''") + "'";     // a path inside a PowerShell single-quoted string

    /// <summary>The PowerShell that creates (or replaces) the task for the program at <paramref name="exePath"/>.</summary>
    public static string EnableCommand(string exePath) =>
        $"$u=[Security.Principal.WindowsIdentity]::GetCurrent().Name; $a=New-ScheduledTaskAction -Execute {Q(exePath)} -WorkingDirectory {Q(System.IO.Path.GetDirectoryName(exePath) ?? "")}; " +
        "$t=New-ScheduledTaskTrigger -AtLogOn -User $u; $t.Delay='PT30S'; " +
        "$p=New-ScheduledTaskPrincipal -UserId $u -LogonType Interactive -RunLevel Highest; " +
        "$s=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew; " +
        $"Register-ScheduledTask -TaskName {TaskName} -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null";

    public static string DisableCommand => $"Unregister-ScheduledTask -TaskName {TaskName} -Confirm:$false -ErrorAction SilentlyContinue";

    /// <summary>The program the task starts, read from the task's XML; null if there is none.</summary>
    public static string? ParseTarget(string taskXml)
    {
        var m = Regex.Match(taskXml, @"<Command>(.*?)</Command>", RegexOptions.Singleline);
        if (!m.Success) return null;
        return System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim().Trim('"');
    }

    /// <summary>The program the sign-in task starts, or null when "start when I sign in" is off. Quick (no PowerShell).</summary>
    public static string? Target()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in new[] { "/Query", "/TN", TaskName, "/XML" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string xml = p.StandardOutput.ReadToEnd(); p.WaitForExit();
            return p.ExitCode == 0 ? ParseTarget(xml) : null;
        }
        catch { return null; }
    }

    public static bool IsEnabled() => Target() is not null;

    /// <summary>Turns it on (for the program at <paramref name="exePath"/>) or off. Needs administrator rights, which the tray has. Returns true when the result is what was asked for.</summary>
    public static bool Set(bool on, string exePath, Action<string>? log = null)
    {
        BackupPolicy.Run(new[] { on ? EnableCommand(exePath) : DisableCommand }, false, log);
        bool now = IsEnabled();
        log?.Invoke(on ? (now ? $"start at sign-in: on ({exePath})" : "start at sign-in: could not be turned on (administrator rights needed)")
                       : (now ? "start at sign-in: could not be turned off" : "start at sign-in: off"));
        return now == on;
    }
}
