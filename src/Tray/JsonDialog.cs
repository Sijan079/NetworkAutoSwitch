using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

/// <summary>The policy as text, for advanced use. OK only closes when the text is a valid policy, and the problem is shown in the dialog until it is fixed.</summary>
sealed class JsonDialog : Form
{
    readonly TextBox _text = new() { Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = Ui.MonoFont(10f), AcceptsTab = true, BorderStyle = BorderStyle.None };
    readonly Label _problem = new() { AutoSize = true, ForeColor = Ui.Red, Padding = new Padding(12, 10, 0, 0), BackColor = Ui.Bg };

    public string Json => _text.Text;

    public JsonDialog(string json)
    {
        Text = "Edit As JSON"; Icon = Ui.AppIcon(Ui.Blue);
        ClientSize = new Size(760, 560); MinimumSize = new Size(560, 400); StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false; MinimizeBox = false; BackColor = Ui.Bg; Font = Ui.UiFont(9.5f);

        _text.Text = json.Replace("\r\n", "\n").Replace("\n", "\r\n");
        _text.SelectionStart = 0; _text.SelectionLength = 0;     // never open fully selected: one keystroke would wipe it
        var card = new Card("", "policy.json") { Margin = new Padding(0) };
        card.SetHint("The same rules and global trigger as the Backup Rules page, as text. It is checked when you click Apply.");
        card.Body.Padding = new Padding(Ui.Gap, 0, Ui.Gap, Ui.Gap - 4); card.Body.Controls.Add(_text);
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Ui.Pad, Ui.Gap, Ui.Pad, 0), BackColor = Ui.Bg };
        host.Controls.Add(card);

        var apply = Ui.Btn(Ui.Check, "Apply", "Check the text and use it in the editor. You still have to click Save And Apply afterwards.", Apply, BtnKind.Primary);
        var cancel = Ui.Btn(Ui.Clear, "Cancel", "Close without changing anything.", () => DialogResult = DialogResult.Cancel);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, Padding = new Padding(Ui.Pad, Ui.Gap, Ui.Pad, Ui.Gap), BackColor = Ui.Bg };
        bar.Controls.Add(apply); bar.Controls.Add(cancel); bar.Controls.Add(_problem);
        Controls.Add(host); Controls.Add(bar);
        Load += (_, _) => Ui.SkinForm(this);
    }

    void Apply()
    {
        try { BackupPolicy.Parse(_text.Text); }
        catch (InvalidDataException e) { _problem.Text = e.Message; return; }
        DialogResult = DialogResult.OK;
    }
}
