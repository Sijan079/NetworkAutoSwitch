using System.Windows;
using System.Windows.Forms.Integration;
using NetworkAutoSwitch.Core;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace NetworkAutoSwitch.Tray.Fluent;

/// <summary>Starts WPF inside the WinForms tray process (once) and shows the Fluent windows.</summary>
static class FluentHost
{
    static bool _ready; static FluentSettingsWindow? _settings;

    static void EnsureReady(ThemeMode mode)
    {
        if (_ready) return;
        WindowsFormsHost.EnableWindowsFormsInterop();
        var app = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ThemeFor(mode) });
        app.Resources.MergedDictionaries.Add(new ControlsDictionary());
        _ready = true;
    }

    static ApplicationTheme ThemeFor(ThemeMode m) => ThemeSettings.Resolve(m) ? ApplicationTheme.Dark : ApplicationTheme.Light;

    public static void ApplyTheme(ThemeMode m) { if (_ready) ApplicationThemeManager.Apply(ThemeFor(m)); }

    public static FluentSettingsWindow ShowSettings(SwitchEngine engine, Func<ThemeMode> getTheme, Action<ThemeMode> setTheme, Func<bool> getMute, Action<bool> setMute)
    {
        EnsureReady(getTheme());
        if (_settings is { IsLoaded: true }) { _settings.Activate(); return _settings; }
        _settings = new FluentSettingsWindow(engine, getTheme, setTheme, getMute, setMute);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        return _settings;
    }

    /// <summary>Renders the Fluent Settings window to a PNG without showing it on screen (off-screen, rendered with RenderTargetBitmap).</summary>
    public static void Snapshot(SwitchEngine engine, Func<ThemeMode> getTheme, Action<ThemeMode> setTheme, Func<bool> getMute, Action<bool> setMute, string file)
    {
        EnsureReady(getTheme());
        var w = new FluentSettingsWindow(engine, getTheme, setTheme, getMute, setMute) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
        // Mica is drawn by Windows, not by WPF, so a bitmap render would be see-through: use a plain stand-in for the backdrop
        bool dark = ThemeSettings.Resolve(getTheme());
        w.WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;
        w.Background = new System.Windows.Media.SolidColorBrush(dark ? System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20) : System.Windows.Media.Color.FromRgb(0xF3, 0xF3, 0xF3));
        w.Show();
        w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        w.UpdateLayout();
        int pw = (int)w.ActualWidth, ph = (int)w.ActualHeight;
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(pw, ph, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(w);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using (var fs = File.Create(file)) enc.Save(fs);
        w.Close();
    }
}
