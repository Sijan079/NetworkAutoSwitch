using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NetworkAutoSwitch.Tray;

/// <summary>Dropdowns and number boxes that ignore the mouse wheel, so scrolling the page can never change a setting by accident.
/// The wheel is handed to the parent instead, so the page still scrolls.</summary>
static class WheelGuard
{
    public const int WM_MOUSEWHEEL = 0x020A;
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <returns>true if the message was a wheel message and was forwarded (the control must not process it).</returns>
    public static bool Forward(Control c, ref Message m)
    {
        if (m.Msg != WM_MOUSEWHEEL) return false;
        if (c.Parent is { IsHandleCreated: true } p) SendMessage(p.Handle, m.Msg, m.WParam, m.LParam);
        return true;
    }
}

sealed class NoWheelComboBox : ComboBox
{
    protected override void WndProc(ref Message m) { if (!WheelGuard.Forward(this, ref m)) base.WndProc(ref m); }
}

sealed class NoWheelNumeric : NumericUpDown
{
    protected override void WndProc(ref Message m) { if (!WheelGuard.Forward(this, ref m)) base.WndProc(ref m); }
}
