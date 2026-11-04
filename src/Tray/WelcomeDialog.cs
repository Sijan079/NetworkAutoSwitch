using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

/// <summary>Shown once, the first time the app runs on a PC: what it found (main and backup connection), what it will do, and the chance to fix the choice or turn on
/// "start when I sign in" before anything happens.</summary>
sealed class WelcomeDialog : Form
{
    readonly SwitchEngine _engine;
    readonly Label _found = new() { AutoSize = false, Dock = DockStyle.Top, Height = 118, ForeColor = Ui.Text, BackColor = Ui.CardBg, Padding = new Padding(16, 12, 16, 8), Font = Ui.UiFont(10.5f) };
    readonly CheckBox _startup = new() { Text = "Start When I Sign In", AutoSize = true, BackColor = Ui.Bg, ForeColor = Ui.Text };
    readonly System.Windows.Forms.Timer _refresh = new() { Interval = 1500 };

    public bool StartAtSignIn => _startup.Checked;

    public WelcomeDialog(SwitchEngine engine)
    {
        _engine = engine;
        Text = "Welcome"; Icon = Ui.AppIcon(Ui.Blue);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = true;
        ClientSize = new Size(600, 420); StartPosition = FormStartPosition.CenterScreen; BackColor = Ui.Bg; Font = Ui.UiFont(9.5f);

        var title = new Label { Text = "Welcome To Network Auto Switch", AutoSize = false, Dock = DockStyle.Top, Height = 56, Font = Ui.UiFont(17f, bold: true), ForeColor = Ui.Text, BackColor = Ui.Bg, Padding = new Padding(Ui.Pad, 14, 0, 0) };
        var intro = new Label
        {
            Text = "It keeps a main connection and a backup (your phone) and switches between them for you. If the main one loses internet, it moves to the backup within seconds " +
                   "and keeps big downloads off your mobile data. When the main one is back, it switches back.",
            AutoSize = false, Dock = DockStyle.Top, Height = 100, ForeColor = Ui.Muted, BackColor = Ui.Bg, Padding = new Padding(Ui.Pad, 4, Ui.Pad, 0)
        };
        var foundHost = new Panel { Dock = DockStyle.Top, Height = 130, Padding = new Padding(Ui.Pad, 6, Ui.Pad, 6), BackColor = Ui.Bg };
        foundHost.Controls.Add(_found);
        var optHost = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(Ui.Pad, 8, Ui.Pad, 0), BackColor = Ui.Bg };
        _startup.Dock = DockStyle.Left; optHost.Controls.Add(_startup);
        _startup.Checked = StartupTask.IsEnabled();

        var ok = Ui.Btn(Ui.Check, "Looks Good", "Close this and start using the app.", () => DialogResult = DialogResult.OK, BtnKind.Primary);
        var choose = Ui.Btn(Ui.Settings, "Choose Connections", "Pick the main and the backup connection yourself.", () => { using var d = new ConnectionsDialog(_engine); d.ShowDialog(this); Show_(); });
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(Ui.Pad, Ui.Gap, Ui.Pad, Ui.Gap), BackColor = Ui.Bg };
        bar.Controls.Add(ok); bar.Controls.Add(choose);
        Controls.Add(optHost); Controls.Add(foundHost); Controls.Add(intro); Controls.Add(title); Controls.Add(bar);
        AcceptButton = ok;

        // the adapters (and the Wi-Fi network names) are still being read in the first seconds: keep the text fresh
        _refresh.Tick += (_, _) => Show_(); _refresh.Start(); FormClosed += (_, _) => _refresh.Stop();
        Load += (_, _) => Ui.SkinForm(this);
        Show_();
    }

    static string Describe(string name, AdapterKind kind, string? network) =>
        $"{name}  ·  {AdapterDiscovery.KindLabel(kind)}" + (string.IsNullOrEmpty(network) ? "" : $"  ·  {network}");

    void Show_()
    {
        string main = _engine.PrimaryName.Length > 0 ? Describe(_engine.PrimaryName, _engine.PrimaryKind, _engine.PrimaryNetwork) : "none found";
        string backup = _engine.BackupName.Length > 0
            ? Describe(_engine.BackupName, _engine.BackupKind, _engine.BackupNetwork)
            : "none found yet. Plug in your phone with USB tethering (or connect Bluetooth tethering) and it is picked up by itself.";
        _found.Text = $"Here is what I found:{Environment.NewLine}{Environment.NewLine}Main connection:    {main}{Environment.NewLine}Backup connection:  {backup}";
    }
}
