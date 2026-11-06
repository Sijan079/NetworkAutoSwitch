using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

/// <summary>Latency of both connections over the last <see cref="Window"/> checks: Main a solid teal line, Backup a dashed amber one,
/// muted grid lines with their ms values, and red ticks at the bottom for failed checks. The samples are copied and the scale and
/// labels worked out in SetData, which repaints only when a new sample arrived; OnPaint only draws.</summary>
sealed class LatencyChart : Control
{
    public const int Window = 60;
    internal static Color PCol => Ui.Green;      // Main is teal and Backup amber everywhere in the app (status line, cards, chart)
    internal static Color BCol => Ui.Amber;

    readonly int?[] _p = new int?[Window], _b = new int?[Window];
    int _pn, _bn, _yMax = 100;
    DateTime _lastP, _lastB;
    readonly string[] _labels = new string[5];
    static readonly int[] Tops = { 100, 200, 400, 600, 1000, 2000, 4000 };
    static readonly Dictionary<int, Pen> _dashed = new();      // the backup's dashed pen, per colour (the kit's shared pens must stay solid)

    public LatencyChart()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Labels();
    }

    public void SetData(Sample[] p, Sample[] b)
    {
        DateTime lp = p.Length > 0 ? p[^1].At : default, lb = b.Length > 0 ? b[^1].At : default;
        if (lp == _lastP && lb == _lastB && Math.Min(p.Length, Window) == _pn && Math.Min(b.Length, Window) == _bn) return;   // no new sample: nothing to redraw
        _lastP = lp; _lastB = lb;
        _pn = Copy(p, _p); _bn = Copy(b, _b);
        int max = 0;
        for (int i = 0; i < _pn; i++) if (_p[i] is int v && v > max) max = v;
        for (int i = 0; i < _bn; i++) if (_b[i] is int v && v > max) max = v;
        int yMax = 100;                                   // the top of the scale: a round value, so the four steps read easily
        foreach (int top in Tops) { yMax = top; if (top >= max) break; }
        if (yMax != _yMax) { _yMax = yMax; Labels(); }
        Invalidate();
    }

    static int Copy(Sample[] src, int?[] dst)
    {
        int n = Math.Min(src.Length, Window), from = src.Length - n;
        for (int i = 0; i < n; i++) dst[i] = src[from + i].Ms;
        return n;
    }

    void Labels() { for (int i = 0; i <= 4; i++) _labels[i] = $"{_yMax * i / 4} ms"; }

    static Pen Dashed(Color c)
    {
        int k = c.ToArgb();
        if (!_dashed.TryGetValue(k, out var p)) _dashed[k] = p = new Pen(c, 2f) { DashPattern = new[] { 2.5f, 2f } };
        return p;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(BackColor);
        var r = new Rectangle(58, 8, Width - 60, Height - 18);
        if (r.Width < 40 || r.Height < 30) return;
        var font = Ui.UiFont(8.25f);
        for (int i = 0; i <= 4; i++)
        {
            int y = r.Bottom - (int)(r.Height * i / 4.0);
            g.DrawLine(Ui.Pen(Ui.Border), r.Left, y, r.Right, y);
            TextRenderer.DrawText(g, _labels[i], font, new Rectangle(0, y - 8, 50, 16), Ui.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Draw(g, r, _b, _bn, Dashed(BCol));          // backup first, so the main line is on top where they cross
        Draw(g, r, _p, _pn, Ui.Pen(PCol, 2f));
        g.SmoothingMode = SmoothingMode.None;
    }

    void Draw(Graphics g, Rectangle r, int?[] s, int n, Pen pen)
    {
        if (n == 0) return;
        float step = r.Width / (float)(Window - 1);
        var fail = Ui.Pen(Ui.Red, 2f);
        float px = 0, py = 0; bool have = false;
        for (int i = 0; i < n; i++)
        {
            float x = r.Right - (n - 1 - i) * step;
            if (s[i] is int ms)
            {
                float y = r.Bottom - Math.Min(1f, ms / (float)_yMax) * r.Height;
                if (have) g.DrawLine(pen, px, py, x, y);
                px = x; py = y; have = true;
            }
            else { have = false; g.DrawLine(fail, x, r.Bottom - 8, x, r.Bottom); }
        }
    }
}

/// <summary>The legend in the Latency card's header: a solid teal swatch with the main network's name, a dashed amber one with the backup's.</summary>
sealed class ChartLegend : Control
{
    string _p = "", _b = "";
    int _pw, _bw;
    public ChartLegend()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 28; Margin = new Padding(0, 0, 4, 0);
    }

    public void Set(string primary, string backup)
    {
        if (primary == _p && backup == _b) return;
        _p = primary; _b = backup;
        _pw = TextRenderer.MeasureText(_p, Font, new Size(int.MaxValue, 100), TextFormatFlags.NoPadding).Width;
        _bw = TextRenderer.MeasureText(_b, Font, new Size(int.MaxValue, 100), TextFormatFlags.NoPadding).Width;
        Width = 14 + 6 + _pw + 16 + 14 + 6 + _bw + 2; Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(BackColor);
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix;
        int x = 0, mid = Height / 2;
        g.FillRectangle(Ui.Brush(LatencyChart.PCol), x, mid - 1, 14, 3);
        x += 20; TextRenderer.DrawText(g, _p, Font, new Rectangle(x, 0, _pw + 2, Height), ForeColor, flags); x += _pw + 16;
        for (int d = 0; d < 14; d += 6) g.FillRectangle(Ui.Brush(LatencyChart.BCol), x + d, mid - 1, Math.Min(4, 14 - d), 3);     // dashed, like the backup line
        x += 20; TextRenderer.DrawText(g, _b, Font, new Rectangle(x, 0, _bw + 2, Height), ForeColor, flags);
    }
}
