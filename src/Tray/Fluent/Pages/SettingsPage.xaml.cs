using System.Windows;
using System.Windows.Controls;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray.Fluent.Pages;

public partial class SettingsPage : Page
{
    readonly FluentContext _cx;

    internal SettingsPage(FluentContext cx)
    {
        _cx = cx;
        InitializeComponent();
        LoadValues();
        Loaded += (_, _) => LoadValues();                // number boxes only show their text once on screen
        SaveBtn.Click += (_, _) => Save();
        RevertBtn.Click += (_, _) => { LoadValues(); StatusText.Text = ""; };
        ThemeBox.SelectionChanged += (_, _) => { if (IsLoaded) { var m = (ThemeMode)ThemeBox.SelectedIndex; _cx.SetTheme(m); FluentHost.ApplyTheme(m); } };
        StyleBox.SelectionChanged += (_, _) => { if (IsLoaded) AppSettings.Update(s => s.Style = StyleBox.SelectedIndex == 1 ? "Fluent" : "Classic"); };
        MuteSwitch.Click += (_, _) => _cx.SetMute(MuteSwitch.IsChecked == true);
    }

    void LoadValues()
    {
        ThemeBox.SelectedIndex = (int)_cx.GetTheme();
        StyleBox.SelectedIndex = AppSettings.Load().Style.Equals("Fluent", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        MuteSwitch.IsChecked = _cx.GetMute();
        RecoverBox.Value = _cx.Engine.RecoverSeconds;
        MbBox.Value = _cx.ReadPolicy().Global.MbPerWindow;
    }

    void Save()
    {
        try
        {
            _cx.Engine.SetRecoverSeconds((int)RecoverBox.Value);
            // re-read the file right before writing: other pages may have saved rules since this page was opened
            var next = _cx.ReadPolicy();
            next = next with { Defaults = (next.Defaults ?? new WatchDefaults()) with { MbPerWindow = MbBox.Value, WindowSec = 10 } };
            _cx.WritePolicy(next, msg => StatusText.Text = msg);
        }
        catch (Exception e) { StatusText.Text = "Not saved. " + e.Message; }
    }
}
