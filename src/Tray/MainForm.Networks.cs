using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

// Networks page: hosts the ConnectionsPanel (ConnectionsDialog.cs).
sealed partial class MainForm
{
    ConnectionsPanel? _networks;

    /// <summary>The Networks page: every network grouped by what it becomes (Main Networks, Backup Networks, Unlabelled, Barred), the mode, and the adapters.
    /// The panel brings its own page header (title, Save and Revert), so the welcome dialog that hosts the same panel looks the same.</summary>
    Control NetworksPage()
    {
        var page = Page();
        _networks = new ConnectionsPanel(_engine) { Dock = DockStyle.Fill };
        page.Controls.Add(_networks);
        return page;
    }

    /// <summary>Opens the Networks page (from the mode menu, the warning banner and the tray menu).</summary>
    public void ChooseConnections() => ShowWindow("Networks");

}
