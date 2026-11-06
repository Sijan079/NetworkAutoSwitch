using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

/// <summary>The app window: Dashboard (live status, latency, stats, backup data, blocks), Log (live, newest first), Policy (rules editor) and Settings.
/// Every card sits directly on its page (cards are never nested). Closing the window hides it to the tray; Exit lives in the tray menu.</summary>
sealed partial class MainForm : Form
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
