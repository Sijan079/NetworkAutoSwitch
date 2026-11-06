using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

/// <summary>The Networks page: sort your networks into a Main table and a Backup table. The role belongs to the NETWORK (its name), so it holds on whichever adapter that network is on.
/// One mode for both tables: Blacklist (a table lists networks that may NOT be that role) or Whitelist (a table lists the ONLY networks that may be that role).
/// Networks on neither table are "unlabelled" and are treated as backup until you sort them. The adapters themselves are under Advanced: they only matter when the tables cannot decide.
/// Nothing changes until Save; Revert (or opening the page again with nothing unsaved) reads the current state again.</summary>
sealed class ConnectionsPanel : Panel
{
    sealed record Choice(string? Name, string Text) { public override string ToString() => Text; }
    sealed record Net(string Name, string Text) { public override string ToString() => Text; }

    readonly SwitchEngine _engine;
    readonly Action? _saved;
    readonly NoWheelComboBox _modeBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    readonly Label _modeHint = new() { AutoSize = false, Height = 40, Dock = DockStyle.Fill, ForeColor = Ui.Muted, BackColor = Ui.Bg };
    readonly Card _mainCard = new(Ui.Home, "Main"), _backupCard = new(Ui.Phone, "Backup"), _noneCard = new(Ui.List, "Unlabelled  (treated as backup)");
    readonly ListBox _lbMain = MakeList(), _lbBackup = MakeList(), _lbNone = MakeList();
    readonly NoWheelComboBox _main = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, Height = 32 };
    readonly NoWheelComboBox _backup = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, Height = 32 };
    readonly Panel _advanced = new() { Dock = DockStyle.Fill, BackColor = Ui.Bg, Visible = false };
    readonly CheckBox _advancedBox = new() { Text = "Choose adapters myself", AutoSize = true, ForeColor = Ui.Muted, BackColor = Ui.Bg };
    readonly Label _status = new() { AutoSize = true, Padding = new Padding(8, 9, 0, 0), ForeColor = Ui.Muted, BackColor = Ui.Bg };
    readonly Button _save;
    readonly TableLayoutPanel _grid;
    int _advancedRow;

    // one working pair of tables per mode (each mode keeps its own), plus every network we know about: connected now + saved in Windows
    readonly Dictionary<ListMode, (List<string> Main, List<string> Backup)> _work = new();
    readonly Dictionary<string, string> _where = new(StringComparer.OrdinalIgnoreCase);     // network -> "connected on Wi-Fi 2" (empty = only saved in Windows)
    readonly List<string> _known = new();
    ListMode _mode;
    bool _dirty, _loading;

    static Label Cap(string t) => new() { Text = t, AutoSize = false, Height = 26, Dock = DockStyle.Fill, ForeColor = Ui.Text, Font = Ui.UiFont(9.5f, bold: true), BackColor = Ui.Bg, Padding = new Padding(0, 4, 0, 0) };
    /// <summary>A list drawn like the app's tables: roomy rows, the network name in the text colour, where it is connected muted, the hover colour for the selection.</summary>
    static ListBox MakeList()
    {
        var lb = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Ui.CardBg, ForeColor = Ui.Text, Font = Ui.UiFont(9.5f), IntegralHeight = false,
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 30 };
        lb.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Ui.Hover : Ui.CardBg)) e.Graphics.FillRectangle(b, e.Bounds);
            var n = (Net)lb.Items[e.Index];
            var r = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
            const TextFormatFlags f = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(e.Graphics, n.Name, lb.Font, r, Ui.Text, f);
            if (n.Text.Length > n.Name.Length)
            {
                int w = TextRenderer.MeasureText(e.Graphics, n.Name, lb.Font, r.Size, f).Width;
                TextRenderer.DrawText(e.Graphics, n.Text[n.Name.Length..].Trim().TrimStart('\u00b7').Trim(), lb.Font, new Rectangle(r.X + w + 14, r.Y, Math.Max(0, r.Width - w - 14), r.Height), Ui.Muted, f);
            }
        };
        return lb;
    }

    /// <param name="saved">Called after Save (the dialog closes on it; the page stays).</param>
    public ConnectionsPanel(SwitchEngine engine, Action? saved = null)
    {
        _engine = engine; _saved = saved;
        BackColor = Ui.Bg; Font = Ui.UiFont(9.5f);

        _modeBox.Items.AddRange(new object[] { "Blacklist", "Whitelist" });
        Ui.Tip.SetToolTip(_modeBox, "Blacklist: listed networks are barred from the role. Whitelist: only listed networks may have it.");
        _modeBox.SelectedIndexChanged += (_, _) => { if (_loading) return; _mode = (ListMode)_modeBox.SelectedIndex; _dirty = true; Refill(); };

        Ui.Tip.SetToolTip(_main, "Used only when the tables can't decide.");
        Ui.Tip.SetToolTip(_backup, "Used only when the tables can't decide.");
        var advBody = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Ui.Bg };
        advBody.Controls.Add(Cap("Main adapter"), 0, 0); advBody.Controls.Add(_main, 0, 1);
        advBody.Controls.Add(Cap("Backup adapter"), 0, 2); advBody.Controls.Add(_backup, 0, 3);
        _advanced.Controls.Add(advBody);
        _advancedBox.CheckedChanged += (_, _) => { _advanced.Visible = _advancedBox.Checked; _grid!.RowStyles[_advancedRow].Height = _advancedBox.Checked ? 132 : 0; };

        // layout: the tables take the height the page has
        var grid = _grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Ui.Bg };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        int Row(float h, SizeType st = SizeType.Absolute) { grid.RowStyles.Add(new RowStyle(st, h)); return grid.RowStyles.Count - 1; }
        var modeRow = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Ui.Bg, WrapContents = false };
        modeRow.Controls.Add(new Label { Text = "Mode", AutoSize = true, ForeColor = Ui.Text, BackColor = Ui.Bg, Padding = new Padding(0, 6, 10, 0), Font = Ui.UiFont(9.5f, bold: true) }); modeRow.Controls.Add(_modeBox);
        grid.Controls.Add(modeRow, 0, Row(40)); grid.SetColumnSpan(modeRow, 2);
        grid.Controls.Add(_modeHint, 0, Row(44)); grid.SetColumnSpan(_modeHint, 2);
        // each table is a card, like the rest of the app
        _mainCard.Body.Controls.Add(_lbMain); _backupCard.Body.Controls.Add(_lbBackup); _noneCard.Body.Controls.Add(_lbNone);
        _noneCard.SetHint("Networks on neither table: seen now or saved in Windows. They are treated as backup (protected) until you sort them.");
        int r = Row(55, SizeType.Percent); grid.Controls.Add(_mainCard, 0, r); grid.Controls.Add(_backupCard, 1, r);
        r = Row(45, SizeType.Percent); grid.Controls.Add(_noneCard, 0, r); grid.SetColumnSpan(_noneCard, 2);
        var moves = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Ui.Bg, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        moves.Controls.Add(Ui.Btn(Ui.Home, "Make Main", "Make the selected network a main network.", () => SetRole(NetworkRole.Main)));
        moves.Controls.Add(Ui.Btn(Ui.Phone, "Make Backup", "Make the selected network a backup network (your phone).", () => SetRole(NetworkRole.Backup)));
        moves.Controls.Add(Ui.Btn(Ui.Undo, "Unlabel", "Take the selected network off both tables.", () => SetRole(NetworkRole.Unlabelled)));
        r = Row(50); grid.Controls.Add(moves, 0, r); grid.SetColumnSpan(moves, 2);
        r = Row(30); grid.Controls.Add(_advancedBox, 0, r); grid.SetColumnSpan(_advancedBox, 2);
        _advancedRow = Row(0); grid.Controls.Add(_advanced, 0, _advancedRow); grid.SetColumnSpan(_advanced, 2);
        grid.RowCount = grid.RowStyles.Count;

        foreach (var lb in new[] { _lbMain, _lbBackup, _lbNone }) lb.SelectedIndexChanged += (_, _) => { if (lb.SelectedIndex >= 0) foreach (var other in new[] { _lbMain, _lbBackup, _lbNone }) if (other != lb) other.ClearSelected(); };

        _save = Ui.Btn(Ui.Save, "Save", "Save the tables and adapters and apply them now.", Save, BtnKind.Primary);
        var revert = Ui.Btn(Ui.Undo, "Revert", "Discard unsaved changes.", Reload);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(0, Ui.Gap, 0, 0), BackColor = Ui.Bg };
        bar.Controls.Add(_save); bar.Controls.Add(revert); bar.Controls.Add(_status);
        Controls.Add(grid); Controls.Add(bar);

        _main.SelectedIndexChanged += (_, _) => { if (!_loading) _dirty = true; Validate_(); };
        _backup.SelectedIndexChanged += (_, _) => { if (!_loading) _dirty = true; Validate_(); };
        // coming back to the page shows the current state (network names change), unless you have unsaved changes here
        VisibleChanged += (_, _) => { if (Visible && !_dirty) Reload(); };
        Reload();
    }

    /// <summary>Reads the tables, the networks and the adapters as they are now. Unsaved changes are dropped.</summary>
    public void Reload()
    {
        _loading = true;
        try
        {
            var t = _engine.Tables; _mode = t.Mode;
            _work[ListMode.Blacklist] = (t.BlacklistMain.ToList(), t.BlacklistBackup.ToList());
            _work[ListMode.Whitelist] = (t.WhitelistMain.ToList(), t.WhitelistBackup.ToList());
            _known.Clear(); _where.Clear();
            void Know(string? n, string where) { if (string.IsNullOrWhiteSpace(n)) return; n = n.Trim(); if (!_known.Contains(n, StringComparer.OrdinalIgnoreCase)) _known.Add(n); if (where.Length > 0) _where[n] = where; }
            Know(_engine.PrimaryNetwork, _engine.PrimaryName.Length > 0 ? $"connected on {_engine.PrimaryName}" : ""); Know(_engine.BackupNetwork, _engine.BackupName.Length > 0 ? $"connected on {_engine.BackupName}" : "");
            foreach (var n in NetworkNames.SavedWifi()) Know(n, "");
            foreach (var n in _work.Values.SelectMany(w => w.Main.Concat(w.Backup))) Know(n, "");        // a labelled network that is not around now stays visible
            _modeBox.SelectedIndex = (int)_mode;

            var all = AdapterDiscovery.Candidates(AdapterDiscovery.Detect());
            Choice Auto(string current) => new(null, $"Automatic  ({(current.Length > 0 ? current : "none found")})");
            Choice Of(AdapterInfo a) => new(a.Name, $"{a.Name}  ·  {AdapterDiscovery.KindLabel(a.Kind)}" + (a.IsUp ? "" : "  ·  Not Connected"));
            Fill(_main, Auto(_engine.PrimaryName), all, Of, _engine.UserPrimary);
            Fill(_backup, Auto(_engine.BackupName), all, Of, _engine.UserBackup);
            Refill();
        }
        finally { _loading = false; }
        _dirty = false; _status.Text = "";
        Validate_();
    }

    /// <summary>Shows the tables of the current mode, with captions that say what a table means in that mode.</summary>
    void Refill()
    {
        bool black = _mode == ListMode.Blacklist;
        _mainCard.SetTitle(black ? "Main table: can't be main" : "Main table: allowed as main");
        _backupCard.SetTitle(black ? "Backup table: can't be backup" : "Backup table: allowed as backup");
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
        _dirty = true; _status.Text = "Not saved yet."; _status.ForeColor = Ui.Muted;
        Refill();
    }

    static void Fill(ComboBox box, Choice auto, List<AdapterInfo> all, Func<AdapterInfo, Choice> of, string chosen)
    {
        box.Items.Clear();
        box.Items.Add(auto);
        foreach (var a in all) box.Items.Add(of(a));
        int i = string.IsNullOrEmpty(chosen) ? 0 : box.Items.Cast<Choice>().ToList().FindIndex(c => string.Equals(c.Name, chosen, StringComparison.OrdinalIgnoreCase));
        if (i < 0) { box.Items.Add(new Choice(chosen, $"{chosen}  ·  Not Found")); i = box.Items.Count - 1; }   // an earlier choice for an adapter that is not here now stays visible
        box.SelectedIndex = i;
    }

    static string? Picked(ComboBox b) => (b.SelectedItem as Choice)?.Name;

    void Validate_()
    {
        bool same = Picked(_main) is string m && Picked(_backup) is string b && m.Equals(b, StringComparison.OrdinalIgnoreCase);
        if (same) { _status.Text = "Pick two different adapters."; _status.ForeColor = Ui.Red; }
        else if (_status.ForeColor == Ui.Red) _status.Text = "";
        _save.Enabled = !same;
    }

    void Save()
    {
        var (m, b) = (Picked(_main), Picked(_backup));
        var bl = _work[ListMode.Blacklist]; var wl = _work[ListMode.Whitelist];
        var tables = new NetworkTables(_mode, bl.Main, bl.Backup, wl.Main, wl.Backup);
        _dirty = false; _status.Text = "Saved. Applying..."; _status.ForeColor = Ui.Green;
        Task.Run(() => { try { _engine.SetNetworkTables(tables); _engine.SetAdapterChoice(m, b); } catch (Exception e) { Paths.AppendLog("Choosing connections failed: " + e.Message); } });   // the engine applies it in the background
        _saved?.Invoke();
    }
}

/// <summary>The same Networks page in a window of its own, for the first-run welcome (before the main window is in use).</summary>
sealed class ConnectionsDialog : Form
{
    public ConnectionsDialog(SwitchEngine engine)
    {
        Text = "Networks"; Icon = Ui.AppIcon(Ui.Blue);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(720, 640); StartPosition = FormStartPosition.CenterParent; BackColor = Ui.Bg; Font = Ui.UiFont(9.5f);
        Padding = new Padding(Ui.Pad + 4, Ui.Pad, Ui.Pad + 4, Ui.Gap);
        Controls.Add(new ConnectionsPanel(engine, () => DialogResult = DialogResult.OK) { Dock = DockStyle.Fill });
        Load += (_, _) => Ui.SkinForm(this);
    }
}
