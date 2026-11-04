using NetworkAutoSwitch.Core;
using Wpf.Ui.Controls;

namespace NetworkAutoSwitch.Tray.Fluent;

/// <summary>The policy as text, for advanced use. Apply only closes when the text is a valid policy; the problem is shown until it is fixed.</summary>
public partial class FluentJsonDialog : FluentWindow
{
    public string Json => Editor.Text;

    internal FluentJsonDialog(string json)
    {
        InitializeComponent();
        Editor.Text = json.Replace("\r\n", "\n").Replace("\n", "\r\n");
        Editor.CaretIndex = 0;
        ApplyBtn.Click += (_, _) =>
        {
            try { BackupPolicy.Parse(Editor.Text); DialogResult = true; }
            catch (InvalidDataException e) { Problem.Text = e.Message; }
        };
    }
}
