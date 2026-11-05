using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NetworkAutoSwitch.Tray;

/// <summary>A page that scrolls with the mouse wheel, a touchpad, or PageUp / PageDown / Home / End, and shows NO scrollbar.
/// The content is laid out at the page's width and at least <see cref="MinContentHeight"/> high; when the window is smaller than that, the content slides up and down inside the page.
/// The wheel is routed here by <see cref="WheelRouter"/>, from anywhere on the page.</summary>
sealed class ScrollSurface : Panel
{
    /// <summary>Put the page's controls here (with whatever padding the page needs).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Panel Content { get; } = new() { BackColor = Ui.Bg };
    int _min, _offset;

    public ScrollSurface() { BackColor = Ui.Bg; Dock = DockStyle.Fill; Controls.Add(Content); }

    /// <summary>The least height the content gets. Below it, the page scrolls instead of squeezing what is on it.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int MinContentHeight { get => _min; set { _min = value; Relayout(); } }

    /// <summary>How far the content is scrolled (0 = top).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Offset => _offset;

    bool Overflows => Content.Height > Height;

    protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }

    void Relayout()
    {
        int h = Math.Max(Height, _min);
        _offset = Math.Clamp(_offset, 0, Math.Max(0, h - Height));
        Content.SetBounds(0, -_offset, Width, h);
    }

    /// <summary>Scrolls by a number of pixels (positive = down). Returns true when the page can scroll at all, so the wheel is used up even at the ends.</summary>
    public bool ScrollBy(int pixels)
    {
        if (!Overflows) return false;
        _offset = Math.Clamp(_offset + pixels, 0, Math.Max(0, Content.Height - Height));
        Content.Top = -_offset;
        return true;
    }

    /// <summary>One wheel notch (delta 120) moves about a tenth of the page, at least 60 px.</summary>
    public bool Wheel(int delta) => ScrollBy(-delta * Math.Max(60, Height / 10) / 120);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var focus = FindForm()?.ActiveControl;
        if (focus is ListView or ListBox or TextBoxBase or ComboBox or UpDownBase) return base.ProcessCmdKey(ref msg, keyData);   // those keep their own paging
        switch (keyData)
        {
            case Keys.PageDown: return ScrollBy(Height * 9 / 10);
            case Keys.PageUp: return ScrollBy(-Height * 9 / 10);
            case Keys.Home: return ScrollBy(-_offset);
            case Keys.End: return ScrollBy(Content.Height);
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

/// <summary>Sends the mouse wheel to the page under the pointer, wherever the keyboard focus is (a page has no scrollbar to grab).
/// A list that has more rows than it shows keeps its own wheel while the pointer is over it.</summary>
sealed class WheelRouter : IMessageFilter
{
    const int WM_MOUSEWHEEL = 0x020A;
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(System.Drawing.Point p);

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WM_MOUSEWHEEL) return false;
        long lp = (long)m.LParam;
        var pt = new System.Drawing.Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));      // screen coordinates
        int delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
        var c = Control.FromChildHandle(WindowFromPoint(pt));
        for (; c is not null; c = c.Parent)
        {
            if (c is ListView lv && CanScroll(lv, delta)) return false;        // let the list scroll itself
            if (c is ScrollSurface s) return s.Wheel(delta);
        }
        return false;
    }

    /// <summary>Does the list have rows to scroll to in this direction? (wheel up = positive delta = towards the first row)</summary>
    static bool CanScroll(ListView lv, int delta)
    {
        if (lv.Items.Count == 0) return false;
        bool atTop = lv.TopItem is null || lv.TopItem.Index == 0;
        bool atBottom = lv.Items[lv.Items.Count - 1].Bounds.Bottom <= lv.ClientSize.Height;
        return delta > 0 ? !atTop : !atBottom;
    }
}
