using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

/// <summary>Small line chart: latency history of both adapters, red ticks at the bottom for failed probes.</summary>
sealed class LatencyChart : Control
{
    Sample[] _p = Array.Empty<Sample>(), _b = Array.Empty<Sample>();
    string _pName = "Primary", _bName = "Backup";
    static Color PCol => Ui.Dark ? Color.FromArgb(96, 165, 250) : Color.FromArgb(31, 119, 180);
    static Color BCol => Ui.Dark ? Color.FromArgb(251, 146, 60) : Color.FromArgb(230, 126, 34);

    public LatencyChart() { DoubleBuffered = true; ResizeRedraw = true; }

    public void SetData(Sample[] p, Sample[] b, string pName, string bName) { _p = p; _b = b; _pName = pName; _bName = bName; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        var r = new Rectangle(46, 22, Width - 60, Height - 40);
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

        // legend
        using var pb = new SolidBrush(PCol); using var bb = new SolidBrush(BCol);
        g.FillRectangle(pb, r.Left, 6, 10, 10); g.DrawString(_pName, Font, txt, r.Left + 14, 3);
        int x2 = r.Left + 24 + (int)g.MeasureString(_pName, Font).Width + 12;
        g.FillRectangle(bb, x2, 6, 10, 10); g.DrawString(_bName, Font, txt, x2 + 14, 3);
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
