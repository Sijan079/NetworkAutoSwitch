namespace NetworkAutoSwitch.Core;

/// <summary>How the two network tables are read, for BOTH tables together.
/// Blacklist: a table lists networks that may NOT be that role. A network barred from main is always a backup; one barred from backup is always main. Anything not listed is free.
/// Whitelist: a table lists the ONLY networks that may be that role. Anything not listed is not allowed to be either.</summary>
public enum ListMode { Blacklist, Whitelist }

/// <summary>What a network turns out to be, once both tables and the mode are applied.
/// Unlabelled = on neither table (the app asks once; until you answer it is treated as a backup, because a wrong guess toward "backup" only costs a blocked download, while a wrong guess toward "main" costs data).
/// Barred = not allowed to be main and not allowed to be backup.</summary>
public enum NetworkRole { Unlabelled, Main, Backup, Either, Barred }

/// <summary>Networks (by name, for example "SNcial") are sorted into a Main table and a Backup table. The role belongs to the NETWORK; an adapter is only a pipe that happens to be on one.
/// So a backup-ish network is protected (heavy downloads blocked, daily data limit) on whichever adapter it is on. Matching is by name only: a different network with the same
/// name is treated the same, which errs on the safe side. All pure functions (easy to test).</summary>
public static class BackupNetworks
{
    public static bool Matches(IEnumerable<string> list, string? network) =>
        !string.IsNullOrWhiteSpace(network) && list.Any(n => n.Trim().Equals(network.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The role of a network given the mode and the two tables (mainTable / backupTable are the lists as the user sees them in that mode).</summary>
    public static NetworkRole RoleOf(string? network, ListMode mode, IEnumerable<string> mainTable, IEnumerable<string> backupTable)
    {
        bool inM = Matches(mainTable, network), inB = Matches(backupTable, network);
        if (!inM && !inB) return NetworkRole.Unlabelled;
        bool canMain = mode == ListMode.Blacklist ? !inM : inM, canBackup = mode == ListMode.Blacklist ? !inB : inB;
        return (canMain, canBackup) switch { (true, false) => NetworkRole.Main, (false, true) => NetworkRole.Backup, (true, true) => NetworkRole.Either, _ => NetworkRole.Barred };
    }

    /// <summary>Does this network get the backup's protections when it is the main connection? Everything except Main and Either: unlabelled is treated as backup.</summary>
    public static bool NeedsProtection(NetworkRole r) => r is NetworkRole.Unlabelled or NetworkRole.Backup or NetworkRole.Barred;

    /// <summary>The tables after saying "this network is Main" or "this network is Backup", in the given mode (the network leaves both tables first).</summary>
    public static (List<string> Main, List<string> Backup) WithRole(string network, NetworkRole role, ListMode mode, IEnumerable<string> mainTable, IEnumerable<string> backupTable)
    {
        bool Other(string x) => !x.Trim().Equals(network.Trim(), StringComparison.OrdinalIgnoreCase);
        var m = mainTable.Where(Other).ToList(); var b = backupTable.Where(Other).ToList();
        // blacklist: "main" = barred from the backup, "backup" = barred from main. whitelist: "main" = allowed as main, "backup" = allowed as backup.
        bool toMainTable = mode == ListMode.Whitelist ? role == NetworkRole.Main : role == NetworkRole.Backup;
        if (role is NetworkRole.Main or NetworkRole.Backup) (toMainTable ? m : b).Add(network.Trim());
        return (m, b);
    }

    /// <summary>Should the user be asked what this network is? Once per network: only when it is on neither table, not when the name is unknown, not when it was asked about,
    /// and not for a phone/modem adapter (protected by kind; Windows gives those generic names like "Network 3" that would match unrelated networks).</summary>
    public static bool ShouldAsk(string? network, AdapterKind kind, ListMode mode, IEnumerable<string> mainTable, IEnumerable<string> backupTable, IEnumerable<string> alreadyAsked) =>
        !string.IsNullOrWhiteSpace(network) && kind is AdapterKind.WiFi or AdapterKind.Ethernet
        && RoleOf(network, mode, mainTable, backupTable) == NetworkRole.Unlabelled && !Matches(alreadyAsked, network);

    /// <summary>Gives the roles from the tables: the adapter on a Main (or Either) network is the main connection, the one on a Backup, Either or unlabelled network is the backup,
    /// whichever adapter that is. Returns null when that cannot decide (no adapter may be main, or no other adapter may be backup): then the normal choice stays and the protections still apply.
    /// The adapters already in use keep their role while they are still valid, so nothing flips needlessly.</summary>
    public static (string Primary, string Backup)? Assign(IReadOnlyList<(string Adapter, string Network)> connected, ListMode mode, IReadOnlyCollection<string> mainTable, IReadOnlyCollection<string> backupTable, string currentPrimary, string currentBackup)
    {
        NetworkRole R(string net) => RoleOf(net, mode, mainTable, backupTable);
        var mains = connected.Where(c => R(c.Network) is NetworkRole.Main or NetworkRole.Either).Select(c => c.Adapter).ToList();
        var backs = connected.Where(c => R(c.Network) is NetworkRole.Backup or NetworkRole.Either or NetworkRole.Unlabelled).Select(c => c.Adapter).ToList();
        if (mains.Count == 0) return null;
        string p = mains.FirstOrDefault(a => a.Equals(currentPrimary, StringComparison.OrdinalIgnoreCase)) ?? mains[0];
        var backChoices = backs.Where(a => !a.Equals(p, StringComparison.OrdinalIgnoreCase)).ToList();
        if (backChoices.Count == 0) return null;
        string b = backChoices.FirstOrDefault(a => a.Equals(currentBackup, StringComparison.OrdinalIgnoreCase)) ?? backChoices[0];
        return (p, b);
    }

    /// <summary>May a network with this role be the BACKUP? Not a Main network and not a barred one (blacklist: "can't be backup"; whitelist: not on the backup list).</summary>
    public static bool BackupAllowed(NetworkRole r) => r is not (NetworkRole.Main or NetworkRole.Barred);

    /// <summary>Why the main connection needs the backup's protection: "" = it does not.
    /// <paramref name="identified"/> = false: Windows has not worked out which network the main adapter is on yet. Until it has, the main connection is protected (fail closed).</summary>
    public static string MainProtectionReason(bool sameNetwork, NetworkRole mainRole, bool identified = true) =>
        sameNetwork ? "same network as the backup"
        : !identified ? "on a network Windows has not identified yet"
        : mainRole == NetworkRole.Backup ? "on a network you labelled as backup"
        : mainRole == NetworkRole.Barred ? "on a network not allowed as main"
        : mainRole == NetworkRole.Unlabelled ? "on a network you have not labelled yet" : "";

    /// <summary>Is this something the user must be told about even with notifications muted? The main connection is (probably) the phone, or both connections are one network.
    /// Not for "Windows has not named it yet" or "not sorted yet": those are brief or already asked about.</summary>
    public static bool IsAlarming(bool sameNetwork, NetworkRole mainRole, bool identified) =>
        sameNetwork || (identified && mainRole is NetworkRole.Backup or NetworkRole.Barred);

    /// <summary>The adapters that get the backup protections (blocked heavy downloads, the watch list, the daily data count) right now.
    /// The backup adapter while it is the one in use, and the main adapter whenever it is really a backup network.</summary>
    public static List<string> ProtectedAdapters(Active active, string primary, string backup, bool mainProtected)
    {
        var l = new List<string>();
        if (active == Active.Backup && backup.Length > 0) l.Add(backup);
        if (mainProtected && primary.Length > 0 && backup.Length > 0 && !l.Contains(primary, StringComparer.OrdinalIgnoreCase)) l.Add(primary);   // with one connection only there is nothing to compare against
        return l;
    }
}
