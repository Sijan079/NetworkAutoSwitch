namespace NetworkAutoSwitch.Core;

public enum LogKind { Normal, Command, Switch, Block, Unblock, Error, Info }

/// <summary>Decides what a log line is about, so the window can color it. Pure logic, no UI.</summary>
public static class LogClassifier
{
    /// <summary>Splits "yyyy-MM-dd HH:mm:ss  message" into its time and message. Lines without a timestamp keep an empty time.</summary>
    public static (string Time, string Message) Split(string line)
    {
        if (line.Length > 19 && DateTime.TryParseExact(line[..19], "yyyy-MM-dd HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out _))
            return (line[..19], line[19..].Trim());
        return ("", line.Trim());
    }

    /// <summary>First matching rule wins. Order matters: "UNBLOCK" contains "BLOCK", so it is checked first.</summary>
    public static LogKind Classify(string message)
    {
        string m = message.Trim();
        if (m.Contains("FAILED") || m.Contains("CRASHED") || m.Contains("WARNING") || m.StartsWith("could not", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("(could not", StringComparison.OrdinalIgnoreCase)) return LogKind.Error;
        if (m.StartsWith("SWITCH")) return LogKind.Switch;
        if (m.Contains("UNBLOCK") || m.Contains("OVERRIDE") || m.Contains("watching again")) return LogKind.Unblock;
        if (m.Contains("[watchdog] BLOCK") || m.Contains("BLOCKED AGAIN")) return LogKind.Block;
        if (m.StartsWith("[run]") || m.StartsWith("[dry-run]") || m.StartsWith("#")) return LogKind.Command;
        if (m.StartsWith("Restoring") || m.StartsWith("paused") || m.StartsWith("resumed") || m.StartsWith("policy reloaded")
            || m.Contains("[watchdog] started") || m.Contains("mode=") || m.StartsWith("tray started") || m.Contains("[migrate]") || m.Contains("[route]") || m.StartsWith("theme:")) return LogKind.Info;
        return LogKind.Normal;
    }

    /// <summary>The short tag shown on the row, or empty for rows that get no tag.</summary>
    public static string Tag(LogKind k) => k switch
    {
        LogKind.Switch => "SWITCH", LogKind.Block => "BLOCK", LogKind.Unblock => "UNBLOCK", LogKind.Error => "ERROR", LogKind.Info => "INFO", _ => "",
    };
}
