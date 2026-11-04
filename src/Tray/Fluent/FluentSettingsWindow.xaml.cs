using System.Windows;
using NetworkAutoSwitch.Core;
using Wpf.Ui.Controls;

namespace NetworkAutoSwitch.Tray.Fluent;

/// <summary>Trial of the real WPF UI (Fluent) look: the Settings page. Same engine and settings as the classic window.</summary>
public partial class FluentSettingsWindow : FluentWindow
{
    readonly SwitchEngine _engine;
    readonly Func<ThemeMode> _getTheme; readonly Action<ThemeMode> _setTheme;
    readonly Func<bool> _getMute; readonly Action<bool> _setMute;

    internal FluentSettingsWindow(SwitchEngine engine, Func<ThemeMode> getTheme, Action<ThemeMode> setTheme, Func<bool> getMute, Action<bool> setMute)
    {
        _engine = engine; _getTheme = getTheme; _setTheme = setTheme; _getMute = getMute; _setMute = setMute;
        InitializeComponent();
        LoadValues();
        Loaded += (_, _) => LoadValues();      // NumberBox only shows its text once it is on screen
        SaveBtn.Click += (_, _) => Save();
        RevertBtn.Click += (_, _) => { LoadValues(); StatusText.Text = ""; };
        ThemeBox.SelectionChanged += (_, _) => { if (IsLoaded) { var m = (ThemeMode)ThemeBox.SelectedIndex; _setTheme(m); FluentHost.ApplyTheme(m); } };
        MuteSwitch.Click += (_, _) => _setMute(MuteSwitch.IsChecked == true);
    }

    void LoadValues()
    {
        ThemeBox.SelectedIndex = (int)_getTheme();
        MuteSwitch.IsChecked = _getMute();
        RecoverBox.Value = _engine.RecoverSeconds;
        var g = ReadPolicy().Global;
        MbBox.Value = g.MbPerWindow; SecBox.Value = g.WindowSec;
    }

    static PolicyFile ReadPolicy()
    {
        try { return BackupPolicy.Parse(File.ReadAllText(Paths.DefaultPolicy)); } catch { return new PolicyFile(new List<AppRule>()); }
    }

    void Save()
    {
        try
        {
            _engine.SetRecoverSeconds((int)(RecoverBox.Value ?? 10));
            var next = ReadPolicy() with { Defaults = new WatchDefaults(MbBox.Value ?? 2, (int)(SecBox.Value ?? 10)) };
            string json = BackupPolicy.ToJson(next); BackupPolicy.Parse(json);
            if (File.Exists(Paths.DefaultPolicy)) File.Copy(Paths.DefaultPolicy, Paths.DefaultPolicy + ".bak", true);
            File.WriteAllText(Paths.DefaultPolicy, json);
            StatusText.Text = "Saving...";
            Task.Run(() =>
            {
                try { _engine.ReloadPolicy(); Dispatcher.Invoke(() => StatusText.Text = "Saved and applied."); }
                catch (Exception e) { Dispatcher.Invoke(() => StatusText.Text = "Saved, but applying failed: " + e.Message); }
            });
        }
        catch (Exception e) { StatusText.Text = "Not saved. " + e.Message; }
    }
}
