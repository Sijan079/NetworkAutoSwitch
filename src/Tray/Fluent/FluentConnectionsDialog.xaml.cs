using System.Windows;
using System.Windows.Controls;
using NetworkAutoSwitch.Core;
using Wpf.Ui.Controls;

namespace NetworkAutoSwitch.Tray.Fluent;

/// <summary>Choose which adapter is the main connection and which is the backup, or leave either on Automatic.</summary>
public partial class FluentConnectionsDialog : FluentWindow
{
    sealed record Choice(string? Name, string Text) { public override string ToString() => Text; }
    readonly SwitchEngine _engine;

    internal FluentConnectionsDialog(SwitchEngine engine)
    {
        _engine = engine;
        InitializeComponent();
        var all = AdapterDiscovery.Candidates(AdapterDiscovery.Detect());
        Choice Auto(string current) => new(null, $"Automatic  ({(current.Length > 0 ? current : "none found")})");
        Choice Of(AdapterInfo a) => new(a.Name, $"{a.Name}  ·  {AdapterDiscovery.KindLabel(a.Kind)}" + (a.IsUp ? "" : "  ·  Not Connected"));
        Fill(MainBox, Auto(engine.PrimaryName), all, Of, engine.UserPrimary);
        Fill(BackupBox, Auto(engine.BackupName), all, Of, engine.UserBackup);
        MainBox.SelectionChanged += (_, _) => Check(); BackupBox.SelectionChanged += (_, _) => Check();
        SaveBtn.Click += (_, _) => Save();
        Check();
    }

    static void Fill(ComboBox box, Choice auto, List<AdapterInfo> all, Func<AdapterInfo, Choice> of, string chosen)
    {
        box.Items.Add(auto);
        foreach (var a in all) box.Items.Add(of(a));
        int i = string.IsNullOrEmpty(chosen) ? 0 : box.Items.Cast<Choice>().ToList().FindIndex(c => string.Equals(c.Name, chosen, StringComparison.OrdinalIgnoreCase));
        if (i < 0) { box.Items.Add(new Choice(chosen, $"{chosen}  ·  Not Found")); i = box.Items.Count - 1; }   // an earlier choice for an adapter that is not here now stays visible
        box.SelectedIndex = i;
    }

    static string? Picked(ComboBox b) => (b.SelectedItem as Choice)?.Name;

    void Check()
    {
        bool same = Picked(MainBox) is string m && Picked(BackupBox) is string b && m.Equals(b, StringComparison.OrdinalIgnoreCase);
        Problem.Text = same ? "The main and the backup connection must be different adapters." : "";
        SaveBtn.IsEnabled = !same;
    }

    void Save()
    {
        var (m, b) = (Picked(MainBox), Picked(BackupBox));
        DialogResult = true;                                            // close now; the engine applies it in the background
        Task.Run(() => { try { _engine.SetAdapterChoice(m, b); } catch (Exception e) { Paths.AppendLog("Choosing connections failed: " + e.Message); } });
    }
}
