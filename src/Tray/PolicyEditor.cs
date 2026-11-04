using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

/// <summary>Form-based editor for the backup rules, as two sibling cards on the page: the rule list on the left,
/// the selected rule on the right. Explanations live in tooltips. Load() fills it, Build() validates and returns the policy.</summary>
sealed class PolicyEditor : UserControl
{
    sealed class Row
    {
        public string Name = "New Rule", Action = "watch", Exes = "", Folder = "", Path = "";
        public bool UseGlobal = true;                       // watch rules follow the global trigger unless this is off
        public decimal Mb = 2; public int WindowSec = 10, Kbps = 500;   // only used when UseGlobal is off
        public override string ToString() => $"{Name}   ·   {Action switch { "watch" => "Watch", "block" => "Block", _ => "Upload Cap" }}";
    }

    static readonly (string Key, string Text)[] Actions =
    {
        ("watch", "Watch Downloads"),
        ("block", "Always Block"),
        ("throttle", "Limit Uploads"),
    };

    readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None, BackColor = Ui.CardBg, Font = Ui.UiFont(10f), ItemHeight = 34, DrawMode = DrawMode.OwnerDrawFixed };
    readonly TextBox _name = new(), _exes = new() { Multiline = true, Height = 84, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true },
                     _folder = new(), _path = new();
    readonly NoWheelComboBox _action = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly NoWheelNumeric _mb = new() { DecimalPlaces = 1, Minimum = 0.1m, Maximum = 10000, Increment = 1, Width = 84 },
                            _win = new() { Minimum = 1, Maximum = 600, Width = 68 },
                            _kbps = new() { Minimum = 1, Maximum = 1_000_000, Increment = 100, Width = 96 };
    readonly Label _rate = new() { AutoSize = true, ForeColor = Ui.Muted, Padding = new Padding(4, 6, 0, 0), BackColor = Ui.CardBg };
    readonly GlobalTriggerCard _global;      // the global trigger (shared by every watch rule that has no trigger of its own); shown on the Settings page
    readonly CheckBox _useGlobal = new() { Text = "Use Global Trigger", AutoSize = true, Checked = true, BackColor = Ui.CardBg };
    readonly TableLayoutPanel _form = new() { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Padding = new Padding(0, 4, 0, 0), BackColor = Ui.CardBg };
    readonly List<Row> _rows = new();
    bool _loading;
    Func<IEnumerable<SeenProgram>> _seen = () => Array.Empty<SeenProgram>();
    Action<string, bool>? _say;

    /// <summary>Where the picker gets "programs that used data on the backup", and where messages go.</summary>
    public void Connect(Func<IEnumerable<SeenProgram>> seenOnBackup, Action<string, bool> say) { _seen = seenOnBackup; _say = say; }

    /// <summary>Makes ONE watch rule from the given programs (a launcher plus its helpers). Returns how many programs were added.
    /// Programs already covered by a rule are skipped and reported.</summary>
    public int AddPrograms(IEnumerable<string> paths)
    {
        var current = _rows.Select(r => new AppRule(r.Name, null, r.Action, Exes: r.Exes.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries), Folder: string.IsNullOrWhiteSpace(r.Folder) ? null : r.Folder)).ToList();
        var (rule, skipped) = PolicyBuilder.WatchRuleFor(paths, current);
        if (rule is null) { _say?.Invoke(skipped.Count > 0 ? $"Already watched: {string.Join(", ", skipped)}" : "Nothing to add.", true); return 0; }
        var row = new Row { Name = rule.Name, Action = "watch", Exes = string.Join("\r\n", rule.Exes!), Folder = rule.Folder ?? "" };
        _rows.Add(row); _list.Items.Add(row); _list.SelectedIndex = _rows.Count - 1;
        _say?.Invoke(skipped.Count > 0 ? $"Added {rule.Name}. Already watched: {string.Join(", ", skipped)}" : $"Added {rule.Name}.", false);
        return rule.Exes!.Length;
    }

    /// <summary>The "Add Rule" button: pick programs from a list, get a ready-made rule.</summary>
    void PickAndAdd()
    {
        using var dlg = new ProgramPicker(_seen());
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK && dlg.Selected.Count > 0) AddPrograms(dlg.Selected);
    }

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
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Ui.Bg };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // rule list
        var left = new Card(Ui.List, "Rules");
        left.AddHeaderAction(Ui.Add, "Add Rule", "Add a rule: pick programs from a list.\nYou can also drag an .exe onto this page.", PickAndAdd, BtnKind.Primary);
        left.AddHeaderButton(Ui.Delete, "Remove the selected rule", RemoveRow);
        left.Body.Padding = new Padding(4, 0, 4, 6); left.Body.Controls.Add(_list);
        _list.SelectedIndexChanged += (_, _) => ShowCurrent();
        _list.DrawItem += (_, e) =>
        {
            e.DrawBackground(); if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using var back = new SolidBrush(sel ? (Ui.Dark ? Color.FromArgb(40, 56, 92) : Color.FromArgb(232, 240, 254)) : Ui.CardBg); e.Graphics.FillRectangle(back, e.Bounds);
            TextRenderer.DrawText(e.Graphics, _list.Items[e.Index].ToString(), _list.Font, new Rectangle(e.Bounds.X + 14, e.Bounds.Y, e.Bounds.Width - 14, e.Bounds.Height), sel ? Ui.Blue : Ui.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        };

        // the selected rule
        var right = new Card(Ui.Settings, "Rule") { Margin = new Padding(0, 0, Ui.Gap, Ui.Gap) };
        _form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104)); _form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var a in Actions) _action.Items.Add(a.Text);
        Ui.Tip.SetToolTip(_action, "Watch: blocked only while it downloads fast.\nAlways block: never allowed on the backup.\nLimit uploads: caps upload speed (cannot limit downloads).");
        _name.Dock = _folder.Dock = _path.Dock = _exes.Dock = _action.Dock = DockStyle.Fill;
        Ui.Tip.SetToolTip(_exes, "One file name per line, e.g. steam.exe.\nList launchers and downloaders, never the games, so a game is never cut off.");
        Ui.Tip.SetToolTip(_folder, "Optional: only match programs inside this folder (useful when two launchers share a file name).");
        Ui.Tip.SetToolTip(_useGlobal, "Off = this rule gets its own trigger.");

        var browse = Ui.Btn(Ui.Add, "Choose Programs", "Pick programs from a list (or browse for a file).", Browse);
        var trigger = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, BackColor = Ui.CardBg };
        trigger.Controls.AddRange(new Control[] { _mb, L("MB Per"), _win, L("s"), _rate });
        var kb = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, BackColor = Ui.CardBg };
        kb.Controls.AddRange(new Control[] { _kbps, L("kbps") });

        AddField("Name", _name, null);
        AddField("Type", _action, null);
        AddField("Programs", _exes, "watch");
        AddField("", browse, null);
        AddField("Folder", _folder, "watch");
        AddField("Path", _path, "block", "throttle");
        AddField("Trigger", _useGlobal, "watch");
        AddField("", trigger, "watch");
        AddField("Upload Cap", kb, "throttle");
        right.Body.AutoScroll = true; right.Body.Controls.Add(_form);


        grid.Controls.Add(left, 0, 0); grid.Controls.Add(right, 1, 0);
        Controls.Add(grid);

        foreach (var c in new Control[] { _name, _exes, _folder, _path }) c.TextChanged += (_, _) => Commit();
        _action.SelectedIndexChanged += (_, _) => { Commit(); ApplyVisibility(); };
        foreach (var n in new[] { _mb, _win, _kbps }) n.ValueChanged += (_, _) => Commit();
        _useGlobal.CheckedChanged += (_, _) => { Commit(); ApplyVisibility(); };
        _global.Changed += () => { if (!_loading) ShowGlobalChange(); };
    }

    static Label L(string t) => new() { Text = t, AutoSize = true, Padding = new Padding(2, 6, 8, 0), ForeColor = Ui.Muted, BackColor = Ui.CardBg };

    int _rowIndex;
    readonly List<(Label lbl, Control ctl, string[] actions)> _fields = new();
    void AddField(string label, Control c, string? action, string? action2 = null)
    {
        var l = new Label { Text = label, AutoSize = true, Padding = new Padding(0, 8, 0, 0), ForeColor = Ui.Muted, BackColor = Ui.CardBg };
        _form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _form.Controls.Add(l, 0, _rowIndex); _form.Controls.Add(c, 1, _rowIndex);
        c.Margin = new Padding(3, 4, 3, 10); _rowIndex++;
        _fields.Add((l, c, new[] { action, action2 }.Where(a => a is not null).Select(a => a!).ToArray()));
    }

    /// <summary>When the global values change, rules that follow them show the new numbers straight away.</summary>
    void ShowGlobalChange() { _loading = true; ShowRuleNumbers(); _loading = false; ApplyVisibility(); }

    void ShowRuleNumbers()
    {
        var r = Cur; if (r is null) return;
        bool g = r.UseGlobal;
        _useGlobal.Checked = g;
        _mb.Value = Clamp(_mb, g ? (decimal)_global.MbValue() : r.Mb); _win.Value = Clamp(_win, g ? _global.WindowSeconds() : r.WindowSec);
        _kbps.Value = Clamp(_kbps, r.Kbps);
    }

    string CurrentAction => Actions[Math.Max(0, _action.SelectedIndex)].Key;

    void ApplyVisibility()
    {
        string a = CurrentAction;
        foreach (var (lbl, ctl, acts) in _fields)
        {
            bool show = acts.Length == 0 || acts.Contains(a);
            lbl.Visible = ctl.Visible = show;
        }
        _mb.Enabled = _win.Enabled = !_useGlobal.Checked;   // editable only when the rule has its own trigger
        UpdateRate();
    }

    void UpdateRate()
    {
        _rate.Text = $"≈ {(double)_mb.Value * 8 / (double)_win.Value:0.0} Mbit/s";
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
                Folder = r.Folder ?? "", Path = r.Path ?? "",
                // a rule is "custom" if it sets any of its own numbers; missing ones fall back to the global value
                UseGlobal = p.UsesGlobal(r), Mb = (decimal)p.MbFor(r), WindowSec = p.WindowFor(r),
                Kbps = r.Kbps > 0 ? r.Kbps : 500
            });
        foreach (var r in _rows) _list.Items.Add(r);
        _loading = false;
        if (_rows.Count > 0) _list.SelectedIndex = 0; else ShowCurrent();
    }

    Row? Cur => _list.SelectedIndex >= 0 ? _rows[_list.SelectedIndex] : null;

    void ShowCurrent()
    {
        _loading = true;
        var r = Cur;
        _form.Enabled = r is not null;
        if (r is not null)
        {
            _name.Text = r.Name; _action.SelectedIndex = Math.Max(0, Array.FindIndex(Actions, a => a.Key == r.Action));
            _exes.Text = r.Exes; _folder.Text = r.Folder; _path.Text = r.Path;
            ShowRuleNumbers();
        }
        _loading = false;
        ApplyVisibility();
    }

    static decimal Clamp(NumericUpDown n, decimal v) => Math.Min(n.Maximum, Math.Max(n.Minimum, v));

    void Commit()
    {
        if (_loading || Cur is not Row r) return;
        r.Name = _name.Text; r.Action = CurrentAction; r.Exes = _exes.Text; r.Folder = _folder.Text; r.Path = _path.Text;
        // turning "use global" off keeps the numbers shown at that moment as the rule's own starting point
        r.UseGlobal = _useGlobal.Checked;
        if (!r.UseGlobal) { r.Mb = _mb.Value; r.WindowSec = (int)_win.Value; }
        r.Kbps = (int)_kbps.Value;
        UpdateRate();
        int i = _list.SelectedIndex; _loading = true; _list.Items[i] = r; _list.SelectedIndex = i; _loading = false;
    }

    void RemoveRow()
    {
        int i = _list.SelectedIndex; if (i < 0) return;
        _rows.RemoveAt(i); _list.Items.RemoveAt(i);
        if (_rows.Count > 0) _list.SelectedIndex = Math.Min(i, _rows.Count - 1); else ShowCurrent();
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
            if (string.IsNullOrWhiteSpace(_folder.Text)) _folder.Text = System.IO.Path.GetDirectoryName(dlg.Selected[0]) ?? "";
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
            if (!names.Add(n)) throw new InvalidDataException($"Two rules are called '{n}'. Names must be different.");
            switch (r.Action)
            {
                case "watch":
                    var exes = r.Exes.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                    if (exes.Length == 0) throw new InvalidDataException($"Rule '{n}': add at least one program.");
                    rules.Add(new AppRule(n, null, "watch", Exes: exes,
                        MbPerWindow: r.UseGlobal ? null : (double)r.Mb, WindowSec: r.UseGlobal ? null : r.WindowSec,
                        Folder: string.IsNullOrWhiteSpace(r.Folder) ? null : r.Folder.Trim()));
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
