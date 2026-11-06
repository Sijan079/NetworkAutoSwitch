using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

// Settings page (docs/ui-mockups/project/Settings.dc.html): Connections, Switching, App and Updates cards.
// Everything applies right away (no Save button): toggles at once, the numbers a moment after you stop changing them.
sealed partial class MainForm
{
    // ---------- updates (the tray does the checking and installing; the window only shows it) ----------
    Button? _updateBtn, _updateNowBtn; Label? _updStatus; UpdateResult? _update;
    /// <summary>Set by the tray: install the update that was found.</summary>
    public Action? InstallUpdate;
    /// <summary>Raised when "Check For Updates" found out the latest version, so the tray can remember it.</summary>
    public Action<UpdateResult>? UpdateChecked;

    // the Settings page's own controls. The old _startup / _mute check boxes (MainForm.cs) are kept in step with these toggles.
    Toggle? _startupToggle;
    Label? _startupDetail;
    const string StartupDetailText = "";      // the row stays one line; a failure to change it still shows here in red

    /// <summary>The daily cap is stored in MB and shown in GB (1 GB = 1000 MB, like the dashboard meter). 10000 MB shows as 10.0 GB.</summary>
    static decimal CapToGb(int mb) => mb / 1000m;
    static int GbToCapMb(decimal gb) => (int)Math.Round(gb * 1000m);

    Button? _openReleaseBtn;
    bool _releaseShown;                 // "Visible" reads false while the page is hidden, so the wanted state is kept here for the layout
    string _releaseUrl = UpdateCheck.ReleasesPage;
    Action? _settingsRelayout;          // re-arranges the page (the Updates card grows when an update shows its buttons)

    /// <summary>A newer version is known (or no longer is): the dashboard shows an Update button and Settings an Install Update button.</summary>
    public void SetUpdate(UpdateResult? u)
    {
        _update = u is { State: UpdateState.Available } ? u : null;
        bool on = _update is not null;
        if (_updateBtn is not null) { _updateBtn.Visible = on; if (on) _updateBtn.Text = "  Update to " + _update!.Latest; }
        if (_updateNowBtn is not null) _updateNowBtn.Visible = on;
        if (_updStatus is not null && on) { _updStatus.Text = _update!.Message; _updStatus.ForeColor = Ui.Amber; }
        if (on) { _releaseUrl = _update!.Url; _releaseShown = true; if (_openReleaseBtn is not null) _openReleaseBtn.Visible = true; }
        _settingsRelayout?.Invoke();
    }

    /// <summary>Progress or an error from the installer download.</summary>
    public void SetUpdateProgress(string text, bool error)
    {
        if (_updStatus is not null) { _updStatus.Text = text; _updStatus.ForeColor = error ? Ui.Red : Ui.Muted; }
        bool busy = !error && text.Length > 0;
        if (_updateBtn is not null) _updateBtn.Enabled = !busy;
        if (_updateNowBtn is not null) _updateNowBtn.Enabled = !busy;
        _settingsRelayout?.Invoke();
    }

    Control SettingsPage()
    {
        var page = new Panel { BackColor = Ui.Bg, Padding = new Padding(28, 0, 28, 0) };
        var header = new PageHeader("Settings");
        var scroll = new ScrollSurface();         // no scrollbar, the wheel scrolls it (a small window can still reach every card)
        var content = scroll.Content;

        Label Lbl(string t, float pt, Weight w, Color c) => new() { Text = t, AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Font = Ui.UiFont(pt, w), ForeColor = c, BackColor = Ui.CardBg, TextAlign = ContentAlignment.MiddleLeft };
        static TextBox ReadOnlyBox() => new() { ReadOnly = true, TabStop = false, Cursor = Cursors.Default };
        // the numbers show without spin arrows (as in the design); the arrow keys and typing still change them
        static NumericUpDown Bare(NumericUpDown n) { if (n.Controls.Count > 0) n.Controls[0].Visible = false; return n; }

        // ---------- Connections: which adapters are main and backup right now ----------
        var conn = new Card("", "Connections");
        conn.SetHint("The adapters the app uses right now. Automatic means the app picked them itself (and follows a phone you plug in).\nChoose Connections opens the Networks page, where you sort your networks or pick the adapters yourself.");
        var mainBox = ReadOnlyBox(); var backBox = ReadOnlyBox();
        var fMain = new Field(Ui.T("Main Adapter"), mainBox); var fBack = new Field(Ui.T("Backup Adapter"), backBox);
        var choose = Ui.Btn(Ui.Wifi, "Choose Connections", "Open the Networks page: sort your networks, or pick the adapters.", () => ChooseConnections());
        conn.Body.Controls.AddRange(new Control[] { fMain, fBack, choose });
        void ShowAdapters()
        {
            static string Describe(string user, string name, string none) => user.Length == 0 ? $"Automatic ({(name.Length > 0 ? name : none)})" : (name.Length > 0 ? name : user);
            mainBox.Text = Describe(_engine.UserPrimary, _engine.PrimaryName, "none found");
            backBox.Text = Describe(_engine.UserBackup, _engine.BackupName, "No Backup");
        }
        ShowAdapters();
        page.VisibleChanged += (_, _) => { if (page.Visible) ShowAdapters(); };   // adapters can change while the app runs; only refreshed when you look

        // ---------- Switching: timings and the daily data cap ----------
        var switching = new Card("", "Switching");
        switching.SetHint("Switch back after: how long the main connection must work, without a single failed check, before the app moves back from the backup. Higher = fewer back-and-forth switches if your Wi-Fi flickers. Going TO the backup is not affected: that still takes only a few seconds.\n" +
            "Check idle backup every: while the main connection is fine, the backup is checked this often. Every check uses a little mobile data.\n" +
            "Daily data cap: you are told when the backup has carried 80 % of it today, and when it is reached. It counts everything through the backup adapter and starts again at midnight. 0 = no limit.");
        _recover.Value = Math.Min(_recover.Maximum, Math.Max(_recover.Minimum, _engine.RecoverSeconds));
        _backupCheck.Value = Math.Min(_backupCheck.Maximum, Math.Max(_backupCheck.Minimum, _engine.BackupCheckSeconds));
        _cap.Value = Math.Min(_cap.Maximum, Math.Max(_cap.Minimum, CapToGb(_engine.DailyCapMB)));
        var fRecover = new Field(Ui.T("Switch back after"), Bare(_recover), "seconds");
        var fCheck = new Field(Ui.T("Check idle backup every"), Bare(_backupCheck), "seconds");
        var fCap = new Field(Ui.T("Daily data cap"), Bare(_cap), "GB");
        Ui.Tip.SetToolTip(_cap, "The most data the backup may use in a day. 0 = no limit. You are told at 80 % and when it is reached.");
        var note = Lbl("", 9f, Weight.Regular, Ui.Muted); note.Visible = false;   // the field tooltips explain the numbers
        note.AutoEllipsis = false; note.TextAlign = ContentAlignment.TopLeft;
        switching.Body.Controls.AddRange(new Control[] { fRecover, fCheck, fCap, note });

        // ---------- App: theme, start at sign-in, mute, update checks, log lines ----------
        var app = new Card("", "App");
        app.SetHint("Theme: Follow System switches with Windows' own light/dark setting.\nStart at sign-in: the same scheduled task the installer creates; it already runs as administrator (no prompt). The delay gives Wi-Fi time to connect.\n" +
            "Mute notifications: hides the tray pop-ups. Everything is still written to the Log, and the Dashboard shows the current state.");
        var themeLbl = Lbl(Ui.T("Theme"), 10f, Weight.SemiBold, Ui.Text);
        var theme = new Segmented("System", "Light", "Dark");
        theme.Show((int)_getTheme());
        Ui.Tip.SetToolTip(theme, "System follows Windows' own light/dark setting. The window is rebuilt in the new colours.");
        // _setTheme rebuilds (and disposes) this window, so it runs after the click has finished
        theme.SelectedChanged += i => { var mode = (ThemeMode)i; BeginInvoke(() => { if (mode != _getTheme()) _setTheme(mode); }); };
        app.Body.Controls.Add(themeLbl); app.Body.Controls.Add(theme);

        // the state is set before the handlers are attached: building the window must never change the scheduled task or the settings
        var startT = _startupToggle = new Toggle { AccessibleName = Ui.T("Start at sign-in") };
        startT.Checked = StartupTask.IsEnabled(); _startup.Checked = startT.Checked;
        startT.CheckedChanged += (_, _) => { _startup.Checked = startT.Checked; SetStartup(startT.Checked); };
        var muteT = new Toggle { AccessibleName = Ui.T("Mute notifications") };
        muteT.Checked = _getMute(); _mute.Checked = muteT.Checked;
        muteT.CheckedChanged += (_, _) => { _mute.Checked = muteT.Checked; _setMute(muteT.Checked); };
        var updT = new Toggle { AccessibleName = Ui.T("Check for updates") };
        updT.Checked = AppSettings.Load().CheckForUpdates;
        updT.CheckedChanged += (_, _) => { try { AppSettings.Update(x => x.CheckForUpdates = updT.Checked); } catch { } };
        _logLines.Value = Math.Min(_logLines.Maximum, Math.Max(_logLines.Minimum, _maxLogRows));
        var fLog = new Field("", Bare(_logLines), "lines") { Width = 200 };
        foreach (Control c in fLog.Controls) if (c is Label) c.Visible = false;     // no label above the box: the row's title is the label
        fLog.Height = 36;

        var rows = new List<(Label Title, Label Detail, Control Ctl)>();
        void Row(string title, string detail, Control ctl, string tip)
        {
            var t = Lbl(Ui.T(title), 10f, Weight.SemiBold, Ui.Text); var d = Lbl(detail, 9f, Weight.Regular, Ui.Muted);
            Ui.Tip.SetToolTip(t, tip); Ui.Tip.SetToolTip(d, tip); Ui.Tip.SetToolTip(ctl, tip);
            app.Body.Controls.AddRange(new[] { t, d, ctl });
            rows.Add((t, d, ctl));
        }
        Row("Start at sign-in", StartupDetailText, startT, "Starts the app by itself 30 seconds after you sign in, already running as administrator (no prompt). The delay gives Wi-Fi time to connect, so a slow sign-in is not mistaken for an outage.\nIt is the same setting as the installer's Start when I sign in option.");
        _startupDetail = rows[^1].Detail;
        Row("Mute notifications", "", muteT, "Hides the tray pop-ups (switching, blocked programs, errors).\nEverything is still written to the Log, and the Dashboard shows the current state.");
        Row("Check for updates", "", updT, "Looks for a newer release on GitHub when the app starts and once a day. Only GitHub's public release information is read; nothing about you is sent.");
        Row("Log lines", "", fLog, "How many lines the Log page keeps on screen. When it is full, the oldest lines drop off the bottom, so the newest are always there. The log file itself is not shortened (it starts a fresh file at 1 MB and keeps the previous one as log.txt.old).");
        var dividers = new List<int>();
        app.Body.Paint += (_, e) => { foreach (int y in dividers) e.Graphics.DrawLine(Ui.Pen(Ui.Border), 14, y, app.Body.Width - 15, y); };

        // ---------- Updates: status, version, Check Now / Install Update ----------
        var upd = new Card("", "Updates");
        upd.SetHint("A new version is never installed without your click on Install Update: the installer is checked against the release's SHA-256 file, closes the app the normal way (your network settings are put back) and starts the new version.");
        var updStatus = _updStatus = Lbl("Version " + AppInfo.Version, 11.25f, Weight.Bold, Ui.Text);
        updStatus.AutoEllipsis = false; updStatus.TextAlign = ContentAlignment.TopLeft;
        var updDetail = Lbl("", 9f, Weight.Regular, Ui.Muted);
        updStatus.TextChanged += (_, _) => { Ui.Tip.SetToolTip(updStatus, updStatus.Text); _settingsRelayout?.Invoke(); };
        _openReleaseBtn = Ui.Btn(Ui.OpenFile, "Release Page", "Open the release page in your browser.", () => { try { Process.Start(new ProcessStartInfo(_releaseUrl) { UseShellExecute = true }); } catch { } });
        _openReleaseBtn.Visible = false;
        _updateNowBtn = Ui.Btn(Ui.Download, "Install Update", "Download this version, check it and install it. The app restarts.", () => InstallUpdate?.Invoke(), BtnKind.Primary);
        _updateNowBtn.Visible = _update is { State: UpdateState.Available };
        Button? checkBtn = null;
        checkBtn = Ui.Btn(Ui.Refresh, "Check Now", "Ask GitHub whether a newer version exists.", async () =>
        {
            checkBtn!.Enabled = false; updStatus.ForeColor = Ui.Muted; updStatus.Text = "Checking...";
            var r = await UpdateCheck.CheckAsync(AppInfo.Version);
            _releaseUrl = r.Url; updStatus.Text = r.Message;
            updStatus.ForeColor = r.State == UpdateState.Available ? Ui.Amber : r.State == UpdateState.Failed ? Ui.Red : Ui.Text;
            updDetail.Text = $"Version {AppInfo.Version} · checked today {DateTime.Now:HH:mm}";
            _releaseShown = r.State == UpdateState.Available; _openReleaseBtn.Visible = _releaseShown; checkBtn.Enabled = true;
            if (r.State != UpdateState.Failed) UpdateChecked?.Invoke(r);
            _settingsRelayout?.Invoke();
        });
        if (_update is { State: UpdateState.Available })
        { updStatus.Text = _update.Message; updStatus.ForeColor = Ui.Amber; updDetail.Text = "You have version " + AppInfo.Version; _releaseUrl = _update.Url; _releaseShown = true; _openReleaseBtn.Visible = true; }
        upd.Body.Controls.AddRange(new Control[] { updStatus, updDetail, _updateNowBtn, checkBtn, _openReleaseBtn });

        // ---------- the numbers apply a moment after the last change (one settings write and one log line, not one per arrow press) ----------
        var apply = new System.Windows.Forms.Timer { Interval = 600 };
        void ApplyValues()
        {
            apply.Stop();
            if ((int)_recover.Value != _engine.RecoverSeconds) _engine.SetRecoverSeconds((int)_recover.Value);
            if ((int)_backupCheck.Value != _engine.BackupCheckSeconds) _engine.SetBackupCheckSeconds((int)_backupCheck.Value);
            if (GbToCapMb(_cap.Value) != _engine.DailyCapMB) _engine.SetDailyCapMB(GbToCapMb(_cap.Value));
            SetLogLines((int)_logLines.Value);       // has its own "unchanged" check
        }
        apply.Tick += (_, _) => ApplyValues();
        void Kick(object? s, EventArgs e) { apply.Stop(); apply.Start(); }
        _recover.ValueChanged += Kick; _backupCheck.ValueChanged += Kick; _cap.ValueChanged += Kick; _logLines.ValueChanged += Kick;
        page.Disposed += (_, _) => { if (apply.Enabled) ApplyValues(); apply.Dispose(); };    // a change made just before a theme switch is not lost

        // ---------- layout: cards stacked; App and Updates side by side when there is room ----------
        bool arranging = false;
        void Arrange()
        {
            int W = content.ClientSize.Width;
            if (arranging || W <= 0) return;
            arranging = true;
            try
            {
                const int G = 16, X0 = 14, Head = 48, Edge = 4;   // card gaps; body inset; title band (inset + header); bottom inset
                int y = 6;

                // Connections
                int inner = W - 8 - 2 * X0, col = (inner - 14) / 2;
                fMain.SetBounds(X0, 2, col, 58); fBack.SetBounds(X0 + col + 14, 2, inner - col - 14, 58);
                choose.Location = new Point(X0, 2 + 58 + 14);
                int h = Head + choose.Bottom + 14 + Edge;
                conn.SetBounds(0, y, W, h); y += h + G;

                // Switching
                col = (inner - 28) / 3;
                fRecover.SetBounds(X0, 2, col, 58); fCheck.SetBounds(X0 + col + 14, 2, col, 58); fCap.SetBounds(X0 + 2 * (col + 14), 2, inner - 2 * (col + 14), 58);
                int nh = note.Text.Length == 0 ? -12 : TextRenderer.MeasureText(note.Text, note.Font, new Size(inner, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                note.SetBounds(X0, 2 + 58 + 10, inner, nh + 2);
                h = Head + (note.Visible ? note.Bottom : 2 + 58) + 14 + Edge;
                switching.SetBounds(0, y, W, h); y += h + G;

                // App | Updates
                bool side = W >= 760;
                int appW = side ? (W - G) * 14 / 24 : W, updW = side ? W - G - appW : W;
                int ai = appW - 8 - 2 * X0;
                theme.Location = new Point(X0 + ai - theme.Width, 2);
                themeLbl.SetBounds(X0, 2, Math.Max(10, ai - theme.Width - 12), 36);
                int ry = 2 + 36 + 12; dividers.Clear();
                foreach (var (t, d, ctl) in rows)
                {
                    const int RowH = 64;
                    dividers.Add(ry);
                    int tw = Math.Max(10, ai - ctl.Width - 16);
                    if (d.Text.Length == 0) t.SetBounds(X0, ry + (RowH - 24) / 2, tw, 24); else t.SetBounds(X0, ry + 12, tw, 22);   // a row without a detail line centres its title
                    d.Visible = d.Text.Length > 0; d.SetBounds(X0, ry + 34, tw, 18);   // hidden when empty, so it never covers the title
                    ctl.Location = new Point(X0 + ai - ctl.Width, ry + (RowH - ctl.Height) / 2);
                    ry += RowH;
                }
                int appH = Head + ry + Edge;

                int ui = updW - 8 - 2 * X0, uy = 2;
                int th = Math.Min(48, TextRenderer.MeasureText(updStatus.Text.Length > 0 ? updStatus.Text : " ", updStatus.Font, new Size(ui, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height);
                updStatus.SetBounds(X0, uy, ui, th + 2); uy += th + 4;
                updDetail.SetBounds(X0, uy, ui, 18); uy += 18 + 14;
                int bx = X0;
                foreach (var b in new Control[] { _updateNowBtn, checkBtn, _openReleaseBtn })
                {
                    bool shown = b == _updateNowBtn ? _update is not null : b == _openReleaseBtn ? _releaseShown : true;
                    if (!shown) continue;
                    int bw = b.PreferredSize.Width;
                    if (bx > X0 && bx + bw > X0 + ui) { bx = X0; uy += Ui.BtnH + 8; }
                    b.Location = new Point(bx, uy); bx += bw + 8;
                }
                uy += Ui.BtnH + 14;
                int updH = Head + uy + Edge;

                if (side) { app.SetBounds(0, y, appW, appH); upd.SetBounds(appW + G, y, updW, updH); y += Math.Max(appH, updH) + G; }
                else { app.SetBounds(0, y, W, appH); y += appH + G; upd.SetBounds(0, y, W, updH); y += updH + G; }
                app.Body.Invalidate();

                scroll.MinContentHeight = y - G + 24;
            }
            finally { arranging = false; }
        }
        _settingsRelayout = Arrange;
        foreach (var c in new Control[] { conn, switching, app, upd }) c.Dock = DockStyle.None;   // a Card fills its parent by default; here Arrange() places each one
        content.Controls.AddRange(new Control[] { conn, switching, app, upd });
        content.Resize += (_, _) => Arrange();
        Arrange();

        page.Controls.Add(scroll);
        page.Controls.Add(header);       // added last, so it docks first (on top), and the scroll surface fills the rest
        return page;
    }

    /// <summary>Turns "start when I sign in" on or off. Talks to Windows, so it runs off the UI thread; the toggle shows what really happened afterwards.</summary>
    void SetStartup(bool on)
    {
        if (_startupBusy) return;
        _startupBusy = true;
        string exe = Environment.ProcessPath ?? "";
        Task.Run(() =>
        {
            bool ok = StartupTask.Set(on, exe, _engine.Log);
            bool now = StartupTask.IsEnabled();
            OnUi(() =>
            {
                // written while still busy, so the toggle's own CheckedChanged does not start another change
                if (_startupToggle is not null) _startupToggle.Checked = now;
                _startup.Checked = now; _startupBusy = false;
                string msg = on ? "Could not turn on start at sign-in." : "Could not turn off start at sign-in.";
                if (_startupDetail is not null) { _startupDetail.Text = ok ? StartupDetailText : msg; _startupDetail.ForeColor = ok ? Ui.Muted : Ui.Red; }
                if (!ok) Say(msg, true);
            });
        });
    }

}
