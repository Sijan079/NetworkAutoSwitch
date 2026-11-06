using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

/// <summary>Form-based editor for the backup rules, as two sibling cards on the page: the rule list on the left (name, details,
/// action chip; the selected rule has a teal border), the selected rule on the right. Explanations live in tooltips and one muted line.
/// SetPolicy() fills it, Build() validates and returns the policy. The page adds Save / JSON / Revert with AddAction().</summary>
sealed class PolicyEditor : UserControl
{
    sealed class Row
    {
        public string Name = "New Rule", Action = "watch", Exes = "", Folder = "", Path = "";
        public bool UseGlobal = true;                       // watch rules follow the global trigger unless they have their own
        public decimal Mb = 2; public int WindowSec = 10, Kbps = 500;   // only used when UseGlobal is off
        public override string ToString() => $"{Name}, {ActionText(Action)}";     // what a screen reader says for the row
    }

    static readonly (string Key, string Text, string Explain)[] Actions =
    {
        ("watch", "Watch", "Blocks the program only while it goes over the trigger."),
        ("block", "Block", "Keeps the program off the internet the whole time you are on the backup."),
        ("throttle", "Upload Cap", "Limits how fast the program uploads. Downloads are not limited."),
    };
    static string ActionText(string key) => Actions.FirstOrDefault(a => a.Key == key).Text ?? "Upload Cap";
    static Color RoleOf(string key) => key switch { "watch" => Ui.Green, "block" => Ui.Red, _ => Ui.Amber };
    const string NameRule = "Names may use letters, digits, spaces and - _ . ( ) + & only (up to 64 characters).";

    const int RowH = 62, RowGap = 8;
    readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None, BackColor = Ui.CardBg, ItemHeight = RowH + RowGap, DrawMode = DrawMode.OwnerDrawFixed };
    readonly TextBox _name = new(), _exes = new() { Multiline = true, Font = Ui.MonoFont(9.75f), ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, WordWrap = false },
                     _folder = new() { Multiline = true, Font = Ui.MonoFont(9.75f), ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, WordWrap = false }, _path = new();
    readonly Label _nameProblem = new() { Dock = DockStyle.Fill, ForeColor = Ui.Red, BackColor = Ui.CardBg, Font = Ui.UiFont(9f), UseMnemonic = false, AutoEllipsis = true };
    readonly Segmented _action = new("Watch", "Block", "Upload Cap");
    readonly Label _explain = new() { Dock = DockStyle.Top, Height = 24, Padding = new Padding(0, 6, 0, 0), ForeColor = Ui.Muted, BackColor = Ui.CardBg, Font = Ui.UiFont(9f), UseMnemonic = false, AutoEllipsis = true };
    readonly NoWheelNumeric _mb = new() { DecimalPlaces = 1, Minimum = 0.1m, Maximum = 10000, Increment = 1 },
                            _win = new() { Minimum = 1, Maximum = 600 },
                            _kbps = new() { Minimum = 1, Maximum = 1_000_000, Increment = 100 };
    readonly Label _rate = new() { AutoSize = true, ForeColor = Ui.Muted, BackColor = Ui.CardBg, Font = Ui.UiFont(9f), Padding = new Padding(0, 32, 0, 0) };
    readonly GlobalTriggerCard _global;      // the global trigger (shared by every watch rule that has no trigger of its own), on the same page
    readonly Toggle _own = new();            // on = this rule has its own trigger (off = it follows the global one)
    readonly FlowLayoutPanel _actions = new() { Dock = DockStyle.Fill, WrapContents = false, BackColor = Ui.CardBg, Margin = new Padding(0) };
    readonly KitButton _remove;

    /// <summary>The status line under the buttons (the page's Say writes to it).</summary>
    public Label Status { get; } = new() { Dock = DockStyle.Top, Height = 28, Padding = new Padding(0, 8, 0, 0), ForeColor = Ui.Muted, BackColor = Ui.CardBg, Font = Ui.UiFont(9f), UseMnemonic = false, AutoEllipsis = true };

    readonly List<Row> _rows = new();
    readonly List<(Control Part, string[] Actions)> _parts = new();    // the editor's sections; an empty list = shown for every action
    Control _nameProblemRow = null!, _ownFields = null!;
    bool _loading;
    Func<IEnumerable<SeenProgram>> _seen = () => Array.Empty<SeenProgram>();
    Action<string, bool>? _say;

    /// <summary>Where the picker gets "programs that used data on the backup", and where messages go.</summary>
    public void Connect(Func<IEnumerable<SeenProgram>> seenOnBackup, Action<string, bool> say) { _seen = seenOnBackup; _say = say; }

    /// <summary>Makes ONE watch rule from the given programs (a launcher plus its helpers). Returns how many programs were added.
    /// Programs already covered by a rule are skipped and reported.</summary>
    public int AddPrograms(IEnumerable<string> paths)
    {
        var current = _rows.Select(r => { var (f, fs) = PolicyBuilder.FromLines(r.Folder); return new AppRule(r.Name, null, r.Action, Exes: r.Exes.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries), Folder: f, Folders: fs); }).ToList();
        var (rule, skipped) = PolicyBuilder.WatchRuleFor(paths, current);
        if (rule is null) { _say?.Invoke(skipped.Count > 0 ? $"Already watched: {string.Join(", ", skipped)}" : "Nothing to add.", true); return 0; }
        var row = new Row { Name = rule.Name, Action = "watch", Exes = string.Join("\r\n", rule.Exes!), Folder = PolicyBuilder.ToLines(rule) };
        _rows.Add(row); _list.Items.Add(row); _list.SelectedIndex = _rows.Count - 1;
        _say?.Invoke(skipped.Count > 0 ? $"Added {rule.Name}. Already watched: {string.Join(", ", skipped)}" : $"Added {rule.Name}.", false);
        return rule.Exes!.Length;
    }

    /// <summary>Add Program / Programs Seen On Backup: pick programs from a list (the backup's programs first), get a ready-made rule.</summary>
    public void PickAndAdd()
    {
        using var dlg = new ProgramPicker(_seen());
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK && dlg.Selected.Count > 0) AddPrograms(dlg.Selected);
    }

    /// <summary>A page-level button (Save And Apply, Edit As JSON, Revert) in the editor's button row, before Remove Rule.</summary>
    public void AddAction(Control c) { c.Margin = new Padding(0, 0, 10, 0); _actions.Controls.Add(c); _actions.Controls.SetChildIndex(_remove, _actions.Controls.Count - 1); }

    public PolicyEditor(GlobalTriggerCard global)
    {
        _global = global; BackColor = Ui.Bg;
        AllowDrop = true; DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
                AddPrograms(files.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));
        };

        // ---- the rule list and the rule sit side by side: nothing is nested inside another card ----
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Ui.Bg, Margin = new Padding(0), Padding = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400 + Ui.Gap)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // rule list
        var left = new Card("", "Rules For The Backup");
        left.AddHeaderAction(Ui.Add, "Add Program", "Add a rule: pick programs from a list.\nYou can also drag an .exe onto this page.", PickAndAdd);
        left.Body.Padding = new Padding(14, 2, 14, 6); left.Body.Controls.Add(_list);
        _list.SelectedIndexChanged += (_, _) => { if (!_loading) ShowCurrent(); };
        _list.DrawItem += DrawRow;
        _list.AccessibleName = "Rules";

        // the selected rule
        var right = new Card("", "Edit Rule");
        right.AddHeaderAction(Ui.Add, "Choose Programs", "Pick the rule's programs from a list (or browse for a file).", Browse);
        right.Body.Padding = new Padding(14, 2, 14, 10);

        var nameField = new Field("Name", _name) { Margin = new Padding(0) };
        _nameProblemRow = new Panel { Height = 22, BackColor = Ui.CardBg, Visible = false };
        _nameProblemRow.Controls.Add(_nameProblem);
        Ui.Tip.SetToolTip(_name, NameRule);

        var actionLbl = new Label { Text = "What Happens On The Backup", Dock = DockStyle.Top, Height = 22, Font = Ui.UiFont(9f, Weight.SemiBold), ForeColor = Ui.Muted, BackColor = Ui.CardBg, UseMnemonic = false };
        var segRow = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Ui.CardBg };
        segRow.Controls.Add(_action); _action.Location = new Point(0, 0); _action.AccessibleName = "What happens on the backup";
        var actionPart = new Panel { BackColor = Ui.CardBg, Height = 22 + 36 + 24 };
        actionPart.Controls.Add(_explain); actionPart.Controls.Add(segRow); actionPart.Controls.Add(actionLbl);
        Ui.Tip.SetToolTip(_action, "Watch: blocked only while it downloads fast.\nBlock: never allowed on the backup.\nUpload Cap: caps upload speed (cannot limit downloads).");

        var exesField = new RuleField("Programs (One File Name Per Line)", _exes, boxHeight: 76);
        Ui.Tip.SetToolTip(_exes, "One file name per line, e.g. steam.exe.\nList launchers and downloaders, never the games, so a game is never cut off.");
        var folderField = new RuleField("Install Folders", _folder, boxHeight: 56);
        Ui.Tip.SetToolTip(_folder, "Optional, one folder per line: only match programs inside these folders.\nA game bundle lists the launcher's folder and the game's folder here, so one rule watches both.");
        var pathField = new Field("Program", _path) { Margin = new Padding(0) };
        _path.Font = Ui.MonoFont(9.75f);
        Ui.Tip.SetToolTip(_path, "The full path of the program, e.g. C:\\Tools\\updater.exe. Choose Programs fills it in.");
        var kbpsField = new Field("Upload Cap", _kbps, "kbps") { Width = 200, Margin = new Padding(0) };
        var kbpsRow = new Panel { BackColor = Ui.CardBg, Height = kbpsField.Height }; kbpsRow.Controls.Add(kbpsField);

        // own trigger: a toggle, and the rule's own numbers only while it is on
        var ownRow = new FlowLayoutPanel { WrapContents = false, BackColor = Ui.CardBg, Height = 36 };
        var ownLbl = new Label { Text = "Own Trigger", AutoSize = true, Font = Ui.UiFont(9.75f, Weight.SemiBold), ForeColor = Ui.Text, BackColor = Ui.CardBg, Margin = new Padding(0, 9, 14, 0), UseMnemonic = false };
        var ownNote = new Label { Text = "Off = it uses the global trigger.", AutoSize = true, Font = Ui.UiFont(9f), ForeColor = Ui.Muted, BackColor = Ui.CardBg, Margin = new Padding(0, 10, 0, 0), UseMnemonic = false };
        _own.Margin = new Padding(0, 7, 10, 0); _own.AccessibleName = "Own trigger";
        ownRow.Controls.AddRange(new Control[] { _own, ownLbl, ownNote });
        Ui.Tip.SetToolTip(_own, "On = this rule gets its own MB and seconds.\nOff = it uses the global trigger above.");
        var ownFields = new FlowLayoutPanel { WrapContents = false, BackColor = Ui.CardBg, Height = 58 };
        ownFields.Controls.Add(new Field("More Than", _mb, "MB") { Width = 150, Margin = new Padding(0, 0, 14, 0) });
        ownFields.Controls.Add(new Field("Within", _win, "seconds") { Width = 140, Margin = new Padding(0, 0, 14, 0) });
        ownFields.Controls.Add(_rate);
        _ownFields = ownFields;

        _remove = Ui.Btn("", "Remove Rule", "Remove the selected rule. Nothing is saved until you click Save And Apply.", RemoveRow, BtnKind.Danger);
        _remove.Margin = new Padding(0);
        _actions.Controls.Add(_remove);

        // top to bottom; each part carries a 12 px gap under it, and a part hidden for this action takes no room
        Part(nameField, 0); Part(_nameProblemRow, 0); Part(new Panel { Height = Ui.Gap, BackColor = Ui.CardBg }, 0);
        Part(actionPart);
        Part(exesField, Ui.Gap, "watch"); Part(folderField, Ui.Gap, "watch");
        Part(pathField, Ui.Gap, "block", "throttle"); Part(kbpsRow, Ui.Gap, "throttle");
        Part(ownRow, Ui.Gap, "watch"); Part(ownFields, Ui.Gap, "watch");
        var buttons = new Panel { Height = Ui.BtnH, BackColor = Ui.CardBg, Padding = new Padding(0, 0, 0, 0) }; buttons.Controls.Add(_actions);
        var stack = _parts.Select(p => p.Part.Parent!).Concat(new Control[] { Wrap(buttons, 4), Status }).ToList();
        for (int i = stack.Count - 1; i >= 0; i--) { stack[i].Dock = DockStyle.Top; right.Body.Controls.Add(stack[i]); }   // Dock=Top stacks the LAST added on top

        grid.Controls.Add(left, 0, 0); grid.Controls.Add(right, 1, 0);
        Controls.Add(grid);

        foreach (var c in new Control[] { _name, _exes, _folder, _path }) c.TextChanged += (_, _) => Commit();
        _name.TextChanged += (_, _) => ShowNameProblem();
        _action.SelectedChanged += _ => { Commit(); ApplyVisibility(); };
        foreach (var n in new[] { _mb, _win, _kbps }) n.ValueChanged += (_, _) => Commit();
        _own.CheckedChanged += (_, _) => { Commit(); ApplyVisibility(); };
        _global.Changed += () => { if (!_loading) ShowGlobalChange(); };
    }

    /// <summary>Wraps a section with the gap under it and remembers which actions it belongs to (none = every action).</summary>
    void Part(Control c, params string[] actions) => Part(c, Ui.Gap, actions);
    void Part(Control c, int gap, params string[] actions) { Wrap(c, gap); _parts.Add((c, actions)); }
    static Panel Wrap(Control c, int gap)
    {
        var w = new Panel { Height = c.Height + gap, Padding = new Padding(0, 0, 0, gap), BackColor = Ui.CardBg };
        c.Dock = DockStyle.Fill; w.Controls.Add(c);
        return w;
    }

    // ---- the rule list: one rounded row per rule, the name, a details line and the action chip ----
    void DrawRow(object? sender, DrawItemEventArgs e)
    {
        var g = e.Graphics;
        g.FillRectangle(Ui.Brush(Ui.CardBg), e.Bounds);
        if (e.Index < 0 || e.Index >= _rows.Count) return;
        var r = _rows[e.Index]; bool sel = e.Index == _list.SelectedIndex;
        var b = e.Bounds;
        g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.Half;
        using (var p = Ui.RoundRect(new RectangleF(b.X + 0.5f, b.Y + 0.5f, b.Width - 1.5f, RowH - 1), 10))
        {
            if (sel) g.FillPath(Ui.Brush(Ui.Raised), p);
            g.DrawPath(Ui.Pen(sel ? Ui.Green : Ui.Border), p);
        }
        g.SmoothingMode = SmoothingMode.Default; g.PixelOffsetMode = PixelOffsetMode.Default;
        string chip = ActionText(r.Action);
        var chipFont = Ui.UiFont(8.25f, Weight.SemiBold);
        int chipW = TextRenderer.MeasureText(g, chip, chipFont, Size.Empty, TextFormatFlags.NoPadding).Width + 18;
        int chipX = b.Right - 14 - chipW;
        Chip.Paint(g, chip, RoleOf(r.Action), chipX, b.Y + RowH / 2);
        int textW = Math.Max(10, chipX - b.X - 14 - 10);
        const TextFormatFlags f = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, r.Name, Ui.UiFont(10.5f, Weight.Bold), new Rectangle(b.X + 14, b.Y + 11, textW, 22), Ui.Text, f);
        TextRenderer.DrawText(g, Details(r), Ui.UiFont(9f), new Rectangle(b.X + 14, b.Y + 34, textW, 18), Ui.Muted, f | TextFormatFlags.PathEllipsis);
        if (sel && (e.State & DrawItemState.Focus) != 0 && (e.State & DrawItemState.NoFocusRect) == 0)
            using (var p = Ui.RoundRect(new RectangleF(b.X + 2.5f, b.Y + 2.5f, b.Width - 5.5f, RowH - 5), 8)) { g.SmoothingMode = SmoothingMode.AntiAlias; g.DrawPath(Ui.Pen(Ui.Blue, 1.5f), p); g.SmoothingMode = SmoothingMode.Default; }
    }

    /// <summary>The muted line under a rule's name: "steam.exe +1 · 2 folders · global trigger", the blocked path, or the path and its cap.</summary>
    string Details(Row r)
    {
        static string[] Lines(string s) => s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        switch (r.Action)
        {
            case "watch":
                var exes = Lines(r.Exes); int folders = Lines(r.Folder).Length;
                var parts = new List<string> { exes.Length == 0 ? "no programs yet" : exes.Length == 1 ? exes[0] : $"{exes[0]} +{exes.Length - 1}" };
                if (folders > 0) parts.Add(folders == 1 ? "1 folder" : $"{folders} folders");
                parts.Add(r.UseGlobal ? "global trigger" : $"{r.Mb:0.#} MB per {r.WindowSec} s");
                return string.Join(" · ", parts);
            case "block": return r.Path.Trim().Length > 0 ? r.Path.Trim() : "no program yet";
            default: return (r.Path.Trim().Length > 0 ? r.Path.Trim() : "no program yet") + $" · {r.Kbps} kbps";
        }
    }

    /// <summary>When the global values change, rules that follow them show the new numbers straight away.</summary>
    void ShowGlobalChange() { _loading = true; ShowRuleNumbers(); _loading = false; ApplyVisibility(); }

    void ShowRuleNumbers()
    {
        var r = Cur; if (r is null) return;
        bool g = r.UseGlobal;
        _own.Checked = !g;
        _mb.Value = Clamp(_mb, g ? (decimal)_global.MbValue() : r.Mb); _win.Value = Clamp(_win, g ? _global.WindowSeconds() : r.WindowSec);
        _kbps.Value = Clamp(_kbps, r.Kbps);
    }

    string CurrentAction => Actions[Math.Clamp(_action.SelectedIndex, 0, Actions.Length - 1)].Key;

    void ApplyVisibility()
    {
        string a = CurrentAction;
        SuspendLayout();
        foreach (var (part, acts) in _parts)
        {
            if (part == _nameProblemRow) continue;
            bool show = acts.Length == 0 || acts.Contains(a);
            if (part == _ownFields) show &= _own.Checked;      // the rule's own numbers only while it has its own trigger
            part.Parent!.Visible = show;
        }
        _explain.Text = Actions[Math.Clamp(_action.SelectedIndex, 0, Actions.Length - 1)].Explain;
        ResumeLayout(true);
        UpdateRate();
        _list.Invalidate();       // the details line follows the trigger and the action
    }

    void UpdateRate() => _rate.Text = $"≈ {(double)_mb.Value * 8 / (double)_win.Value:0.0} Mbit/s";

    /// <summary>Rule names end up in firewall commands, so Core only lets plain name characters through. Say so while typing,
    /// instead of letting the name be cleaned up silently when the policy is read back.</summary>
    void ShowNameProblem()
    {
        string n = _name.Text.Trim();
        string problem = Cur is null ? "" : n.Length == 0 ? "A rule needs a name." : BackupPolicy.IsValidName(n) ? "" : NameRule;
        _nameProblem.Text = problem;
        bool show = problem.Length > 0;
        if (_nameProblemRow.Parent!.Visible != show) _nameProblemRow.Parent.Visible = _nameProblemRow.Visible = show;
    }

    // ---- data <-> UI ----
    public void SetPolicy(PolicyFile p)
    {
        _loading = true;
        _global.SetValues(p.Global.MbPerWindow, p.Global.WindowSec);
        _rows.Clear(); _list.Items.Clear();
        foreach (var r in p.BackupRules)
            _rows.Add(new Row
            {
                Name = r.Name, Action = r.Action.ToLowerInvariant(), Exes = string.Join("\r\n", r.Exes ?? Array.Empty<string>()),
                Folder = PolicyBuilder.ToLines(r), Path = r.Path ?? "",
                // a rule is "custom" if it sets any of its own numbers; missing ones fall back to the global value
                UseGlobal = p.UsesGlobal(r), Mb = (decimal)p.MbFor(r), WindowSec = p.WindowFor(r),
                Kbps = r.Kbps > 0 ? r.Kbps : 500
            });
        foreach (var r in _rows) _list.Items.Add(r);
        _loading = false;
        if (_rows.Count > 0) _list.SelectedIndex = 0; else ShowCurrent();
    }

    Row? Cur => _list.SelectedIndex >= 0 && _list.SelectedIndex < _rows.Count ? _rows[_list.SelectedIndex] : null;

    void ShowCurrent()
    {
        _loading = true;
        var r = Cur;
        foreach (var (part, _) in _parts) part.Enabled = r is not null;
        _remove.Enabled = r is not null;
        if (r is not null)
        {
            _name.Text = r.Name; _action.Show(Math.Max(0, Array.FindIndex(Actions, a => a.Key == r.Action)));
            _exes.Text = r.Exes; _folder.Text = r.Folder; _path.Text = r.Path;
            ShowRuleNumbers();
        }
        else { _name.Text = _exes.Text = _folder.Text = _path.Text = ""; }
        _loading = false;
        ShowNameProblem();
        ApplyVisibility();
    }

    static decimal Clamp(NumericUpDown n, decimal v) => Math.Min(n.Maximum, Math.Max(n.Minimum, v));

    void Commit()
    {
        if (_loading || Cur is not Row r) return;
        r.Name = _name.Text; r.Action = CurrentAction; r.Exes = _exes.Text; r.Folder = _folder.Text; r.Path = _path.Text;
        // turning the own trigger on keeps the numbers shown at that moment (the global ones) as the rule's own starting point
        r.UseGlobal = !_own.Checked;
        if (!r.UseGlobal) { r.Mb = _mb.Value; r.WindowSec = (int)_win.Value; }
        r.Kbps = (int)_kbps.Value;
        UpdateRate();
        _list.Invalidate();
    }

    void RemoveRow()
    {
        int i = _list.SelectedIndex; if (i < 0) return;
        string name = _rows[i].Name;
        _loading = true; _rows.RemoveAt(i); _list.Items.RemoveAt(i); _loading = false;
        if (_rows.Count > 0) _list.SelectedIndex = Math.Min(i, _rows.Count - 1);
        ShowCurrent();
        _say?.Invoke($"Removed {name}. Click Save And Apply to keep it.", false);
    }

    void Browse()
    {
        if (Cur is null) return;
        bool watch = CurrentAction == "watch";
        using var dlg = new ProgramPicker(_seen(), single: !watch);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK || dlg.Selected.Count == 0) return;
        if (watch)
        {
            var have = _exes.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
            foreach (var p in dlg.Selected) { var f = System.IO.Path.GetFileName(p); if (!have.Contains(f, StringComparer.OrdinalIgnoreCase)) have.Add(f); }
            _exes.Text = string.Join("\r\n", have);
            // keep every folder the picked programs live in (a launcher and its game): add the ones not covered yet
            var dirs = _folder.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
            foreach (var p in dlg.Selected) { var d = System.IO.Path.GetDirectoryName(p) ?? ""; if (d.Length > 0 && !dirs.Any(x => d.StartsWith(x, StringComparison.OrdinalIgnoreCase))) dirs.Add(d); }
            _folder.Text = string.Join("\r\n", dirs);
        }
        else _path.Text = dlg.Selected[0];
    }

    /// <summary>Checks every rule and returns the policy. Throws InvalidDataException naming the rule that is wrong.</summary>
    public PolicyFile Build()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rules = new List<AppRule>();
        foreach (var r in _rows)
        {
            string n = r.Name.Trim();
            if (n.Length == 0) throw new InvalidDataException("A rule has no name.");
            if (!BackupPolicy.IsValidName(n)) throw new InvalidDataException($"Rule '{n}': {NameRule}");
            if (!names.Add(n)) throw new InvalidDataException($"Two rules are called '{n}'. Names must be different.");
            switch (r.Action)
            {
                case "watch":
                    var exes = r.Exes.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                    if (exes.Length == 0) throw new InvalidDataException($"Rule '{n}': add at least one program.");
                    var (folder, folders) = PolicyBuilder.FromLines(r.Folder);
                    rules.Add(new AppRule(n, null, "watch", Exes: exes,
                        MbPerWindow: r.UseGlobal ? null : (double)r.Mb, WindowSec: r.UseGlobal ? null : r.WindowSec,
                        Folder: folder, Folders: folders));
                    break;
                case "block":
                    if (string.IsNullOrWhiteSpace(r.Path)) throw new InvalidDataException($"Rule '{n}': pick the program to block.");
                    rules.Add(new AppRule(n, r.Path.Trim(), "block"));
                    break;
                default:
                    if (string.IsNullOrWhiteSpace(r.Path)) throw new InvalidDataException($"Rule '{n}': pick the program to limit.");
                    rules.Add(new AppRule(n, r.Path.Trim(), "throttle", Kbps: r.Kbps));
                    break;
            }
        }
        return new PolicyFile(rules, new WatchDefaults(_global.MbValue(), _global.WindowSeconds()));
    }
}
