using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

// Settings page: adapters, timings, app options and the update card.
sealed partial class MainForm
{
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

}
