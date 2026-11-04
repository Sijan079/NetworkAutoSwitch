using System.Windows;
using System.Windows.Forms.Integration;
using NetworkAutoSwitch.Core;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace NetworkAutoSwitch.Tray.Fluent;

/// <summary>Starts WPF inside the WinForms tray process (once) and owns the Fluent window.</summary>
static class FluentHost
{
    static bool _ready; static FluentMainWindow? _main; static FluentContext? _cx;

    public static bool Enabled => AppSettings.Load().Style.Equals("Fluent", StringComparison.OrdinalIgnoreCase);

    public static void Init(FluentContext cx) => _cx = cx;
    public static void Feed(string line) => _cx?.FeedLog(line);

    static void EnsureReady()
    {
        if (_ready) return;
        WindowsFormsHost.EnableWindowsFormsInterop();
        var app = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // a WPF exception must not kill the tray without restoring the network: log it, show it as a pop-up, carry on
        app.DispatcherUnhandledException += (_, e) => { Paths.AppendLog("FLUENT UI ERROR: " + e.Exception); TrayProgram.OnCrash?.Invoke("Window error: " + e.Exception.Message); e.Handled = true; };
        app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ThemeFor(_cx!.GetTheme()) });
        app.Resources.MergedDictionaries.Add(new ControlsDictionary());
        _ready = true;
    }

    static ApplicationTheme ThemeFor(ThemeMode m) => ThemeSettings.Resolve(m) ? ApplicationTheme.Dark : ApplicationTheme.Light;

    public static void ApplyTheme(ThemeMode m) { if (_ready) ApplicationThemeManager.Apply(ThemeFor(m)); }

    /// <summary>Every modeless Fluent window goes through here: without the keyboard hook the WinForms message loop swallows typing.</summary>
    public static void Show(Window w)
    {
        ElementHost.EnableModelessKeyboardInterop(w);
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    public static FluentMainWindow Main()
    {
        EnsureReady();
        return _main ??= new FluentMainWindow(_cx!);
    }

    public static void ShowMain(string? page = null)
    {
        var w = Main();
        if (page is not null || !w.IsLoaded) w.Go(page ?? "Dashboard");
        Show(w);
    }

    /// <summary>The "Choose Connections" dialog, over the Fluent window (or alone when it is not open).</summary>
    public static void ChooseConnections()
    {
        EnsureReady();
        var dlg = new FluentConnectionsDialog(_cx!.Engine);
        if (_main is { IsVisible: true }) dlg.Owner = _main; else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.ShowDialog();
    }

    public static void Quit() { if (_main is not null) { _main.ReallyClose = true; _main.Close(); _main = null; } }

    /// <summary>Renders every page to fluent-&lt;page&gt;.png without showing the window (off-screen; RenderTargetBitmap, never a screen capture).</summary>
    public static void Snapshot(string folder)
    {
        EnsureReady();
        var w = new FluentMainWindow(_cx!) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false, Height = 1300 };
        // Mica is drawn by Windows, not by WPF, so a bitmap render would be see-through: use a plain stand-in for the backdrop
        bool dark = ThemeSettings.Resolve(_cx!.GetTheme());
        w.WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;
        w.Background = new System.Windows.Media.SolidColorBrush(dark ? System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20) : System.Windows.Media.Color.FromRgb(0xF3, 0xF3, 0xF3));
        w.ReallyClose = true;
        w.Show();
        foreach (var page in new[] { "Dashboard", "Log", "Policy", "Settings" })
        {
            w.Go(page);
            for (int i = 0; i < 6; i++) { Idle(w); System.Threading.Thread.Sleep(60); }
            int pw = (int)w.ActualWidth, ph = (int)w.ActualHeight;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(pw, ph, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(w);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(folder, "fluent-" + page.ToLowerInvariant() + ".png")); enc.Save(fs);
        }
        w.Close();
    }

    static void Idle(Window w) { w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); w.UpdateLayout(); }
}
