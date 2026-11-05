using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

/// <summary>Asked once for a network that is on neither table: is it your main connection or a backup (your phone)?</summary>
sealed class NetworkRoleDialog : Form
{
    /// <summary>Main, Backup, or Unlabelled when the user chose "Later".</summary>
    public NetworkRole Answer { get; private set; } = NetworkRole.Unlabelled;

    public NetworkRoleDialog(string network)
    {
        Text = "New Network"; Icon = Ui.AppIcon(Ui.Blue);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; TopMost = true;
        ClientSize = new Size(460, 220); StartPosition = FormStartPosition.CenterScreen; BackColor = Ui.Bg; Font = Ui.UiFont(9.5f);

        var icon = new PictureBox { Image = Ui.Glyph(Ui.Wifi, 34, Ui.Blue), SizeMode = PictureBoxSizeMode.CenterImage, Size = new Size(56, 56), Location = new Point(Ui.Pad + 4, Ui.Pad + 4), BackColor = Ui.Bg };
        var name = new Label { Text = network, AutoEllipsis = true, Font = Ui.UiFont(13f, bold: true), ForeColor = Ui.Text, BackColor = Ui.Bg, Location = new Point(84, Ui.Pad + 6), Size = new Size(360, 28) };
        var ask = new Label { Text = "Is this your main connection, or a backup like your phone?", ForeColor = Ui.Text, BackColor = Ui.Bg, Location = new Point(84, Ui.Pad + 38), Size = new Size(360, 24) };
        var note = new Label { Text = "A backup network blocks heavy downloads and counts toward your data limit.", ForeColor = Ui.Muted, BackColor = Ui.Bg, Location = new Point(84, Ui.Pad + 66), Size = new Size(360, 40) };

        void Pick(NetworkRole r) { Answer = r; DialogResult = r == NetworkRole.Unlabelled ? DialogResult.Cancel : DialogResult.OK; }
        var main = Ui.Btn(Ui.Home, "Main", "Your normal connection: home, dorm or work.", () => Pick(NetworkRole.Main), BtnKind.Primary);
        var backup = Ui.Btn(Ui.Phone, "Backup", "Your phone, or any connection with limited data.", () => Pick(NetworkRole.Backup));
        var later = Ui.Btn(Ui.Clock, "Later", "Ask again the next time the app starts. Until then it is treated as a backup.", () => Pick(NetworkRole.Unlabelled));
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(Ui.Pad + 4, Ui.Gap, Ui.Pad + 4, Ui.Gap), BackColor = Ui.Bg };
        bar.Controls.Add(later); bar.Controls.Add(backup); bar.Controls.Add(main);

        Controls.AddRange(new Control[] { icon, name, ask, note, bar });
        AcceptButton = main; CancelButton = later;
        Load += (_, _) => Ui.SkinForm(this);
    }
}
