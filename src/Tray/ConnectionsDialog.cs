using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

/// <summary>Sort your networks into a Main table and a Backup table. The role belongs to the NETWORK (its name), so it holds on whichever adapter that network is on.
/// One mode for both tables: Blacklist (a table lists networks that may NOT be that role) or Whitelist (a table lists the ONLY networks that may be that role).
/// Networks on neither table are "unlabelled" and are treated as backup until you sort them. The adapters themselves are under Advanced: they only matter when the tables cannot decide.</summary>
sealed class ConnectionsDialog : Form
{
    sealed record Choice(string? Name, string Text) { public override string ToString() => Text; }
    sealed record Net(string Name, string Text) { public override string ToString() => Text; }

    readonly SwitchEngine _engine;
    readonly NoWheelComboBox _modeBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    readonly Label _modeHint = new() { AutoSize = false, Height = 40, Dock = DockStyle.Fill, ForeColor = Ui.Muted, BackColor = Ui.Bg };
    readonly Label _mainCap = Cap(""), _backupCap = Cap(""), _noneCap = Cap("Unlabelled  (treated as backup)");
    readonly ListBox _lbMain = MakeList(), _lbBackup = MakeList(), _lbNone = MakeList();
    readonly NoWheelComboBox _main = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, Height = 32 };
    readonly NoWheelComboBox _backup = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, Height = 32 };
    readonly Panel _advanced = new() { Dock = DockStyle.Fill, BackColor = Ui.Bg, Visible = false };
    readonly CheckBox _advancedBox = new() { Text = "Choose adapters myself", AutoSize = true, ForeColor = Ui.Muted, BackColor = Ui.Bg };
    readonly Label _problem = new() { AutoSize = true, ForeColor = Ui.Red, Padding = new Padding(0, 6, 0, 0), BackColor = Ui.Bg };
    readonly Button _save;

    // one working pair of tables per mode (each mode keeps its own), plus every network we know about: connected now + saved in Windows
    readonly Dictionary<ListMode, (List<string> Main, List<string> Backup)> _work = new();
    readonly Dictionary<string, string> _where = new(StringComparer.OrdinalIgnoreCase);     // network -> "connected on Wi-Fi 2" (empty = only saved in Windows)
    readonly List<string> _known = new();
    ListMode _mode;

    static Label Cap(string t) => new() { Text = t, AutoSize = false, Height = 26, Dock = DockStyle.Fill, ForeColor = Ui.Muted, Font = Ui.UiFont(9.5f, bold: true), BackColor = Ui.Bg, Padding = new Padding(0, 4, 0, 0) };
    static ListBox MakeList() => new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Ui.CardBg, ForeColor = Ui.Text, Font = Ui.UiFont(9.5f), IntegralHeight = false, Margin = new Padding(0, 0, 8, 0) };

    public ConnectionsDialog(SwitchEngine engine)
    {
        _engine = engine;
        Text = "Choose Connections"; Icon = Ui.AppIcon(Ui.Blue);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(700, 600); StartPosition = FormStartPosition.CenterParent; BackColor = Ui.Bg; Font = Ui.UiFont(9.5f);

        var t = engine.Tables; _mode = t.Mode;
        _work[ListMode.Blacklist] = (t.BlacklistMain.ToList(), t.BlacklistBackup.ToList());
        _work[ListMode.Whitelist] = (t.WhitelistMain.ToList(), t.WhitelistBackup.ToList());
        void Know(string? n, string where) { if (string.IsNullOrWhiteSpace(n)) return; n = n.Trim(); if (!_known.Contains(n, StringComparer.OrdinalIgnoreCase)) _known.Add(n); if (where.Length > 0) _where[n] = where; }
        Know(engine.PrimaryNetwork, engine.PrimaryName.Length > 0 ? $"connected on {engine.PrimaryName}" : ""); Know(engine.BackupNetwork, engine.BackupName.Length > 0 ? $"connected on {engine.BackupName}" : "");
        foreach (var n in NetworkNames.SavedWifi()) Know(n, "");
        foreach (var n in _work.Values.SelectMany(w => w.Main.Concat(w.Backup))) Know(n, "");        // a labelled network that is not around now stays visible

        _modeBox.Items.AddRange(new object[] { "Blacklist", "Whitelist" }); _modeBox.SelectedIndex = (int)_mode;
        Ui.Tip.SetToolTip(_modeBox, "Blacklist: listed networks are barred from the role. Whitelist: only listed networks may have it.");
        _modeBox.SelectedIndexChanged += (_, _) => { _mode = (ListMode)_modeBox.SelectedIndex; Refill(); };

        // adapters (Advanced)
        var all = AdapterDiscovery.Candidates(AdapterDiscovery.Detect());
        Choice Auto(string current) => new(null, $"Automatic  ({(current.Length > 0 ? current : "none found")})");
        Choice Of(AdapterInfo a) => new(a.Name, $"{a.Name}  ·  {AdapterDiscovery.KindLabel(a.Kind)}" + (a.IsUp ? "" : "  ·  Not Connected"));
        Fill(_main, Auto(engine.PrimaryName), all, Of, engine.UserPrimary);
        Fill(_backup, Auto(engine.BackupName), all, Of, engine.UserBackup);
        Ui.Tip.SetToolTip(_main, "Used only when the tables can't decide.");
        Ui.Tip.SetToolTip(_backup, "Used only when the tables can't decide.");
        var advBody = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Ui.Bg };
        advBody.Controls.Add(Cap("Main adapter"), 0, 0); advBody.Controls.Add(_main, 0, 1);
        advBody.Controls.Add(Cap("Backup adapter"), 0, 2); advBody.Controls.Add(_backup, 0, 3);
        _advanced.Controls.Add(advBody);
        _advancedBox.CheckedChanged += (_, _) => { _advanced.Visible = _advancedBox.Checked; ClientSize = new Size(700, _advancedBox.Checked ? 780 : 600); };

        // layout
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Ui.Bg, Padding = new Padding(Ui.Pad + 4, Ui.Pad, Ui.Pad + 4, 0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        int Row(float h, SizeType st = SizeType.Absolute) { grid.RowStyles.Add(new RowStyle(st, h)); return grid.RowStyles.Count - 1; }
        var modeRow = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Ui.Bg, WrapContents = false };
        modeRow.Controls.Add(new Label { Text = "Mode", AutoSize = true, ForeColor = Ui.Text, BackColor = Ui.Bg, Padding = new Padding(0, 6, 10, 0), Font = Ui.UiFont(9.5f, bold: true) }); modeRow.Controls.Add(_modeBox);
        grid.Controls.Add(modeRow, 0, Row(36)); grid.SetColumnSpan(modeRow, 2);
        grid.Controls.Add(_modeHint, 0, Row(44)); grid.SetColumnSpan(_modeHint, 2);
        int r = Row(26); grid.Controls.Add(_mainCap, 0, r); grid.Controls.Add(_backupCap, 1, r);
        r = Row(150); grid.Controls.Add(_lbMain, 0, r); grid.Controls.Add(_lbBackup, 1, r);
        r = Row(26); grid.Controls.Add(_noneCap, 0, r); grid.SetColumnSpan(_noneCap, 2);
        r = Row(100); grid.Controls.Add(_lbNone, 0, r); grid.SetColumnSpan(_lbNone, 2);
        var moves = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Ui.Bg, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        moves.Controls.Add(Ui.Btn(Ui.Home, "Make Main", "Make the selected network a main network.", () => SetRole(NetworkRole.Main)));
        moves.Controls.Add(Ui.Btn(Ui.Phone, "Make Backup", "Make the selected network a backup network (your phone).", () => SetRole(NetworkRole.Backup)));
        moves.Controls.Add(Ui.Btn(Ui.Undo, "Unlabel", "Take the selected network off both tables.", () => SetRole(NetworkRole.Unlabelled)));
        r = Row(48); grid.Controls.Add(moves, 0, r); grid.SetColumnSpan(moves, 2);
        r = Row(30); grid.Controls.Add(_advancedBox, 0, r); grid.SetColumnSpan(_advancedBox, 2);
        r = Row(100, SizeType.Percent); grid.Controls.Add(_advanced, 0, r); grid.SetColumnSpan(_advanced, 2);
        r = Row(26); grid.Controls.Add(_problem, 0, r); grid.SetColumnSpan(_problem, 2);
        grid.RowCount = grid.RowStyles.Count;

        foreach (var lb in new[] { _lbMain, _lbBackup, _lbNone }) lb.SelectedIndexChanged += (_, _) => { if (lb.SelectedIndex >= 0) foreach (var other in new[] { _lbMain, _lbBackup, _lbNone }) if (other != lb) other.ClearSelected(); };

        _save = Ui.Btn(Ui.Save, "Save", "Save the tables and adapters.", Save, BtnKind.Primary);
        var cancel = Ui.Btn(Ui.Clear, "Cancel", "Close without saving.", () => DialogResult = DialogResult.Cancel);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(Ui.Pad + 4, Ui.Gap, Ui.Pad + 4, Ui.Gap), BackColor = Ui.Bg };
        bar.Controls.Add(_save); bar.Controls.Add(cancel);
        Controls.Add(grid); Controls.Add(bar);
        AcceptButton = _save; CancelButton = cancel;

        _main.SelectedIndexChanged += (_, _) => Validate_();
        _backup.SelectedIndexChanged += (_, _) => Validate_();
        Load += (_, _) => Ui.SkinForm(this);
        Refill();
        Validate_();
    }

    /// <summary>Shows the tables of the current mode, with captions that say what a table means in that mode.</summary>
    void Refill()
    {
        bool black = _mode == ListMode.Blacklist;
        _mainCap.Text = black ? "Main table: can't be main" : "Main table: allowed as main";
        _backupCap.Text = black ? "Backup table: can't be backup" : "Backup table: allowed as backup";
        _modeHint.Text = black ? "Listed networks are barred from that role. Barred from main = always backup; barred from backup = always main."
                               : "Only listed networks may have that role. A network on both tables can be either.";
        var (m, b) = _work[_mode];
        Net Show(string n) => new(n, _where.TryGetValue(n, out var w) && w.Length > 0 ? $"{n}   ·   {w}" : n);
        void Load_(ListBox lb, IEnumerable<string> names) { lb.Items.Clear(); foreach (var n in names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) lb.Items.Add(Show(n)); }
        Load_(_lbMain, m); Load_(_lbBackup, b);
        Load_(_lbNone, _known.Where(n => !BackupNetworks.Matches(m, n) && !BackupNetworks.Matches(b, n)));
    }

    /// <summary>Moves the selected network: Main / Backup = give it that role in the current mode (the tables are updated for you), Unlabelled = off both tables.</summary>
    void SetRole(NetworkRole role)
    {
        string? name = ((_lbMain.SelectedItem ?? _lbBackup.SelectedItem ?? _lbNone.SelectedItem) as Net)?.Name;
        if (name is null) return;
        var (m, b) = _work[_mode];
        _work[_mode] = role == NetworkRole.Unlabelled
            ? (m.Where(x => !x.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList(), b.Where(x => !x.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList())
            : BackupNetworks.WithRole(name, role, _mode, m, b);
        Refill();
    }

    static void Fill(ComboBox box, Choice auto, List<AdapterInfo> all, Func<AdapterInfo, Choice> of, string chosen)
    {
        box.Items.Add(auto);
        foreach (var a in all) box.Items.Add(of(a));
        int i = string.IsNullOrEmpty(chosen) ? 0 : box.Items.Cast<Choice>().ToList().FindIndex(c => string.Equals(c.Name, chosen, StringComparison.OrdinalIgnoreCase));
        if (i < 0) { box.Items.Add(new Choice(chosen, $"{chosen}  ·  Not Found")); i = box.Items.Count - 1; }   // an earlier choice for an adapter that is not here now stays visible
        box.SelectedIndex = i;
    }

    string? Picked(ComboBox b) => (b.SelectedItem as Choice)?.Name;

    void Validate_()
    {
        bool same = Picked(_main) is string m && Picked(_backup) is string b && m.Equals(b, StringComparison.OrdinalIgnoreCase);
        _problem.Text = same ? "Pick two different adapters." : "";
        _save.Enabled = !same;
    }

    void Save()
    {
        var (m, b) = (Picked(_main), Picked(_backup));
        var bl = _work[ListMode.Blacklist]; var wl = _work[ListMode.Whitelist];
        var tables = new NetworkTables(_mode, bl.Main, bl.Backup, wl.Main, wl.Backup);
        DialogResult = DialogResult.OK;                                  // close now; the engine applies it in the background
        Task.Run(() => { try { _engine.SetNetworkTables(tables); _engine.SetAdapterChoice(m, b); } catch (Exception e) { Paths.AppendLog("Choosing connections failed: " + e.Message); } });
    }
}
