using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

/// <summary>The app window: Dashboard (live status, latency, stats, backup data, blocks), Log (live, newest first), Policy (rules editor) and Settings.
/// Every card sits directly on its page (cards are never nested). Closing the window hides it to the tray; Exit lives in the tray menu.</summary>
sealed class MainForm : Form
{
    readonly SwitchEngine _engine;
    readonly string _policyPath;
    string _primary => _engine.PrimaryName;                                              // always the adapters detected right now (they can change while the app runs)
    string _backup => _engine.BackupName.Length > 0 ? _engine.BackupName : "No Backup";
    readonly Action _togglePause;
    readonly Action<ThemeMode> _setTheme;
    readonly Func<ThemeMode> _getTheme;
    readonly Action<EngineStatus> _statusHandler;
    readonly Action _adaptersHandler;

    // dashboard: banner
    readonly PictureBox _bannerIcon = new() { Width = 48, Height = 48, SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Ui.Bg, Dock = DockStyle.Fill };
    readonly Label _banner = new() { Dock = DockStyle.Fill, Font = Ui.UiFont(17f, bold: true), TextAlign = ContentAlignment.MiddleLeft, BackColor = Ui.Bg, Text = "Starting..." };
    readonly Label _dryBadge = new() { Text = "Dry-Run", AutoSize = true, BackColor = Ui.AmberFill, ForeColor = Color.White, Font = Ui.UiFont(9f, bold: true), Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 18, 14, 0) };
    Button _pauseBtn = new(), _resumeBtn = new(), _modeBtn = new();
    readonly Label _sameBadge = new() { Text = "SAME NETWORK", AutoSize = true, BackColor = Ui.Amber, ForeColor = Color.Black, Font = Ui.UiFont(9f, bold: true), Padding = new Padding(8, 3, 8, 3), Visible = false };
    readonly Label _phoneMainBadge = new() { Text = "MAIN IS PHONE", AutoSize = true, BackColor = Ui.Amber, ForeColor = Color.Black, Font = Ui.UiFont(9f, bold: true), Padding = new Padding(8, 3, 8, 3), Visible = false };
    readonly Label _vpnBadge = new() { Text = "VPN", AutoSize = true, BackColor = Ui.BlueFill, ForeColor = Color.White, Font = Ui.UiFont(9f, bold: true), Padding = new Padding(8, 3, 8, 3), Visible = false };
    // dashboard: cards
    readonly AdapterCard _cardP, _cardB;
    readonly Label _stSwitches = new(), _stOnBackup = new(), _stLast = new(), _stData = new();
    readonly LatencyChart _chart = new() { Dock = DockStyle.Fill };
    readonly ChartLegend _legend = new() { BackColor = Ui.CardBg, ForeColor = Ui.Muted, Font = Ui.UiFont(9.5f) };
    readonly ListView _usage = new();                  // Activity page: the real table (sort, select, actions)
    readonly ListView _usageView = new(), _blockedView = new();      // Dashboard: read-only copies
    readonly Card _blockedViewCard = new(Ui.Lock, "Blocked");
    readonly Label _blockedViewEmpty = new() { Dock = DockStyle.Fill, Text = "Nothing is blocked.", ForeColor = Ui.Muted, BackColor = Ui.CardBg, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0) };
    readonly System.Windows.Forms.Timer _refresh = new() { Interval = 2000 };
    // watchdog blocks + manual override
    readonly Card _blockedCard = new(Ui.Lock, "Blocked");
    readonly ListView _blockedList = new() { MultiSelect = true };
    readonly NoWheelComboBox _grace = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 172, Margin = new Padding(0, 0, 0, 10) };
    TableLayoutPanel _grid = new();
    const float ChartPct = 27, BottomPct = 73;                        // the two stretching rows (percent of what is left)
    const int BlockedRowHeight = 198, DashboardMinHeight = 700;      // below this the page scrolls instead of squeezing (fits a 1366x768 / 125 % screen)
    Control[] _sessionTiles = Array.Empty<Control>();
    readonly TableLayoutPanel _tilesGrid = new() { Dock = DockStyle.Fill, BackColor = Ui.CardBg };
    readonly TableLayoutPanel _bottom = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Ui.Bg, Margin = new Padding(0) };
    readonly Label _blockedEmpty = new() { Dock = DockStyle.Fill, Text = "Nothing is blocked. Programs the watchdog stops show up here.", ForeColor = Ui.Muted, BackColor = Ui.CardBg, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0) };
    FlowLayoutPanel? _blockedSide;
    Button? _unblockBtn, _unblockAllBtn, _blockNowBtn, _watchAgainBtn;
    string _sortKey = "total"; bool _sortFlip;
    static readonly string[] UsageKeys = { "program", "total", "backup", "trigger", "watch" };      // the sort key of each Network Activity column, left to right
    ScrollSurface? _dashPage;
    readonly WheelRouter _wheel = new();
    readonly Panel _warnBar = new() { Dock = DockStyle.Fill, Padding = new Padding(16, 0, 8, 0), Visible = false, Margin = new Padding(0, 0, Ui.Gap, Ui.Gap) };
    readonly Label _warnText = new() { Dock = DockStyle.Fill, ForeColor = Color.White, BackColor = Color.Transparent, Font = Ui.UiFont(10f, bold: true), TextAlign = ContentAlignment.MiddleLeft };
    // programs you unblocked: countdown until the watchdog watches them again
    // the log is a custom-drawn list so each row can be tinted and tagged by what it is about
    sealed record LogEntry(string Time, string Message, LogKind Kind);
    readonly ListBox _log = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 28, IntegralHeight = false, BorderStyle = BorderStyle.None,
        BackColor = Ui.CardBg, HorizontalScrollbar = true, SelectionMode = SelectionMode.MultiExtended };
    int _logWidth = 600;
    int _maxLogRows = Math.Clamp(AppSettings.Load().LogLines, 100, 20000);      // lines kept on screen (Settings → Log)
    // policy and settings
    readonly List<Label> _policyStatus = new();
    readonly GlobalTriggerCard _global = new() { Margin = new Padding(0, 0, Ui.Gap, Ui.Gap) };    // lives on the Settings page; the rules editor on the Policy page reads it
    readonly PolicyEditor _editor;
    readonly NoWheelNumeric _recover = new() { Minimum = 3, Maximum = 600, Increment = 5, Width = 84 };
    readonly NoWheelNumeric _cap = new() { Minimum = 0, Maximum = 1_000_000, Increment = 100, Width = 96 }, _backupCheck = new() { Minimum = 1, Maximum = 60, Increment = 1, Width = 68 };
    readonly NoWheelNumeric _logLines = new() { Minimum = 100, Maximum = 20000, Increment = 100, Width = 96 };
    readonly CheckBox _startup = new() { Text = "Start When I Sign In", AutoSize = true, BackColor = Ui.CardBg };
    bool _startupBusy;
    readonly CheckBox _mute = new() { Text = "Mute Notifications", AutoSize = true, BackColor = Ui.CardBg };
    readonly Func<bool> _getMute; readonly Action<bool> _setMute;
    // loading placeholders (shown while the engine changes the network, so old values never show under new names)
    readonly Action<bool> _busyHandler;
    readonly System.Windows.Forms.Timer _loadingTimeout = new() { Interval = 12000 };
    readonly Skeleton _bannerSkeleton = new(new[] { (0.5f, 22) }, 0, 8) { Dock = DockStyle.Fill, Visible = false, Surface = Ui.Bg };
    Card _chartCard = null!;
    bool _loading, _awaitStatus;
    TabHost _tabs = new();
    readonly Dictionary<string, Bitmap> _glyphs = new();
    bool _reallyClose;

    public MainForm(SwitchEngine engine, Action togglePause, Action<ThemeMode> setTheme, Func<ThemeMode> getTheme, Func<bool> getMute, Action<bool> setMute)
    {
        _engine = engine; _togglePause = togglePause; _setTheme = setTheme; _getTheme = getTheme; _getMute = getMute; _setMute = setMute;
        _editor = new PolicyEditor(_global) { Dock = DockStyle.Fill };
        _policyPath = Paths.DefaultPolicy;
        Text = $"Network Auto Switch {AppInfo.Version}"; Width = 1000; Height = 1000; MinimumSize = new Size(860, 640);
        // open no bigger than the screen (minus the taskbar), so the whole dashboard is visible without scrolling
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1000, 1000);
        Width = Math.Min(Width, wa.Width); Height = Math.Max(MinimumSize.Height, Math.Min(Height, wa.Height));
        StartPosition = FormStartPosition.CenterScreen; Font = Ui.UiFont(9.5f); BackColor = Ui.Bg; ForeColor = Ui.Text;
        Icon = Ui.AppIcon(Ui.Blue);
        _cardP = new AdapterCard(Ui.AdapterGlyph(_engine.PrimaryKind), "Main"); _cardB = new AdapterCard(Ui.AdapterGlyph(_engine.BackupKind), "Backup");
        _ = Handle;

        _tabs.AddPage("dashboard", "Dashboard", Ui.Home, DashboardPage());
        _tabs.AddPage("activity", "Activity", Ui.List, ActivityPage());
        _tabs.AddPage("networks", "Networks", Ui.Wifi, NetworksPage());
        _tabs.AddPage("log", "Log", Ui.Document, LogPage());
        _tabs.AddPage("policy", "Policy", Ui.Shield, PolicyPage());
        _tabs.AddPage("settings", "Settings", Ui.Settings, SettingsPage());
        Controls.Add(_tabs);

        _statusHandler = s => OnUi(() => Apply(s));
        _engine.Status += _statusHandler;
        _adaptersHandler = () => OnUi(OnAdaptersChanged);
        _engine.AdaptersChanged += _adaptersHandler;
        _busyHandler = busy => OnUi(() => OnBusy(busy));
        _engine.Busy += _busyHandler;
        _loadingTimeout.Tick += (_, _) => SetLoading(false);       // never leave placeholders up for good, whatever happens
        _refresh.Tick += (_, _) => RefreshUsage();
        _refresh.Start();
        Application.AddMessageFilter(_wheel);
        _editor.Connect(() => _engine.Usage.Seen(), (text, error) => Say(text, error));
        LoadLogTail(); LoadPolicy();
        Ui.SkinForm(this);
        OnAdaptersChanged();           // names, icons and network names straight away
    }

    void OnUi(Action a) { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }

    /// <summary>Stops listening to the engine, so this window can be discarded (used when the theme changes).</summary>
    protected override void Dispose(bool disposing) { if (disposing) Application.RemoveMessageFilter(_wheel); base.Dispose(disposing); }

    public void Detach() { Application.RemoveMessageFilter(_wheel); _engine.Status -= _statusHandler; _engine.AdaptersChanged -= _adaptersHandler; _engine.Busy -= _busyHandler; _refresh.Stop(); _loadingTimeout.Stop(); }

    // ---------- loading placeholders ----------
    static readonly (float W, int H)[] ChartBars = { (1f, 14), (0.7f, 14), (0.9f, 14), (0.55f, 14) };

    /// <summary>The cards that show the connections get grey placeholder bars while the engine changes things.</summary>
    void SetLoading(bool on)
    {
        _loading = on; _loadingTimeout.Stop();
        _cardP.SetLoading(on, AdapterCard.Bars); _cardB.SetLoading(on, AdapterCard.Bars); _chartCard.SetLoading(on, ChartBars);
        _ = _bannerSkeleton.Handle; _banner.Visible = !on; _bannerSkeleton.Visible = on; if (on) _loadingTimeout.Start(); else _awaitStatus = false;
    }

    /// <summary>The engine started (true) or finished (false) changing the network. When it finishes, the placeholders stay until the next
    /// measurement arrives, so the old adapter's latency is never shown under the new adapter's name.</summary>
    void OnBusy(bool busy)
    {
        if (busy) SetLoading(true);
        else if (_loading) _awaitStatus = true;
    }

    public void BeginLoading() => SetLoading(true);

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
        var page = new ScrollSurface();                                       // no scrollbar: the wheel, a touchpad and PageUp / PageDown scroll it
        var pad = Page().Padding; page.Content.Padding = pad;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Ui.Bg };
        _grid = grid;
        _dashPage = page; FitDashboardHeight();      // a small window scrolls instead of squeezing the cards
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));                 // 0  status
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));                  // 1  warning banner: height 0 until the main connection is a backup network (or both are one network)
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 182));                // 2  the two connection cards
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, ChartPct));            // 3  latency
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, BottomPct));           // 4  Session (a quarter of the width, both rows high), Network Activity above Blocked

        // banner: status icon, status text, badges (dry-run, VPN, locked), mode, pause/resume
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = Ui.Bg, Margin = new Padding(0, 0, Ui.Gap, Ui.Gap) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _dryBadge.Visible = !_engine.Live;
        Ui.Tip.SetToolTip(_dryBadge, "Dry-run: nothing is changed on this PC.");
        _modeBtn = Ui.Btn(Ui.Swap, ModeButtonLabel(_engine.ConnMode), "How the app switches: automatically, or locked to one connection.", () => { });
        _modeBtn.Margin = new Padding(0, 0, 8, 0);
        _modeBtn.Click += (_, _) => ModeMenu().Show(_modeBtn, new Point(0, _modeBtn.Height));
        _pauseBtn = Ui.Btn(Ui.Pause, "Pause", "Stop switching and put Windows back to normal.", () => { _togglePause(); UpdatePauseButton(); });
        _resumeBtn = Ui.Btn(Ui.Play, "Resume", "Start switching again.", () => { _togglePause(); UpdatePauseButton(); }, BtnKind.Success);
        _resumeBtn.Visible = false;
        // badge, theme button and Pause/Resume share ONE row container, so they are all vertically centered on the same line
        // (the badge is shorter than the buttons, so it gets a little extra top margin to sit in the middle)
        _dryBadge.Margin = new Padding(0, (Ui.BtnH - 26) / 2, 12, 0); _vpnBadge.Margin = new Padding(0, (Ui.BtnH - 26) / 2, 12, 0); _sameBadge.Margin = _vpnBadge.Margin; _phoneMainBadge.Margin = _vpnBadge.Margin;
        Ui.Tip.SetToolTip(_phoneMainBadge, "The main connection is on a network you marked as your phone / limited data, so it gets the backup's protections: heavy downloads on your watch list are blocked and its data counts toward the daily limit. Change it in Choose Connections.");
        Ui.Tip.SetToolTip(_sameBadge, "The main and the backup connection are on the same network (same router), so if that network fails, both fail and switching cannot help. Connect one of them to a different network, for example your phone's hotspot.");
        Ui.Tip.SetToolTip(_vpnBadge, "A VPN (such as Cloudflare WARP) refuses the internet check on a single connection. The app checks the connection in use through the VPN and the other one by its gateway, so it cannot confirm the other connection has internet before switching to it. For exact checks, exclude 8.8.8.8 and 9.9.9.9 from the VPN (in WARP: Preferences, Advanced, Split Tunnels, Exclude IPs).");
        _pauseBtn.Margin = _resumeBtn.Margin = new Padding(0);
        var cluster = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, BackColor = Ui.Bg, Margin = new Padding(0), Padding = new Padding(0, (68 - Ui.BtnH) / 2 - Ui.Gap / 2, 0, 0) };
        _updateBtn = Ui.Btn(Ui.Download, "Update", "A newer version is available. Click to download, check and install it.", () => InstallUpdate?.Invoke(), BtnKind.Primary);
        _updateBtn.Margin = new Padding(0, 0, 8, 0); _updateBtn.Visible = false;
        cluster.Controls.Add(_updateBtn); cluster.Controls.Add(_dryBadge); cluster.Controls.Add(_sameBadge); cluster.Controls.Add(_phoneMainBadge); cluster.Controls.Add(_vpnBadge); cluster.Controls.Add(_modeBtn); cluster.Controls.Add(_pauseBtn); cluster.Controls.Add(_resumeBtn);
        top.Controls.Add(_bannerIcon, 0, 0); top.Controls.Add(_banner, 1, 0); top.Controls.Add(_bannerSkeleton, 1, 0); top.Controls.Add(cluster, 2, 0);

        // adapters
        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Ui.Bg, Margin = new Padding(0) };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cards.Controls.Add(_cardP, 0, 0); cards.Controls.Add(_cardB, 1, 0);
        Ui.Tip.SetToolTip(_cardP, "Main connection"); Ui.Tip.SetToolTip(_cardB, "Backup connection");

        // latency chart
        var chartCard = _chartCard = new Card(Ui.Chart, "Latency");
        chartCard.SetHint("Last ~10 minutes. Red ticks mark failed probes.");
        chartCard.AddHeaderControl(_legend);
        _chart.BackColor = Ui.CardBg; _chart.ForeColor = Ui.Muted; _chart.Font = Ui.UiFont(8.5f);
        chartCard.Body.Padding = new Padding(Ui.Gap, 0, Ui.Pad, Ui.Gap); chartCard.Body.Controls.Add(_chart);

        // Session is a narrow card as high as the two cards beside it; Network Activity is above Blocked (which is always there)
        var statsCard = new Card(Ui.Swap, "Session");
        _sessionTiles = new[] { Tile(Ui.Swap, _stSwitches, "Switches"), Tile(Ui.Clock, _stOnBackup, "On Backup"), Tile(Ui.Check, _stLast, "Last Switch"), Tile(Ui.Download, _stData, "Backup Data Today") };
        statsCard.Body.Controls.Add(_tilesGrid);
        LayoutTiles();
        _bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25)); _bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 75));
        _bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); _bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, BlockedRowHeight));

        // read-only copies of the two tables: a diagnostic view. The actions (sorting, watch list, unblock) are on the Activity page.
        Ui.StyleList(_usageView);
        _usageView.ShowItemToolTips = true;
        _usageView.Columns.Add("Program", 196); _usageView.Columns.Add("Total", 104, HorizontalAlignment.Right); _usageView.Columns.Add("On Backup", 140, HorizontalAlignment.Right);
        _usageView.Columns.Add("Trigger", 108, HorizontalAlignment.Right); _usageView.Columns.Add("Watch List", 112);
        FitUsageColumns(_usageView);
        _usageView.SelectedIndexChanged += (_, _) => _usageView.SelectedIndices.Clear();
        var usageCard = new Card(Ui.Download, "Network Activity");
        usageCard.SetHint("What each program moved today (download plus upload), and how much of it went through the backup. Sort it, add programs to the watch list or unblock them on the Activity page.");
        usageCard.Body.Controls.Add(_usageView);

        Ui.StyleList(_blockedView);
        _blockedView.Columns.Add("Program", 330); _blockedView.Columns.Add("Rule", 120); _blockedView.Columns.Add("Status", 170);
        _blockedView.SelectedIndexChanged += (_, _) => _blockedView.SelectedIndices.Clear();
        FitFirstColumn(_blockedView);
        _blockedViewCard.SetHint("Programs the watchdog stopped (Blocked) and programs you let through for a while (Allowed). Unblock or block them again on the Activity page.");
        _blockedViewCard.Body.Controls.Add(_blockedView); _blockedViewCard.Body.Controls.Add(_blockedViewEmpty);

        var fixBtn = Ui.Btn(Ui.Wifi, "Networks", "Open the Networks page: sort your networks, or pick the adapters.", () => ChooseConnections());
        fixBtn.Dock = DockStyle.Right; fixBtn.Margin = new Padding(0);
        _warnBar.Controls.Add(_warnText); _warnBar.Controls.Add(fixBtn);      // docked: the button first (right), then the text fills the rest
        grid.Controls.Add(top, 0, 0); grid.Controls.Add(_warnBar, 0, 1); grid.Controls.Add(cards, 0, 2); grid.Controls.Add(chartCard, 0, 3);
        _bottom.Controls.Add(statsCard, 0, 0); _bottom.SetRowSpan(statsCard, 2);
        _bottom.Controls.Add(usageCard, 1, 0); _bottom.Controls.Add(_blockedViewCard, 1, 1);
        grid.Controls.Add(_bottom, 0, 4);
        page.Content.Controls.Add(grid);
        return page;
    }

    /// <summary>The adapters changed (a phone was plugged in, an adapter was renamed or you chose others): card titles and icons follow.</summary>
    void OnAdaptersChanged()
    {
        _cardP.SetIcon(Ui.AdapterGlyph(_engine.PrimaryKind)); _cardB.SetIcon(Ui.AdapterGlyph(_engine.BackupKind));
        _cardP.SetIdentity(_engine.PrimaryNetwork ?? "", _primary); _cardB.SetIdentity(_engine.BackupNetwork ?? "", _backup);
        _modeBtn.Text = Ui.T(ModeButtonLabel(_engine.ConnMode));
    }

    ConnectionsPanel? _networks;

    /// <summary>The Networks page: the Main and Backup network tables, and the adapters under Advanced.</summary>
    Control NetworksPage()
    {
        var page = Page();
        _networks = new ConnectionsPanel(_engine) { Dock = DockStyle.Fill };
        page.Controls.Add(_networks);
        return page;
    }

    /// <summary>Opens the Networks page (from the mode menu, the warning banner and the tray menu).</summary>
    public void ChooseConnections() => ShowWindow("Networks");

    /// <summary>The button next to the status is short (so the status text keeps its room); the menu entries below it name the network.</summary>
    static string ModeButtonLabel(ConnectionMode m) => m switch { ConnectionMode.StayPrimary => "Stay On Main", ConnectionMode.StayBackup => "Stay On Backup", _ => "Automatic" };

    string ModeLabel(ConnectionMode m) => m switch { ConnectionMode.StayPrimary => $"Stay On {Ui.Role("Main", _engine.PrimaryNetwork, _primary)}", ConnectionMode.StayBackup => $"Stay On {Ui.Role("Backup", _engine.BackupNetwork, _backup)}", _ => "Automatic" };

    /// <summary>Automatic, or locked to one connection (no automatic switching at all).</summary>
    ContextMenuStrip ModeMenu()
    {
        var m = new ContextMenuStrip { Renderer = new ThemedRenderer(), BackColor = Ui.CardBg, ForeColor = Ui.Text, ShowImageMargin = true };
        foreach (var mode in new[] { ConnectionMode.Auto, ConnectionMode.StayPrimary, ConnectionMode.StayBackup })
        {
            var it = new ToolStripMenuItem(ModeLabel(mode)) { Checked = _engine.ConnMode == mode, BackColor = Ui.CardBg, ForeColor = Ui.Text, Padding = new Padding(4, 6, 4, 6) };
            it.Click += (_, _) => { _engine.SetConnectionMode(mode); _modeBtn.Text = Ui.T(ModeButtonLabel(mode)); };
            m.Items.Add(it);
        }
        m.Items.Add(new ToolStripSeparator());
        var choose = new ToolStripMenuItem("Networks...", Ui.Glyph(Ui.Wifi, 16, Ui.Text)) { BackColor = Ui.CardBg, ForeColor = Ui.Text, Padding = new Padding(4, 6, 4, 6) };
        choose.Click += (_, _) => ChooseConnections();
        m.Items.Add(choose);
        return m;
    }

    /// <summary>Session tiles: one above the other, sharing the card's height equally (each is centered in its slot).</summary>
    void LayoutTiles()
    {
        _tilesGrid.SuspendLayout();
        _tilesGrid.Controls.Clear(); _tilesGrid.ColumnStyles.Clear(); _tilesGrid.RowStyles.Clear();
        _tilesGrid.ColumnCount = 1; _tilesGrid.RowCount = _sessionTiles.Length;
        _tilesGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < _sessionTiles.Length; i++) { _tilesGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / _sessionTiles.Length)); _tilesGrid.Controls.Add(_sessionTiles[i], 0, i); }
        _tilesGrid.ResumeLayout();
    }

    /// <summary>The least height the dashboard needs (a smaller window scrolls instead of squeezing the cards).</summary>
    void FitDashboardHeight()
    {
        if (_dashPage is null) return;
        _dashPage.MinContentHeight = DashboardMinHeight;
    }

    /// <summary>Blocked is always on the dashboard: the list while something is blocked or allowed for now, a short note while nothing is.</summary>
    void ShowBlockedList(int count)
    {
        _blockedList.Visible = count > 0; _blockedEmpty.Visible = count == 0;
        _blockedView.Visible = count > 0; _blockedViewEmpty.Visible = count == 0;
        UpdateBlockedButtons();
    }

    /// <summary>Each button works only on what it can act on: Unblock / Unblock All on blocked rows, Block Now / Watch Again on allowed rows.</summary>
    void UpdateBlockedButtons()
    {
        if (_unblockBtn is null) return;
        var sel = _blockedList.SelectedItems.Cast<ListViewItem>().ToList();
        bool anyBlocked = _blockedList.Items.Cast<ListViewItem>().Any(i => i.Name == "blocked");
        _unblockBtn.Enabled = sel.Any(i => i.Name == "blocked");
        _unblockAllBtn!.Enabled = anyBlocked;
        _blockNowBtn!.Enabled = _watchAgainBtn!.Enabled = sel.Any(i => i.Name == "allowed");
    }

    /// <summary>Icon + big number + caption.</summary>
    static Control Tile(string glyph, Label value, string caption)
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Ui.CardBg, Margin = Padding.Empty };
        var icon = new PictureBox { Image = Ui.GlyphBold(glyph, 20, Ui.Text), Size = new Size(22, 30), SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Ui.CardBg };
        value.AutoSize = true; value.Font = Ui.UiFont(15f, bold: true); value.Text = "-"; value.BackColor = Ui.CardBg; value.ForeColor = Ui.Text; value.Margin = Padding.Empty; value.Padding = Padding.Empty;
        var cap = new Label { Text = caption, AutoSize = false, AutoEllipsis = true, ForeColor = Ui.Muted, Font = Ui.UiFont(9f), BackColor = Ui.CardBg, Margin = Padding.Empty, Padding = Padding.Empty };
        bool placing = false;
        // Everything is placed from the real sizes of the text (whatever the font size or display scaling is), so nothing overlaps or is cut off:
        // the number shrinks until it fits next to the icon, the caption sits right under it, and the pair is centered in the slot.
        void Place()
        {
            if (placing || p.Width < 80) return;
            placing = true;
            try
            {
                const int left = 30;              // the same distance as the card title from the card icon
                int room = Math.Max(30, p.Width - left - 8);
                float pt = 15f;
                for (; pt > 9f; pt -= 0.5f)
                {
                    using var f = Ui.UiFont(pt, bold: true);
                    if (TextRenderer.MeasureText(value.Text, f, new Size(int.MaxValue, 200), TextFormatFlags.NoPadding).Width <= room) break;
                }
                if (Math.Abs(value.Font.Size - pt) > 0.01f) { var old = value.Font; value.Font = Ui.UiFont(pt, bold: true); old.Dispose(); }
                var vs = value.GetPreferredSize(Size.Empty); int capH = TextRenderer.MeasureText("Ag", cap.Font).Height; cap.Size = new Size(Math.Max(20, p.Width - left - 10), capH); var cs = cap.Size;
                int block = vs.Height + cs.Height, top = Math.Max(0, (p.Height - block) / 2);
                value.Location = new Point(left, top); cap.Location = new Point(left, top + vs.Height);
                icon.Location = new Point(0, top + (block - icon.Height) / 2);
            }
            finally { placing = false; }
        }
        p.Controls.Add(icon); p.Controls.Add(value); p.Controls.Add(cap);
        p.Resize += (_, _) => Place();
        value.TextChanged += (_, _) => Place();
        Place();
        return p;
    }

    /// <summary>Shows a status right away (used after a theme change, so the new window is not blank until the next probe).</summary>
    public void ShowStatus(EngineStatus s) => Apply(s);

    /// <summary>The banner under the status row: shown while the main connection is protected like a backup, or both connections are one network.
    /// Red when it is (probably) your phone, amber when it is only "not sorted yet" or "not identified yet".</summary>
    // ---------- updates (the tray does the checking and installing; the window only shows it) ----------
    Button? _updateBtn, _updateNowBtn; Label? _updStatus; UpdateResult? _update;
    /// <summary>Set by the tray: install the update that was found.</summary>
    public Action? InstallUpdate;
    /// <summary>Raised when "Check For Updates" found out the latest version, so the tray can remember it.</summary>
    public Action<UpdateResult>? UpdateChecked;

    /// <summary>A newer version is known (or no longer is): the dashboard shows an Update button and Settings an Update Now button.</summary>
    public void SetUpdate(UpdateResult? u)
    {
        _update = u is { State: UpdateState.Available } ? u : null;
        bool on = _update is not null;
        if (_updateBtn is not null) { _updateBtn.Visible = on; if (on) _updateBtn.Text = "  Update to " + _update!.Latest; }
        if (_updateNowBtn is not null) _updateNowBtn.Visible = on;
        if (_updStatus is not null && on) { _updStatus.Text = _update!.Message; _updStatus.ForeColor = Ui.Amber; }
    }

    /// <summary>Progress or an error from the installer download.</summary>
    public void SetUpdateProgress(string text, bool error)
    {
        if (_updStatus is not null) { _updStatus.Text = text; _updStatus.ForeColor = error ? Ui.Red : Ui.Muted; }
        bool busy = !error && text.Length > 0;
        if (_updateBtn is not null) _updateBtn.Enabled = !busy;
        if (_updateNowBtn is not null) _updateNowBtn.Enabled = !busy;
    }

    void UpdateWarning()
    {
        string net = _engine.PrimaryNetwork ?? "";
        string? text = null; bool red = true;
        if (_engine.Paused) { }
        else if (_engine.SameNetworkWarning) text = $"Both connections are on the same network{(net.Length > 0 ? $" ({net})" : "")}. No backup if it fails.";
        else if (_engine.MainProtected)
        {
            if (net.Length == 0) { text = "Windows is still identifying the main connection. Protected like a backup until then."; red = false; }
            else if (_engine.RoleOf(net) is NetworkRole.Backup or NetworkRole.Barred) text = $"Main connection is on {net}, a backup network. Mobile data may be in use.";
            else { text = $"Main connection is on {net}, which is not sorted yet. Treated as a backup."; red = false; }
        }
        SetWarning(text, red);
    }

    void SetWarning(string? text, bool red)
    {
        _warnText.Text = text ?? ""; _warnBar.BackColor = red ? Ui.RedFill : Ui.AmberFill; _warnBar.Visible = text is not null;
        _grid.RowStyles[1].Height = text is null ? 0 : 48;
        _grid.RowStyles[3].Height = text is null ? ChartPct : ChartPct - 5; _grid.RowStyles[4].Height = text is null ? BottomPct : BottomPct + 5;      // the banner takes its height from the chart, so the table does not get squeezed
    }

    /// <summary>The label at the top: "On Main: SNcial_5G" (the adapter name when the network name is not known).</summary>
    static string Net(string? network, string adapter) => string.IsNullOrEmpty(network) ? adapter : network;
    static string OnLabel(string role, string? net, string adapter) => $"On {role}: {(string.IsNullOrEmpty(net) ? adapter : net)}";

    void Apply(EngineStatus s)
    {
        _cardP.SetIdentity(_engine.PrimaryNetwork ?? "", _primary); _cardB.SetIdentity(_engine.BackupNetwork ?? "", _backup);   // names are refreshed by the engine in the background
        bool anyOk = s.Primary.Healthy || s.Backup.Healthy;
        if (s.Paused) { _banner.Text = "Paused"; _banner.ForeColor = Ui.Muted; _bannerIcon.Image = G(Ui.Pause, Ui.Muted); }
        else if (!anyOk) { _banner.Text = "No Connection"; _banner.ForeColor = Ui.Red; _bannerIcon.Image = G(Ui.Warning, Ui.Red); }
        else if (s.Active == Active.Primary) { _banner.Text = OnLabel("Main", _engine.PrimaryNetwork, _primary); _banner.ForeColor = Ui.Green; _bannerIcon.Image = G(Ui.AdapterGlyph(_engine.PrimaryKind), Ui.Green); }
        else { _banner.Text = OnLabel("Backup", _engine.BackupNetwork, _backup); _banner.ForeColor = Ui.Amber; _bannerIcon.Image = G(Ui.AdapterGlyph(_engine.BackupKind), Ui.Amber); }
        UpdatePauseButton();
        _vpnBadge.Visible = s.Degraded;
        _sameBadge.Visible = _engine.SameNetworkWarning;
        _phoneMainBadge.Visible = _engine.MainProtected && !_engine.SameNetworkWarning;
        UpdateWarning();
        _modeBtn.Text = Ui.T(ModeButtonLabel(s.Mode));

        var st = _engine.Stats;
        Color? inUse = s.Paused || !anyOk ? null : s.Active == Active.Primary ? Ui.Green : Ui.Amber;
        _cardP.Show(s.Primary, StatsTracker.UptimePct(st.ProbesPrimary, st.FailPrimary), st.AvgMs(true), s.Active == Active.Primary ? inUse : null, selected: !s.Paused && s.Active == Active.Primary);
        _cardB.Show(s.Backup, StatsTracker.UptimePct(st.ProbesBackup, st.FailBackup), st.AvgMs(false), s.Active == Active.Backup ? inUse : null, selected: !s.Paused && s.Active == Active.Backup);
        var (p, b) = st.Snapshot();
        _chart.SetData(p, b); _legend.Set(Net(_engine.PrimaryNetwork, _primary), Net(_engine.BackupNetwork, _backup));      // the networks, not the adapters (the adapter only while the network is not known)
        if (_awaitStatus) SetLoading(false);       // the first measurement after a change has arrived

        var onBackup = TimeSpan.FromSeconds(st.SecondsOnBackup);
        _stSwitches.Text = st.Switches.ToString();
        _stOnBackup.Text = onBackup.TotalHours >= 1 ? $"{(int)onBackup.TotalHours} h {onBackup.Minutes} min" : $"{(int)onBackup.TotalMinutes} min";
        _stLast.Text = st.LastSwitchAt is DateTime t ? t.ToString("HH:mm") : "None";
        long today = _engine.BackupBytesToday; long capMb = _engine.DailyCapMB;
        _stData.Text = capMb > 0 ? $"{Fmt(today)} / {(capMb >= 1000 ? $"{capMb / 1000.0:0.#} GB" : $"{capMb} MB")}" : Fmt(today);
        _stData.ForeColor = capMb > 0 && DataBudget.LevelFor(today, capMb * 1_000_000L) == BudgetLevel.Reached ? Ui.Red : capMb > 0 && DataBudget.LevelFor(today, capMb * 1_000_000L) == BudgetLevel.Warning ? Ui.Amber : Ui.Text;
        Ui.Tip.SetToolTip(_stData, "Everything that went through the backup connection today (all programs). The count starts again at midnight." + (_engine.MainProtected ? $" The main connection counts too while it is protected ({_engine.MainProtectedReason})." : "") + (capMb > 0 ? $" Your daily limit is {capMb} MB." : " Set a daily limit in Settings."));
        Ui.Tip.SetToolTip(_stLast, st.LastSwitchAt is null ? "No switch yet." : st.LastSwitchReason);
    }

    void UpdatePauseButton() { bool paused = _engine.Paused; _pauseBtn.Visible = !paused; _resumeBtn.Visible = paused; }

    static readonly int[] GraceMinutes = { 10, 30, 60, 180 };

    void OverrideSelected(bool all)
    {
        var names = (all ? _blockedList.Items.Cast<ListViewItem>() : _blockedList.SelectedItems.Cast<ListViewItem>()).Where(i => i.Name == "blocked").Select(i => (string)i.Tag!).ToList();
        if (names.Count == 0) return;
        int minutes = GraceMinutes[Math.Max(0, _grace.SelectedIndex)];
        Task.Run(() => { foreach (var n in names) _engine.OverrideBlock(n, minutes); OnUi(RefreshBlocked); });   // runs PowerShell: keep off the UI thread
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowScrollBar(IntPtr hWnd, int bar, bool show);
    static bool DemoBlocked = Environment.GetCommandLineArgs().Contains("--demo-blocked");

    void RefreshBlocked()
    {
        var list = _engine.Blocked; var allowed = _engine.Allowed;
        var selected = _blockedList.SelectedItems.Cast<ListViewItem>().Select(i => i.Name + "|" + (string)i.Tag!).ToHashSet();
        _blockedList.BeginUpdate(); _blockedList.Items.Clear();
        foreach (var b in list)
        {
            var since = DateTime.UtcNow - b.Since; if (since < TimeSpan.Zero) since = TimeSpan.Zero;
            string ago = since.TotalHours >= 1 ? $"{(int)since.TotalHours} h {since.Minutes} m" : $"{(int)since.TotalMinutes} min";
            _blockedList.Items.Add(new ListViewItem(new[] { b.Path, b.Rule, "Blocked " + ago }) { Name = "blocked", Tag = b.DisplayName, Selected = selected.Contains("blocked|" + b.DisplayName) });
        }
        foreach (var a in allowed)
        {
            var left = a.Until - DateTime.UtcNow; if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            string t = left.TotalHours >= 1 ? $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}" : $"{(int)left.TotalMinutes}:{left.Seconds:00}";
            _blockedList.Items.Add(new ListViewItem(new[] { a.Path, a.Rule, "Allowed, watched in " + t }) { Name = "allowed", Tag = a.DisplayName, Selected = selected.Contains("allowed|" + a.DisplayName) });
        }
        int shown = list.Count + allowed.Count, blocked = list.Count;
        if (DemoBlocked && !_engine.Live)      // dev only (--dry-run --demo-blocked): sample rows to see the card
        {
            _blockedList.Items.Add(new ListViewItem(new[] { @"C:\Program Files (x86)\Steam\steam.exe", "Steam", "Blocked 3 min" }) { Name = "blocked", Tag = "demo-steam" });
            _blockedList.Items.Add(new ListViewItem(new[] { @"D:\Games\Endfield\Endfield.exe", "Endfield", "Blocked 12 min" }) { Name = "blocked", Tag = "demo-endfield" });
            _blockedList.Items.Add(new ListViewItem(new[] { @"C:\Program Files\HoYoPlay\HYPWorker.exe", "HoYoPlay", "Allowed, watched in 9:42" }) { Name = "allowed", Tag = "demo-hyp" });
            shown += 3; blocked += 2;
        }
        _blockedList.EndUpdate();
        _blockedView.BeginUpdate(); _blockedView.Items.Clear();
        foreach (ListViewItem it in _blockedList.Items) { var c = (ListViewItem)it.Clone(); c.Selected = false; _blockedView.Items.Add(c); }
        _blockedView.EndUpdate();
        _blockedCard.SetTitle(blocked > 0 ? $"Blocked ({blocked})" : "Blocked"); _blockedViewCard.SetTitle(blocked > 0 ? $"Blocked ({blocked})" : "Blocked");
        ShowBlockedList(shown);
    }

    void AllowedAction(bool block)
    {
        var names = _blockedList.SelectedItems.Cast<ListViewItem>().Where(i => i.Name == "allowed").Select(i => (string)i.Tag!).ToList();
        if (names.Count == 0) return;
        Task.Run(() => { foreach (var n in names) { if (block) _engine.BlockAgain(n); else _engine.WatchAgain(n); } OnUi(RefreshBlocked); });
    }

    /// <summary>What the sort button at the end of a Network Activity column header shows: 0 idle, 1 ascending, 2 descending.</summary>
    int UsageSortState(int column)
    {
        if (column < 0 || column >= UsageKeys.Length) return -1;
        if (_sortKey != UsageKeys[column]) return 0;
        bool desc = _sortKey != "program"; if (_sortFlip) desc = !desc;
        return desc ? 2 : 1;
    }

    /// <summary>A click on a column header presses its sort button: a new column sorts by it, the same column again flips ascending and descending.</summary>
    void SortByColumn(int column)
    {
        if (column < 0 || column >= UsageKeys.Length) return;
        if (_sortKey == UsageKeys[column]) _sortFlip = !_sortFlip; else { _sortKey = UsageKeys[column]; _sortFlip = false; }
        Ui.RedrawHeader(_usage);
        RefreshUsage();
    }

    void RefreshUsage()
    {
        RefreshBlocked();
        var top = UsageSort.Apply(_engine.Usage.Rows(200), _sortKey, _sortFlip, r => _engine.IsWatched(_engine.Usage.PathFor(r.Name) ?? r.Name));
        long triggerBytes = (long)(_engine.TriggerMb * 1_000_000);
        // this table is rebuilt every 2 s, so remember what is selected (by program name) and select it again
        var selected = _usage.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _usage.BeginUpdate(); _usage.Items.Clear();
        foreach (var r in top)
        {
            string path = _engine.Usage.PathFor(r.Name) ?? r.Name;
            bool watched = _engine.IsWatched(path);
            var item = new ListViewItem(new[] { r.Name, Fmt(r.Total), r.BackupBytes > 0 ? Fmt(r.BackupBytes) : "", $"{r.PeakDown / 1e6:0.0} MB", watched ? "Watched" : "" })
            { Tag = r.Name, Selected = selected.Contains(r.Name), UseItemStyleForSubItems = false,
              ToolTipText = $"{r.Name}\nDown {Fmt(r.Down)}  \u00b7  Up {Fmt(r.Up)}" + (r.On.Length > 0 ? $"\nSeen on: {r.On}" : "") };
            item.SubItems[2].ForeColor = Ui.Amber;                                                 // data that went through your phone stands out
            item.SubItems[3].ForeColor = r.PeakDown >= triggerBytes ? Ui.Amber : Ui.Text;           // above the watch trigger: this is what would get a watched program blocked
            item.SubItems[4].ForeColor = Ui.Green;
            _usage.Items.Add(item);
        }
        _usage.EndUpdate();
        _usageView.BeginUpdate(); _usageView.Items.Clear();
        foreach (ListViewItem it in _usage.Items) { var c = (ListViewItem)it.Clone(); c.Selected = false; _usageView.Items.Add(c); }
        _usageView.EndUpdate();
    }

    /// <summary>One click from the "Network Activity" table: the selected programs become a watch rule, saved and applied at once.</summary>
    void AddWatchFromUsage()
    {
        var paths = _usage.SelectedItems.Cast<ListViewItem>().Select(i => _engine.Usage.PathFor((string)i.Tag!)).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
        if (paths.Count == 0) { Ui.Tip.Show("Select a program in the list first.", _usage, 10, 10, 2500); return; }
        ShowWindow("Policy");
        if (_editor.AddPrograms(paths) > 0) SavePolicy();   // validates, saves and applies right away
    }

    static string Fmt(long b) => b >= 1_000_000_000 ? $"{b / 1e9:0.00} GB" : b >= 1_000_000 ? $"{b / 1e6:0.0} MB" : $"{b / 1e3:0.0} KB";

    // ---------- Activity: the full tables with their actions ----------
    Control ActivityPage()
    {
        var page = Page();
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Ui.Bg };
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 58)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        var usageCard = new Card(Ui.Download, "Network Activity");
        usageCard.SetHint("Total = what each program downloaded plus uploaded today, on the main connection and the backup (kept across restarts, starts again at midnight). Hover a row for the split. Click a column header, or its small button, to sort; click again to reverse.\nOn Backup = how much of that went through your backup (your phone's data).\nTrigger = the most it downloaded in any 10 seconds today. It is measured the same way as your watch trigger (set in Settings): amber means it went above it, so a watched program like this is blocked on the backup. Set a rule's trigger between a program's quiet value and its download value.");
        Ui.StyleList(_usage, UsageSortState);
        _usage.ColumnClick += (_, e) => SortByColumn(e.Column);
        _usage.ShowItemToolTips = true;
        _usage.Columns.Add("Program", 196); _usage.Columns.Add("Total", 104, HorizontalAlignment.Right); _usage.Columns.Add("On Backup", 140, HorizontalAlignment.Right);
        _usage.Columns.Add("Trigger", 108, HorizontalAlignment.Right); _usage.Columns.Add("Watch List", 112);
        // the number columns keep their size; the program name gives way first in a narrow window, and Watch List takes what is left (so nothing is pushed out of view)
        FitUsageColumns(_usage);
        // the actions live in the card's header, right of the title (the first one added is the rightmost)
        usageCard.AddHeaderAction(Ui.Refresh, "Reset", "Reset the counters.", () => { _engine.Usage.Reset(); RefreshUsage(); });
        usageCard.AddHeaderAction(Ui.Eye, "Add To Watch List", "Make the selected programs a watch rule (saved and applied at once).", AddWatchFromUsage, BtnKind.Primary);
        usageCard.Body.Controls.Add(_usage);

        // one table for what is blocked and what you let through for now (Status says which)
        Ui.StyleList(_blockedList);
        _blockedList.Columns.Add("Program", 330); _blockedList.Columns.Add("Rule", 120); _blockedList.Columns.Add("Status", 170);
        foreach (var g in new[] { "10 Min", "30 Min", "1 Hour", "3 Hours" }) _grace.Items.Add(g);
        _grace.SelectedIndex = 0;
        Ui.Tip.SetToolTip(_grace, "How long to leave the program alone after you unblock it.");
        _unblockBtn = Ui.Btn(Ui.Unlock, "Unblock", "Unblock the selected blocked programs. The watchdog leaves them alone for the time on the left.\nBlocks never expire by themselves; you decide.", () => OverrideSelected(all: false), BtnKind.Success);
        _unblockAllBtn = Ui.Btn(Ui.Unlock, "Unblock All", "Unblock everything that is blocked.", () => OverrideSelected(all: true));
        _blockNowBtn = Ui.Btn(Ui.Lock, "Block", "Block the selected allowed programs again immediately.", () => AllowedAction(block: true), BtnKind.Danger);
        _watchAgainBtn = Ui.Btn(Ui.Eye, "Watch", "End the allowed time of the selected programs now. They are blocked only if they download too fast.", () => AllowedAction(block: false));
        foreach (var bt in new[] { _unblockBtn, _unblockAllBtn, _blockNowBtn, _watchAgainBtn }) { bt.AutoSize = true; bt.Margin = new Padding(0, 0, 6, 0); }
        _grace.Width = 112; _grace.Margin = new Padding(0, 2, 6, 0);
        var side = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Ui.CardBg, Padding = new Padding(0, 8, 0, 0) };
        side.Controls.Add(_grace); side.Controls.Add(_unblockBtn); side.Controls.Add(_unblockAllBtn); side.Controls.Add(_blockNowBtn); side.Controls.Add(_watchAgainBtn);
        _blockedSide = side;
        _blockedList.SelectedIndexChanged += (_, _) => UpdateBlockedButtons();
        FitFirstColumn(_blockedList);
        _blockedCard.Body.Controls.Add(_blockedList); _blockedCard.Body.Controls.Add(_blockedEmpty); _blockedCard.Body.Controls.Add(side);
        ShowBlockedList(0);
        _blockedCard.SetHint("Programs the watchdog stopped because they downloaded too fast on a protected connection (Status: Blocked). Blocks never expire by themselves: you decide.\nPrograms you unblocked stay here with a countdown (Status: Allowed): the watchdog leaves them alone until it ends. Block (again) or Watch act on those.");

        grid.Controls.Add(usageCard, 0, 0); grid.Controls.Add(_blockedCard, 0, 1);
        page.Controls.Add(grid);
        return page;
    }

    /// <summary>The number columns keep their size, the program name gives way first in a narrow window, and the last column takes what is left,
    /// so the header never shows an empty (white) strip when a divider is dragged and there is no horizontal bar.</summary>
    /// <summary>The first column (the program path) takes what the other columns leave, so the table never scrolls sideways.</summary>
    static void FitFirstColumn(ListView lv)
    {
        void Fit()
        {
            int w = lv.ClientSize.Width; if (w < 200) return;
            int others = 0; for (int i = 1; i < lv.Columns.Count; i++) others += lv.Columns[i].Width;
            int first = Math.Max(120, w - others);
            if (lv.Columns[0].Width != first) lv.Columns[0].Width = first;
            ShowScrollBar(lv.Handle, 0, false);
        }
        lv.Resize += (_, _) => Fit();
    }

    static void FitUsageColumns(ListView lv)
    {
        bool fitting = false;
        void FillLast()
        {
            int w = lv.ClientSize.Width; if (w < 300 || fitting) return;
            fitting = true;
            try
            {
                int others = 0; for (int i = 0; i < 4; i++) others += lv.Columns[i].Width;
                int last = Math.Max(110, w - others);
                if (lv.Columns[4].Width != last) lv.Columns[4].Width = last;
                ShowScrollBar(lv.Handle, 0, false);      // an exact fit can make Windows add a horizontal bar: never wanted here
            }
            finally { fitting = false; }
        }
        lv.Resize += (_, _) =>
        {
            int w = lv.ClientSize.Width; if (w < 300) return;
            int numbers = lv.Columns[1].Width + lv.Columns[2].Width + lv.Columns[3].Width;
            fitting = true; lv.Columns[0].Width = Math.Max(100, w - numbers - 110); fitting = false;
            FillLast();
        };
        lv.ColumnWidthChanged += (_, _) => FillLast();
    }

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
            var lines = ReadLines(Math.Min(300, _maxLogRows)); lines.Reverse();
            _log.BeginUpdate(); _log.Items.Clear();
            foreach (var l in lines) { var e = ToEntry(l); _log.Items.Add(e); GrowWidth(e); }
            _log.EndUpdate();
        }
        catch (Exception e) { _log.Items.Clear(); _log.Items.Add(new LogEntry("", "could not read log file: " + e.Message, LogKind.Error)); }
    }

    /// <summary>The setting changed: remember it, and shorten what is on screen right away if it is now too long.</summary>
    void SetLogLines(int n)
    {
        n = Math.Clamp(n, 100, 20000);
        if (n == _maxLogRows) return;
        _maxLogRows = n; AppSettings.Update(x => x.LogLines = n); TrimLog();
    }

    /// <summary>Drops the OLDEST rows (the bottom of the list) beyond the limit set in Settings.</summary>
    void TrimLog() { while (_log.Items.Count > _maxLogRows) _log.Items.RemoveAt(_log.Items.Count - 1); }

    /// <summary>Called from any thread for every log line. New lines go on top; the view keeps its place if you scrolled down.</summary>
    public void AddLog(string line) => OnUi(() =>
    {
        var e = ToEntry($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}");   // same timestamp format as the file, so old and new lines line up
        int top = _log.TopIndex;
        _log.Items.Insert(0, e);
        if (top > 0) _log.TopIndex = top + 1;
        TrimLog();
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

    // ---------- Policy (the rules) ----------
    Control PolicyPage()
    {
        var page = Page();
        page.Controls.Add(_editor);
        page.Controls.Add(ActionBar(jsonButton: true));
        return page;
    }

    /// <summary>Save / Revert (and, on the Policy page, "Edit As JSON"). Saving writes the whole policy: the rules AND the global trigger.</summary>
    Control ActionBar(Action? beforeSave = null, bool jsonButton = false)
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(0, Ui.Gap, 0, 0), BackColor = Ui.Bg };
        var status = new Label { AutoSize = true, Padding = new Padding(8, 9, 0, 0), ForeColor = Ui.Muted, BackColor = Ui.Bg };
        _policyStatus.Add(status);
        bar.Controls.Add(Ui.Btn(Ui.Save, "Save", "Check everything, save it and apply it now.", () => { beforeSave?.Invoke(); SavePolicy(); }, BtnKind.Primary));
        bar.Controls.Add(Ui.Btn(Ui.Undo, "Revert", "Discard unsaved changes.", () => { _recover.Value = _engine.RecoverSeconds; _cap.Value = _engine.DailyCapMB; _backupCheck.Value = _engine.BackupCheckSeconds; _logLines.Value = _maxLogRows; LoadPolicy(); }));
        if (jsonButton) bar.Controls.Add(Ui.Btn(Ui.Code, "Edit As JSON", "Edit the same rules as text (for advanced use). Nothing is saved until you click Save.", EditJson));
        bar.Controls.Add(status);
        return bar;
    }

    /// <summary>The rules as text, in a dialog. The text is checked before it is accepted, and only changes the editor: Save still decides.</summary>
    void EditJson()
    {
        string json;
        try { json = BackupPolicy.ToJson(_editor.Build()); }
        catch (InvalidDataException e) { Say("Fix this first: " + e.Message, true); return; }
        using var dlg = new JsonDialog(json);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { _editor.SetPolicy(BackupPolicy.Parse(dlg.Json)); Say("JSON applied here. Click Save to keep it.", false); }
        catch (InvalidDataException e) { Say(e.Message, true); }
    }

    void LoadPolicy()
    {
        try { _editor.SetPolicy(BackupPolicy.Parse(File.ReadAllText(_policyPath))); Say("", false); }
        catch (Exception e) { Say("Could not read policy: " + e.Message, true); }
    }

    void SavePolicy()
    {
        string json;
        try
        {
            // validate first: a typo or an incomplete rule must never reach the running engine
            json = BackupPolicy.ToJson(_editor.Build());
            BackupPolicy.Parse(json);
        }
        catch (InvalidDataException e) { Say("Not saved. " + e.Message, true); return; }
        try
        {
            if (File.Exists(_policyPath)) File.Copy(_policyPath, _policyPath + ".bak", true);
            File.WriteAllText(_policyPath, json);
            LoadPolicy();                       // show what was really saved
            Say("Saving...", false);
            Task.Run(() =>
            {
                try { _engine.ReloadPolicy(); OnUi(() => Say("Saved and applied.", false, ok: true)); }
                catch (Exception e) { OnUi(() => Say("Saved, but applying failed: " + e.Message, true)); }
            });
        }
        catch (Exception e) { Say("Could not save: " + e.Message, true); }
    }

    // ---------- Settings ----------
    Control SettingsPage()
    {
        var page = Page();
        // the cards are stacked at fixed heights inside a panel that scrolls, so a small window can still reach every card
        int[] heights = { 124, 124, 100, 110, 164, 110, 124, 164 };
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = heights.Length, BackColor = Ui.Bg, Height = heights.Sum() };
        foreach (int h in heights) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, h));

        // appearance: applies at once (the window is rebuilt in the new colors)
        var appearance = new Card(Ui.Brightness, "Appearance");
        appearance.SetHint("Follow System switches with Windows' own light/dark setting.");
        var cur = _getTheme();
        var themes = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Ui.CardBg };
        foreach (var (mode, text, glyph) in new[] { (ThemeMode.System, "Follow System", Ui.Monitor), (ThemeMode.Light, "Light", Ui.Brightness), (ThemeMode.Dark, "Dark", Ui.Moon) })
            themes.Controls.Add(Ui.Btn(glyph, text, $"Use the {text.ToLowerInvariant()} look.", () => { if (mode != _getTheme()) _setTheme(mode); }, mode == cur ? BtnKind.Primary : BtnKind.Secondary));
        appearance.Body.Controls.Add(themes);

        // how long the main connection must be good before moving back
        var switching = new Card(Ui.Swap, "Switching Back");
        switching.SetHint("How long the main connection must work, without a single failed check, before the app moves back from the backup.\nHigher = fewer back-and-forth switches if your Wi-Fi flickers.\nGoing TO the backup is not affected: that still takes only a few seconds.");
        _recover.Value = Math.Min(_recover.Maximum, Math.Max(_recover.Minimum, _engine.RecoverSeconds));
        Label L(string t) => new() { Text = t, AutoSize = true, Padding = new Padding(2, 6, 8, 0), ForeColor = Ui.Muted, BackColor = Ui.CardBg };
        var sw = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Ui.CardBg };
        sw.Controls.AddRange(new Control[] { L("Move back to the main connection after"), _recover, L("s of good connection") });
        switching.Body.Controls.Add(sw);

        // notifications: applies at once
        var notes = new Card(Ui.Mute, "Notifications");
        notes.SetHint("Hides the tray pop-ups (switching, blocked programs, errors).\nEverything is still written to the Log, and the Dashboard shows the current state.");
        _mute.Checked = _getMute();
        _mute.CheckedChanged += (_, _) => _setMute(_mute.Checked);
        var nflow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Ui.CardBg, Padding = new Padding(0, 4, 0, 0) };
        nflow.Controls.Add(_mute);
        notes.Body.Controls.Add(nflow);

        // start when I sign in: the same scheduled task the installer creates; applies at once
        var startup = new Card(Ui.Play, "Startup");
        startup.SetHint("Starts the app by itself 30 seconds after you sign in, already running as administrator (no prompt). The delay gives Wi-Fi time to connect, so a slow sign-in is not mistaken for an outage.\nIt is the same setting as the installer's Start when I sign in option.");
        _startup.Checked = StartupTask.IsEnabled();
        _startup.CheckedChanged += (_, _) => SetStartup(_startup.Checked);
        var sflow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Ui.CardBg, Padding = new Padding(0, 4, 0, 0) };
        sflow.Controls.Add(_startup);
        startup.Body.Controls.Add(sflow);

        // data on the backup (a phone tether is usually on a mobile plan)
        var data = new Card(Ui.Download, "Backup Data");
        data.SetHint("Daily limit: you are told when the backup has carried 80 % of it today, and when it is reached. The count is everything that goes through the backup adapter and starts again at midnight. 0 = no limit.\nIdle backup check: while the main connection is fine, the backup is checked this often. Every check uses a little mobile data; going to the backup stays just as fast.");
        _cap.Value = Math.Min(_cap.Maximum, Math.Max(_cap.Minimum, _engine.DailyCapMB));
        _backupCheck.Value = Math.Min(_backupCheck.Maximum, Math.Max(_backupCheck.Minimum, _engine.BackupCheckSeconds));
        var dflow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, BackColor = Ui.CardBg };
        dflow.Controls.AddRange(new Control[] { L("Daily Limit"), _cap, L("MB  (0 = no limit)") });
        dflow.SetFlowBreak(dflow.Controls[2], true);
        dflow.Controls.AddRange(new Control[] { L("Check The Idle Backup Every"), _backupCheck, L("s") });
        data.Body.Controls.Add(dflow);

        // the log page keeps this many lines on screen
        var logCard = new Card(Ui.Document, "Log");
        logCard.SetHint("How many lines the Log page keeps on screen. When it is full, the oldest lines drop off the bottom, so the newest are always there. The log file itself is not shortened (it starts a fresh file at 1 MB and keeps the previous one as log.txt.old).");
        _logLines.Value = Math.Min(_logLines.Maximum, Math.Max(_logLines.Minimum, _maxLogRows));
        var lflow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Ui.CardBg };
        lflow.Controls.AddRange(new Control[] { L("Keep The Newest"), _logLines, L("Lines On Screen") });
        logCard.Body.Controls.Add(lflow);

        // about: the version, and a way to ask GitHub whether a newer one exists (only when you click; the app never checks by itself)
        var about = new Card(Ui.Info, "About");
        about.SetHint("Looks for a newer release on GitHub when the app starts and once a day (turn it off below). Only GitHub's public release information is read; nothing about you is sent. A new version is never installed without your click on Update Now: the installer is checked against the release's SHA-256 file, closes the app the normal way (your network settings are put back) and starts the new version.");
        var updStatus = _updStatus = new Label { AutoSize = true, ForeColor = Ui.Muted, BackColor = Ui.CardBg, Padding = new Padding(8, 8, 0, 0) };
        string releaseUrl = UpdateCheck.ReleasesPage;
        var openRelease = Ui.Btn(Ui.OpenFile, "Open Release Page", "Open the release page in your browser.", () => { try { Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true }); } catch { } }, BtnKind.Primary);
        openRelease.Visible = false;
        _updateNowBtn = Ui.Btn(Ui.Download, "Update Now", "Download this version, check it and install it. The app restarts.", () => InstallUpdate?.Invoke(), BtnKind.Primary);
        _updateNowBtn.Visible = _update is { State: UpdateState.Available };
        Button? checkBtn = null;
        checkBtn = Ui.Btn(Ui.Refresh, "Check For Updates", "Ask GitHub whether a newer version exists.", async () =>
        {
            checkBtn!.Enabled = false; updStatus.ForeColor = Ui.Muted; updStatus.Text = "Checking...";
            var r = await UpdateCheck.CheckAsync(AppInfo.Version);
            releaseUrl = r.Url; updStatus.Text = r.Message;
            updStatus.ForeColor = r.State == UpdateState.Available ? Ui.Amber : r.State == UpdateState.Failed ? Ui.Red : Ui.Green;
            openRelease.Visible = r.State == UpdateState.Available; checkBtn.Enabled = true;
            if (r.State != UpdateState.Failed) UpdateChecked?.Invoke(r);
        });
        var autoUpd = new CheckBox { Text = "Check For Updates Automatically", AutoSize = true, BackColor = Ui.CardBg, Checked = AppSettings.Load().CheckForUpdates, Margin = new Padding(2, 8, 0, 0) };
        autoUpd.CheckedChanged += (_, _) => { try { AppSettings.Update(x => x.CheckForUpdates = autoUpd.Checked); } catch { } };
        var aflow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, BackColor = Ui.CardBg, Padding = new Padding(0, 4, 0, 0) };
        aflow.Controls.Add(new Label { Text = "Version " + AppInfo.Version, AutoSize = true, ForeColor = Ui.Text, BackColor = Ui.CardBg, Padding = new Padding(2, 8, 14, 0) });
        aflow.Controls.AddRange(new Control[] { checkBtn, _updateNowBtn, openRelease, updStatus });
        aflow.SetFlowBreak(updStatus, true); aflow.Controls.Add(autoUpd);
        if (_update is { State: UpdateState.Available }) { updStatus.Text = _update.Message; updStatus.ForeColor = Ui.Amber; releaseUrl = _update.Url; openRelease.Visible = true; }
        about.Body.Controls.Add(aflow);

        foreach (var c in new Control[] { appearance, switching, notes, startup, data, logCard, about }) c.Margin = new Padding(0, 0, Ui.Gap, Ui.Gap);
        grid.Controls.Add(appearance, 0, 0); grid.Controls.Add(switching, 0, 1); grid.Controls.Add(notes, 0, 2); grid.Controls.Add(startup, 0, 3); grid.Controls.Add(data, 0, 4); grid.Controls.Add(logCard, 0, 5); grid.Controls.Add(_global, 0, 6); grid.Controls.Add(about, 0, 7);
        var scroll = new ScrollSurface { MinContentHeight = heights.Sum() };      // the same: no scrollbar, the wheel scrolls it
        scroll.Content.Controls.Add(grid);
        page.Controls.Add(scroll);
        page.Controls.Add(ActionBar(beforeSave: () => { _engine.SetRecoverSeconds((int)_recover.Value); _engine.SetDailyCapMB((int)_cap.Value); _engine.SetBackupCheckSeconds((int)_backupCheck.Value); SetLogLines((int)_logLines.Value); }));
        return page;
    }

    /// <summary>Turns "start when I sign in" on or off. Talks to Windows, so it runs off the UI thread; the box shows what really happened afterwards.</summary>
    void SetStartup(bool on)
    {
        if (_startupBusy) return;
        _startupBusy = true;
        string exe = Environment.ProcessPath ?? "";
        Task.Run(() =>
        {
            bool ok = StartupTask.Set(on, exe, _engine.Log);
            bool now = StartupTask.IsEnabled();
            OnUi(() => { _startup.Checked = now; _startupBusy = false; if (!ok) Say(on ? "Could not turn on start at sign-in." : "Could not turn off start at sign-in.", true); });
        });
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
        _engine.Usage.Add(@"C:\Program Files (x86)\Steam\steam.exe", 52_500_000, DateTime.UtcNow, "Backup"); _engine.Usage.Add(@"C:\Program Files\HoYoPlay\1.19.0.382\HYPWorker.exe", 3_200_000, DateTime.UtcNow, "Main");
        _engine.Usage.Add(@"D:\Tools\newtool.exe", 800_000, DateTime.UtcNow, "Backup"); _engine.Usage.Add(@"C:\Program Files\Mozilla Firefox\firefox.exe", 400_000, DateTime.UtcNow, "Main");
        _engine.Usage.AddUp(@"C:\Program Files\Mozilla Firefox\firefox.exe", 1_900_000, "Main"); _engine.Usage.AddUp(@"C:\Program Files (x86)\Steam\steam.exe", 2_400_000, "Backup");
        DemoBlocked = true; RefreshUsage(); if (_usage.Items.Count > 1) { _usage.Items[1].Selected = true; _usage.Items[2].Selected = true; }
        foreach (var key in _tabs.Keys())
        {
            _tabs.Select(key); Application.DoEvents();
            int h0 = Height; if (key == "settings") { Height = 1250; Application.DoEvents(); }      // taller, so every card on the scrolling Settings page is in the picture
            using var bmp = new Bitmap(Width, Height);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            bmp.Save(Path.Combine(folder, key + ".png"));
            Height = h0;
        }
        // the dashboard while the engine is changing things (grey placeholder bars)
        _tabs.Select("dashboard"); SetLoading(true); Application.DoEvents();
        using (var lb = new Bitmap(Width, Height)) { DrawToBitmap(lb, new Rectangle(0, 0, Width, Height)); lb.Save(Path.Combine(folder, "loading.png")); }
        SetLoading(false);
        // the smallest window the app allows: nothing may be cut off here either
        { int w0 = Width, h0 = Height; Width = MinimumSize.Width; Height = MinimumSize.Height; _tabs.Select("dashboard"); Application.DoEvents();
          using (var sm = new Bitmap(Width, Height)) { DrawToBitmap(sm, new Rectangle(0, 0, Width, Height)); sm.Save(Path.Combine(folder, "small.png")); }
          _dashPage?.ScrollBy(100000); Application.DoEvents();       // and scrolled to the end, to see the lower cards of the small window
          using (var sb = new Bitmap(Width, Height)) { DrawToBitmap(sb, new Rectangle(0, 0, Width, Height)); sb.Save(Path.Combine(folder, "small-bottom.png")); }
          File.WriteAllText(Path.Combine(folder, "small-columns.txt"), $"list client width {_usageView.ClientSize.Width}; columns: {string.Join(", ", _usageView.Columns.Cast<ColumnHeader>().Select(c => c.Text + "=" + c.Width))}");
          _dashPage?.ScrollBy(-100000);
          Width = w0; Height = h0; Application.DoEvents(); }
        // the scrolling of a page without a scrollbar, checked in code (a small page with taller content)
        {
            var ss = new ScrollSurface { Width = 300, Height = 200 }; ss.MinContentHeight = 500;
            bool scrolled = ss.Wheel(-120); int one = ss.Offset;
            for (int i = 0; i < 40; i++) ss.Wheel(-120); int bottom = ss.Offset;
            ss.ScrollBy(-100); int back = ss.Offset;
            for (int i = 0; i < 40; i++) ss.Wheel(120); int top = ss.Offset;
            var roomy = new ScrollSurface { Width = 300, Height = 600 }; roomy.MinContentHeight = 500;
            File.WriteAllText(Path.Combine(folder, "scroll-check.txt"),
                $"wheel down scrolls: {scrolled}; one notch moves {one} px; bottom stops at {bottom} (max 300); 100 px back = {back}; top stops at {top}; content that fits does not scroll: {!roomy.Wheel(-120)}; content top at bottom = {-bottom}");
        }
        // the dashboard with sample blocked rows (this snapshot only)
        _tabs.Select("dashboard"); Application.DoEvents();
        using (var bb0 = new Bitmap(Width, Height)) { DrawToBitmap(bb0, new Rectangle(0, 0, Width, Height)); bb0.Save(Path.Combine(folder, "blocked.png")); }
        // the warning banner, with sample text (this snapshot only; the real one follows the engine)
        SetWarning("Main connection is on SNcial, a backup network. Mobile data may be in use.", red: true); Application.DoEvents();
        using (var wb0 = new Bitmap(Width, Height)) { DrawToBitmap(wb0, new Rectangle(0, 0, Width, Height)); wb0.Save(Path.Combine(folder, "warning.png")); }
        SetWarning(null, red: true);
        // the program picker, rendered off-screen (never shown) with sample "seen on the backup" data
        using var pk = new ProgramPicker(new[] { new SeenProgram("steam.exe", @"C:\Program Files (x86)\Steam\steam.exe", 52_500_000), new SeenProgram("HYPWorker.exe", @"C:\Program Files\HoYoPlay\1.19.0.382\HYPWorker.exe", 3_200_000) });
        pk.StartPosition = FormStartPosition.Manual; pk.Location = new Point(-32000, -32000); pk.Opacity = 0;   // invisible and off-screen: nothing flashes on the user's desktop
        pk.Show(); Application.DoEvents();
        using var pb = new Bitmap(pk.Width, pk.Height);
        pk.DrawToBitmap(pb, new Rectangle(0, 0, pk.Width, pk.Height));
        pb.Save(Path.Combine(folder, "picker.png"));
        pk.Close();
        // the first-run welcome, the same way
        using var wd = new WelcomeDialog(_engine);
        wd.StartPosition = FormStartPosition.Manual; wd.Location = new Point(-32000, -32000); wd.Opacity = 0;
        wd.Show(); Application.DoEvents();
        using var wb = new Bitmap(wd.Width, wd.Height);
        wd.DrawToBitmap(wb, new Rectangle(0, 0, wd.Width, wd.Height));
        wb.Save(Path.Combine(folder, "welcome.png"));
        wd.Close();
    }

    public void CloseForReal() { _reallyClose = true; Detach(); Close(); }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }   // X hides to tray
        base.OnFormClosing(e);
    }
}

/// <summary>Menu look that follows the theme (used by the mode menu and the tray menu).</summary>
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
