using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

/// <summary>The colour role of a log row's chip. Kept as a role, not a Color, so the row follows the theme (the palette is read at paint time).</summary>
enum LogRole { Muted, Main, Backup, Red, Blue }

/// <summary>One line of the Log page, worked out once when it arrives (never per paint): the clock time, the chip label and its role.</summary>
sealed class LogRow
{
    public string Stamp { get; }      // "yyyy-MM-dd HH:mm:ss" as in the file ("" for lines without one), kept for copying
    public string Clock { get; }      // "HH:mm:ss": the row shows the time, the date is in the file
    public string Message { get; }
    public LogKind Kind { get; }
    public string Label { get; }      // the chip text ("" = no chip)
    public LogRole Role { get; }

    public LogRow(string line)
    {
        var (time, msg) = LogClassifier.Split(line);
        Stamp = time; Clock = time.Length >= 19 ? time[11..19] : time; Message = msg; Kind = LogClassifier.Classify(msg);
        (Label, Role) = Describe(Kind, msg);
    }

    /// <summary>The chip: the kind from LogClassifier, with a finer label for the blue info lines (Route, Migrate, Watchdog, ...) taken
    /// from the prefixes the engine writes. A switch is amber when it goes to the backup and teal when it comes back to the main connection.</summary>
    static (string, LogRole) Describe(LogKind k, string m)
    {
        switch (k)
        {
            case LogKind.Switch: return ("Switch", m.Contains("-> Backup", StringComparison.OrdinalIgnoreCase) ? LogRole.Backup : LogRole.Main);
            case LogKind.Block: return ("Block", LogRole.Red);
            case LogKind.Unblock: return ("Unblock", LogRole.Main);
            case LogKind.Error: return ("Error", LogRole.Red);
            case LogKind.Command: return ("Command", LogRole.Muted);
            case LogKind.Info:
                if (m.Contains("[route]")) return ("Route", LogRole.Blue);
                if (m.Contains("[migrate]")) return ("Migrate", LogRole.Blue);
                if (m.Contains("[watchdog]")) return ("Watchdog", LogRole.Blue);
                if (m.StartsWith("policy reloaded")) return ("Rules", LogRole.Blue);
                if (m.Contains("mode=")) return ("Mode", LogRole.Blue);
                if (m.StartsWith("tray started") || m.StartsWith("paused") || m.StartsWith("resumed") || m.StartsWith("Restoring")) return ("Engine", LogRole.Blue);
                if (m.StartsWith("theme:")) return ("Settings", LogRole.Blue);
                return ("Info", LogRole.Blue);
            default:
                // plain lines: a label when the prefix says what it is about, otherwise a muted "Note"
                if (m.StartsWith("settings:")) return ("Settings", LogRole.Blue);
                if (m.StartsWith("mode:")) return ("Mode", LogRole.Blue);
                if (m.StartsWith("adapters changed") || m.StartsWith("primary=")) return ("Adapters", LogRole.Blue);
                if (m.Contains("network", StringComparison.OrdinalIgnoreCase)) return ("Network", LogRole.Blue);
                return ("Note", LogRole.Muted);
        }
    }

    /// <summary>The page's filter (All, Switches, Blocks, Errors, Commands) and the search text.</summary>
    public bool Matches(int filter, string query)
    {
        bool kind = filter switch
        {
            1 => Kind == LogKind.Switch,
            2 => Kind is LogKind.Block or LogKind.Unblock,
            3 => Kind == LogKind.Error,
            4 => Kind == LogKind.Command,
            _ => true,
        };
        return kind && (query.Length == 0 || Message.Contains(query, StringComparison.OrdinalIgnoreCase) || Label.Contains(query, StringComparison.OrdinalIgnoreCase) || Clock.Contains(query));
    }

    public override string ToString() => Stamp.Length > 0 ? $"{Stamp}  {Message}" : Message;     // what Copy puts on the clipboard (the file's format)
}
