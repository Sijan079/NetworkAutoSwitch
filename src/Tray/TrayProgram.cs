using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

static class TrayProgram
{
    public static string QuitEventName(bool dryRun) => dryRun ? @"Global\NetworkAutoSwitchTray-quit-dry" : @"Global\NetworkAutoSwitchTray-quit";

    public static Action<string>? OnCrash;   // set by the tray so errors also show as a pop-up

    static void ReportCrash(string where, Exception? e)
    {
        Paths.AppendLog($"{where} CRASHED: {e}");
        try { OnCrash?.Invoke($"Something went wrong ({e?.GetType().Name}). Details are in the Log tab."); } catch { }
    }

    // Usage: NetworkAutoSwitchTray.exe [--dry-run] [--primary "Wi-Fi"] [--backup "Ethernet 2"] [--interval 5] [--theme system|light|dark]
    [STAThread]
    static int Main(string[] args)
    {
        string Arg(string k, string d) { int i = Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        bool dryRun = args.Contains("--dry-run");

        // --export-logo <folder>: write app.ico + preview images, then exit (used once to create the icon file)
        int li = Array.IndexOf(args, "--export-logo");
        if (li >= 0 && li + 1 < args.Length) { Logo.Export(args[li + 1]); return 0; }

        // --ui-test <file>: drive the add-a-rule screens in code and write the outcome to the file (for reproducing UI crashes)
        int ui = Array.IndexOf(args, "--ui-test");
        if (ui >= 0 && ui + 1 < args.Length) { Ui.SetTheme(ThemeSettings.Resolve(Enum.TryParse<ThemeMode>(Arg("--theme", ""), true, out var tm) ? tm : ThemeMode.Light)); UiSelfTest.Run(args[ui + 1]); return 0; }

        // --quit: ask the running tray to exit the normal way (it restores metrics and rules first). Needs no admin.
        if (args.Contains("--quit"))
        {
            try { using var e = EventWaitHandleAcl.OpenExisting(QuitEventName(dryRun), System.Security.AccessControl.EventWaitHandleRights.Modify); e.Set(); return 0; }
            catch { return 1; }   // not running
        }

        // Changing metrics and firewall rules needs admin. Ask Windows (UAC) once at start, then run elevated.
        if (!dryRun && !BackupPolicy.IsAdmin())
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!, string.Join(" ", args.Select(a => $"\"{a}\""))) { UseShellExecute = true, Verb = "runas" });
            }
            catch { MessageBox.Show("Network Auto Switch needs administrator rights to change network settings.", "Network Auto Switch"); }
            return 0;
        }

        // --restore: put the network back the way the app found it (saved metrics, and remove the app's firewall/QoS rules), then exit.
        // The uninstaller runs this so nothing of ours is left behind even if the app was killed instead of closed.
        if (args.Contains("--restore"))
        {
            var cmds = MetricSwitcher.RestoreFromState().Concat(BackupPolicy.RemoveCommands()).ToList();
            int rc = BackupPolicy.RunParallel(cmds, dryRun, m => Paths.AppendLog(m));
            if (!dryRun && rc == 0) MetricSwitcher.ClearState();
            Paths.AppendLog($"restore {(dryRun ? "dry-run" : rc == 0 ? "done" : "FAILED")}");
            return rc;
        }

        // A dry-run copy (testing only, changes nothing) gets its own lock so it can run beside the real one.
        using var single = new Mutex(true, dryRun ? @"Global\NetworkAutoSwitchTray-dry" : @"Global\NetworkAutoSwitchTray", out bool first);
        if (!first) { MessageBox.Show("Network Auto Switch is already running (check the tray).", "Network Auto Switch"); return 0; }

        // theme: --theme overrides the saved choice for this run only (used for testing)
        var forced = Arg("--theme", "");
        ThemeMode mode = Enum.TryParse<ThemeMode>(forced, true, out var f) ? f : ThemeSettings.Load();
        Ui.SetTheme(ThemeSettings.Resolve(mode));

        // A mistake inside the window must never take the tray (and with it the network switching) down, and must never fail silently:
        // it is written to the log with its full stack trace and shown as a pop-up. Must be set before any window exists.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash("UI", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash("FATAL", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { ReportCrash("TASK", e.Exception); e.SetObserved(); };

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext(Arg("--primary", ""), Arg("--backup", ""), int.Parse(Arg("--interval", "1")), dryRun, args.Contains("--show"), mode, saveTheme: forced.Length == 0));
        return 0;
    }
}

sealed class TrayContext : ApplicationContext
{
    readonly NotifyIcon _icon;
    ToolStripMenuItem _status = new(), _pause = new();
    readonly SwitchEngine _engine;
    readonly CancellationTokenSource _cts = new();
    readonly Task _run;
    readonly Control _ui = new();     // hidden control so engine events can hop onto the UI thread
    Icon _green = null!, _amber = null!, _red = null!, _grey = null!;
    MainForm? _window;
    EngineStatus? _lastStatus;
    string _primary => _engine.PrimaryName;
    string _backup => _engine.BackupName.Length > 0 ? _engine.BackupName : "No Backup";
    ThemeMode _mode;
    readonly bool _saveTheme;
    bool _stopping;

    public TrayContext(string primary, string backup, int interval, bool dryRun, bool showWindow, ThemeMode mode, bool saveTheme)
    {
        _mode = mode; _saveTheme = saveTheme;
        _ = _ui.Handle;
        MakeIcons();
        _engine = new SwitchEngine(primary, backup, Paths.DefaultPolicy, enforce: true, execute: !dryRun, interval);
        _engine.Log = m => { Paths.AppendLog(m); _window?.AddLog(m); Fluent.FluentHost.Feed(m); };
        _icon = new NotifyIcon { Icon = _grey, Visible = true, ContextMenuStrip = BuildMenu(), Text = "Network Auto Switch: starting" };
        _icon.DoubleClick += (_, _) => Open("Dashboard");

        _window = NewWindow();
        Fluent.FluentHost.Init(new Fluent.FluentContext(_engine, TogglePause, () => _mode, SetThemeMode, () => _mute, SetMute));
        _engine.AdaptersChanged += () => { if (!_stopping) _ui.BeginInvoke(RebuildMenu); };
        // Blocks never expire by themselves, so tell the user and make the pop-up open the dashboard (where the Unblock buttons are).
        _engine.WatchdogBlocked += a => { if (!_stopping) _ui.BeginInvoke(() => Notify($"{a.Rule} was downloading, so it is blocked on the backup. Click here to unblock it.", ToolTipIcon.Warning)); };
        _icon.BalloonTipClicked += (_, _) => Open("Log");
        TrayProgram.OnCrash = msg => { if (!_stopping) _ui.BeginInvoke(() => Notify(msg, ToolTipIcon.Error)); };
        if (showWindow) Open("Dashboard");

        // --snapshot <folder>: dry-run helper. After 20 s save PNGs of each tab, then quit (no screen capture involved).
        var snap = Environment.GetCommandLineArgs().SkipWhile(a => a != "--snapshot").Skip(1).FirstOrDefault();
        if (snap is not null)
        {
            var t = new System.Windows.Forms.Timer { Interval = 20000 };
            t.Tick += (_, _) => { t.Stop(); try { _window!.Show(); _window.SaveSnapshots(snap); Fluent.FluentHost.Snapshot(snap); } catch (Exception e) { Paths.AppendLog("snapshot failed: " + e.Message); } Quit(); };
            t.Start();
        }
        _engine.Status += s => { if (!_stopping) _ui.BeginInvoke(() => OnStatus(s)); };
        _run = Task.Run(async () =>
        {
            try { await _engine.RunAsync(0, _cts.Token); }
            catch (Exception e) { Paths.AppendLog("ENGINE CRASHED: " + e); _ui.BeginInvoke(() => Notify("Stopped: " + e.Message, ToolTipIcon.Error)); }
        });
        StartQuitListener(dryRun);
        Application.ApplicationExit += (_, _) => Stop();
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => Stop();
        // "Follow System": when Windows switches between light and dark, follow it
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (_mode == ThemeMode.System && e.Category == Microsoft.Win32.UserPreferenceCategory.General && !_stopping)
                _ui.BeginInvoke(() => { if (ThemeSettings.Resolve(_mode) != Ui.Dark) ApplyTheme(); });
        };
        Paths.AppendLog($"tray started: {AppInfo.Name} {AppInfo.FullVersion}" + (dryRun ? " (DRY-RUN mode)" : ""));
    }

    /// <summary>The adapters changed (phone plugged in, adapter renamed or chosen by you): the menu's "Stay On ..." entries follow.</summary>
    void RebuildMenu()
    {
        if (_stopping) return;
        var old = _icon.ContextMenuStrip; _icon.ContextMenuStrip = BuildMenu(); old?.Dispose();
    }

    MainForm NewWindow() => new(_engine, TogglePause, SetThemeMode, () => _mode, () => _mute, SetMute);

    bool _mute = AppSettings.Load().MuteNotifications;
    void SetMute(bool on) { _mute = on; try { AppSettings.Update(x => x.MuteNotifications = on); } catch { } }

    // ---------- theme ----------
    /// <summary>Opens a page in the window of the chosen style (Settings: Style).</summary>
    void Open(string page) { if (Fluent.FluentHost.Enabled) Fluent.FluentHost.ShowMain(page); else _window?.ShowWindow(page); }

    void SetThemeMode(ThemeMode m)
    {
        _mode = m;
        if (_saveTheme) ThemeSettings.Save(m);
        ApplyTheme();
    }

    /// <summary>Rebuilds the window, the tray menu and the status icons with the new palette (everything is drawn from the palette at creation).</summary>
    void ApplyTheme()
    {
        Ui.SetTheme(ThemeSettings.Resolve(_mode));
        Fluent.FluentHost.ApplyTheme(_mode);
        var old = _window;
        bool wasVisible = old?.Visible ?? false; string tab = old?.CurrentTab ?? "dashboard"; var bounds = old?.Bounds ?? Rectangle.Empty;
        old?.Detach(); old?.CloseForReal();

        MakeIcons();
        var oldMenu = _icon.ContextMenuStrip; _icon.ContextMenuStrip = BuildMenu(); oldMenu?.Dispose();
        _window = NewWindow();
        if (wasVisible)
        {
            _window.StartPosition = FormStartPosition.Manual; _window.Bounds = bounds;
            _window.ShowWindow(tab);
        }
        if (_lastStatus is not null) { _window.ShowStatus(_lastStatus); _icon.Icon = IconFor(_lastStatus); }
        Paths.AppendLog($"theme: {_mode} ({(Ui.Dark ? "dark" : "light")})");
    }

    void MakeIcons() { _green = Logo.Icon(Ui.Green); _amber = Logo.Icon(Ui.Amber); _red = Logo.Icon(Ui.Red); _grey = Logo.Icon(Color.Gray); }

    ContextMenuStrip BuildMenu()
    {
        _status = new ToolStripMenuItem(string.IsNullOrEmpty(_status?.Text) ? "Starting..." : _status.Text) { Enabled = false };
        _pause = new ToolStripMenuItem(_engineIsPaused ? "Resume" : "Pause", Ui.Glyph(_engineIsPaused ? Ui.Play : Ui.Pause, 16, Ui.Text), (_, _) => TogglePause());
        var modeMenu = new ToolStripMenuItem("Mode", Ui.Glyph(Ui.Swap, 16, Ui.Text));
        foreach (var (m, text) in new[] { (ConnectionMode.Auto, "Automatic"), (ConnectionMode.StayPrimary, $"Stay On {_primary}"), (ConnectionMode.StayBackup, $"Stay On {_backup}") })
            modeMenu.DropDownItems.Add(new ToolStripMenuItem(text, null, (_, _) => _engine.SetConnectionMode(m)) { Checked = _engine.ConnMode == m });
        modeMenu.DropDownItems.Add(new ToolStripSeparator());
        modeMenu.DropDownItems.Add(new ToolStripMenuItem("Choose Connections...", null, (_, _) => { if (Fluent.FluentHost.Enabled) Fluent.FluentHost.ChooseConnections(); else _window?.ChooseConnections(); }));

        var menu = new ContextMenuStrip { Renderer = new ThemedRenderer(), BackColor = Ui.CardBg, ForeColor = Ui.Text, ShowImageMargin = true, Font = new Font("Segoe UI", 9.5f) };
        menu.Items.AddRange(new ToolStripItem[]
        {
            _status, new ToolStripSeparator(),
            new ToolStripMenuItem("Dashboard", Ui.Glyph(Ui.Home, 16, Ui.Text), (_, _) => Open("Dashboard")) { Font = new Font("Segoe UI Semibold", 9.5f) },
            _pause,
            new ToolStripMenuItem("Log", Ui.Glyph(Ui.Document, 16, Ui.Text), (_, _) => Open("Log")),
            new ToolStripMenuItem("Policy", Ui.Glyph(Ui.Settings, 16, Ui.Text), (_, _) => Open("Policy")),
            modeMenu,
            new ToolStripMenuItem("Fluent Window (Preview)", Ui.Glyph(Ui.Settings, 16, Ui.Text), (_, _) => Fluent.FluentHost.ShowMain("Dashboard")),
            new ToolStripMenuItem("Settings", Ui.Glyph(Ui.Settings, 16, Ui.Text), (_, _) => Open("Settings")),
            new ToolStripSeparator(),
            new ToolStripMenuItem($"Version {AppInfo.Version}") { Enabled = false },
            new ToolStripMenuItem("Exit", Ui.Glyph(Ui.Clear, 16, Ui.Text), (_, _) => Quit()),
        });
        foreach (ToolStripItem it in menu.Items) { it.BackColor = Ui.CardBg; it.ForeColor = it.Enabled ? Ui.Text : Ui.Muted; it.Padding = new Padding(4, 5, 4, 5); }
        foreach (var sub in new[] { modeMenu })
        {
            foreach (ToolStripItem it in sub.DropDownItems) { it.BackColor = Ui.CardBg; it.ForeColor = Ui.Text; }
            sub.DropDown.BackColor = Ui.CardBg;
            if (sub.DropDown is ToolStripDropDownMenu dd) dd.Renderer = new ThemedRenderer();
        }
        return menu;
    }

    bool _engineIsPaused => _engine?.Paused ?? false;

    Icon IconFor(EngineStatus s) { bool anyOk = s.Primary.Healthy || s.Backup.Healthy; return s.Paused ? _grey : !anyOk ? _red : s.Active == Active.Primary ? _green : _amber; }

    void OnStatus(EngineStatus s)
    {
        if (_stopping) return;        // a status can still be queued when the tray is quitting: the icon is already gone by then
        _lastStatus = s;
        _icon.Icon = IconFor(s);
        string Net(string? n) => string.IsNullOrEmpty(n) ? "" : $" – {n}";
        string which = s.Paused ? "Paused" : s.Active == Active.Primary ? $"On {_primary}{Net(_engine.PrimaryNetwork)}" : $"On Backup ({_backup}{Net(_engine.BackupNetwork)})";
        if (s.Mode != ConnectionMode.Auto && !s.Paused) which += " [Locked]";
        string line = $"{which} | {_primary}: {(s.Primary.Healthy ? s.Primary.Ms + "ms" : "down")} | {_backup}: {(s.Backup.Healthy ? s.Backup.Ms + "ms" : "down")}";
        _status.Text = line;
        _icon.Text = line.Length > 63 ? line[..63] : line;
        if (s.Changed)
        {
            Paths.AppendLog($"SWITCH -> {s.Active}: {s.Reason}");
            Notify(s.Active == Active.Backup ? $"{_primary} lost internet. Switched to {_backup}." : $"{_primary} is back. Switched back.", ToolTipIcon.Info);
        }
    }

    EventWaitHandle? _quitEvent;

    /// <summary>Lets a non-admin "NetworkAutoSwitchTray.exe --quit" ask this (elevated) tray to exit cleanly.</summary>
    void StartQuitListener(bool dryRun)
    {
        try
        {
            var sec = new System.Security.AccessControl.EventWaitHandleSecurity();
            sec.AddAccessRule(new System.Security.AccessControl.EventWaitHandleAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.EventWaitHandleRights.Modify | System.Security.AccessControl.EventWaitHandleRights.Synchronize,
                System.Security.AccessControl.AccessControlType.Allow));
            _quitEvent = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, TrayProgram.QuitEventName(dryRun), out _, sec);
            var evt = _quitEvent;
            new Thread(() => { try { evt.WaitOne(); if (!_stopping) _ui.BeginInvoke(Quit); } catch { } }) { IsBackground = true }.Start();
        }
        catch (Exception e) { Paths.AppendLog("quit listener not available: " + e.Message); }
    }

    void TogglePause()
    {
        // Pause/Resume run PowerShell commands (a few seconds), so keep them off the UI thread.
        bool pausing = !_engine.Paused;
        _pause.Enabled = false; _status.Text = pausing ? "Pausing..." : "Resuming...";
        Task.Run(() => { if (pausing) _engine.Pause(); else _engine.Resume(); })
            .ContinueWith(_ => _ui.BeginInvoke(() =>
            {
                _pause.Enabled = true;
                _pause.Text = _engine.Paused ? "Resume" : "Pause"; _pause.Image = Ui.Glyph(_engine.Paused ? Ui.Play : Ui.Pause, 16, Ui.Text);
                if (_engine.Paused) { _icon.Icon = _grey; _status.Text = "Paused (Windows back to normal)"; }
            }));
    }

    readonly bool _quiet = Environment.GetCommandLineArgs().Contains("--snapshot");   // snapshot test runs show no pop-ups
    void Notify(string text, ToolTipIcon kind) { if (!_quiet && !_mute) _icon.ShowBalloonTip(4000, "Network Auto Switch", text, kind); }

    /// <summary>Stops the engine and waits (max 20 s) so it can restore metrics and remove rules.</summary>
    void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        _cts.Cancel();
        try { _run.Wait(TimeSpan.FromSeconds(20)); } catch { }
    }

    void Quit()
    {
        _status.Text = "Restoring network settings...";
        Stop();
        _window?.CloseForReal(); Fluent.FluentHost.Quit();
        _icon.Visible = false; _icon.Dispose();
        ExitThread();
    }
}
