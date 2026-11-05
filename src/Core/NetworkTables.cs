namespace NetworkAutoSwitch.Core;

/// <summary>The two tables (Main, Backup) and the mode, as saved. Each mode keeps its OWN pair of tables, because the same list means the opposite thing in the two modes
/// (a Blacklist "Main" table = networks barred from main; a Whitelist "Main" table = the only networks allowed to be main). Immutable: the engine swaps the whole object.</summary>
public sealed record NetworkTables(ListMode Mode, List<string> BlacklistMain, List<string> BlacklistBackup, List<string> WhitelistMain, List<string> WhitelistBackup)
{
    /// <summary>The Main table as the user sees it in the current mode.</summary>
    public List<string> Main => Mode == ListMode.Blacklist ? BlacklistMain : WhitelistMain;
    public List<string> Backup => Mode == ListMode.Blacklist ? BlacklistBackup : WhitelistBackup;

    public NetworkRole RoleOf(string? network) => BackupNetworks.RoleOf(network, Mode, Main, Backup);

    public static NetworkTables From(AppSettings s) => new(
        Enum.TryParse<ListMode>(s.NetworkListMode, true, out var m) ? m : ListMode.Blacklist,
        Real(s.BlacklistMainNetworks), Real(s.BlacklistBackupNetworks), Real(s.WhitelistMainNetworks), Real(s.WhitelistBackupNetworks));

    // a stand-in name such as "Identifying..." was saved once by a test build: drop it on load
    static List<string> Real(List<string>? l) => (l ?? new()).Where(n => !NetworkNames.IsPlaceholder(n)).ToList();

    public void WriteTo(AppSettings s)
    {
        s.NetworkListMode = Mode.ToString();
        s.BlacklistMainNetworks = BlacklistMain; s.BlacklistBackupNetworks = BlacklistBackup; s.WhitelistMainNetworks = WhitelistMain; s.WhitelistBackupNetworks = WhitelistBackup;
    }

    /// <summary>The same tables after saying "this network is Main" / "Backup" in the CURRENT mode (the other mode's tables are left alone).</summary>
    public NetworkTables WithRole(string network, NetworkRole role)
    {
        var (m, b) = BackupNetworks.WithRole(network, role, Mode, Main, Backup);
        return Mode == ListMode.Blacklist ? this with { BlacklistMain = m, BlacklistBackup = b } : this with { WhitelistMain = m, WhitelistBackup = b };
    }
}
