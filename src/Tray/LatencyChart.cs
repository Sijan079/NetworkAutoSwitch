using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

/// <summary>Small line chart: latency history of both adapters, red ticks at the bottom for failed probes.</summary>
sealed class LatencyChart : Control
{
    Sample[] _p = Array.Empty<Sample>(), _b = Array.Empty<Sample>();
    internal static Color PCol => Ui.Green;      // Main is green and Backup amber everywhere in the app (status line, cards, chart)
    internal static Color BCol => Ui.Amber;

    public LatencyChart() { DoubleBuffered = true; ResizeRedraw = true; }

    public void SetData(Sample[] p, Sample[] b) { _p = p; _b = b; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        var r = new Rectangle(46, 10, Width - 48, Height - 28);      // the plot ends at the card's right content edge, like the legend and the tables
        if (r.Width < 40 || r.Height < 30) return;

        const int window = 120;   // last ~10 minutes at one probe per 5 s
        int yMax = Math.Max(200, (int)(Math.Ceiling((_p.Concat(_b).Where(s => s.Ms.HasValue).Select(s => s.Ms!.Value).DefaultIfEmpty(0).Max()) / 100.0) * 100));
        using var grid = new Pen(Color.FromArgb(60, ForeColor)); using var txt = new SolidBrush(ForeColor);
        for (int i = 0; i <= 4; i++)
        {
            int y = r.Bottom - (int)(r.Height * i / 4.0);
            g.DrawLine(grid, r.Left, y, r.Right, y);
            g.DrawString($"{yMax * i / 4} ms", Font, txt, 2, y - 7);
        }
        Draw(g, r, _p, PCol, yMax, window); Draw(g, r, _b, BCol, yMax, window);

    }

    void Draw(Graphics g, Rectangle r, Sample[] data, Color col, int yMax, int window)
    {
        var s = data.TakeLast(window).ToArray();
        if (s.Length == 0) return;
        float step = r.Width / (float)(window - 1);
        float X(int i) => r.Right - (s.Length - 1 - i) * step;
        using var pen = new Pen(col, 2f); using var fail = new Pen(Color.FromArgb(200, 207, 34, 46), 2f);
        PointF? prev = null;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i].Ms is int ms)
            {
                var pt = new PointF(X(i), r.Bottom - Math.Min(1f, ms / (float)yMax) * r.Height);
                if (prev is PointF pp) g.DrawLine(pen, pp, pt);
                prev = pt;
            }
            else { prev = null; g.DrawLine(fail, X(i), r.Bottom - 8, X(i), r.Bottom); }
        }
    }
}

/// <summary>The two colored adapter names, shown in the header row of the Latency card (same row as its title).</summary>
sealed class ChartLegend : Control
{
    string _p = "", _b = "";
    public ChartLegend()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 32; Margin = new Padding(0, 0, 6, 0);
    }

    public void Set(string primary, string backup)
    {
        if (primary == _p && backup == _b) return;
        _p = primary; _b = backup; Width = 10 + 6 + TextW(_p) + 18 + 10 + 6 + TextW(_b) + 4; Invalidate();
    }

    int TextW(string t) => TextRenderer.MeasureText(t, Font, new Size(int.MaxValue, 100), TextFormatFlags.NoPadding).Width;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(BackColor);
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
        int x = 0, mid = Height / 2;
        using (var pb = new SolidBrush(LatencyChart.PCol)) g.FillRectangle(pb, x, mid - 5, 10, 10);
        x += 16; TextRenderer.DrawText(g, _p, Font, new Rectangle(x, 0, TextW(_p) + 2, Height), ForeColor, flags); x += TextW(_p) + 18;
        using (var bb = new SolidBrush(LatencyChart.BCol)) g.FillRectangle(bb, x, mid - 5, 10, 10);
        x += 16; TextRenderer.DrawText(g, _b, Font, new Rectangle(x, 0, TextW(_b) + 2, Height), ForeColor, flags);
    }
}
