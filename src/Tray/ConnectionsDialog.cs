using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

/// <summary>Choose which adapter is the main connection and which is the backup, or leave either on Automatic (detected for you,
/// and re-detected whenever a phone is plugged in or an adapter changes).</summary>
sealed class ConnectionsDialog : Form
{
    sealed record Choice(string? Name, string Text) { public override string ToString() => Text; }

    readonly SwitchEngine _engine;
    readonly NoWheelComboBox _main = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, Height = 32 };
    readonly NoWheelComboBox _backup = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, Height = 32 };
    readonly Label _problem = new() { AutoSize = true, ForeColor = Ui.Red, Padding = new Padding(0, 6, 0, 0), BackColor = Ui.Bg };
    readonly Button _save;

    public ConnectionsDialog(SwitchEngine engine)
    {
        _engine = engine;
        Text = "Choose Connections"; Icon = Ui.AppIcon(Ui.Blue);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(560, 285); StartPosition = FormStartPosition.CenterParent; BackColor = Ui.Bg; Font = Ui.UiFont(9.5f);

        var all = AdapterDiscovery.Candidates(AdapterDiscovery.Detect());
        Choice Auto(string current) => new(null, $"Automatic  ({(current.Length > 0 ? current : "none found")})");
        Choice Of(AdapterInfo a) => new(a.Name, $"{a.Name}  ·  {AdapterDiscovery.KindLabel(a.Kind)}" + (a.IsUp ? "" : "  ·  Not Connected"));

        Fill(_main, Auto(engine.PrimaryName), all, Of, engine.UserPrimary);
        Fill(_backup, Auto(engine.BackupName), all, Of, engine.UserBackup);
        Ui.Tip.SetToolTip(_main, "The connection you normally use.");
        Ui.Tip.SetToolTip(_backup, "The connection to fall back on, usually your phone. Automatic keeps looking: a phone plugged in later is picked up.");

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Ui.Pad + 4, Ui.Pad, Ui.Pad + 4, 0), BackColor = Ui.Bg };
        Label Cap(string t) => new() { Text = t, AutoSize = false, Height = 26, Dock = DockStyle.Top, ForeColor = Ui.Muted, Font = Ui.UiFont(9.5f, bold: true), BackColor = Ui.Bg, Padding = new Padding(0, 4, 0, 0) };
        Control Gap() => new Panel { Dock = DockStyle.Top, Height = 14, BackColor = Ui.Bg };
        // docking: the control added LAST is at the top
        body.Controls.Add(_problem); body.Controls.Add(Gap()); body.Controls.Add(_backup); body.Controls.Add(Cap("Backup Connection"));
        body.Controls.Add(Gap()); body.Controls.Add(_main); body.Controls.Add(Cap("Main Connection"));

        _save = Ui.Btn(Ui.Save, "Save", "Use these connections from now on.", Save, BtnKind.Primary);
        var cancel = Ui.Btn(Ui.Clear, "Cancel", "Close without changing anything.", () => DialogResult = DialogResult.Cancel);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(Ui.Pad + 4, Ui.Gap, Ui.Pad + 4, Ui.Gap), BackColor = Ui.Bg };
        bar.Controls.Add(_save); bar.Controls.Add(cancel);
        Controls.Add(body); Controls.Add(bar);
        AcceptButton = _save; CancelButton = cancel;

        _main.SelectedIndexChanged += (_, _) => Validate_();
        _backup.SelectedIndexChanged += (_, _) => Validate_();
        Load += (_, _) => Ui.SkinForm(this);
        Validate_();
    }

    static void Fill(ComboBox box, Choice auto, List<AdapterInfo> all, Func<AdapterInfo, Choice> of, string chosen)
    {
        box.Items.Add(auto);
        foreach (var a in all) box.Items.Add(of(a));
        int i = string.IsNullOrEmpty(chosen) ? 0 : box.Items.Cast<Choice>().ToList().FindIndex(c => string.Equals(c.Name, chosen, StringComparison.OrdinalIgnoreCase));
        if (i < 0) { box.Items.Add(new Choice(chosen, $"{chosen}  ·  Not Found")); i = box.Items.Count - 1; }   // an earlier choice for an adapter that is not here now stays visible
        box.SelectedIndex = i;
    }

    string? Picked(ComboBox b) => (b.SelectedItem as Choice)?.Name;

    void Validate_()
    {
        bool same = Picked(_main) is string m && Picked(_backup) is string b && m.Equals(b, StringComparison.OrdinalIgnoreCase);
        _problem.Text = same ? "The main and the backup connection must be different adapters." : "";
        _save.Enabled = !same;
    }

    void Save()
    {
        _engine.SetAdapterChoice(Picked(_main), Picked(_backup));
        DialogResult = DialogResult.OK;
    }
}
