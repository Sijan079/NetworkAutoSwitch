using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

/// <summary>"What Is This Network?": asked once for a network that is on neither table. Main (your normal connection), Backup (your phone),
/// or Decide Later. Main/Backup close with OK and set <see cref="Answer"/>; Decide Later, Esc and the X close with Cancel and leave it Unlabelled.</summary>
sealed class NetworkRoleDialog : Form
{
    /// <summary>Main, Backup, or Unlabelled when the user chose "Decide Later" (or closed the window).</summary>
    public NetworkRole Answer { get; private set; } = NetworkRole.Unlabelled;

    /// <param name="network">The network's name (Wi-Fi name or Windows network profile).</param>
    /// <param name="adapter">The adapter it came in on ("Wi-Fi", "Ethernet 2"), when the caller knows it.</param>
    /// <param name="gateway">The router's address, when the caller knows it.</param>
    /// <param name="kind">What the adapter is, for the icon.</param>
    public NetworkRoleDialog(string network, string adapter = "", string gateway = "", AdapterKind kind = AdapterKind.WiFi)
    {
        DialogParts.Prepare(this, "New Network", new Size(520, 320), taskbar: false);
        TopMost = true;
        const int side = DialogParts.Side, w = 520 - 2 * side;

        var title = DialogParts.Title("What Is This Network?"); title.SetBounds(side, 20, w, 32);
        var sub = DialogParts.Text("This is the first time you connected to it.", Ui.UiFont(9.75f), Ui.Muted); sub.SetBounds(side, 54, w, 22);

        // the network: an amber icon (it counts as a backup until answered), its name, and where it came in
        var well = new DialogWell(); well.SetBounds(side, 88, w, 72);
        var icon = new DialogGlyph(Ui.AdapterGlyph(kind), 24, Ui.Amber) { Location = new Point(16, 24) };
        var name = DialogParts.Text(network, Ui.UiFont(11.25f, Weight.Bold), Ui.Text, Ui.Bg); name.SetBounds(54, 13, w - 70, 26);
        var where = DialogParts.Text(Where(adapter, gateway), Ui.UiFont(9f), Ui.Muted, Ui.Bg); where.SetBounds(54, 39, w - 70, 20);
        well.Controls.AddRange(new Control[] { icon, name, where });
        Ui.Tip.SetToolTip(name, network);

        var note = DialogParts.Text("Until you answer, it is treated as a backup so nothing heavy downloads on it.", Ui.UiFont(9.4f), Ui.Muted);
        note.SetBounds(side, 174, w, 40);

        void Pick(NetworkRole r) { Answer = r; DialogResult = r == NetworkRole.Unlabelled ? DialogResult.Cancel : DialogResult.OK; }
        var later = DialogParts.Button("Decide Later", "Ask again the next time the app starts. Until then it is treated as a backup.", () => Pick(NetworkRole.Unlabelled));
        var backup = DialogParts.Button("Backup", "Your phone, or any connection with limited data. Heavy downloads are kept off it.", () => Pick(NetworkRole.Backup));
        var main = DialogParts.Button("Main", "Your normal connection: home, dorm or work.", () => Pick(NetworkRole.Main), BtnKind.Primary);
        var footer = new DialogFooter();
        footer.AddRight(later); footer.AddRight(backup); footer.AddRight(main);      // left to right: Main ends up on the far right

        Controls.AddRange(new Control[] { title, sub, well, note, footer });
        AcceptButton = main; CancelButton = later;
        Shown += (_, _) => main.Focus();
    }

    /// <summary>"Connected through Wi-Fi · router 10.0.0.1", with only the parts that are known.</summary>
    static string Where(string adapter, string gateway)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(adapter)) parts.Add("Connected through " + adapter.Trim());
        if (!string.IsNullOrWhiteSpace(gateway)) parts.Add((parts.Count == 0 ? "Router " : "router ") + gateway.Trim());
        return parts.Count > 0 ? string.Join("  ·  ", parts) : "Not on your Main or Backup table yet.";
    }
}
