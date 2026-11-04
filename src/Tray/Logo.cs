using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace NetworkAutoSwitch.Tray;

/// <summary>The app logo, drawn in code so it stays sharp at every size: two paths (main solid, backup dimmer) merging into one,
/// on a blue-violet rounded tile. The tray version adds a status dot in the corner.</summary>
static class Logo
{
    static readonly Color Blue = Color.FromArgb(37, 99, 235), Violet = Color.FromArgb(109, 40, 217);

    /// <param name="status">null = plain logo; a color adds the status dot (tray).</param>
    public static Bitmap Draw(int size, Color? status = null)
    {
        int k = size >= 96 ? 1 : 4;               // small sizes are drawn 4x bigger and shrunk, which keeps the edges clean
        int S = size * k;
        using var big = new Bitmap(S, S, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(big))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.Clear(Color.Transparent);

            // tile
            float m = S * 0.03f;
            var tile = new RectangleF(m, m, S - 2 * m, S - 2 * m);
            using (var path = Rounded(tile, S * 0.23f))
            using (var grad = new LinearGradientBrush(tile, Blue, Violet, 55f))
                g.FillPath(grad, path);

            // the mark: main path (solid) and backup path (dimmer) merging into one point
            float x1 = S * 0.27f, x2 = S * 0.74f, yTop = S * 0.31f, yBot = S * 0.69f, yMid = S * 0.5f, reach = (x2 - x1) * 0.55f, w = S * 0.078f * (size <= 24 ? 1.4f : 1f);   // chunkier strokes at tray size
            using var main = new Pen(Color.White, w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var back = new Pen(Color.FromArgb(150, 255, 255, 255), w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawBezier(back, x1, yBot, x1 + reach, yBot, x2 - reach, yMid, x2, yMid);
            g.DrawBezier(main, x1, yTop, x1 + reach, yTop, x2 - reach, yMid, x2, yMid);
            using var white = new SolidBrush(Color.White); using var dim = new SolidBrush(Color.FromArgb(170, 255, 255, 255));
            float r = S * 0.082f * (size <= 24 ? 1.25f : 1f), R = S * 0.108f * (size <= 24 ? 1.2f : 1f);
            g.FillEllipse(white, x1 - r, yTop - r, 2 * r, 2 * r);
            g.FillEllipse(dim, x1 - r, yBot - r, 2 * r, 2 * r);
            g.FillEllipse(white, x2 - R, yMid - R, 2 * R, 2 * R);

            // status dot (tray only)
            if (status is Color c)
            {
                float cx = S * 0.745f, cy = S * 0.745f, d = S * 0.215f, ring = S * 0.05f;
                using var outline = new SolidBrush(Color.White); using var fill = new SolidBrush(c);
                g.FillEllipse(outline, cx - d - ring, cy - d - ring, 2 * (d + ring), 2 * (d + ring));
                g.FillEllipse(fill, cx - d, cy - d, 2 * d, 2 * d);
            }
        }
        if (k == 1) return (Bitmap)big.Clone();
        var small = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(big, 0, 0, size, size);
        }
        return small;
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2; var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    /// <summary>An .ico with several sizes (PNG-compressed), so Windows picks a crisp one for the tray, taskbar and title bar.</summary>
    public static byte[] IcoBytes(Color? status, params int[] sizes)
    {
        var pngs = sizes.Select(s => { using var b = Draw(s, status); using var ms = new MemoryStream(); b.Save(ms, ImageFormat.Png); return ms.ToArray(); }).ToList();
        using var o = new MemoryStream(); using var w = new BinaryWriter(o);
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32); w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
        return o.ToArray();
    }

    static readonly Dictionary<int, Icon> _cache = new();
    /// <summary>Icon for the window title bar / tray. Cached, because each distinct status color is created once.</summary>
    public static Icon Icon(Color? status = null)
    {
        int key = status?.ToArgb() ?? 0;
        lock (_cache)
        {
            if (!_cache.TryGetValue(key, out var ic))
                _cache[key] = ic = new Icon(new MemoryStream(IcoBytes(status, 16, 20, 24, 32, 48, 64)));
            return ic;
        }
    }

    /// <summary>Writes the logo files (app.ico and preview PNGs) to a folder. Run once with --export-logo.</summary>
    public static void Export(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "app.ico"), IcoBytes(null, 16, 24, 32, 48, 64, 128, 256));
        using (var b = Draw(512)) b.Save(Path.Combine(folder, "logo-512.png"), ImageFormat.Png);
        var states = new (string Name, Color C)[] { ("green", Ui.Green), ("amber", Ui.Amber), ("red", Ui.Red), ("grey", Color.Gray) };
        // preview sheet: big logo, then the four tray states at 64, 32 and 16 px (the real tray sizes) on light and dark
        using var sheet = new Bitmap(900, 300);
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.White); g.FillRectangle(new SolidBrush(Color.FromArgb(32, 32, 36)), 0, 150, 900, 150);
            using (var big = Draw(256)) g.DrawImage(big, 10, 22, 256, 256);
            int x = 300;
            foreach (var (name, c) in states)
            {
                using var b64 = Draw(64, c); using var b32 = Draw(32, c); using var b16 = Draw(16, c);
                g.DrawImage(b64, x, 20, 64, 64); g.DrawImage(b32, x + 8, 100, 32, 32); g.DrawImage(b16, x + 48, 108, 16, 16);
                g.DrawImage(b64, x, 190, 64, 64); g.DrawImage(b32, x + 8, 262 - 6, 32, 32); g.DrawImage(b16, x + 48, 270 - 6, 16, 16);
                using var b2 = Draw(64, c); b2.Save(Path.Combine(folder, $"tray-{name}.png"), ImageFormat.Png);
                x += 150;
            }
        }
        sheet.Save(Path.Combine(folder, "preview.png"), ImageFormat.Png);
    }
}
