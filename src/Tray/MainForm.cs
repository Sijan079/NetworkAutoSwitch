using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

/// <summary>The app window: Dashboard (live status, latency, stats, backup data, blocks), Log (live, newest first), Policy (rules editor) and JSON.
/// Every card sits directly on its page (cards are never nested). Closing the window hides it to the tray; Exit lives in the tray menu.</summary>
sealed class MainForm : Form
{
    readonly SwitchEngine _engine;
    readonly string _primary, _backup, _policyPath;
    readonly Action _togglePause;
    readonly Action<ThemeMode> _setTheme;
    readonly Func<ThemeMode> _getTheme;
    readonly Action<EngineStatus> _statusHandler;

    // dashboard: banner
    readonly PictureBox _bannerIcon = new() { Width = 48, Height = 48, SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Ui.Bg, Dock = DockStyle.Fill };
    readonly Label _banner = new() { Dock = DockStyle.Fill, Font = Ui.UiFont(17f, bold: true), TextAlign = ContentAlignment.MiddleLeft, BackColor = Ui.Bg, Text = "Starting..." };
    readonly Label _dryBadge = new() { Text = "Dry-Run", AutoSize = true, BackColor = Ui.AmberFill, ForeColor = Color.White, Font = Ui.UiFont(9f, bold: true), Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 18, 14, 0) };
    Button _pauseBtn = new(), _resumeBtn = new(), _modeBtn = new();
    readonly Label _lockBadge = new() { Text = "Locked", AutoSize = true, BackColor = Ui.BlueFill, ForeColor = Color.White, Font = Ui.UiFont(9f, bold: true), Padding = new Padding(8, 3, 8, 3), Visible = false };
    // dashboard: cards
    readonly AdapterCard _cardP, _cardB;
    readonly Label _stSwitches = new(), _stOnBackup = new(), _stLast = new();
    readonly LatencyChart _chart = new() { Dock = DockStyle.Fill };
    readonly ListView _usage = new();
    readonly System.Windows.Forms.Timer _refresh = new() { Interval = 2000 };
    // watchdog blocks + manual override
    readonly Card _blockedCard = new(Ui.Lock, "Blocked");
    readonly ListView _blockedList = new() { MultiSelect = true };
    readonly NoWheelComboBox _grace = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 172, Margin = new Padding(0, 0, 0, 10) };
    TableLayoutPanel _grid = new();
    // programs you unblocked: countdown until the watchdog watches them again
    readonly Card _allowedCard = new(Ui.Eye, "Allowed For Now");
    readonly ListView _allowedList = new() { MultiSelect = true };
    // the log is a custom-drawn list so each row can be tinted and tagged by what it is about
    sealed record LogEntry(string Time, string Message, LogKind Kind);
    readonly ListBox _log = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 28, IntegralHeight = false, BorderStyle = BorderStyle.None,
        BackColor = Ui.CardBg, HorizontalScrollbar = true, SelectionMode = SelectionMode.MultiExtended };
    int _logWidth = 600;
    const int MaxLogRows = 2000;
    // policy
    readonly TextBox _policy = new();
    readonly List<Label> _policyStatus = new();
    readonly PolicyEditor _editor = new() { Dock = DockStyle.Fill };
    TabHost _tabs = new();
    string _prevTab = "dashboard";
    readonly Dictionary<string, Bitmap> _glyphs = new();
    bool _reallyClose;

    public MainForm(SwitchEngine engine, string primary, string backup, Action togglePause, Action<ThemeMode> setTheme, Func<ThemeMode> getTheme)
    {
        _engine = engine; _primary = primary; _backup = backup; _togglePause = togglePause; _setTheme = setTheme; _getTheme = getTheme;
        _policyPath = Paths.DefaultPolicy;
        Text = "Network Auto Switch"; Width = 1000; Height = 900; MinimumSize = new Size(860, 640);
        StartPosition = FormStartPosition.CenterScreen; Font = Ui.UiFont(9.5f); BackColor = Ui.Bg; ForeColor = Ui.Text;
        Icon = Ui.AppIcon(Ui.Blue);
        _cardP = new AdapterCard(Ui.Wifi, primary); _cardB = new AdapterCard(Ui.Ethernet, backup);
        _ = Handle;

        _tabs.AddPage("dashboard", "Dashboard", Ui.Home, DashboardPage());
        _tabs.AddPage("log", "Log", Ui.Document, LogPage());
        _tabs.AddPage("policy", "Policy", Ui.Settings, PolicyPage());
        _tabs.AddPage("json", "JSON", Ui.Code, JsonPage());
        _tabs.Changed += SyncPolicyViews;
        Controls.Add(_tabs);

        _statusHandler = s => OnUi(() => Apply(s));
        _engine.Status += _statusHandler;
        _refresh.Tick += (_, _) => RefreshUsage();
        _refresh.Start();
        _editor.Connect(() => _engine.Usage.Seen(), (text, error) => Say(text, error));
        LoadLogTail(); LoadPolicy();
        Ui.SkinForm(this);
    }

    void OnUi(Action a) { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }

    /// <summary>Stops listening to the engine, so this window can be discarded (used when the theme changes).</summary>
    public void Detach() { _engine.Status -= _statusHandler; _refresh.Stop(); }

    Bitmap G(string glyph, Color c, int px = 32)
    {
        string key = $"{glyph}{c.ToArgb()}{px}";
        if (!_glyphs.TryGetValue(key, out var b)) _glyphs[key] = b = Ui.Glyph(glyph, px, c);
        return b;
    }

    /// <summary>A page: padding all round, the right edge sits one gap short because cards carry their own right margin.</summary>
    static Panel Page() => new() { BackColor = Ui.Bg, Padding = new Padding(Ui.Pad, Ui.Gap, Ui.Pad - Ui.Gap, Ui.Pad - Ui.Gap) };

    // ---------- Dashboard ----------
    Control DashboardPage()
    {
        var page = Page();
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, BackColor = Ui.Bg };
        _grid = grid;
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));      // blocked: height 0 until something is blocked
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));      // allowed: height 0 until you have unblocked something
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 46));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 54));

        // banner: status icon, status text, dry-run badge, theme menu, pause/resume
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = Ui.Bg, Margin = new Padding(0, 0, Ui.Gap, Ui.Gap) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _dryBadge.Visible = !_engine.Live;
        Ui.Tip.SetToolTip(_dryBadge, "Dry-run: nothing is changed on this PC.");
        _modeBtn = Ui.Btn(Ui.Swap, ModeLabel(_engine.ConnMode), "How the app switches: automatically, or locked to one connection.", () => { });
        _modeBtn.Margin = new Padding(0, 0, 8, 0);
        _modeBtn.Click += (_, _) => ModeMenu().Show(_modeBtn, new Point(0, _modeBtn.Height));
        Ui.Tip.SetToolTip(_lockBadge, "Automatic switching is off. The app stays on the connection you chose.");
        var themeBtn = Ui.Btn(Ui.Brightness, "", "Theme", () => { });
        themeBtn.Click += (_, _) => ThemeMenu().Show(themeBtn, new Point(0, themeBtn.Height));
        _pauseBtn = Ui.Btn(Ui.Pause, "Pause", "Stop switching and put Windows back to normal.", () => { _togglePause(); UpdatePauseButton(); });
        _resumeBtn = Ui.Btn(Ui.Play, "Resume", "Start switching again.", () => { _togglePause(); UpdatePauseButton(); }, BtnKind.Success);
        _resumeBtn.Visible = false;
        // badge, theme button and Pause/Resume share ONE row container, so they are all vertically centered on the same line
        // (the badge is shorter than the buttons, so it gets a little extra top margin to sit in the middle)
        _dryBadge.Margin = new Padding(0, (Ui.BtnH - 26) / 2, 12, 0); _lockBadge.Margin = new Padding(0, (Ui.BtnH - 26) / 2, 12, 0);
        themeBtn.Margin = new Padding(0, 0, 8, 0); _pauseBtn.Margin = _resumeBtn.Margin = new Padding(0);
        var cluster = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, BackColor = Ui.Bg, Margin = new Padding(0), Padding = new Padding(0, (68 - Ui.BtnH) / 2 - Ui.Gap / 2, 0, 0) };
        cluster.Controls.Add(_dryBadge); cluster.Controls.Add(_lockBadge); cluster.Controls.Add(_modeBtn); cluster.Controls.Add(themeBtn); cluster.Controls.Add(_pauseBtn); cluster.Controls.Add(_resumeBtn);
        top.Controls.Add(_bannerIcon, 0, 0); top.Controls.Add(_banner, 1, 0); top.Controls.Add(cluster, 2, 0);

        // adapters
        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Ui.Bg, Margin = new Padding(0) };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cards.Controls.Add(_cardP, 0, 0); cards.Controls.Add(_cardB, 1, 0);
        Ui.Tip.SetToolTip(_cardP, "Main connection"); Ui.Tip.SetToolTip(_cardB, "Backup connection");

        // blocked-now card (override controls)
        Ui.StyleList(_blockedList);
        _blockedList.Columns.Add("Program", 340); _blockedList.Columns.Add("Rule", 120); _blockedList.Columns.Add("Since", 90);
        foreach (var g in new[] { "10 Min", "30 Min", "1 Hour", "3 Hours" }) _grace.Items.Add(g);
        _grace.SelectedIndex = 0;
        Ui.Tip.SetToolTip(_grace, "How long to leave the program alone after you unblock it.");
        var one = Ui.Btn(Ui.Unlock, "Unblock", "Unblock the selected programs.\nBlocks never expire by themselves; you decide.", () => OverrideSelected(all: false), BtnKind.Success);
        var all = Ui.Btn(Ui.Unlock, "Unblock All", "Unblock everything that is blocked.", () => OverrideSelected(all: true));
        one.AutoSize = all.AutoSize = false; one.Width = all.Width = 172; one.Margin = all.Margin = new Padding(0, 0, 0, 10);
        var side = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 188, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Ui.CardBg, Padding = new Padding(Ui.Gap, 0, 0, 0) };
        side.Controls.Add(_grace); side.Controls.Add(one); side.Controls.Add(all);
        _blockedCard.Body.Controls.Add(_blockedList); _blockedCard.Body.Controls.Add(side);
        _blockedCard.SetHint("Stopped by the watchdog because they downloaded too fast on the backup.");

        // allowed-for-now card (countdown)
        Ui.StyleList(_allowedList);
        _allowedList.Columns.Add("Program", 340); _allowedList.Columns.Add("Rule", 120); _allowedList.Columns.Add("Watched Again In", 140);
        var blockNow = Ui.Btn(Ui.Lock, "Block Now", "Block it again immediately.", () => AllowedAction(block: true), BtnKind.Danger);
        var watchAgain = Ui.Btn(Ui.Eye, "Watch Again", "End the allowed time now. It is blocked only if it downloads too fast.", () => AllowedAction(block: false));
        blockNow.AutoSize = watchAgain.AutoSize = false; blockNow.Width = watchAgain.Width = 172; blockNow.Margin = watchAgain.Margin = new Padding(0, 0, 0, 10);
        var aSide = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 188, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Ui.CardBg, Padding = new Padding(Ui.Gap, 0, 0, 0) };
        aSide.Controls.Add(blockNow); aSide.Controls.Add(watchAgain);
        _allowedCard.Body.Controls.Add(_allowedList); _allowedCard.Body.Controls.Add(aSide);
        _allowedCard.SetHint("You unblocked these. The watchdog leaves them alone until the countdown ends.");

        // latency chart
        var chartCard = new Card(Ui.Chart, "Latency");
        chartCard.SetHint("Last ~10 minutes. Red ticks mark failed probes.");
        _chart.BackColor = Ui.CardBg; _chart.ForeColor = Ui.Muted; _chart.Font = Ui.UiFont(8.5f);
        chartCard.Body.Padding = new Padding(Ui.Gap, 0, Ui.Pad, Ui.Gap); chartCard.Body.Controls.Add(_chart);

        // bottom row: session tiles + backup data (two sibling cards)
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Ui.Bg, Margin = new Padding(0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        var statsCard = new Card(Ui.Swap, "Session");
        var tiles = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Ui.CardBg };
        for (int i = 0; i < 3; i++) tiles.RowStyles.Add(new RowStyle(SizeType.Percent, 33.3f));
        tiles.Controls.Add(Tile(Ui.Swap, _stSwitches, "Switches"), 0, 0);
        tiles.Controls.Add(Tile(Ui.Clock, _stOnBackup, "On Backup"), 0, 1);
        tiles.Controls.Add(Tile(Ui.Check, _stLast, "Last Switch"), 0, 2);
        statsCard.Body.Controls.Add(tiles);

        var usageCard = new Card(Ui.Download, "Backup Data");
        usageCard.SetHint("Data received on the backup this session.\nPeak = a program's fastest 10 seconds. Set a rule's trigger between a program's idle value and its download peak.");
        Ui.StyleList(_usage);
        _usage.Columns.Add("Program", 190); _usage.Columns.Add("Total", 100, HorizontalAlignment.Right); _usage.Columns.Add("Peak 10 s", 100, HorizontalAlignment.Right); _usage.Columns.Add("Watch List", 100);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, BackColor = Ui.CardBg, Padding = new Padding(0, Ui.Gap, 0, 0) };
        actions.Controls.Add(Ui.Btn(Ui.Eye, "Add To Watch List", "Make the selected programs a watch rule (saved and applied at once).", AddWatchFromUsage, BtnKind.Primary));
        actions.Controls.Add(Ui.Btn(Ui.Refresh, "Reset", "Reset the counters.", () => { _engine.Usage.Reset(); RefreshUsage(); }));
        usageCard.Body.Controls.Add(_usage); usageCard.Body.Controls.Add(actions);
        usageCard.Margin = new Padding(0, 0, 0, Ui.Gap);
        bottom.Controls.Add(statsCard, 0, 0); bottom.Controls.Add(usageCard, 1, 0);

        grid.Controls.Add(top, 0, 0); grid.Controls.Add(cards, 0, 1); grid.Controls.Add(_blockedCard, 0, 2); grid.Controls.Add(_allowedCard, 0, 3);
        grid.Controls.Add(chartCard, 0, 4); grid.Controls.Add(bottom, 0, 5);
        page.Controls.Add(grid);
        return page;
    }

    string ModeLabel(ConnectionMode m) => m switch { ConnectionMode.StayPrimary => $"Stay On {_primary}", ConnectionMode.StayBackup => $"Stay On {_backup}", _ => "Automatic" };

    /// <summary>Automatic, or locked to one connection (no automatic switching at all).</summary>
    ContextMenuStrip ModeMenu()
    {
        var m = new ContextMenuStrip { Renderer = new ThemedRenderer(), BackColor = Ui.CardBg, ForeColor = Ui.Text, ShowImageMargin = true };
        foreach (var mode in new[] { ConnectionMode.Auto, ConnectionMode.StayPrimary, ConnectionMode.StayBackup })
        {
            var it = new ToolStripMenuItem(ModeLabel(mode)) { Checked = _engine.ConnMode == mode, BackColor = Ui.CardBg, ForeColor = Ui.Text, Padding = new Padding(4, 6, 4, 6) };
            it.Click += (_, _) => { _engine.SetConnectionMode(mode); _modeBtn.Text = Ui.T(ModeLabel(mode)); _lockBadge.Visible = mode != ConnectionMode.Auto; };
            m.Items.Add(it);
        }
        return m;
    }

    ContextMenuStrip ThemeMenu()
    {
        var m = new ContextMenuStrip { Renderer = new ThemedRenderer(), BackColor = Ui.CardBg, ForeColor = Ui.Text, ShowImageMargin = true };
        var cur = _getTheme();
        foreach (var (mode, text) in new[] { (ThemeMode.System, "Follow System"), (ThemeMode.Light, "Light"), (ThemeMode.Dark, "Dark") })
        {
            var it = new ToolStripMenuItem(text) { Checked = cur == mode, BackColor = Ui.CardBg, ForeColor = Ui.Text, Padding = new Padding(4, 6, 4, 6) };
            it.Click += (_, _) => _setTheme(mode);
            m.Items.Add(it);
        }
        return m;
    }

    /// <summary>Icon + big number + caption.</summary>
    static Control Tile(string glyph, Label value, string caption)
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Ui.CardBg };
        var icon = new PictureBox { Image = Ui.Glyph(glyph, 22, Ui.Muted), Location = new Point(0, 12), Size = new Size(30, 30), SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Ui.CardBg };
        value.AutoSize = true; value.Font = Ui.UiFont(18f, bold: true); value.Location = new Point(40, 2); value.Text = "-"; value.BackColor = Ui.CardBg; value.ForeColor = Ui.Text;
        var cap = new Label { Text = caption, AutoSize = true, ForeColor = Ui.Muted, Font = Ui.UiFont(9f), Location = new Point(42, 38), BackColor = Ui.CardBg };
        p.Controls.Add(icon); p.Controls.Add(value); p.Controls.Add(cap);
        return p;
    }

    /// <summary>Shows a status right away (used after a theme change, so the new window is not blank until the next probe).</summary>
    public void ShowStatus(EngineStatus s) => Apply(s);

    void Apply(EngineStatus s)
    {
        bool anyOk = s.Primary.Healthy || s.Backup.Healthy;
        if (s.Paused) { _banner.Text = "Paused"; _banner.ForeColor = Ui.Muted; _bannerIcon.Image = G(Ui.Pause, Ui.Muted); }
        else if (!anyOk) { _banner.Text = "No Connection"; _banner.ForeColor = Ui.Red; _bannerIcon.Image = G(Ui.Warning, Ui.Red); }
        else if (s.Active == Active.Primary) { _banner.Text = _primary; _banner.ForeColor = Ui.Green; _bannerIcon.Image = G(Ui.Wifi, Ui.Green); }
        else { _banner.Text = _backup + "  ·  Backup"; _banner.ForeColor = Ui.Amber; _bannerIcon.Image = G(Ui.Ethernet, Ui.Amber); }
        UpdatePauseButton();
        _lockBadge.Visible = s.Mode != ConnectionMode.Auto; _modeBtn.Text = Ui.T(ModeLabel(s.Mode));

        var st = _engine.Stats;
        Color? inUse = s.Paused || !anyOk ? null : s.Active == Active.Primary ? Ui.Green : Ui.Amber;
        _cardP.Show(s.Primary, StatsTracker.UptimePct(st.ProbesPrimary, st.FailPrimary), st.AvgMs(true), s.Active == Active.Primary ? inUse : null);
        _cardB.Show(s.Backup, StatsTracker.UptimePct(st.ProbesBackup, st.FailBackup), st.AvgMs(false), s.Active == Active.Backup ? inUse : null);
        var (p, b) = st.Snapshot();
        _chart.SetData(p, b, _primary, _backup);

        var onBackup = TimeSpan.FromSeconds(st.SecondsOnBackup);
        _stSwitches.Text = st.Switches.ToString();
        _stOnBackup.Text = onBackup.TotalHours >= 1 ? $"{(int)onBackup.TotalHours} h {onBackup.Minutes} min" : $"{(int)onBackup.TotalMinutes} min";
        _stLast.Text = st.LastSwitchAt is DateTime t ? t.ToString("HH:mm") : "None";
        Ui.Tip.SetToolTip(_stLast, st.LastSwitchAt is null ? "No switch yet." : st.LastSwitchReason);
    }

    void UpdatePauseButton() { bool paused = _engine.Paused; _pauseBtn.Visible = !paused; _resumeBtn.Visible = paused; }

    static readonly int[] GraceMinutes = { 10, 30, 60, 180 };

    void OverrideSelected(bool all)
    {
        var names = (all ? _blockedList.Items.Cast<ListViewItem>() : _blockedList.SelectedItems.Cast<ListViewItem>()).Select(i => (string)i.Tag!).ToList();
        if (names.Count == 0) return;
        int minutes = GraceMinutes[Math.Max(0, _grace.SelectedIndex)];
        Task.Run(() => { foreach (var n in names) _engine.OverrideBlock(n, minutes); OnUi(() => { RefreshBlocked(); RefreshAllowed(); }); });   // runs PowerShell: keep off the UI thread
    }

    void RefreshBlocked()
    {
        var list = _engine.Blocked;
        var selected = _blockedList.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToHashSet();
        _blockedList.BeginUpdate(); _blockedList.Items.Clear();
        foreach (var b in list)
        {
            var since = DateTime.UtcNow - b.Since; if (since < TimeSpan.Zero) since = TimeSpan.Zero;
            string ago = since.TotalHours >= 1 ? $"{(int)since.TotalHours} h {since.Minutes} m" : $"{(int)since.TotalMinutes} min";
            _blockedList.Items.Add(new ListViewItem(new[] { b.Path, b.Rule, ago }) { Tag = b.DisplayName, Selected = selected.Contains(b.DisplayName) });
        }
        _blockedList.EndUpdate();
        _blockedCard.SetTitle(list.Count > 0 ? $"Blocked ({list.Count})" : "Blocked");
        _grid.RowStyles[2].Height = list.Count > 0 ? 190 : 0;   // the card only takes space while something is blocked
    }

    void AllowedAction(bool block)
    {
        var names = _allowedList.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList();
        if (names.Count == 0) return;
        Task.Run(() => { foreach (var n in names) { if (block) _engine.BlockAgain(n); else _engine.WatchAgain(n); } OnUi(() => { RefreshBlocked(); RefreshAllowed(); }); });
    }

    void RefreshAllowed()
    {
        var list = _engine.Allowed;
        var selected = _allowedList.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToHashSet();
        _allowedList.BeginUpdate(); _allowedList.Items.Clear();
        foreach (var a in list)
        {
            var left = a.Until - DateTime.UtcNow; if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            string t = left.TotalHours >= 1 ? $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}" : $"{(int)left.TotalMinutes}:{left.Seconds:00}";
            _allowedList.Items.Add(new ListViewItem(new[] { a.Path, a.Rule, t }) { Tag = a.DisplayName, Selected = selected.Contains(a.DisplayName) });
        }
        _allowedList.EndUpdate();
        _grid.RowStyles[3].Height = list.Count > 0 ? 190 : 0;   // only takes space while something is allowed
    }

    void RefreshUsage()
    {
        RefreshBlocked(); RefreshAllowed();
        var top = _engine.Usage.TopWithPeak(10);
        // this table is rebuilt every 2 s, so remember what is selected (by program name) and select it again
        var selected = _usage.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _usage.BeginUpdate(); _usage.Items.Clear();
        foreach (var (name, bytes, peak) in top)
        {
            string path = _engine.Usage.PathFor(name) ?? name;
            bool watched = _engine.IsWatched(path);
            var item = new ListViewItem(new[] { name, Fmt(bytes), $"{peak / 1e6:0.0} MB", watched ? "Watched" : "" })
            { Tag = name, Selected = selected.Contains(name), UseItemStyleForSubItems = false };
            item.SubItems[3].ForeColor = Ui.Green;
            _usage.Items.Add(item);
        }
        _usage.EndUpdate();
    }

    /// <summary>One click from the "Backup Data" table: the selected programs become a watch rule, saved and applied at once.</summary>
    void AddWatchFromUsage()
    {
        var paths = _usage.SelectedItems.Cast<ListViewItem>().Select(i => _engine.Usage.PathFor((string)i.Tag!)).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
        if (paths.Count == 0) { Ui.Tip.Show("Select a program in the list first.", _usage, 10, 10, 2500); return; }
        ShowWindow("Policy");
        if (_editor.AddPrograms(paths) > 0) SavePolicy();   // validates, saves and applies right away
    }

    static string Fmt(long b) => b >= 1_000_000_000 ? $"{b / 1e9:0.00} GB" : b >= 1_000_000 ? $"{b / 1e6:0.0} MB" : $"{b / 1e3:0.0} KB";

    // ---------- Log ----------
    Control LogPage()
    {
        var page = Page();
        var card = new Card(Ui.Document, "Log") { Margin = new Padding(0, 0, Ui.Gap, Ui.Gap) };
        card.SetHint("Newest line first. Select rows and press Ctrl+C to copy.");
        card.AddHeaderAction(Ui.OpenFile, "Open File", "Open the log file in Notepad (newest first).", OpenLogNewestFirst);
        card.AddHeaderAction(Ui.Refresh, "Reload", "Reload the history from the file.", () => LoadLogTail());
        card.AddHeaderAction(Ui.Clear, "Clear", "Clear this view (the file is kept).", () => _log.Items.Clear());
        card.Body.Padding = new Padding(Ui.Gap - 4, 0, Ui.Gap - 4, Ui.Gap - 4); card.Body.Controls.Add(_log);
        _log.DrawItem += DrawLogRow;
        _log.KeyDown += (_, e) =>   // Ctrl+C copies the selected rows as plain text
        {
            if (e.Control && e.KeyCode == Keys.C && _log.SelectedItems.Count > 0)
                Clipboard.SetText(string.Join("\r\n", _log.SelectedItems.Cast<LogEntry>().Select(x => $"{x.Time}  {x.Message}")));
        };
        page.Controls.Add(card);
        return page;
    }

    /// <summary>All lines of the log file (or the last <paramref name="n"/>), oldest first. Opened shared because the tray is writing to it.</summary>
    static List<string> ReadLines(int n = int.MaxValue)
    {
        var lines = new Queue<string>();
        if (!File.Exists(Paths.LogFile)) return new();
        using var fs = new FileStream(Paths.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        for (string? l; (l = sr.ReadLine()) is not null;) { lines.Enqueue(l); if (lines.Count > n) lines.Dequeue(); }
        return lines.ToList();
    }

    static LogEntry ToEntry(string line)
    {
        var (time, msg) = LogClassifier.Split(line);
        return new LogEntry(time, msg, LogClassifier.Classify(msg));
    }

    /// <summary>The log view shows the NEWEST line at the top and the oldest at the bottom.</summary>
    void LoadLogTail()
    {
        try
        {
            var lines = ReadLines(300); lines.Reverse();
            _log.BeginUpdate(); _log.Items.Clear();
            foreach (var l in lines) { var e = ToEntry(l); _log.Items.Add(e); GrowWidth(e); }
            _log.EndUpdate();
        }
        catch (Exception e) { _log.Items.Clear(); _log.Items.Add(new LogEntry("", "could not read log file: " + e.Message, LogKind.Error)); }
    }

    /// <summary>Called from any thread for every log line. New lines go on top; the view keeps its place if you scrolled down.</summary>
    public void AddLog(string line) => OnUi(() =>
    {
        var e = ToEntry($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}");   // same timestamp format as the file, so old and new lines line up
        int top = _log.TopIndex;
        _log.Items.Insert(0, e);
        if (top > 0) _log.TopIndex = top + 1;
        while (_log.Items.Count > MaxLogRows) _log.Items.RemoveAt(_log.Items.Count - 1);   // drop the OLDEST rows (bottom)
        GrowWidth(e);
    });

    void GrowWidth(LogEntry e)
    {
        int w = 290 + TextRenderer.MeasureText(e.Message, _logFont).Width + 20;
        if (w > _logWidth) { _logWidth = w; _log.HorizontalExtent = w; }
    }

    static readonly Font _logFont = new("Consolas", 9.5f), _tagFont = new("Segoe UI Semibold", 8.5f), _timeFont = new("Consolas", 9f);

    // tint of the whole row, stripe/pill color, and whether the text is dimmed (tints are darker in the dark theme)
    static (Color Tint, Color Accent, bool Dim) Style(LogKind k)
    {
        bool d = Ui.Dark;
        return k switch
        {
            LogKind.Switch  => (d ? Color.FromArgb(58, 46, 24) : Color.FromArgb(255, 244, 225), Ui.AmberFill, false),
            LogKind.Block   => (d ? Color.FromArgb(62, 32, 36) : Color.FromArgb(253, 235, 237), Ui.RedFill, false),
            LogKind.Unblock => (d ? Color.FromArgb(26, 54, 40) : Color.FromArgb(230, 247, 237), Ui.GreenFill, false),
            LogKind.Error   => (d ? Color.FromArgb(84, 28, 36) : Color.FromArgb(251, 220, 224), Color.FromArgb(160, 20, 35), false),
            LogKind.Info    => (d ? Color.FromArgb(28, 42, 70) : Color.FromArgb(234, 241, 254), Ui.BlueFill, false),
            LogKind.Command => (Ui.CardBg, Ui.Border, true),
            _               => (Ui.CardBg, Ui.Border, false),
        };
    }

    void DrawLogRow(object? sender, DrawItemEventArgs ev)
    {
        if (ev.Index < 0 || ev.Index >= _log.Items.Count) return;
        var e = (LogEntry)_log.Items[ev.Index];
        var (tint, accent, dim) = Style(e.Kind);
        var g = ev.Graphics; var r = ev.Bounds;
        bool selected = (ev.State & DrawItemState.Selected) != 0;

        using (var back = new SolidBrush(selected ? (Ui.Dark ? Color.FromArgb(52, 72, 112) : Color.FromArgb(215, 227, 252)) : tint)) g.FillRectangle(back, r);
        using (var stripe = new SolidBrush(accent)) g.FillRectangle(stripe, r.X, r.Y, 4, r.Height);
        using (var line = new Pen(Ui.Dark ? Color.FromArgb(44, 48, 58) : Color.FromArgb(235, 236, 240))) g.DrawLine(line, r.X, r.Bottom - 1, r.Right, r.Bottom - 1);

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        int x = r.X + 16;
        string time = e.Time.Length >= 19 ? e.Time[11..19] : e.Time;      // the date is in the file; the row shows the clock time
        TextRenderer.DrawText(g, time, _timeFont, new Rectangle(x, r.Y, 72, r.Height), Ui.Muted, flags);
        x += 80;

        string tag = LogClassifier.Tag(e.Kind);
        if (tag.Length > 0)
        {
            tag = Ui.T(tag.ToLowerInvariant());     // Title Case tags: Switch, Block, Unblock, Error, Info
            var sz = TextRenderer.MeasureText(g, tag, _tagFont, new Size(200, 20), TextFormatFlags.NoPadding);
            var pill = new Rectangle(x, r.Y + (r.Height - 18) / 2, sz.Width + 16, 18);
            using (var pb = new SolidBrush(accent)) g.FillRectangle(pb, pill);
            TextRenderer.DrawText(g, tag, _tagFont, pill, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        x += 92;   // fixed column so messages line up whether or not the row has a tag

        TextRenderer.DrawText(g, e.Message, _logFont, new Rectangle(x, r.Y, 100000, r.Height),
            dim ? Ui.Muted : e.Kind == LogKind.Error ? (Ui.Dark ? Color.FromArgb(255, 140, 150) : Color.FromArgb(160, 20, 35)) : Ui.Text, flags);
    }

    /// <summary>The file on disk stays oldest-first (the normal way log files are written); this opens a newest-first copy of it.</summary>
    static void OpenLogNewestFirst()
    {
        try
        {
            var all = ReadLines(); all.Reverse();
            string tmp = Path.Combine(Path.GetTempPath(), "NetworkAutoSwitch-log-newest-first.txt");
            File.WriteAllLines(tmp, all);
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{tmp}\"") { UseShellExecute = true });
        }
        catch { }
    }

    // ---------- Policy (rules editor) and JSON: two pages, each with its own Save / Revert bar ----------
    Control PolicyPage()
    {
        var page = Page();
        page.Controls.Add(_editor);
        page.Controls.Add(ActionBar());
        return page;
    }

    Control JsonPage()
    {
        var page = Page();
        _policy.Multiline = true; _policy.ScrollBars = ScrollBars.Both; _policy.Dock = DockStyle.Fill; _policy.WordWrap = false;
        _policy.Font = new Font("Consolas", 10f); _policy.AcceptsTab = true; _policy.BorderStyle = BorderStyle.None;
        var card = new Card(Ui.Code, "policy.json") { Margin = new Padding(0, 0, Ui.Gap, Ui.Gap) };
        card.SetHint("The same rules as the Policy page, as text. Saving checks it first.");
        card.Body.Padding = new Padding(Ui.Gap, 0, Ui.Gap, Ui.Gap - 4); card.Body.Controls.Add(_policy);
        page.Controls.Add(card);
        page.Controls.Add(ActionBar());
        return page;
    }

    Control ActionBar()
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(0, Ui.Gap, 0, 0), BackColor = Ui.Bg };
        var status = new Label { AutoSize = true, Padding = new Padding(8, 9, 0, 0), ForeColor = Ui.Muted, BackColor = Ui.Bg };
        _policyStatus.Add(status);
        bar.Controls.Add(Ui.Btn(Ui.Save, "Save", "Check the rules, save them and apply them now.", SavePolicy, BtnKind.Primary));
        bar.Controls.Add(Ui.Btn(Ui.Undo, "Revert", "Discard unsaved changes.", LoadPolicy));
        bar.Controls.Add(status);
        return bar;
    }

    /// <summary>Keeps the Policy and JSON views in step when you switch between them (unsaved edits carry over).</summary>
    void SyncPolicyViews()
    {
        string now = _tabs.SelectedKey;
        try
        {
            if (_prevTab == "policy" && now == "json") _policy.Text = BackupPolicy.ToJson(_editor.Build()).Replace("\r\n", "\n").Replace("\n", "\r\n");
            else if (_prevTab == "json" && now == "policy") _editor.SetPolicy(BackupPolicy.Parse(_policy.Text));
            _policy.SelectionStart = 0; _policy.SelectionLength = 0;
        }
        catch (InvalidDataException e) { Say("Couldn't carry your edits across: " + e.Message, true); }
        _prevTab = now;
    }

    void LoadPolicy()
    {
        try
        {
            _policy.Text = File.ReadAllText(_policyPath).Replace("\r\n", "\n").Replace("\n", "\r\n");
            _policy.SelectionStart = 0; _policy.SelectionLength = 0;   // never open fully selected: one keystroke would wipe it
            _editor.SetPolicy(BackupPolicy.Parse(_policy.Text));
            Say("", false);
        }
        catch (Exception e) { Say("Could not read policy: " + e.Message, true); }
    }

    void SavePolicy()
    {
        string json;
        try
        {
            // validate first: a typo or an incomplete rule must never reach the running engine
            json = _tabs.SelectedKey == "json" ? _policy.Text : BackupPolicy.ToJson(_editor.Build());
            BackupPolicy.Parse(json);
        }
        catch (InvalidDataException e) { Say("Not saved. " + e.Message, true); return; }
        try
        {
            if (File.Exists(_policyPath)) File.Copy(_policyPath, _policyPath + ".bak", true);
            File.WriteAllText(_policyPath, json);
            LoadPolicy();                       // refresh both views from what was really saved
            Say("Saving...", false);
            Task.Run(() =>
            {
                try { _engine.ReloadPolicy(); OnUi(() => Say("Saved and applied.", false, ok: true)); }
                catch (Exception e) { OnUi(() => Say("Saved, but applying failed: " + e.Message, true)); }
            });
        }
        catch (Exception e) { Say("Could not save: " + e.Message, true); }
    }

    void Say(string text, bool error, bool ok = false)
    {
        foreach (var l in _policyStatus) { l.Text = text; l.ForeColor = error ? Ui.Red : ok ? Ui.Green : Ui.Muted; }
    }

    // ---------- window behaviour ----------
    public void ShowWindow(string? tab = null)
    {
        if (tab is not null) { _tabs.Select(tab.ToLowerInvariant(), silent: false); }
        Show(); WindowState = FormWindowState.Normal; Activate(); BringToFront();
    }

    public string CurrentTab => _tabs.SelectedKey;

    /// <summary>Renders the window (every tab) to PNG files from inside the app, so it works even when
    /// the window is hidden behind a game. Used only by --snapshot for checking the layout.</summary>
    public void SaveSnapshots(string folder)
    {
        Directory.CreateDirectory(folder);
        // sample rows so the Backup Data table can be checked (this dry-run copy only; nothing real is touched)
        _engine.Usage.Add(@"C:\Program Files (x86)\Steam\steam.exe", 52_500_000); _engine.Usage.Add(@"C:\Program Files\HoYoPlay\1.19.0.382\HYPWorker.exe", 3_200_000);
        _engine.Usage.Add(@"D:\Tools\newtool.exe", 800_000); _engine.Usage.Add(@"C:\Program Files\Mozilla Firefox\firefox.exe", 400_000);
        RefreshUsage(); if (_usage.Items.Count > 1) { _usage.Items[1].Selected = true; _usage.Items[2].Selected = true; }
        foreach (var key in _tabs.Keys())
        {
            _tabs.Select(key); Application.DoEvents();
            using var bmp = new Bitmap(Width, Height);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            bmp.Save(Path.Combine(folder, key + ".png"));
        }
        // the program picker, rendered off-screen (never shown) with sample "seen on the backup" data
        using var pk = new ProgramPicker(new[] { new SeenProgram("steam.exe", @"C:\Program Files (x86)\Steam\steam.exe", 52_500_000), new SeenProgram("HYPWorker.exe", @"C:\Program Files\HoYoPlay\1.19.0.382\HYPWorker.exe", 3_200_000) });
        pk.StartPosition = FormStartPosition.Manual; pk.Location = new Point(-32000, -32000); pk.Opacity = 0;   // invisible and off-screen: nothing flashes on the user's desktop
        pk.Show(); Application.DoEvents();
        using var pb = new Bitmap(pk.Width, pk.Height);
        pk.DrawToBitmap(pb, new Rectangle(0, 0, pk.Width, pk.Height));
        pb.Save(Path.Combine(folder, "picker.png"));
        pk.Close();
    }

    public void CloseForReal() { _reallyClose = true; Detach(); Close(); }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }   // X hides to tray
        base.OnFormClosing(e);
    }
}

/// <summary>Menu look that follows the theme (used by the theme menu and the tray menu).</summary>
sealed class ThemedRenderer : ToolStripProfessionalRenderer
{
    sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Ui.CardBg;
        public override Color ImageMarginGradientBegin => Ui.CardBg;
        public override Color ImageMarginGradientMiddle => Ui.CardBg;
        public override Color ImageMarginGradientEnd => Ui.CardBg;
        public override Color MenuBorder => Ui.Border;
        public override Color MenuItemBorder => Ui.Hover;
        public override Color MenuItemSelected => Ui.Hover;
        public override Color MenuItemSelectedGradientBegin => Ui.Hover;
        public override Color MenuItemSelectedGradientEnd => Ui.Hover;
        public override Color SeparatorDark => Ui.Border;
        public override Color SeparatorLight => Ui.Border;
    }
    public ThemedRenderer() : base(new Colors()) { RoundedEdges = false; }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.Enabled ? Ui.Text : Ui.Muted; base.OnRenderItemText(e); }
}
