using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NetworkAutoSwitch.Core;
using Wpf.Ui.Controls;

namespace NetworkAutoSwitch.Tray.Fluent.Pages;

/// <summary>A row in one of the tables. Changing a property updates the row on screen, so a table is never cleared and refilled (that would lose the selection).</summary>
sealed class Row : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Key { get; init; } = "";
    string _path = "", _rule = "", _when = "", _name = "", _total = "", _peak = "", _watched = "";
    public string Path { get => _path; set => Set(ref _path, value, nameof(Path)); }
    public string Rule { get => _rule; set => Set(ref _rule, value, nameof(Rule)); }
    public string When { get => _when; set => Set(ref _when, value, nameof(When)); }
    public string Name { get => _name; set => Set(ref _name, value, nameof(Name)); }
    public string Total { get => _total; set => Set(ref _total, value, nameof(Total)); }
    public string Peak { get => _peak; set => Set(ref _peak, value, nameof(Peak)); }
    public string Watched { get => _watched; set => Set(ref _watched, value, nameof(Watched)); }
    void Set(ref string f, string v, string n) { if (f == v) return; f = v; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n)); }

    /// <summary>Makes <paramref name="rows"/> match <paramref name="wanted"/> (keyed by Key): changes rows in place, adds new ones, removes gone ones.</summary>
    public static void Sync(ObservableCollection<Row> rows, IReadOnlyList<(string Key, Action<Row> Fill)> wanted)
    {
        for (int i = rows.Count - 1; i >= 0; i--) if (!wanted.Any(w => w.Key == rows[i].Key)) rows.RemoveAt(i);
        for (int i = 0; i < wanted.Count; i++)
        {
            var (key, fill) = wanted[i];
            int at = -1; for (int j = 0; j < rows.Count; j++) if (rows[j].Key == key) { at = j; break; }
            if (at < 0) { var r = new Row { Key = key }; fill(r); rows.Insert(Math.Min(i, rows.Count), r); }
            else { fill(rows[at]); if (at != i) rows.Move(at, i); }
        }
    }
}

public partial class DashboardPage : Page
{
    readonly FluentContext _cx;
    SwitchEngine E => _cx.Engine;
    readonly ObservableCollection<Row> _blocked = new(), _allowed = new(), _usage = new();
    readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer _loadingTimeout = new() { Interval = TimeSpan.FromSeconds(12) };
    bool _loading, _awaitStatus;
    static readonly int[] GraceMinutes = { 10, 30, 60, 180 };

    string Primary => E.PrimaryName;
    string Backup => E.BackupName.Length > 0 ? E.BackupName : "No Backup";

    internal DashboardPage(FluentContext cx)
    {
        _cx = cx;
        InitializeComponent();
        BlockedList.ItemsSource = _blocked; AllowedList.ItemsSource = _allowed; UsageList.ItemsSource = _usage;
        DryBadge.Visibility = E.Live ? Visibility.Collapsed : Visibility.Visible;

        // engine events arrive on background threads; the page lives as long as the window, so these are subscribed exactly once
        E.Status += s => Dispatcher.BeginInvoke(() => Apply(s));
        E.AdaptersChanged += () => Dispatcher.BeginInvoke(OnAdaptersChanged);
        E.Busy += busy => Dispatcher.BeginInvoke(() => OnBusy(busy));
        _loadingTimeout.Tick += (_, _) => SetLoading(false);       // never leave placeholders up for good
        _refresh.Tick += (_, _) => RefreshTables();
        _refresh.Start();

        PauseBtn.Click += (_, _) => { _cx.TogglePause(); UpdatePause(); };
        ResumeBtn.Click += (_, _) => { _cx.TogglePause(); UpdatePause(); };
        ModeBtn.Click += (_, _) => ShowModeMenu();
        UnblockBtn.Click += (_, _) => Unblock(all: false);
        UnblockAllBtn.Click += (_, _) => Unblock(all: true);
        BlockNowBtn.Click += (_, _) => AllowedAction(block: true);
        WatchAgainBtn.Click += (_, _) => AllowedAction(block: false);
        AddWatchBtn.Click += (_, _) => AddWatch();
        ResetBtn.Click += (_, _) => { E.Usage.Reset(); RefreshTables(); };
        OnAdaptersChanged();
        RefreshTables();
    }

    // ---------- loading placeholders ----------
    void SetLoading(bool on)
    {
        _loading = on; _loadingTimeout.Stop();
        var vis = on ? Visibility.Visible : Visibility.Collapsed; var rev = on ? Visibility.Collapsed : Visibility.Visible;
        BodyP.Visibility = BodyB.Visibility = BannerRow.Visibility = rev;
        SkelP.Visibility = SkelB.Visibility = BannerSkeleton.Visibility = SkelChart.Visibility = vis;
        Chart.Opacity = on ? 0 : 1;
        if (on) _loadingTimeout.Start(); else _awaitStatus = false;
    }

    /// <summary>The engine started (true) or finished (false) changing the network. The placeholders stay until the next measurement arrives,
    /// so an old adapter's latency is never shown under the new adapter's name.</summary>
    void OnBusy(bool busy) { if (busy) SetLoading(true); else if (_loading) _awaitStatus = true; }

    // ---------- adapters / status ----------
    static Wpf.Ui.Controls.SymbolRegular Glyph(AdapterKind k) => k switch { AdapterKind.WiFi => SymbolRegular.Wifi124, AdapterKind.Tether => SymbolRegular.Phone24, _ => SymbolRegular.PlugConnected24 };

    void OnAdaptersChanged()
    {
        NameP.Text = Primary; IconP.Symbol = Glyph(E.PrimaryKind); NetP.Text = E.PrimaryNetwork ?? "";
        NameB.Text = Backup; IconB.Symbol = Glyph(E.BackupKind); NetB.Text = E.BackupNetwork ?? "";
        ModeBtn.Content = ModeLabel(E.ConnMode);
    }

    string ModeLabel(ConnectionMode m) => m switch { ConnectionMode.StayPrimary => $"Stay On {Primary}", ConnectionMode.StayBackup => $"Stay On {Backup}", _ => "Automatic" };

    void ShowModeMenu()
    {
        var m = new ContextMenu();
        foreach (var mode in new[] { ConnectionMode.Auto, ConnectionMode.StayPrimary, ConnectionMode.StayBackup })
        {
            var it = new Wpf.Ui.Controls.MenuItem { Header = ModeLabel(mode), IsCheckable = true, IsChecked = E.ConnMode == mode };
            it.Click += (_, _) => { E.SetConnectionMode(mode); ModeBtn.Content = ModeLabel(mode); LockBadge.Visibility = mode != ConnectionMode.Auto ? Visibility.Visible : Visibility.Collapsed; };
            m.Items.Add(it);
        }
        m.Items.Add(new Separator());
        var choose = new Wpf.Ui.Controls.MenuItem { Header = "Choose Connections..." };
        choose.Click += (_, _) => ChooseConnections();
        m.Items.Add(choose);
        m.PlacementTarget = ModeBtn; m.IsOpen = true;
    }

    /// <summary>Opens the dialog where you choose the main and backup adapter yourself (or leave them on Automatic).</summary>
    public void ChooseConnections()
    {
        var dlg = new FluentConnectionsDialog(E) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }

    void UpdatePause() { PauseBtn.Visibility = E.Paused ? Visibility.Collapsed : Visibility.Visible; ResumeBtn.Visibility = E.Paused ? Visibility.Visible : Visibility.Collapsed; }

    static string WithNet(string adapter, string? net) => string.IsNullOrEmpty(net) ? adapter : $"{adapter}  ·  {net}";

    void Paint(Wpf.Ui.Controls.TextBlock t, string brushKey) => t.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, brushKey);

    void Apply(EngineStatus s)
    {
        NetP.Text = E.PrimaryNetwork ?? ""; NetB.Text = E.BackupNetwork ?? "";
        bool anyOk = s.Primary.Healthy || s.Backup.Healthy;
        string brush; SymbolRegular icon; string text;
        if (s.Paused) { text = "Paused"; brush = "TextFillColorSecondaryBrush"; icon = SymbolRegular.Pause24; }
        else if (!anyOk) { text = "No Connection"; brush = "SystemFillColorCriticalBrush"; icon = SymbolRegular.Warning24; }
        else if (s.Active == Active.Primary) { text = WithNet(Primary, E.PrimaryNetwork); brush = "SystemFillColorSuccessBrush"; icon = Glyph(E.PrimaryKind); }
        else { text = WithNet(Backup, E.BackupNetwork) + "  ·  Backup"; brush = "SystemFillColorCautionBrush"; icon = Glyph(E.BackupKind); }
        BannerText.Text = text; BannerIcon.Symbol = icon;
        BannerText.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, brush); BannerIcon.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, brush);
        UpdatePause();
        LockBadge.Visibility = s.Mode != ConnectionMode.Auto ? Visibility.Visible : Visibility.Collapsed; ModeBtn.Content = ModeLabel(s.Mode);

        var st = E.Stats;
        Show(BigP, SmallP, CardP, s.Primary, StatsTracker.UptimePct(st.ProbesPrimary, st.FailPrimary), st.AvgMs(true), !s.Paused && anyOk && s.Active == Active.Primary, "SystemFillColorSuccessBrush");
        Show(BigB, SmallB, CardB, s.Backup, StatsTracker.UptimePct(st.ProbesBackup, st.FailBackup), st.AvgMs(false), !s.Paused && anyOk && s.Active == Active.Backup, "SystemFillColorCautionBrush");
        var (p, b) = st.Snapshot();
        Chart.SetData(p, b, Primary, Backup);
        if (_awaitStatus) SetLoading(false);       // the first measurement after a change has arrived

        var onBackup = TimeSpan.FromSeconds(st.SecondsOnBackup);
        StSwitches.Text = st.Switches.ToString();
        StOnBackup.Text = onBackup.TotalHours >= 1 ? $"{(int)onBackup.TotalHours} h {onBackup.Minutes} min" : $"{(int)onBackup.TotalMinutes} min";
        StLast.Text = st.LastSwitchAt is DateTime t ? t.ToString("HH:mm") : "None";
        StLast.ToolTip = st.LastSwitchAt is null ? "No switch yet." : st.LastSwitchReason;
    }

    static void Show(Wpf.Ui.Controls.TextBlock big, Wpf.Ui.Controls.TextBlock small, Border card, ProbeResult r, double uptime, int? avg, bool inUse, string inUseBrush)
    {
        if (r.Healthy)
        {
            big.Text = $"{r.Ms} ms"; big.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "SystemFillColorSuccessBrush");
            small.Text = $"Avg {(avg is int a ? a + " ms" : "-")}  ·  {uptime:0.#}% Up";
        }
        else { big.Text = "Down"; big.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "SystemFillColorCriticalBrush"); small.Text = r.Detail; }
        if (inUse) card.SetResourceReference(Border.BorderBrushProperty, inUseBrush); else card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        card.BorderThickness = new Thickness(inUse ? 2 : 1);
    }

    // ---------- tables (refreshed every 2 s, in place) ----------
    static string Ago(TimeSpan since) => since.TotalHours >= 1 ? $"{(int)since.TotalHours} h {since.Minutes} m" : $"{(int)since.TotalMinutes} min";
    static string Left(TimeSpan left) => left.TotalHours >= 1 ? $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}" : $"{(int)left.TotalMinutes}:{left.Seconds:00}";
    static string Fmt(long b) => b >= 1_000_000_000 ? $"{b / 1e9:0.00} GB" : b >= 1_000_000 ? $"{b / 1e6:0.0} MB" : $"{b / 1e3:0.0} KB";

    void RefreshTables()
    {
        var blocked = E.Blocked;
        Row.Sync(_blocked, blocked.Select(b => (b.DisplayName, (Action<Row>)(r => { r.Path = b.Path; r.Rule = b.Rule; var d = DateTime.UtcNow - b.Since; r.When = Ago(d < TimeSpan.Zero ? TimeSpan.Zero : d); }))).ToList());
        BlockedCard.Visibility = blocked.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BlockedTitle.Text = blocked.Count > 0 ? $"Blocked ({blocked.Count})" : "Blocked";

        var allowed = E.Allowed;
        Row.Sync(_allowed, allowed.Select(a => (a.DisplayName, (Action<Row>)(r => { r.Path = a.Path; r.Rule = a.Rule; var d = a.Until - DateTime.UtcNow; r.When = Left(d < TimeSpan.Zero ? TimeSpan.Zero : d); }))).ToList());
        AllowedCard.Visibility = allowed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        Row.Sync(_usage, E.Usage.TopWithPeak(10).Select(u => (u.Item1, (Action<Row>)(r =>
        {
            r.Name = u.Item1; r.Total = Fmt(u.Item2); r.Peak = $"{u.Item3 / 1e6:0.0} MB";
            r.Watched = E.IsWatched(E.Usage.PathFor(u.Item1) ?? u.Item1) ? "Watched" : "";
        }))).ToList());
    }

    void Unblock(bool all)
    {
        var names = (all ? _blocked : BlockedList.SelectedItems.Cast<Row>()).Select(r => r.Key).ToList();
        if (names.Count == 0) return;
        int minutes = GraceMinutes[Math.Max(0, Grace.SelectedIndex)];
        Task.Run(() => { foreach (var n in names) E.OverrideBlock(n, minutes); Dispatcher.BeginInvoke(RefreshTables); });   // runs PowerShell: keep off the UI thread
    }

    void AllowedAction(bool block)
    {
        var names = AllowedList.SelectedItems.Cast<Row>().Select(r => r.Key).ToList();
        if (names.Count == 0) return;
        Task.Run(() => { foreach (var n in names) { if (block) E.BlockAgain(n); else E.WatchAgain(n); } Dispatcher.BeginInvoke(RefreshTables); });
    }

    /// <summary>One click from the "Backup Data" table: the selected programs become a watch rule, saved and applied at once.</summary>
    void AddWatch()
    {
        var paths = UsageList.SelectedItems.Cast<Row>().Select(r => E.Usage.PathFor(r.Key)).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
        if (paths.Count == 0) { UsageStatus.Text = "Select a program in the list first."; return; }
        try
        {
            var pol = _cx.ReadPolicy();
            var (rule, skipped) = PolicyBuilder.WatchRuleFor(paths, pol.BackupRules);
            if (rule is null) { UsageStatus.Text = skipped.Count > 0 ? $"Already watched: {string.Join(", ", skipped)}" : "Nothing to add."; return; }
            var rules = pol.BackupRules.ToList(); rules.Add(rule);
            _cx.WritePolicy(pol with { BackupRules = rules }, msg => UsageStatus.Text = msg == "Saved and applied." ? $"Added {rule.Name}. Saved and applied." : msg);
        }
        catch (Exception e) { UsageStatus.Text = "Not saved. " + e.Message; }
    }
}
