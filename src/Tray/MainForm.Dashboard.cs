using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

// Dashboard page (docs/ui-mockups Main.dc.html and Dashboard-Alarm.dc.html): the page header with the state line, the alarm banner,
// the Main and Backup cards, the mode, the latency chart with the daily data meter, and quick views of Network Activity and the Watchdog.
// Nothing here is updated while the page cannot be seen: the latest status is kept and shown when the page comes back into view.
sealed partial class MainForm
{
    PageHeader _dashHeader = null!;
    Control _dashRoot = null!;
    Banner _alarm = null!;
    Segmented _modeSeg = null!;
    readonly Label _modeHint = new() { AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleRight, ForeColor = Ui.Muted, BackColor = Ui.Bg, Font = Ui.UiFont(9f), UseMnemonic = false };
    readonly CapMeter _capMeter = new() { Dock = DockStyle.Fill };
    DashTable _actTable = null!, _dogTable = null!;
    Chip _dryChip = null!, _vpnChip = null!;
    EngineStatus? _dashStatus;          // the latest status, shown when the page comes into view
    string? _warnShown; bool _warnRed = true;
    string _capTip = "";
    const int AlarmRowH = 84, HeaderH = 76;      // the same header height as every other page
    static readonly ConnectionMode[] Modes = { ConnectionMode.Auto, ConnectionMode.StayPrimary, ConnectionMode.StayBackup };

    /// <summary>True only while the dashboard is really on screen (the window is shown and this page is the one in view).</summary>
    bool DashShown => _dashRoot is { Visible: true };

    // ---------- Dashboard ----------
    Control DashboardPage()
    {
        var page = new ScrollSurface();                                       // no scrollbar: the wheel, a touchpad and PageUp / PageDown scroll it
        _dashPage = page; _dashRoot = page;
        page.Content.Padding = new Padding(28, 0, 12, 8);                  // 28 px each side, as in the mockup (the cards carry 16 px of it on their right)

        // ---- header: the title and the actions on the right (no line under the title: the filled card says which connection is in use) ----
        var header = _dashHeader = new PageHeader("Dashboard") { Height = HeaderH, Padding = new Padding(0, 0, 16, 0) };

        _updateBtn = Ui.Btn(Ui.Download, "Update", "A newer version is available. Click to download, check and install it.", () => InstallUpdate?.Invoke(), BtnKind.Primary);
        _updateBtn.Visible = false;
        _dryChip = new Chip("Dry-Run", Ui.Amber) { Visible = !_engine.Live };
        Ui.Tip.SetToolTip(_dryChip, "Dry-run: nothing is changed on this PC.");
        _vpnChip = new Chip("VPN", Ui.Blue) { Visible = false };
        Ui.Tip.SetToolTip(_vpnChip, "A VPN (such as Cloudflare WARP) refuses the internet check on a single connection. The app checks the connection in use through the VPN and the other one by its gateway, so it cannot confirm the other connection has internet before switching to it. For exact checks, exclude 8.8.8.8 and 9.9.9.9 from the VPN (in WARP: Preferences, Advanced, Split Tunnels, Exclude IPs).");
        _pauseBtn = Ui.Btn(Ui.Pause, "Pause", "Stop switching and put Windows back to normal.", () => { _togglePause(); UpdatePauseButton(); });
        _resumeBtn = Ui.Btn(Ui.Play, "Resume", "Start switching again.", () => { _togglePause(); UpdatePauseButton(); }, BtnKind.Primary);
        _resumeBtn.Visible = false;
        var netsBtn = Ui.Btn(Ui.Wifi, "Choose Connections", "Open the Networks page: sort your networks, or pick the adapters.", () => ChooseConnections());
        header.AddAction(_updateBtn); header.AddAction(_dryChip); header.AddAction(_vpnChip); header.AddAction(_pauseBtn); header.AddAction(_resumeBtn); header.AddAction(netsBtn);
        _dryChip.Margin = _vpnChip.Margin = new Padding(10, (Ui.BtnH - 22) / 2, 0, 0);       // chips are shorter than the buttons: centred on the same line

        var grid = _grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Ui.Bg, Margin = new Padding(0) };
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));                 // 0  alarm banner: height 0 until main is the phone or both are one network
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 204));               // 1  Main and Backup cards
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));                // 2  mode
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 232));               // 3  latency + backup data today
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));                // 4  Network Activity + Watchdog take the rest

        // ---- alarm ----
        _alarm = new Banner { Dock = DockStyle.Fill, Margin = Gutter, Visible = false };
        _alarm.AddAction(Ui.Btn(Ui.Wifi, "Fix In Networks", "Open the Networks page: mark which networks are Main and which are Backup.", () => ChooseConnections()));

        // ---- connection cards ----
        var cards = Row2(50, 50);
        _cardP.Margin = _cardB.Margin = Gutter;
        cards.Controls.Add(_cardP, 0, 0); cards.Controls.Add(_cardB, 1, 0);

        // ---- mode ----
        _modeSeg = new Segmented("Automatic", "Stay On Main", "Stay On Backup");
        _modeSeg.Show(Array.IndexOf(Modes, _engine.ConnMode));
        _modeSeg.SelectedFill = i => i == 0 ? Ui.Blue : i == 1 ? Ui.Green : Ui.Amber;      // the choice in force is filled with its colour, so it is seen at a glance
        _modeSeg.SelectedChanged += i => { var m = Modes[i]; if (m != _engine.ConnMode) _engine.SetConnectionMode(m); RenderModeHint(m); };
        var modeLabel = new Label { Text = "MODE", AutoSize = true, Font = Ui.UiFont(9.4f, Weight.Bold), ForeColor = Ui.Text, BackColor = Ui.Bg, UseMnemonic = false };
        var modeRow = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Margin = Gutter };
        modeRow.Controls.Add(modeLabel); modeRow.Controls.Add(_modeSeg); modeRow.Controls.Add(_modeHint);
        modeRow.Layout += (_, _) =>
        {
            modeLabel.Location = new Point(0, (36 - modeLabel.Height) / 2);
            _modeSeg.Location = new Point(modeLabel.Right + 14, 0);
            int hx = _modeSeg.Right + 16, hw = modeRow.Width - hx;
            _modeHint.Visible = hw >= DashPaint.TextWidth(_modeHint.Text, 9f, Weight.Regular) + 4;      // in a small window the hint gives way rather than being cut (it is in the tooltip too)
            _modeHint.SetBounds(hx, 0, Math.Max(0, hw), 36);
        };
        RenderModeHint(_engine.ConnMode);

        // ---- latency + data today ----
        var chartRow = Row2(100, 0, 276);
        var chartCard = _chartCard = new Card("", "Latency, Last 60 Checks") { Margin = Gutter };
        chartCard.SetHint("Main is the solid line, Backup the dashed one. Red ticks at the bottom mark failed checks.");
        _legend.BackColor = Ui.CardBg; _legend.ForeColor = Ui.Muted; _legend.Font = Ui.UiFont(9f);
        chartCard.AddHeaderControl(_legend);
        void FitLegend() => _legend.Visible = chartCard.Width >= _legend.Width + 300;      // the title keeps its room: the legend gives way in a narrow window
        chartCard.Resize += (_, _) => FitLegend(); _legend.SizeChanged += (_, _) => FitLegend();
        _chart.BackColor = Ui.CardBg;
        chartCard.Body.Padding = new Padding(10, 0, 14, 12); chartCard.Body.Controls.Add(_chart);
        var capCard = new Card("", "Backup Data Today") { Margin = Gutter };
        capCard.SetHint("Everything that went through the backup connection today (all programs), counted from the adapter's byte counters. The count starts again at midnight.");
        capCard.Body.Padding = new Padding(14, 0, 14, 10); capCard.Body.Controls.Add(_capMeter);
        chartRow.Controls.Add(chartCard, 0, 0); chartRow.Controls.Add(capCard, 1, 0);

        // ---- quick views: what each program moved, and what the watchdog is doing. The actions with selection are on the Programs page. ----
        var bottom = Row2(50, 50);
        _actTable = new DashTable(new[] { new DashColumn("PROGRAM", 0), new DashColumn("DOWN", 78, Right: true), new DashColumn("UP", 74, Right: true, Optional: true), new DashColumn("ON", 70) },
            "Nothing measured yet today.") { Dock = DockStyle.Fill };
        var actCard = new Card("", "Network Activity") { Margin = Gutter };
        actCard.SetHint("What each program downloaded and uploaded today, and which connection it was seen on. Sort it and add programs to the watch list on the Programs page.");
        actCard.Body.Padding = new Padding(14, 0, 14, 8); actCard.Body.Controls.Add(_actTable);

        _dogTable = new DashTable(new[] { new DashColumn("PROGRAM", 0), new DashColumn("STATE", 80), new DashColumn("TIME LEFT", 70, Right: true, Optional: true) },
            "Nothing is blocked or watched.", actionWidth: 84) { Dock = DockStyle.Fill };
        _dogTable.ActionClicked += WatchdogAction;
        var dogCard = new Card("", "Watchdog") { Margin = Gutter };
        dogCard.SetHint("Blocked = the watchdog stopped it for downloading too fast on a protected connection. Allowed = you let it through for a while (the countdown). Watching = on your watch list.\nBlocks never expire on their own: you decide. More actions are on the Programs page.");
        var dogNote = HeaderNote("");
        dogCard.AddHeaderControl(dogNote);
        dogNote.Visible = false;
        dogCard.Body.Padding = new Padding(14, 0, 14, 8); dogCard.Body.Controls.Add(_dogTable);
        bottom.Controls.Add(actCard, 0, 0); bottom.Controls.Add(dogCard, 1, 0);

        grid.Controls.Add(_alarm, 0, 0); grid.Controls.Add(cards, 0, 1); grid.Controls.Add(modeRow, 0, 2); grid.Controls.Add(chartRow, 0, 3); grid.Controls.Add(bottom, 0, 4);
        page.Content.Controls.Add(grid); page.Content.Controls.Add(header);      // docked: the header (added last) takes the top, the grid the rest
        FitDashboardHeight();

        // catch up when the page comes back into view (nothing is drawn while it is hidden). The quick views also follow RefreshBlocked (see ShowBlockedList).
        page.VisibleChanged += (_, _) => { if (DashShown) { if (_dashStatus is { } s) Render(s); RefreshDashTables(); } };
        return page;
    }

    static readonly Padding Gutter = new(0, 0, 16, 16);

    /// <summary>A one-row, two-column layout; widths in percent, or a fixed second column when <paramref name="fixedSecond"/> is set.</summary>
    static TableLayoutPanel Row2(float a, float b, int fixedSecond = 0)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Ui.Bg, Margin = new Padding(0), Padding = new Padding(0) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, a));
        t.ColumnStyles.Add(fixedSecond > 0 ? new ColumnStyle(SizeType.Absolute, fixedSecond) : new ColumnStyle(SizeType.Percent, b));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        return t;
    }

    static Label HeaderNote(string text) => new() { Text = text, AutoSize = true, Font = Ui.UiFont(9f), ForeColor = Ui.Muted, BackColor = Ui.CardBg, Margin = new Padding(0, 6, 4, 0), UseMnemonic = false };

    /// <summary>The adapters changed (a phone was plugged in, an adapter was renamed or you chose others): card icons, names and the mode tooltip follow.</summary>
    void OnAdaptersChanged()
    {
        _cardP.SetGlyph(Ui.AdapterGlyph(_engine.PrimaryKind)); _cardB.SetGlyph(Ui.AdapterGlyph(_engine.BackupKind));
        IdentifyCards();
        Ui.Tip.SetToolTip(_modeSeg, $"How the app switches.\nAutomatic: it moves to the backup when main fails, and back when main is good again.\n{ModeLabel(ConnectionMode.StayPrimary)}: locked, never switches.\n{ModeLabel(ConnectionMode.StayBackup)}: locked, never switches.");
    }

    void IdentifyCards()
    {
        _cardP.SetIdentity(_engine.PrimaryNetwork ?? "", _primary);
        if (_engine.BackupName.Length == 0) _cardB.SetIdentity("No Backup Connected", "Plug in a tether or a second Wi-Fi adapter");
        else _cardB.SetIdentity(_engine.BackupNetwork ?? "", _backup);
    }

    string ModeLabel(ConnectionMode m) => m switch { ConnectionMode.StayPrimary => $"Stay On {Ui.Role("Main", _engine.PrimaryNetwork, _primary)}", ConnectionMode.StayBackup => $"Stay On {Ui.Role("Backup", _engine.BackupNetwork, _backup)}", _ => "Automatic" };

    void RenderModeHint(ConnectionMode m)
    {
        string t = m == ConnectionMode.Auto
            ? $"Switches after 3 failed checks. Returns after {_engine.RecoverSeconds} s of good checks."
            : "Locked: the app does not switch by itself.";
        Ui.Tip.SetToolTip(_modeSeg, t); _modeHint.Visible = false;   // the explanation lives in the tooltip; the bar shows the choice only
    }

    /// <summary>The least height the dashboard needs (a smaller window scrolls instead of squeezing the cards).</summary>
    void FitDashboardHeight()
    {
        if (_dashPage is null) return;
        _dashPage.MinContentHeight = HeaderH + (_warnShown is null ? 0 : AlarmRowH) + 204 + 52 + 232 + 236 + 8;
    }

    /// <summary>Programs page: the list while something is blocked or allowed for now, a short note while nothing is.</summary>
    void ShowBlockedList(int count)
    {
        _blockedList.Visible = count > 0; _blockedEmpty.Visible = count == 0;
        _blockedView.Visible = count > 0; _blockedViewEmpty.Visible = count == 0;
        UpdateBlockedButtons();
        RefreshDashTables();       // RefreshBlocked ends here (every 2 s with RefreshUsage, and after an action): the quick views follow
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

    // ---------- quick views ----------
    const int AllowMinutes = 30;

    /// <summary>Rebuilds the two quick views from what the Programs page has just loaded (and the usage counters). The tables repaint only if a row changed.</summary>
    void RefreshDashTables()
    {
        if (!DashShown) return;
        var usage = _engine.Usage.Rows(30);
        var act = new DashRow[usage.Count];
        for (int i = 0; i < usage.Count; i++)
        {
            var r = usage[i];
            Color? on = r.On == "Main" ? Ui.Green : r.On.Length > 0 ? Ui.Amber : null;      // anything that touched the backup is amber (your phone's data)
            act[i] = new DashRow(new[] { new DashCell(r.Name), new DashCell(Fmt(r.Down)), new DashCell(Fmt(r.Up)), new DashCell(r.On, on) }, null, r.Name,
                $"{_engine.Usage.PathFor(r.Name) ?? r.Name}\nTotal {Fmt(r.Total)}" + (r.BackupBytes > 0 ? $"  ·  On backup {Fmt(r.BackupBytes)}" : ""));
        }
        _actTable.SetRows(act);

        // the watchdog: what _blockedList holds (blocked and allowed, plus the --demo-blocked samples), then watched programs seen today
        var dog = new List<DashRow>(); var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ListViewItem it in _blockedList.Items)
        {
            string path = it.SubItems[0].Text, name = Path.GetFileName(path), status = it.SubItems.Count > 2 ? it.SubItems[2].Text : "";
            listed.Add(name);
            if (it.Name == "blocked")
                dog.Add(new DashRow(new[] { new DashCell(name), new DashCell("Blocked", Ui.Red), new DashCell("—") }, "Allow", (string)it.Tag!, $"{path}\n{status}. Allow lets it through for {AllowMinutes} minutes."));
            else
            {
                int at = status.LastIndexOf(" in ", StringComparison.Ordinal);
                dog.Add(new DashRow(new[] { new DashCell(name), new DashCell("Allowed", Ui.Blue), new DashCell(at >= 0 ? status[(at + 4)..] : "") }, "Block Now", (string)it.Tag!, $"{path}\nWatched again when the countdown ends. Block Now blocks it again at once.", Danger: true));
            }
        }
        foreach (var r in usage)
        {
            string path = _engine.Usage.PathFor(r.Name) ?? r.Name;
            if (listed.Contains(r.Name) || !_engine.IsWatched(path)) continue;
            dog.Add(new DashRow(new[] { new DashCell(r.Name), new DashCell("Watching", Ui.Muted), new DashCell("—") }, null, r.Name, $"{path}\nOn your watch list: blocked on a protected connection if it downloads too fast."));
        }
        _dogTable.SetRows(dog.ToArray());
    }

    /// <summary>Allow / Block Now in a Watchdog row: the same engine calls as the Programs page, off the UI thread (they run PowerShell).</summary>
    void WatchdogAction(DashRow row)
    {
        string key = row.Key; bool allow = row.Action!.StartsWith("Allow", StringComparison.Ordinal);
        Task.Run(() => { if (allow) _engine.OverrideBlock(key, AllowMinutes); else _engine.BlockAgain(key); OnUi(RefreshBlocked); });
    }

    // ---------- status ----------
    /// <summary>Shows a status right away (used after a theme change, so the new window is not blank until the next probe).</summary>
    public void ShowStatus(EngineStatus s) { _dashStatus = s; Render(s); }

    /// <summary>The alarm banner: shown while the main connection is protected like a backup, or both connections are one network.
    /// Red when it is (probably) your phone, amber when it is only "not sorted yet" or "not identified yet". It is part of the page, so mute does not hide it.</summary>
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
        if (text == _warnShown && red == _warnRed) return;          // called on every probe: only a real change touches the layout
        _warnShown = text; _warnRed = red;
        if (text is not null)
        {
            string title = _engine.SameNetworkWarning ? "Both Connections Are One Network"
                : _engine.MainProtected && !red ? "Main Is Protected Like a Backup"
                : _engine.MainProtected ? "Main Looks Like Your Phone"
                : "Check Your Connections";
            _alarm.Severe = red; _alarm.Set(title, text); Ui.Tip.SetToolTip(_alarm, text);
        }
        _alarm.Visible = text is not null;
        _grid.RowStyles[0].Height = text is null ? 0 : AlarmRowH;
        FitDashboardHeight();
    }

    /// <summary>The state line under the title: "On Main: SNcial_5G" (the adapter name when the network name is not known).</summary>
    static string OnLabel(string role, string? net, string adapter) => $"On {role}: {(string.IsNullOrEmpty(net) ? adapter : net)}";
    static string Net(string? network, string adapter) => string.IsNullOrEmpty(network) ? adapter : network;

    void Apply(EngineStatus s)
    {
        _dashStatus = s;
        if (DashShown) Render(s);                // hidden: nothing to draw; the page catches up when it comes into view
        if (_awaitStatus) SetLoading(false);     // the first measurement after a change has arrived
    }

    void Render(EngineStatus s)
    {
        IdentifyCards();                          // names are refreshed by the engine in the background
        bool anyOk = s.Primary.Healthy || s.Backup.Healthy;
        string state; Color ink = Ui.Muted;
        if (s.Paused) state = "Paused. Windows networking is back to normal.";
        else if (!anyOk) { state = "No Connection. Neither connection answers."; ink = Ui.Red; }
        else if (s.Active == Active.Primary)
            state = OnLabel("Main", _engine.PrimaryNetwork, _primary) + (_engine.MainProtected ? ". Heavy downloads are blocked." : s.Backup.Healthy ? ". Backup is ready." : "");
        else { state = OnLabel("Backup", _engine.BackupNetwork, _backup) + (s.Primary.Healthy ? "" : ". Main is down."); ink = Ui.Amber; }
        if (_banner.Text != state) { _banner.Text = state; Ui.Tip.SetToolTip(_banner, state); }      // the full line, in case a narrow window cuts it
        if (_banner.ForeColor != ink) _banner.ForeColor = ink;
        UpdatePauseButton();
        _vpnChip.Visible = s.Degraded;
        UpdateWarning();
        int mi = Array.IndexOf(Modes, s.Mode);
        if (mi >= 0 && _modeSeg.SelectedIndex != mi) _modeSeg.Show(mi);
        RenderModeHint(s.Mode);

        var st = _engine.Stats;
        bool pIn = !s.Paused && anyOk && s.Active == Active.Primary, bIn = !s.Paused && anyOk && s.Active == Active.Backup;
        int? pAvg = st.AvgMs(true), bAvg = st.AvgMs(false);
        string pNote = _engine.MainProtected ? "Protected like a backup: firewall rules, watchdog and data counter active"
            : "";
        bool hasBackup = _engine.BackupName.Length > 0;
        string bNote = !hasBackup ? "Plug in a tether or a second Wi-Fi adapter"
            : "";

        _cardP.Show(s.Primary, true, StatsTracker.UptimePct(st.ProbesPrimary, st.FailPrimary), pIn, s.Paused, pNote);
        _cardB.Show(s.Backup, hasBackup, StatsTracker.UptimePct(st.ProbesBackup, st.FailBackup), bIn, s.Paused, bNote);

        var (p, b) = st.Snapshot();
        _chart.SetData(p, b); _legend.Set(Net(_engine.PrimaryNetwork, _primary), Net(_engine.BackupNetwork, _backup));      // the networks, not the adapters (the adapter only while the network is not known)

        var onBackup = TimeSpan.FromSeconds(st.SecondsOnBackup);
        string onB = onBackup.TotalHours >= 1 ? $"{(int)onBackup.TotalHours} h {onBackup.Minutes} min" : $"{(int)onBackup.TotalMinutes} min";
        long capMb = _engine.DailyCapMB;
        _capMeter.Set(_engine.BackupBytesToday, capMb, $"{st.Switches} {(st.Switches == 1 ? "switch" : "switches")}  ·  {onB} on backup",
            st.LastSwitchAt is DateTime t ? $"Last switch at {t:HH:mm}" : "No switch yet");
        string tip = "Everything that went through the backup connection today (all programs). The count starts again at midnight." + (_engine.MainProtected ? $" The main connection counts too while it is protected ({_engine.MainProtectedReason})." : "") + (capMb > 0 ? $" Your daily limit is {capMb} MB." : " Set a daily limit in Settings.")
            + (st.LastSwitchAt is null ? "" : $"\nLast switch: {st.LastSwitchReason}");
        if (tip != _capTip) { _capTip = tip; Ui.Tip.SetToolTip(_capMeter, tip); }
    }

    void UpdatePauseButton() { bool paused = _engine.Paused; _pauseBtn.Visible = !paused; _resumeBtn.Visible = paused; }      // the same value again is a no-op
}
