using System.Drawing;
using System.Drawing.Drawing2D;
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

/// <summary>A dropdown drawn in the app's own theme: a rounded box in the card colour with a chevron, and a list whose rows
/// use the card colour and the hover colour (instead of Windows' white box and blue selection).</summary>
sealed class NoWheelComboBox : ComboBox
{
    bool _hot;

    public NoWheelComboBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed; FlatStyle = FlatStyle.Flat; ItemHeight = 26;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);   // the closed box is drawn by OnPaint
        Font = Ui.UiFont(9.5f);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = false; Invalidate(); }
    protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    /// <summary>One row of the open list.</summary>
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        using (var b = new SolidBrush(sel ? Ui.Hover : Ui.CardBg)) e.Graphics.FillRectangle(b, e.Bounds);
        var r = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, r, Ui.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    protected override void WndProc(ref Message m)
    {
        if (WheelGuard.Forward(this, ref m)) return;
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width, Height);
        using (var bg = new SolidBrush(Parent?.BackColor ?? Ui.CardBg)) g.FillRectangle(bg, bounds);
        var box = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = new GraphicsPath(); float d = 16;      // radius 8, like every field
        path.AddArc(box.Left, box.Top, d, d, 180, 90); path.AddArc(box.Right - d, box.Top, d, d, 270, 90);
        path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90); path.AddArc(box.Left, box.Bottom - d, d, d, 90, 90); path.CloseFigure();
        using (var fill = new SolidBrush(_hot && Enabled ? Ui.Hover : Ui.InputBg)) g.FillPath(fill, path);
        using (var pen = new Pen(Focused || DroppedDown ? Ui.Green : Ui.Border)) g.DrawPath(pen, path);      // focus is teal on every field
        g.SmoothingMode = SmoothingMode.None;
        Color fg = Enabled ? Ui.Text : Ui.Muted;
        string text = (SelectedIndex >= 0 ? GetItemText(SelectedItem!) : Text) ?? "";
        var tr = new Rectangle(12, 0, Width - 40, Height);      // same text inset as the other fields
        TextRenderer.DrawText(g, text, Font, tr, fg, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        using var chevron = Ui.GlyphBold("", 14, Ui.Muted);      // ChevronDown
        g.DrawImage(chevron, Width - 24, (Height - 14) / 2);
    }
}

sealed class NoWheelNumeric : NumericUpDown
{
    protected override void WndProc(ref Message m) { if (!WheelGuard.Forward(this, ref m)) base.WndProc(ref m); }
}
