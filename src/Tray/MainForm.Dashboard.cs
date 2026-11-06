using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

// Dashboard page: connection cards, mode, session tiles, latency chart, read-only tables and the warning banner.
sealed partial class MainForm
{
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

}
