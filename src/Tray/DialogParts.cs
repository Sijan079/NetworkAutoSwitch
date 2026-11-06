using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NetworkAutoSwitch.Tray;

// Pieces shared by the small dialogs (the network prompt, the welcome), as in the mockups: the body on the surface colour, a title and a
// muted line at the top, inset boxes on the ground colour, and a footer bar on the ground colour with a line above it.

static class DialogParts
{
    /// <summary>Left and right padding of the dialog body (the mockups use 26 px).</summary>
    public const int Side = 26;

    /// <summary>Sets up a fixed dialog the same way for every small dialog. The body is the surface colour (Ui.SkinForm sets the ground
    /// colour on the form, so it is put back after it; the footer has its own ground colour).</summary>
    public static void Prepare(Form f, string text, Size client, bool taskbar)
    {
        f.Text = text; f.Icon = Ui.AppIcon(Ui.Blue);
        f.FormBorderStyle = FormBorderStyle.FixedDialog; f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = taskbar;
        f.ClientSize = client; f.StartPosition = FormStartPosition.CenterScreen; f.BackColor = Ui.CardBg; f.Font = Ui.UiFont(9.75f);
        f.Load += (_, _) => { Ui.SkinForm(f); f.BackColor = Ui.CardBg; };
    }

    /// <summary>The dialog title (20 px bold).</summary>
    public static Label Title(string text) => Text(text, Ui.UiFont(15f, Weight.Bold), Ui.Text);

    /// <summary>A label with no auto-size, no mnemonics (network names may hold "&amp;") and the surface colour behind it.</summary>
    public static Label Text(string text, Font font, Color ink, Color? back = null) =>
        new() { Text = text, AutoSize = false, Font = font, ForeColor = ink, BackColor = back ?? Ui.CardBg, UseMnemonic = false, AutoEllipsis = true };

    /// <summary>A footer button: the kit button, text only (the mockups show no icons here), with the explanation in the tooltip.</summary>
    public static KitButton Button(string text, string tip, Action click, BtnKind kind = BtnKind.Secondary)
    {
        var b = Ui.Btn("", text, tip, click, kind);
        b.Margin = new Padding(0, 0, 10, 0);
        return b;
    }
}

/// <summary>The footer bar of a dialog: the ground colour, a 1 px line on top, buttons on the left and on the right (in the order added).</summary>
sealed class DialogFooter : Panel
{
    readonly FlowLayoutPanel _left = Flow(DockStyle.Left), _right = Flow(DockStyle.Right);

    static FlowLayoutPanel Flow(DockStyle dock) => new()
    {
        Dock = dock, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, FlowDirection = FlowDirection.LeftToRight,
        BackColor = Ui.Bg, Margin = new Padding(0), Padding = new Padding(0)
    };

    public DialogFooter()
    {
        Dock = DockStyle.Bottom; Height = 64; BackColor = Ui.Bg;
        // the buttons carry a 10 px right margin, so the right side gives back 10 px of the padding
        Padding = new Padding(DialogParts.Side, 14, DialogParts.Side - 10, 0);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        Controls.Add(_left); Controls.Add(_right);
    }

    public T AddLeft<T>(T c) where T : Control { _left.Controls.Add(c); return c; }
    public T AddRight<T>(T c) where T : Control { _right.Controls.Add(c); return c; }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.DrawLine(Ui.Pen(Ui.Border), 0, 0, Width, 0);
    }
}

/// <summary>An inset box inside a dialog: the ground colour, a 1 px line, radius 10. Children should use <see cref="Ui.Bg"/> behind them.</summary>
sealed class DialogWell : Panel
{
    GraphicsPath? _path; Size _pathSize;

    public DialogWell()
    {
        BackColor = Ui.Bg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Ui.CardBg);
        g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.Half;
        if (_path is null || _pathSize != Size) { _path?.Dispose(); _path = Ui.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 10); _pathSize = Size; }
        g.FillPath(Ui.Brush(Ui.Bg), _path);
        g.DrawPath(Ui.Pen(Ui.Border), _path);
    }

    protected override void Dispose(bool disposing) { if (disposing) _path?.Dispose(); base.Dispose(disposing); }
}

/// <summary>A glyph drawn on whatever is behind it (no PictureBox background box). The bitmap comes from Ui.GlyphCached: shared, never disposed.</summary>
sealed class DialogGlyph : Control
{
    readonly Bitmap _img;
    public DialogGlyph(string glyph, int px, Color ink)
    {
        _img = Ui.GlyphCached(glyph, px, ink); Size = new Size(px, px);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }
    protected override void OnPaint(PaintEventArgs e) { e.Graphics.Clear(Parent?.BackColor ?? Ui.CardBg); e.Graphics.DrawImage(_img, 0, 0); }
}
