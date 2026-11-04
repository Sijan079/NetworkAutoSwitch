using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray.Fluent.Pages;

/// <summary>One rule being edited. Numbers a rule does not show (its own time window) are kept exactly as they were loaded.</summary>
sealed class RuleVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    string _name = "New Rule", _action = "watch";
    public string Name { get => _name; set { _name = value; Changed(); } }
    public string Action { get => _action; set { _action = value; Changed(); } }
    public string Exes = "", Folder = "", Path = "";
    public bool UseGlobal = true;
    public double Mb = 2; public int WindowSec = 10, Kbps = 500;     // WindowSec has no field on screen: it is carried through untouched
    public string Display => $"{Name}   ·   {Action switch { "watch" => "Watch", "block" => "Block", _ => "Upload Cap" }}";
    void Changed() { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display))); }
    public void Refresh() => Changed();
}

/// <summary>The rules editor: the list of rules on the left, the selected rule on the right. Load() fills it, Build() validates and returns the policy.</summary>
public partial class PolicyPage : Page
{
    static readonly string[] Actions = { "watch", "block", "throttle" };
    readonly FluentContext _cx;
    readonly ObservableCollection<RuleVm> _rows = new();
    bool _loading, _dirty, _stale;
    WatchDefaults? _pendingDefaults;            // set when "Edit As JSON" changed the global trigger: used by the next Save
    WatchDefaults _defaults = new();            // only used to show a rule's numbers while it follows the global trigger

    internal PolicyPage(FluentContext cx)
    {
        _cx = cx;
        InitializeComponent();
        RuleList.ItemsSource = _rows;
        Load();
        _cx.PolicyChanged += () => Dispatcher.BeginInvoke(OnPolicyChangedElsewhere);

        RuleList.SelectionChanged += (_, _) => ShowCurrent();
        AddBtn.Click += (_, _) => PickAndAdd();
        RemoveBtn.Click += (_, _) => RemoveRow();
        ChooseExesBtn.Click += (_, _) => ChooseExes();
        ChoosePathBtn.Click += (_, _) => ChoosePath();
        NameBox.TextChanged += (_, _) => Commit(); ExesBox.TextChanged += (_, _) => Commit();
        FolderBox.TextChanged += (_, _) => Commit(); PathBox.TextChanged += (_, _) => Commit();
        TypeBox.SelectionChanged += (_, _) => { Commit(); ApplyVisibility(); };
        UseGlobal.Click += (_, _) => { Commit(); ApplyVisibility(); };
        SaveBtn.Click += (_, _) => Save();
        RevertBtn.Click += (_, _) => { Load(); Say(""); };
        JsonBtn.Click += (_, _) => EditJson();
        DragOver += (_, e) => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddPrograms(files.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));
        };
        ShowCurrent();
        MbStepper.ValueChanged += (_, _) => { if (!_loading) CommitNumbers(); };
        KbpsStepper.ValueChanged += (_, _) => { if (!_loading) CommitNumbers(); };
    }

    // ---------- data <-> UI ----------
    void Say(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, error ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
    }

    void Load()
    {
        var p = _cx.ReadPolicy();
        _defaults = p.Global; _loading = true;
        _rows.Clear();
        foreach (var r in p.BackupRules)
            _rows.Add(new RuleVm
            {
                Name = r.Name, Action = r.Action.ToLowerInvariant(), Exes = string.Join("\r\n", r.Exes ?? Array.Empty<string>()),
                Folder = r.Folder ?? "", Path = r.Path ?? "",
                UseGlobal = p.UsesGlobal(r), Mb = p.MbFor(r), WindowSec = p.WindowFor(r), Kbps = r.Kbps > 0 ? r.Kbps : 500,
            });
        _loading = false; _dirty = _stale = false; _pendingDefaults = null;
        if (_rows.Count > 0) RuleList.SelectedIndex = 0; else ShowCurrent();
    }

    /// <summary>Another page saved the policy (for example "Add To Watch List" on the Dashboard). If nothing is being edited here, show the new rules;
    /// otherwise remember it, so Save cannot silently overwrite the other change.</summary>
    void OnPolicyChangedElsewhere() { if (_dirty) { _stale = true; Say("The rules were changed on another page. Click Revert to see them.", true); } else Load(); }

    RuleVm? Cur => RuleList.SelectedItem as RuleVm;

    void ShowCurrent()
    {
        _loading = true;
        var r = Cur;
        Form.IsEnabled = r is not null; Form.Visibility = r is null ? Visibility.Collapsed : Visibility.Visible; EmptyHint.Visibility = r is null ? Visibility.Visible : Visibility.Collapsed;
        if (r is not null)
        {
            NameBox.Text = r.Name; TypeBox.SelectedIndex = Math.Max(0, Array.IndexOf(Actions, r.Action));
            ExesBox.Text = r.Exes; FolderBox.Text = r.Folder; PathBox.Text = r.Path;
            UseGlobal.IsChecked = r.UseGlobal;
            MbStepper.Value = Math.Min(MbStepper.Maximum, Math.Max(MbStepper.Minimum, r.UseGlobal ? _defaults.MbPerWindow : r.Mb));
            KbpsStepper.Value = r.Kbps;
        }
        _loading = false;
        ApplyVisibility();
    }

    string CurrentAction => Actions[Math.Max(0, TypeBox.SelectedIndex)];

    void ApplyVisibility()
    {
        string a = CurrentAction;
        void Show(bool on, params UIElement[] els) { foreach (var e in els) e.Visibility = on ? Visibility.Visible : Visibility.Collapsed; }
        Show(a == "watch", ExesLbl, ExesPanel, FolderLbl, FolderBox, TriggerLbl, TriggerPanel);
        Show(a != "watch", PathLbl, PathPanel);
        Show(a == "throttle", CapLbl, CapPanel);
        MbStepper.IsEnabled = UseGlobal.IsChecked != true;      // editable only when the rule has its own trigger
        PathLbl.Text = a == "block" ? "Program" : "Program";
    }

    void Commit()
    {
        if (_loading || Cur is not RuleVm r) return;
        r.Name = NameBox.Text; r.Action = CurrentAction; r.Exes = ExesBox.Text; r.Folder = FolderBox.Text; r.Path = PathBox.Text;
        r.UseGlobal = UseGlobal.IsChecked == true;
        CommitNumbers(); _dirty = true; Say("");
    }

    void CommitNumbers()
    {
        if (_loading || Cur is not RuleVm r) return;
        // turning "use global" off keeps the numbers shown at that moment as the rule's own starting point
        if (!r.UseGlobal) { if (Math.Abs(r.Mb - MbStepper.Value) > 1e-9) { r.Mb = MbStepper.Value; _dirty = true; } }
        else { _loading = true; MbStepper.Value = Math.Min(MbStepper.Maximum, Math.Max(MbStepper.Minimum, _defaults.MbPerWindow)); _loading = false; }
        if (r.Kbps != (int)KbpsStepper.Value && CurrentAction == "throttle") { r.Kbps = (int)KbpsStepper.Value; _dirty = true; }
    }

    void RemoveRow()
    {
        int i = RuleList.SelectedIndex; if (i < 0) return;
        _rows.RemoveAt(i); _dirty = true;
        if (_rows.Count > 0) RuleList.SelectedIndex = Math.Min(i, _rows.Count - 1); else ShowCurrent();
    }

    // ---------- adding programs ----------
    void PickAndAdd()
    {
        var dlg = new FluentProgramPicker(_cx.Engine.Usage.Seen()) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.Selected.Count > 0) AddPrograms(dlg.Selected);
    }

    /// <summary>Makes ONE watch rule from the given programs (a launcher plus its helpers). Programs already covered by a rule are skipped and reported.</summary>
    public int AddPrograms(IEnumerable<string> paths)
    {
        var current = _rows.Select(r => new AppRule(r.Name, null, r.Action, Exes: r.Exes.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries), Folder: string.IsNullOrWhiteSpace(r.Folder) ? null : r.Folder)).ToList();
        var (rule, skipped) = PolicyBuilder.WatchRuleFor(paths, current);
        if (rule is null) { Say(skipped.Count > 0 ? $"Already watched: {string.Join(", ", skipped)}" : "Nothing to add.", true); return 0; }
        _rows.Add(new RuleVm { Name = rule.Name, Action = "watch", Exes = string.Join("\r\n", rule.Exes!), Folder = rule.Folder ?? "" });
        RuleList.SelectedIndex = _rows.Count - 1; _dirty = true;
        Say(skipped.Count > 0 ? $"Added {rule.Name}. Already watched: {string.Join(", ", skipped)}" : $"Added {rule.Name}. Click Save to keep it.");
        return rule.Exes!.Length;
    }

    void ChooseExes()
    {
        if (Cur is null) return;
        var dlg = new FluentProgramPicker(_cx.Engine.Usage.Seen()) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.Selected.Count == 0) return;
        var have = ExesBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        foreach (var p in dlg.Selected) { var f = System.IO.Path.GetFileName(p); if (!have.Contains(f, StringComparer.OrdinalIgnoreCase)) have.Add(f); }
        ExesBox.Text = string.Join("\r\n", have);
        if (string.IsNullOrWhiteSpace(FolderBox.Text)) FolderBox.Text = System.IO.Path.GetDirectoryName(dlg.Selected[0]) ?? "";
    }

    void ChoosePath()
    {
        if (Cur is null) return;
        var dlg = new FluentProgramPicker(_cx.Engine.Usage.Seen(), single: true) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.Selected.Count > 0) PathBox.Text = dlg.Selected[0];
    }

    // ---------- building / saving ----------
    /// <summary>Checks every rule and returns the policy. Throws InvalidDataException naming the rule that is wrong.
    /// The global trigger is NOT taken from this page's memory but from the file, so a Settings change made meanwhile is never undone.</summary>
    PolicyFile Build()
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
                        MbPerWindow: r.UseGlobal ? null : r.Mb, WindowSec: r.UseGlobal ? null : r.WindowSec,
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
        return new PolicyFile(rules, _pendingDefaults ?? _cx.ReadPolicy().Defaults);
    }

    void Save()
    {
        if (_stale) { Say("Not saved: the rules were changed on another page. Click Revert, then make your change again.", true); return; }
        try
        {
            var policy = Build();
            _cx.WritePolicy(policy, msg => { Say(msg); });
            _dirty = false; _pendingDefaults = null; _defaults = policy.Global;
        }
        catch (InvalidDataException e) { Say("Not saved. " + e.Message, true); }
        catch (Exception e) { Say("Could not save: " + e.Message, true); }
    }

    /// <summary>The rules as text, in a dialog. The text is checked before it is accepted, and only changes the editor: Save still decides.</summary>
    void EditJson()
    {
        string json;
        try { json = BackupPolicy.ToJson(Build()); }
        catch (InvalidDataException e) { Say("Fix this first: " + e.Message, true); return; }
        var dlg = new FluentJsonDialog(json) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var p = BackupPolicy.Parse(dlg.Json);
            _loading = true; _rows.Clear();
            foreach (var r in p.BackupRules)
                _rows.Add(new RuleVm { Name = r.Name, Action = r.Action.ToLowerInvariant(), Exes = string.Join("\r\n", r.Exes ?? Array.Empty<string>()), Folder = r.Folder ?? "", Path = r.Path ?? "",
                    UseGlobal = p.UsesGlobal(r), Mb = p.MbFor(r), WindowSec = p.WindowFor(r), Kbps = r.Kbps > 0 ? r.Kbps : 500 });
            _pendingDefaults = p.Defaults; _loading = false; _dirty = true;
            if (_rows.Count > 0) RuleList.SelectedIndex = 0; else ShowCurrent();
            Say("JSON applied here. Click Save to keep it.");
        }
        catch (InvalidDataException e) { Say(e.Message, true); }
    }
}
