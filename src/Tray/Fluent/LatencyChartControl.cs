using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray.Fluent;

/// <summary>Latency history of both adapters as a line chart, red ticks at the bottom for failed probes. Drawn by WPF itself (no WinForms inside, so it renders in snapshots and over Mica).</summary>
sealed class LatencyChartControl : FrameworkElement
{
    Sample[] _p = Array.Empty<Sample>(), _b = Array.Empty<Sample>();
    string _pName = "Main", _bName = "Backup";
    const int Window = 120;   // last ~10 minutes

    public void SetData(Sample[] p, Sample[] b, string pName, string bName) { _p = p; _b = b; _pName = pName; _bName = bName; InvalidateVisual(); }

    Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
    static readonly Brush Blue = Frozen(Color.FromRgb(96, 165, 250)), Orange = Frozen(Color.FromRgb(251, 146, 60)), Red = Frozen(Color.FromRgb(220, 60, 60));
    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var text = Res("TextFillColorSecondaryBrush", Brushes.Gray);
        var gridPen = new Pen(Res("ControlStrokeColorDefaultBrush", Brushes.Gray), 1);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var r = new Rect(54, 26, Math.Max(10, w - 70), Math.Max(10, h - 44));
        if (r.Width < 40 || r.Height < 30) return;

        int yMax = Math.Max(200, (int)(Math.Ceiling(_p.Concat(_b).Where(s => s.Ms.HasValue).Select(s => s.Ms!.Value).DefaultIfEmpty(0).Max() / 100.0) * 100));
        FormattedText Ft(string s) => new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Variable Text, Segoe UI"), 11, text, dpi);
        for (int i = 0; i <= 4; i++)
        {
            double y = r.Bottom - r.Height * i / 4.0;
            dc.DrawLine(gridPen, new Point(r.Left, y), new Point(r.Right, y));
            var t = Ft($"{yMax * i / 4} ms"); dc.DrawText(t, new Point(r.Left - 8 - t.Width, y - t.Height / 2));
        }
        Line(dc, r, _p, Blue, yMax); Line(dc, r, _b, Orange, yMax);

        dc.DrawRoundedRectangle(Blue, null, new Rect(r.Left, 7, 10, 10), 2, 2);
        var pn = Ft(_pName); dc.DrawText(pn, new Point(r.Left + 16, 4));
        double x2 = r.Left + 16 + pn.Width + 18;
        dc.DrawRoundedRectangle(Orange, null, new Rect(x2, 7, 10, 10), 2, 2);
        dc.DrawText(Ft(_bName), new Point(x2 + 16, 4));
    }

    static void Line(DrawingContext dc, Rect r, Sample[] data, Brush col, int yMax)
    {
        var s = data.TakeLast(Window).ToArray();
        if (s.Length == 0) return;
        double step = r.Width / (Window - 1);
        double X(int i) => r.Right - (s.Length - 1 - i) * step;
        var pen = new Pen(col, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var fail = new Pen(Red, 2);
        Point? prev = null;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i].Ms is int ms)
            {
                var pt = new Point(X(i), r.Bottom - Math.Min(1.0, ms / (double)yMax) * r.Height);
                if (prev is Point pp) dc.DrawLine(pen, pp, pt);
                prev = pt;
            }
            else { prev = null; dc.DrawLine(fail, new Point(X(i), r.Bottom - 8), new Point(X(i), r.Bottom)); }
        }
    }
}
