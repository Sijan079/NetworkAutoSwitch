using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NetworkAutoSwitch.Tray;

/// <summary>Small painting helpers for the dashboard's self-drawn parts. Kit's Chip.Paint builds a new rounded path and measures the
/// text on every call; here the paths are cached by size (drawn at the origin and moved with a transform) and chip widths by text,
/// so painting a table row allocates nothing.</summary>
static class DashPaint
{
    static readonly Dictionary<(int W, int H, int R), GraphicsPath> _paths = new();
    static readonly Dictionary<string, int> _chipW = new();
    static readonly Dictionary<(string, float, Weight), int> _textW = new();

    /// <summary>A rounded rectangle at (0,0), inset half a pixel so a 1 px edge is crisp. Shared: never dispose it.</summary>
    public static GraphicsPath Round(int w, int h, int r)
    {
        var k = (w, h, r);
        if (!_paths.TryGetValue(k, out var p)) _paths[k] = p = Ui.RoundRect(new RectangleF(0.5f, 0.5f, w - 1, h - 1), r);
        return p;
    }

    public static void Fill(Graphics g, int x, int y, int w, int h, int r, Color c)
    {
        var s = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(x, y); g.FillPath(Ui.Brush(c), Round(w, h, r)); g.TranslateTransform(-x, -y);
        g.SmoothingMode = s;
    }

    public static void Outline(Graphics g, int x, int y, int w, int h, int r, Color c)
    {
        var s = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(x, y); g.DrawPath(Ui.Pen(c), Round(w, h, r)); g.TranslateTransform(-x, -y);
        g.SmoothingMode = s;
    }

    /// <summary>Text width, cached (TextRenderer.MeasureText allocates).</summary>
    public static int TextWidth(string text, float pt, Weight w)
    {
        var k = (text, pt, w);
        if (!_textW.TryGetValue(k, out int v)) _textW[k] = v = TextRenderer.MeasureText(text, Ui.UiFont(pt, w), Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        return v;
    }

    public static int ChipWidth(string text)
    {
        if (!_chipW.TryGetValue(text, out int w)) _chipW[text] = w = TextWidth(text, 8.25f, Weight.SemiBold) + 18;
        return w;
    }

    /// <summary>The kit chip look (22 px high, fully rounded, outlined or filled with OnAccent text), drawn straight onto a surface.</summary>
    public static int Chip(Graphics g, string text, Color role, int x, int centreY, bool filled)
    {
        int w = ChipWidth(text), y = centreY - 11;
        if (filled) Fill(g, x, y, w, 22, 11, role); else Outline(g, x, y, w, 22, 11, role);
        TextRenderer.DrawText(g, text, Ui.UiFont(8.25f, Weight.SemiBold), new Rectangle(x, y, w, 22), filled ? Ui.OnAccent : role,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        return w;
    }
}
