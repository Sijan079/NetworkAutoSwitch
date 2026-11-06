using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

/// <summary>The Networks page: which networks are your main connection and which are the backup. The role belongs to the NETWORK (its name), so it holds on whichever adapter
/// that network is on.
/// Underneath, the engine keeps a Main table and a Backup table read in one mode for both (Blacklist: a table bars a network from that role; Whitelist: a table is the only
/// networks allowed that role). Reading those tables directly confused people: in Blacklist "Make Main" put a network in the Backup table. So this page never shows the raw
/// tables: it groups every network by what it BECOMES (BackupNetworks.RoleOf) and writes the tables with BackupNetworks.WithRole. Make Main always lands in Main Networks,
/// Make Backup always in Backup Networks, in both modes; the mode only decides how that is stored.
/// Nothing changes until Save; Revert (or opening the page again with nothing unsaved) reads the current state again.</summary>
sealed class ConnectionsPanel : Panel
{
    sealed record Choice(string? Name, string Text) { public override string ToString() => Text; }

    readonly SwitchEngine _engine;
    readonly Action? _saved;
    readonly PageHeader _header = new("Networks");
    readonly Segmented _mode = new("Blacklist", "Whitelist");
    readonly Label _modeHint = new() { AutoSize = true, ForeColor = Ui.Muted, BackColor = Ui.Bg, Font = Ui.UiFont(9f), Margin = new Padding(0, 12, 0, 0), UseMnemonic = false };
    readonly Card _mainCard = new(Ui.Home, "Main Networks"), _backupCard = new(Ui.Phone, "Backup Networks"), _noneCard = new(Ui.List, "Unlabelled"), _barredCard = new(Ui.Block, "Barred");
    readonly NetworksList _lsMain = new(Ui.Green, "No main networks yet. Select a network and press Make Main."),
                          _lsBackup = new(Ui.Amber, "No backup networks yet. Select your phone's network and press Make Backup."),
                          _lsNone = new(Ui.Muted, "Every network you know is sorted."),
                          _lsBarred = new(Ui.Red, "");
    readonly Label _selName = new() { AutoSize = true, ForeColor = Ui.Text, BackColor = Ui.Bg, Font = Ui.UiFont(9.75f, Weight.SemiBold), Margin = new Padding(0, 9, 4, 0), UseMnemonic = false };
    readonly Label _selRole = new() { AutoSize = true, ForeColor = Ui.Muted, BackColor = Ui.Bg, Font = Ui.UiFont(9.75f), Margin = new Padding(0, 9, 14, 0), UseMnemonic = false };
    readonly KitButton _makeMain, _makeBackup, _unlabel, _save, _revert;
    readonly Card _adapterCard = new(Ui.Settings, "Adapters");
    readonly Toggle _chooseAdapters = new() { Margin = new Padding(0, 3, 0, 0) };
    readonly Label _autoLine = new() { Dock = DockStyle.Fill, ForeColor = Ui.Muted, BackColor = Ui.CardBg, Font = Ui.UiFont(9f), UseMnemonic = false, AutoEllipsis = true };
    readonly TableLayoutPanel _fields = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Ui.CardBg, Visible = false };
    readonly NoWheelComboBox _main = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly NoWheelComboBox _backup = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly Label _status = new() { AutoSize = true, Padding = new Padding(0, 10, 0, 0), ForeColor = Ui.Muted, BackColor = Ui.Bg };
    readonly TableLayoutPanel _grid;
    int _lowerRow, _adapterRow;

    // one working pair of tables per mode (each mode keeps its own), plus every network we know about: connected now + saved in Windows + already on a table
    readonly Dictionary<ListMode, (List<string> Main, List<string> Backup)> _work = new();
    readonly Dictionary<string, string> _where = new(StringComparer.OrdinalIgnoreCase);     // network -> "Connected on Wi-Fi 2" / "Saved in Windows"
    readonly List<string> _known = new();
    ListMode _modeNow;
    string? _selected;
    bool _dirty, _loading;

    NetworksList[] Lists => new[] { _lsMain, _lsBackup, _lsNone, _lsBarred };

    /// <param name="saved">Called after Save (the dialog closes on it; the page stays).</param>
    public ConnectionsPanel(SwitchEngine engine, Action? saved = null)
    {
        _engine = engine; _saved = saved;
        BackColor = Ui.Bg; Font = Ui.UiFont(9.5f);

        // header: Save and Revert on the right, with the save state next to them
        _save = Ui.Btn(Ui.Save, "Save", "Save your networks and adapters and apply them now.", Save, BtnKind.Primary);
        var revert = _revert = Ui.Btn(Ui.Undo, "Revert", "Discard unsaved changes.", Reload);
        _header.AddAction(_status); _header.AddAction(revert); _header.AddAction(_save);
        _header.Padding = new Padding(0, 0, Ui.Gap, 0);      // the cards below carry a Gap on their right; the buttons end on the same line as they do

        // mode: only HOW the choices are stored. The groups below mean the same in both modes, so this sits quietly under the header.
        Ui.Tip.SetToolTip(_mode, "How your choices are saved. Main Networks, Backup Networks and Unlabelled mean the same in both modes.\n"
            + "Blacklist: a network is saved as barred from the other role. A network barred from both is listed as Barred.\n"
            + "Whitelist: a network is saved as allowed in its role. A network allowed in both is marked Either.\n"
            + "Each mode keeps its own lists, so switching shows the networks as that mode has them.");
        _mode.SelectedChanged += i => { if (_loading) return; _modeNow = (ListMode)i; MarkDirty(); Refill(); };

        // the groups, each a card: what a network becomes
        _mainCard.SetHint("Networks that become your main connection, on whichever adapter they are on.\nEITHER: allowed as main and as backup (a Whitelist network on both lists). It is shown in both groups.");
        _backupCard.SetHint("Networks that become the backup (usually your phone). On them heavy downloads are blocked and the daily data limit applies, on any adapter.\nEITHER: allowed as main and as backup. It is shown in both groups.");
        _noneCard.SetTitle("Unlabelled (treated as backup)");
        _noneCard.SetHint("Networks you have not sorted yet: connected now or saved in Windows.\nThey are treated as backup (protected) until you sort them: a wrong guess toward backup only costs a blocked download, a wrong guess toward main costs mobile data.");
        _barredCard.SetHint("Not allowed as main and not allowed as backup (a Blacklist network barred from both roles).\nThe app protects it like a backup. Select it and press Make Main or Make Backup to fix it.");
        foreach (var (card, list) in new[] { (_mainCard, _lsMain), (_backupCard, _lsBackup), (_noneCard, _lsNone), (_barredCard, _lsBarred) })
        {
            card.Body.Controls.Add(list);
            list.Picked += Pick;
        }
        _lsMain.AccessibleName = "Main Networks"; _lsBackup.AccessibleName = "Backup Networks"; _lsNone.AccessibleName = "Unlabelled Networks"; _lsBarred.AccessibleName = "Barred Networks";

        // what to do with the selected network
        _makeMain = Ui.Btn(Ui.Home, "Make Main", "Make the selected network a main network.", () => SetRole(NetworkRole.Main));
        _makeBackup = Ui.Btn(Ui.Phone, "Make Backup", "Make the selected network a backup network (your phone).", () => SetRole(NetworkRole.Backup));
        _unlabel = Ui.Btn(Ui.Undo, "Unlabel", "Take the selected network out of both groups. It is treated as backup until you sort it again.", () => SetRole(NetworkRole.Unlabelled));
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Ui.Bg, WrapContents = false, Margin = new Padding(0, 0, Ui.Gap, 0), Padding = new Padding(0) };
        actions.Controls.AddRange(new Control[] { _makeMain, _makeBackup, _unlabel });   // the highlighted row shows what is selected; no sentence repeats it
        // one row on top of all three lists: the sort buttons on the left, the list mode on the right, both as tall as a button
        var strip = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Margin = new Padding(0, 0, Ui.Gap, Ui.Gap) };
        actions.Margin = Padding.Empty; _mode.Margin = Padding.Empty; _mode.Dock = DockStyle.Right; _mode.Height = Ui.BtnH;
        strip.Controls.Add(actions); strip.Controls.Add(_mode);      // the Fill control first, then the one docked to the right edge

        // adapters: only matter when the groups cannot decide, so they stay folded behind a switch
        _adapterCard.SetHint("The groups above decide which adapter is main: the one on a main network. These choices are used only when they can't (for example no main network is connected).");
        var chooseLabel = new Label { Text = "Choose Adapters Myself", AutoSize = true, ForeColor = Ui.Text, BackColor = Ui.CardBg, Font = Ui.UiFont(9f, Weight.SemiBold), Margin = new Padding(0, 5, 8, 0), UseMnemonic = false };
        _adapterCard.AddHeaderControl(_chooseAdapters); _adapterCard.AddHeaderControl(chooseLabel);
        Ui.Tip.SetToolTip(_chooseAdapters, "Off: the app picks the adapters. On: pick the main and backup adapter yourself.");
        Ui.Tip.SetToolTip(chooseLabel, "Off: the app picks the adapters. On: pick the main and backup adapter yourself.");
        _fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); _fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var mainField = new Field("Main Adapter", _main) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Ui.Gap, 0) };
        var backupField = new Field("Backup Adapter", _backup) { Dock = DockStyle.Fill, Margin = new Padding(0) };
        Ui.Tip.SetToolTip(_main, "Used only when the groups can't decide."); Ui.Tip.SetToolTip(_backup, "Used only when the groups can't decide.");
        _fields.Controls.Add(mainField, 0, 0); _fields.Controls.Add(backupField, 1, 0);
        _adapterCard.Body.Controls.Add(_fields); _adapterCard.Body.Controls.Add(_autoLine);
        _chooseAdapters.CheckedChanged += (_, _) => ShowAdapters();

        // layout: the two groups side by side take most of the height, unlabelled (and barred, when there is any) under them
        var grid = _grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Ui.Bg };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        int Row(float h, SizeType st = SizeType.Absolute) { grid.RowStyles.Add(new RowStyle(st, h)); return grid.RowStyles.Count - 1; }
        int r = Row(36 + Ui.Gap); grid.Controls.Add(strip, 0, r); grid.SetColumnSpan(strip, 2);     // on top of all the lists: the buttons sort networks in Main, Backup and Unlabelled alike
        r = Row(56, SizeType.Percent); grid.Controls.Add(_mainCard, 0, r); grid.Controls.Add(_backupCard, 1, r);
        _lowerRow = Row(44, SizeType.Percent); grid.Controls.Add(_noneCard, 0, _lowerRow); grid.Controls.Add(_barredCard, 1, _lowerRow);
        _adapterRow = Row(AdapterRowHeight(false)); grid.Controls.Add(_adapterCard, 0, _adapterRow); grid.SetColumnSpan(_adapterCard, 2);
        grid.RowCount = grid.RowStyles.Count;

        Controls.Add(grid); Controls.Add(_header);       // docked last to first: the header on top, the grid fills the rest

        _main.SelectedIndexChanged += (_, _) => { if (!_loading) MarkDirty(); Validate_(); };
        _backup.SelectedIndexChanged += (_, _) => { if (!_loading) MarkDirty(); Validate_(); };
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
            var t = _engine.Tables; _modeNow = t.Mode;
            _work[ListMode.Blacklist] = (t.BlacklistMain.ToList(), t.BlacklistBackup.ToList());
            _work[ListMode.Whitelist] = (t.WhitelistMain.ToList(), t.WhitelistBackup.ToList());
            _known.Clear(); _where.Clear();
            void Know(string? n, string where) { if (string.IsNullOrWhiteSpace(n)) return; n = n.Trim(); if (!_known.Contains(n, StringComparer.OrdinalIgnoreCase)) _known.Add(n); if (where.Length > 0 && !_where.ContainsKey(n)) _where[n] = where; }
            Know(_engine.PrimaryNetwork, _engine.PrimaryName.Length > 0 ? $"Connected on {_engine.PrimaryName}" : ""); Know(_engine.BackupNetwork, _engine.BackupName.Length > 0 ? $"Connected on {_engine.BackupName}" : "");
            foreach (var n in NetworkNames.SavedWifi()) Know(n, "Saved in Windows");
            foreach (var n in _work.Values.SelectMany(w => w.Main.Concat(w.Backup))) Know(n, "");        // a sorted network that is not around now stays visible
            if (Environment.GetCommandLineArgs().Contains("--snapshot")) AddSnapshotSamples(Know);
            _mode.Show((int)_modeNow);

            var all = AdapterDiscovery.Candidates(AdapterDiscovery.Detect());
            Choice Auto(string current) => new(null, $"Automatic  ({(current.Length > 0 ? current : "none found")})");
            Choice Of(AdapterInfo a) => new(a.Name, $"{a.Name}  ·  {AdapterDiscovery.KindLabel(a.Kind)}" + (a.IsUp ? "" : "  ·  Not Connected"));
            Fill(_main, Auto(_engine.PrimaryName), all, Of, _engine.UserPrimary);
            Fill(_backup, Auto(_engine.BackupName), all, Of, _engine.UserBackup);
            _chooseAdapters.Checked = _engine.UserPrimary.Length > 0 || _engine.UserBackup.Length > 0;     // open when you already chose one, so you can see it
            _autoLine.Text = $"Automatic: main adapter {Or(_engine.PrimaryName)}, backup adapter {Or(_engine.BackupName)}. Used only when the groups above can't decide.";
            ShowAdapters();
            if (_selected is not null && !_known.Contains(_selected, StringComparer.OrdinalIgnoreCase)) _selected = null;
            Refill();
        }
        finally { _loading = false; }
        _dirty = false; _status.Text = "";
        Validate_();
    }

    static string Or(string name) => name.Length > 0 ? name : "none found";

    /// <summary>The dry-run snapshot (--snapshot) only: a few sample networks in memory so every group, the EITHER chip and the Barred card can be checked
    /// in the picture. Never saved: nothing here reaches the engine unless Save is pressed, and a snapshot run never presses it.</summary>
    void AddSnapshotSamples(Action<string?, string> know)
    {
        var (m, b) = _work[_modeNow];
        (m, b) = BackupNetworks.WithRole("Home-5G", NetworkRole.Main, _modeNow, m, b);
        (m, b) = BackupNetworks.WithRole("Pixel 7 Hotspot", NetworkRole.Backup, _modeNow, m, b);
        if (!BackupNetworks.Matches(m, "Cafe Wifi")) m.Add("Cafe Wifi");
        if (!BackupNetworks.Matches(b, "Cafe Wifi")) b.Add("Cafe Wifi");     // on both tables: Barred in Blacklist, Either in Whitelist
        _work[_modeNow] = (m, b);
        know("Home-5G", "Connected on Wi-Fi"); know("Pixel 7 Hotspot", "Saved in Windows"); know("Cafe Wifi", "Saved in Windows"); know("Airport Free", "Saved in Windows");
    }

    NetworkRole RoleOf(string name) { var (m, b) = _work[_modeNow]; return BackupNetworks.RoleOf(name, _modeNow, m, b); }

    /// <summary>Sorts every known network into the groups by what it becomes in the current mode, and says what the mode means.</summary>
    void Refill()
    {
        _modeHint.Text = _modeNow == ListMode.Blacklist
            ? "Blacklist: saved as the role a network may not have. Unsorted networks are treated as backup."
            : "Whitelist: saved as the roles a network may have. Unsorted networks are treated as backup.";
        _modeHint.Visible = false;   // the mode control's tooltip explains both modes; nothing extra on screen

        var rows = _known.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(n => (Name: n, Role: RoleOf(n))).ToList();
        NetworksList.Row Show(string n, bool either) => new(n, _where.TryGetValue(n, out var w) ? w : "", either);
        IEnumerable<NetworksList.Row> Group(params NetworkRole[] roles) => rows.Where(x => roles.Contains(x.Role)).Select(x => Show(x.Name, x.Role == NetworkRole.Either));
        _lsMain.SetRows(Group(NetworkRole.Main, NetworkRole.Either));
        _lsBackup.SetRows(Group(NetworkRole.Backup, NetworkRole.Either));
        _lsNone.SetRows(Group(NetworkRole.Unlabelled));
        _lsBarred.SetRows(Group(NetworkRole.Barred));
        _mainCard.SetTitle(Title("Main Networks", _lsMain.Count)); _backupCard.SetTitle(Title("Backup Networks", _lsBackup.Count));
        _noneCard.SetTitle(Title("Unlabelled (treated as backup)", _lsNone.Count)); _barredCard.SetTitle(Title("Barred", _lsBarred.Count));

        // the Barred card only takes room when something is barred; otherwise Unlabelled gets the whole width
        bool barred = _lsBarred.Count > 0;
        if (_barredCard.Visible != barred || _grid.GetColumnSpan(_noneCard) != (barred ? 1 : 2))
        {
            _grid.SuspendLayout();
            _barredCard.Visible = barred; _grid.SetColumnSpan(_noneCard, barred ? 1 : 2);
            _grid.ResumeLayout();
        }
        UpdateSelection();
    }

    static string Title(string t, int n) => n > 0 ? $"{t}  ·  {n}" : t;

    void Pick(string name) { _selected = name; UpdateSelection(); }

    /// <summary>Highlights the selected network in every group it is in, says what it is now, and enables only the moves that change it.</summary>
    void UpdateSelection()
    {
        foreach (var l in Lists) l.Selected = _selected;
        if (_selected is null)
        {
            _selName.Text = ""; _selRole.Text = "";
            _makeMain.Enabled = _makeBackup.Enabled = _unlabel.Enabled = false;
            return;
        }
        var role = RoleOf(_selected);
        _selName.Text = _selected;
        _selRole.Text = role switch
        {
            NetworkRole.Main => "is a main network.",
            NetworkRole.Backup => "is a backup network.",
            NetworkRole.Either => "may be main or backup.",
            NetworkRole.Barred => "is barred from both roles.",
            _ => "is not sorted yet (treated as backup).",
        };
        _makeMain.Enabled = role != NetworkRole.Main; _makeBackup.Enabled = role != NetworkRole.Backup; _unlabel.Enabled = role != NetworkRole.Unlabelled;
    }

    /// <summary>Gives the selected network a role. WithRole writes the tables the way the current mode reads them, so Make Main always ends in Main Networks
    /// and Make Backup in Backup Networks, whichever table that means underneath. Unlabel takes it off both tables.</summary>
    void SetRole(NetworkRole role)
    {
        if (_selected is not { } name) return;
        var (m, b) = _work[_modeNow];
        _work[_modeNow] = role == NetworkRole.Unlabelled
            ? (m.Where(x => !x.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)).ToList(), b.Where(x => !x.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)).ToList())
            : BackupNetworks.WithRole(name, role, _modeNow, m, b);
        MarkDirty();
        Refill();
    }

    void MarkDirty() { _dirty = true; UpdateButtons(); }

    bool _same, _saving;
    /// <summary>Save and Revert are on only when there is something to save or to undo (no "Not saved yet" text); while a save runs Save stays as it is (spinner) and Revert is off.</summary>
    void UpdateButtons()
    {
        _save.Enabled = _saving || (_dirty && !_same);
        _revert.Enabled = !_saving && _dirty;
    }

    // The card's row is worked out from its parts (not a fixed number), so a spacing change can never cut the card short again:
    // card inset 4 + header 48 + body padding 4 / 14 + inset 4, the content (one text line, or a field: label 22 + box 36), and the Gap under the card.
    static int AdapterRowHeight(bool on) => 4 + 48 + 4 + (on ? 22 + 36 : 24) + 14 + 4 + Ui.Gap;

    /// <summary>The adapter card: one muted line while the app picks the adapters, the two fields when you pick them. Turning it off goes back to Automatic.</summary>
    void ShowAdapters()
    {
        bool on = _chooseAdapters.Checked;
        _fields.Visible = on; _autoLine.Visible = !on;
        _grid.RowStyles[_adapterRow].Height = AdapterRowHeight(on);
        if (!on && !_loading && (_main.SelectedIndex != 0 || _backup.SelectedIndex != 0)) { _main.SelectedIndex = 0; _backup.SelectedIndex = 0; }
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
        else if (_status.ForeColor == Ui.Red) { _status.Text = ""; _status.ForeColor = Ui.Muted; }
        _same = same; UpdateButtons();
    }

    /// <summary>A save is running: Save shows a spinner and ignores clicks, Revert is disabled (it would undo what is being saved).</summary>
    void SetSaving(bool on) { _saving = on; _save.Busy = on; UpdateButtons(); }

    void Save()
    {
        if (_save.Busy) return;
        var (m, b) = (Picked(_main), Picked(_backup));
        var bl = _work[ListMode.Blacklist]; var wl = _work[ListMode.Whitelist];
        var tables = new NetworkTables(_modeNow, bl.Main, bl.Backup, wl.Main, wl.Backup);
        _dirty = false; _status.Text = "";
        SetSaving(true);          // a spinner on Save, and Save / Revert ignore clicks until it is done
        Task.Run(async () =>
        {
            var shown = Task.Delay(700);          // the change itself is quick; keep the spinner up long enough to be seen
            try { _engine.SetNetworkTables(tables); _engine.SetAdapterChoice(m, b); } catch (Exception e) { Paths.AppendLog("Choosing connections failed: " + e.Message); }
            await shown;
            if (IsHandleCreated && !IsDisposed) BeginInvoke(() => { SetSaving(false); _save.Flash(Ui.Check); });
        });   // the engine applies it in the background
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
        ClientSize = new Size(820, 680); StartPosition = FormStartPosition.CenterParent; BackColor = Ui.Bg; Font = Ui.UiFont(9.5f);
        Padding = new Padding(0);       // the page around it already gives the standard frame
        Controls.Add(new ConnectionsPanel(engine, () => DialogResult = DialogResult.OK) { Dock = DockStyle.Fill });
        Load += (_, _) => Ui.SkinForm(this);
    }
}
