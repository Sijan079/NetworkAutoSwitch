using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

enum ThemeMode { System, Light, Dark }
enum BtnKind { Secondary, Primary, Success, Danger }

/// <summary>Remembers the theme choice (follow the system, light or dark) across restarts.</summary>
static class ThemeSettings
{
    // stored in the shared settings file (next to the connection mode), so neither setting can overwrite the other
    public static ThemeMode Load() => Enum.TryParse<ThemeMode>(AppSettings.Load().Theme, true, out var m) ? m : ThemeMode.System;
    public static void Save(ThemeMode m) => AppSettings.Update(s => s.Theme = m.ToString());

    /// <summary>Windows' "app mode" setting: 0 = dark, 1 = light.</summary>
    public static bool SystemIsDark()
    {
        try { return Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int v && v == 0; }
        catch { return false; }
    }
    public static bool Resolve(ThemeMode m) => m == ThemeMode.Dark || (m == ThemeMode.System && SystemIsDark());
}

/// <summary>Shared look: theme colors, icon glyphs (Windows' built-in icon font), spacing, tooltips, buttons, cards and tabs.
/// Colors are properties, so a window built after SetTheme() picks up the new palette.</summary>
static class Ui
{
    // ---- spacing: everything snaps to this scale ----
    public const int Gap = 12, Pad = 16, BtnH = 36;

    // ---- theme palette (docs/UI-DESIGN.md). Every colour in the app comes from here. ----
    public static bool Dark { get; private set; }
    public static Color Bg { get; private set; }         // ground: the page behind the cards
    public static Color CardBg { get; private set; }     // surface: cards, the nav rail
    public static Color Raised { get; private set; }     // raised: secondary buttons, the active nav item, the selected segment
    public static Color InputBg { get; private set; }    // the inside of fields (the ground colour, so a field reads as "sunk" into its card)
    public static Color Border { get; private set; }     // line: card and field borders, dividers
    public static Color Text { get; private set; }
    public static Color Muted { get; private set; }
    public static Color Hover { get; private set; }
    public static Color Green { get; private set; }      // role: main / healthy (teal)
    public static Color Amber { get; private set; }      // role: backup / standby
    public static Color Red { get; private set; }        // role: blocked / error
    public static Color Blue { get; private set; }       // role: info
    /// <summary>Text and icons drawn ON a role colour (a filled button or chip).</summary>
    public static Color OnAccent { get; private set; }
    // "fill" colours: backgrounds of filled buttons, chips and the card of the connection in use. They are the role colours,
    // so text on them must use OnAccent (dark in the dark theme, white in the light one), never a fixed white.
    public static Color BlueFill => Blue;
    public static Color GreenFill => Green;
    public static Color RedFill => Red;
    public static Color AmberFill => Amber;
    /// <summary>A role colour mixed into the surface: the background of an alarm banner or a soft chip.</summary>
    public static Color Tint(Color role, float amount = 0.14f) => Mix(role, CardBg, amount);
    public static Color Mix(Color a, Color b, float t) => Color.FromArgb((int)(a.R * t + b.R * (1 - t)), (int)(a.G * t + b.G * (1 - t)), (int)(a.B * t + b.B * (1 - t)));

    static Ui() { SetTheme(false); }

    static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);

    public static void SetTheme(bool dark)
    {
        Dark = dark;
        _brushes.Clear(); _pens.Clear();          // cached by colour; a theme change makes new ones
        if (dark)
        {
            Bg = Hex(0x0f1419); CardBg = Hex(0x171d25); Raised = Hex(0x1f2731); InputBg = Hex(0x0f1419); Border = Hex(0x2b3541);
            Text = Hex(0xe8edf3); Muted = Hex(0x9aa7b6); Hover = Hex(0x1f2731);
            Green = Hex(0x3ddc97); Amber = Hex(0xf5b04c); Red = Hex(0xff6b6b); Blue = Hex(0x6cb2ff); OnAccent = Hex(0x0b1a12);
        }
        else
        {
            // the same roles, darkened so text in them passes 4.5:1 on white and white text on them passes too
            Bg = Hex(0xf3f5f8); CardBg = Hex(0xffffff); Raised = Hex(0xeef1f5); InputBg = Hex(0xf6f8fa); Border = Hex(0xd8dee6);
            Text = Hex(0x141a21); Muted = Hex(0x56616e); Hover = Hex(0xe8ecf1);
            Green = Hex(0x0b7a53); Amber = Hex(0x9a5b00); Red = Hex(0xc4302b); Blue = Hex(0x1c64c4); OnAccent = Hex(0xffffff);
        }
    }

    // ---- cached GDI objects: paint code must never allocate these per paint ----
    static readonly Dictionary<int, SolidBrush> _brushes = new();
    static readonly Dictionary<(int, float), Pen> _pens = new();
    /// <summary>A shared brush. Never dispose it.</summary>
    public static SolidBrush Brush(Color c) { int k = c.ToArgb(); if (!_brushes.TryGetValue(k, out var b)) _brushes[k] = b = new SolidBrush(c); return b; }
    /// <summary>A shared pen. Never dispose it.</summary>
    public static Pen Pen(Color c, float w = 1f) { var k = (c.ToArgb(), w); if (!_pens.TryGetValue(k, out var p)) _pens[k] = p = new Pen(c, w); return p; }

    /// <summary>A rounded rectangle path. The caller owns it (controls keep theirs and rebuild it only when their size changes).</summary>
    public static System.Drawing.Drawing2D.GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    // ---- icons: Segoe Fluent Icons (Windows 11) with Segoe MDL2 Assets (Windows 10) as the fallback ----
    public const string Wifi = "", Ethernet = "", Pause = "", Play = "", Add = "", Delete = "",
        Refresh = "", OpenFile = "", Save = "", Undo = "", Unlock = "", Lock = "", Swap = "",
        Clock = "", Home = "", Document = "", Settings = "", Download = "", Warning = "",
        Info = "", Chart = "", Data = "", Folder = "", Code = "", Shield = "", Check = "",
        Clear = "", Dot = "", Eye = "", Block = "", List = "", Brightness = "", Phone = "", Moon = "", Monitor = "", Mute = "", Speed = "", Up = "";

    static readonly string IconFont = PickFont();
    static string PickFont()
    {
        using var fc = new InstalledFontCollection();
        return fc.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    }

    public static readonly ToolTip Tip = new() { InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 12000 };

    public static Font IconFnt(float px) => new(IconFont, px, FontStyle.Regular, GraphicsUnit.Pixel);

    // ---- text fonts: Inter (bundled), Segoe UI when it could not be loaded ----
    static readonly Dictionary<(float, Weight), Font> _fonts = new();
    /// <summary>A shared font (cached): never dispose it. <paramref name="bold"/> = SemiBold, the weight of labels, titles and buttons.</summary>
    public static Font UiFont(float pt, bool bold = false) => UiFont(pt, bold ? Weight.SemiBold : Weight.Regular);
    public static Font UiFont(float pt, Weight w)
    {
        if (_fonts.TryGetValue((pt, w), out var f)) return f;
        return _fonts[(pt, w)] = AppFonts.Make(pt, w);
    }
    /// <summary>The log and command text. Cascadia Mono ships with Windows 11 (Terminal), Consolas everywhere.</summary>
    public static Font MonoFont(float pt) => _mono.TryGetValue(pt, out var f) ? f : _mono[pt] = new Font(MonoName, pt);
    static readonly Dictionary<float, Font> _mono = new();
    static readonly string MonoName = PickMono();
    static string PickMono() { using var fc = new InstalledFontCollection(); return fc.Families.Any(f => f.Name == "Cascadia Mono") ? "Cascadia Mono" : "Consolas"; }

    /// <summary>A glyph drawn a little thicker (the icon font only has thin outlines), for small symbols that must be noticed.</summary>
    public static Bitmap GlyphBold(string g, int px, Color c)
    {
        var bmp = new Bitmap(px, px);
        using var gr = Graphics.FromImage(bmp);
        gr.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var f = IconFnt(px * 0.78f); using var b = new SolidBrush(c);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        foreach (var (dx, dy) in new[] { (0f, 0f), (0.6f, 0f), (0f, 0.6f), (0.6f, 0.6f) }) gr.DrawString(g, f, b, new RectangleF(dx, dy, px, px), sf);
        return bmp;
    }

    public static Bitmap Glyph(string g, int px, Color c)
    {
        var bmp = new Bitmap(px, px);
        using var gr = Graphics.FromImage(bmp);
        gr.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var f = IconFnt(px * 0.78f); using var b = new SolidBrush(c);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        gr.DrawString(g, f, b, new RectangleF(0, 0, px, px), sf);
        return bmp;
    }

    public static Icon AppIcon(Color _) => Logo.Icon();

    /// <summary>The icon that matches what an adapter is: Wi-Fi waves, a network plug, or a phone.</summary>
    /// <summary>"Main: SNcial_5G", or "Main (Wi-Fi)" when the network name is not known.</summary>
    public static string Role(string role, string? network, string adapter) => string.IsNullOrEmpty(network) ? $"{role} ({adapter})" : $"{role}: {network}";

    public static string AdapterGlyph(AdapterKind k) => k switch { AdapterKind.WiFi => Wifi, AdapterKind.Tether => Phone, _ => Ethernet };

    // ---- Title Case for every label (names such as HoYoPlay or Wi-Fi are left alone) ----
    static readonly HashSet<string> Small = new(StringComparer.OrdinalIgnoreCase) { "a", "an", "the", "of", "for", "to", "in", "on", "and", "or", "per", "with", "at", "by", "from", "as" };
    static readonly HashSet<string> Units = new(StringComparer.Ordinal) { "s", "ms", "kbps", "mbps" };
    public static string T(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var w = s.Split(' ');
        for (int i = 0; i < w.Length; i++)
        {
            string x = w[i];
            if (x.Length == 0 || Units.Contains(x) || x.Any(char.IsUpper) || !char.IsLetter(x[0])) continue;   // units, names and numbers stay as typed
            w[i] = i > 0 && Small.Contains(x) ? x.ToLowerInvariant() : char.ToUpperInvariant(x[0]) + x[1..];
        }
        return string.Join(' ', w);
    }

    // ---- buttons ----
    /// <summary>The kit button (see KitButton): Primary/Success = teal fill, Secondary = raised, Danger = red outline.
    /// Empty text = icon-only square button (the tooltip says what it does).</summary>
    public static KitButton Btn(string glyph, string text, string tip, Action click, BtnKind kind = BtnKind.Secondary)
    {
        var b = new KitButton { Kind = kind, Glyph = glyph, Text = T(text), Height = BtnH, Margin = new Padding(0, 0, 8, 0), Font = UiFont(9.75f, Weight.SemiBold) };
        if (text.Length == 0) b.Width = BtnH; else { b.AutoSize = true; b.AutoSizeMode = AutoSizeMode.GrowAndShrink; b.MinimumSize = new Size(0, BtnH); }
        Tip.SetToolTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    static readonly Dictionary<(string, int, int), Bitmap> _glyphCache = new();
    /// <summary>A bold glyph bitmap, cached by glyph, size and colour (paint code calls this every paint). Never dispose it.</summary>
    public static Bitmap GlyphCached(string g, int px, Color c)
    {
        var k = (g, px, c.ToArgb());
        if (!_glyphCache.TryGetValue(k, out var b)) _glyphCache[k] = b = GlyphBold(g, px, c);
        return b;
    }

    /// <param name="sortState">null = a plain list. Otherwise every column header gets a small sort button at its end and can be clicked:
    /// it returns -1 (this column cannot be sorted), 0 (not the sort column), 1 (ascending) or 2 (descending).</param>
    public static void StyleList(ListView lv, Func<int, int>? sortState = null)
    {
        lv.View = View.Details; lv.FullRowSelect = true; lv.HeaderStyle = ColumnHeaderStyle.Nonclickable; lv.BorderStyle = BorderStyle.None;
        lv.BackColor = CardBg; lv.ForeColor = Text; lv.Dock = DockStyle.Fill; lv.GridLines = false; lv.Font = UiFont(9.5f);
        lv.SmallImageList ??= new ImageList { ImageSize = new Size(1, 36) };   // a 1x30 image list is the standard trick to get taller rows

        // the native column header can't be themed, so draw it ourselves; rows and cells are drawn by Windows as usual
        lv.OwnerDraw = true;
        lv.DrawColumnHeader += (_, e) =>
        {
            using (var back = new SolidBrush(CardBg)) e.Graphics.FillRectangle(back, e.Bounds);
            using (var line = new Pen(Border)) e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            var align = e.Header?.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
            int sort = sortState?.Invoke(e.ColumnIndex) ?? -1;
            var textArea = Rectangle.Inflate(e.Bounds, -8, 0); if (sort >= 0) textArea.Width = Math.Max(0, textArea.Width - 26);      // room for the sort button at the end
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", UiFont(9f, bold: true), textArea, Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | align);
            if (sort >= 0) DrawSortButton(e.Graphics, new Rectangle(e.Bounds.Right - 28, e.Bounds.Top + (e.Bounds.Height - 18) / 2, 20, 18), sort);
        };
        // rows are drawn here too (not by Windows): no hover effect, and a selected row keeps readable text in both themes
        lv.DrawItem += (_, _) => { };                      // all drawing happens per cell below
        lv.DrawSubItem += (_, e) => DrawCell(lv, e);
        // the last column stretches to the right edge, so there is no leftover (undrawn) strip in the header
        lv.Resize += (_, _) => FitLastColumn(lv);
        lv.HandleCreated += (_, _) => FitLastColumn(lv);
        if (sortState is not null) lv.HeaderStyle = ColumnHeaderStyle.Clickable;      // a click on a header presses its sort button
    }

    /// <summary>The small button at the end of a sortable column header: two small arrows when idle, one filled arrow (up = ascending, down = descending) when it is the sort column.</summary>
    static void DrawSortButton(Graphics g, Rectangle r, int state)
    {
        var old = g.SmoothingMode; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        bool on = state > 0;
        using (var fill = new SolidBrush(on ? BlueFill : CardBg)) g.FillRectangle(fill, r);
        using (var edge = new Pen(on ? BlueFill : Border)) g.DrawRectangle(edge, r.X, r.Y, r.Width - 1, r.Height - 1);
        using var glyph = new SolidBrush(on ? Color.White : Muted);
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        void Tri(float y, bool up, float w, float h) => g.FillPolygon(glyph, up
            ? new[] { new PointF(cx - w, y + h / 2), new PointF(cx + w, y + h / 2), new PointF(cx, y - h / 2) }
            : new[] { new PointF(cx - w, y - h / 2), new PointF(cx + w, y - h / 2), new PointF(cx, y + h / 2) });
        if (!on) { Tri(cy - 3.2f, true, 3f, 3.6f); Tri(cy + 3.2f, false, 3f, 3.6f); }
        else Tri(cy, state == 1, 4f, 5f);
        g.SmoothingMode = old;
    }

    /// <summary>Repaints the (self-drawn) column headers of a list, for example after the sort changed.</summary>
    public static void RedrawHeader(ListView lv)
    {
        if (!lv.IsHandleCreated) return;
        var hdr = SendMessage(lv.Handle, 0x101F /* LVM_GETHEADER */, IntPtr.Zero, IntPtr.Zero);
        if (hdr != IntPtr.Zero) InvalidateRect(hdr, IntPtr.Zero, true);
    }
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr h, IntPtr rect, bool erase);

    public static Color SelectedRow => Mix(Green, CardBg, Dark ? 0.16f : 0.12f);

    static void DrawCell(ListView lv, DrawListViewSubItemEventArgs e)
    {
        var g = e.Graphics; var r = e.Bounds;
        bool selected = e.Item?.Selected == true;
        using (var back = new SolidBrush(selected ? SelectedRow : CardBg)) g.FillRectangle(back, r);

        int x = r.Left + 8;
        if (e.ColumnIndex == 0 && lv.SmallImageList is { } il && il.ImageSize.Width >= 8 && e.Item!.ImageIndex >= 0 && e.Item.ImageIndex < il.Images.Count)
        {
            g.DrawImage(il.Images[e.Item.ImageIndex], x, r.Top + (r.Height - il.ImageSize.Height) / 2, il.ImageSize.Width, il.ImageSize.Height);
            x += il.ImageSize.Width + 6;
        }
        var color = e.Item!.UseItemStyleForSubItems ? Text : e.SubItem!.ForeColor;       // a cell can carry its own color (e.g. the green "Watched")
        bool right = e.ColumnIndex < lv.Columns.Count && lv.Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right;
        var area = new Rectangle(x, r.Top, Math.Max(0, r.Right - x - 8), r.Height);
        TextRenderer.DrawText(g, e.SubItem!.Text, lv.Font, area, color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | (right ? TextFormatFlags.Right : TextFormatFlags.Left));
    }

    static void FitLastColumn(ListView lv)
    {
        if (lv.Columns.Count == 0 || lv.ClientSize.Width <= 0) return;
        int used = 0; for (int i = 0; i < lv.Columns.Count - 1; i++) used += lv.Columns[i].Width;
        lv.Columns[^1].Width = Math.Max(70, lv.ClientSize.Width - used);
    }

    // ---- making native controls follow the theme (dark title bar, dark scrollbars, input boxes) ----
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string? ids);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

    public static void SkinForm(Form f)
    {
        f.BackColor = Bg; f.ForeColor = Text;
        int v = Dark ? 1 : 0;
        if (DwmSetWindowAttribute(f.Handle, 20, ref v, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref v, 4);   // dark title bar (20 on current Windows, 19 on older builds)
        SkinTree(f);
    }

    public static void SkinTree(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case TextBox t: t.BackColor = InputBg; t.ForeColor = Text; Theme(t, "DarkMode_Explorer"); break;
                case NumericUpDown n: n.BackColor = InputBg; n.ForeColor = Text; Theme(n, "DarkMode_Explorer"); break;
                case ComboBox cb: cb.BackColor = InputBg; cb.ForeColor = Text; cb.FlatStyle = Dark ? FlatStyle.Flat : FlatStyle.Standard; Theme(cb, "DarkMode_CFD"); break;
                case ListView lv: lv.BackColor = CardBg; lv.ForeColor = Text; Theme(lv, "DarkMode_Explorer"); break;
                case ListBox lb: lb.BackColor = CardBg; lb.ForeColor = Text; Theme(lb, "DarkMode_Explorer"); break;
                case CheckBox ch: ch.ForeColor = Text; break;
            }
            SkinTree(c);
        }
    }

    static void Theme(Control c, string darkClass) { if (Dark) { try { SetWindowTheme(c.Handle, darkClass, null); } catch { } } }
}

/// <summary>Grey placeholder bars with a moving shine ("skeleton loading"): shown on a card while its content is being changed,
/// so you never see the old values under the new names.</summary>
sealed class Skeleton : Control
{
    readonly (float W, int H)[] _bars; readonly int _left, _top;
    readonly System.Windows.Forms.Timer _t = new() { Interval = 33 };
    float _phase;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color? Surface { get; set; }        // the color behind the bars (the page color when not inside a card)

    public Skeleton((float W, int H)[] bars, int left, int top)
    {
        _bars = bars; _left = left; _top = top + 4;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.CardBg;
        _t.Tick += (_, _) => { _phase = (_phase + 0.022f) % 1.5f; Invalidate(); };
        VisibleChanged += (_, _) => _t.Enabled = Visible;    // the animation only runs while it can be seen
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Surface ?? Ui.CardBg);
        var baseColor = Ui.Dark ? Color.FromArgb(46, 50, 60) : Color.FromArgb(230, 233, 240);
        var shine = Ui.Dark ? Color.FromArgb(78, 84, 100) : Color.FromArgb(250, 251, 253);
        int avail = Math.Max(10, Width - _left * 2);
        float y = _top;
        foreach (var (w, h) in _bars)
        {
            var r = new RectangleF(_left, y, avail * w, h);
            using var path = RoundedRect(r, Math.Min(6, h / 2f));
            using (var b = new SolidBrush(baseColor)) g.FillPath(b, path);
            float bandW = Math.Max(30, r.Width * 0.35f), x = r.Left - bandW + (r.Width + bandW) * Math.Min(1f, _phase / 1.2f);
            if (_phase <= 1.2f)
            {
                g.SetClip(path);
                var band = new RectangleF(x, r.Top, bandW, r.Height);
                using var lg = new System.Drawing.Drawing2D.LinearGradientBrush(band, Color.Transparent, Color.Transparent, System.Drawing.Drawing2D.LinearGradientMode.Horizontal)
                { InterpolationColors = new System.Drawing.Drawing2D.ColorBlend { Colors = new[] { Color.FromArgb(0, shine), shine, Color.FromArgb(0, shine) }, Positions = new[] { 0f, 0.5f, 1f } } };
                g.FillRectangle(lg, band);
                g.ResetClip();
            }
            y += h + 12;
        }
    }

    static System.Drawing.Drawing2D.GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2; var p = new System.Drawing.Drawing2D.GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    protected override void Dispose(bool disposing) { if (disposing) _t.Dispose(); base.Dispose(disposing); }
}

/// <summary>A card: radius 12, 1 px line border, on the page ground. The header holds an uppercase muted title (with an optional icon in
/// front), an info tooltip after it and optional buttons on the right. Border colour can mark state. Cards are never placed inside other cards.</summary>
class Card : Panel
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Panel Body { get; } = new() { Dock = DockStyle.Fill, BackColor = Ui.CardBg, Padding = new Padding(14, 2, 14, 14) };
    readonly FlowLayoutPanel _right = new() { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Ui.CardBg, Padding = new Padding(0, 8, 10, 0) };
    readonly PictureBox _icon = new() { Width = 20, Height = 20, SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Ui.CardBg };
    readonly Label _title = new() { AutoSize = true, ForeColor = Ui.Muted, Font = Ui.UiFont(9.4f, Weight.Bold), BackColor = Ui.CardBg, UseMnemonic = false };
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get => _border; set { _border = value; Invalidate(); } }
    Color _border = Ui.Border;
    /// <summary>The card's own background colour (the connection in use: teal on Main, amber on Backup). Null = the normal card colour.</summary>
    public Color? Fill => _fill;
    Color? _fill;
    string _glyph = "";
    Color TitleInk => _fill is null ? Ui.Muted : Ui.OnAccent;

    /// <summary>Paints the whole card in <paramref name="fill"/> (null = normal). <paramref name="soft"/> colours the hint icon so it reads on it.
    /// Text on a filled card uses Ui.OnAccent.</summary>
    public void SetFill(Color? fill, Color? soft)
    {
        if (_fill == fill) return;
        _fill = fill;
        Color bg = fill ?? Ui.CardBg, ink = soft ?? Ui.Muted;
        void Recolor(Control c) { foreach (Control k in c.Controls) { if (k is Skeleton) continue; k.BackColor = bg; Recolor(k); } }
        Recolor(this);
        _title.ForeColor = TitleInk;
        if (_glyph.Length > 0) _icon.Image = Ui.GlyphCached(_glyph, 18, TitleInk);
        if (_hint is not null) _hint.Image = Ui.GlyphCached(Ui.Info, 16, ink);
        Invalidate(true);
    }
    const int Radius = 12, Inset = 4;       // rounded corners; the content sits Inset px in, so it never covers the curve

    public Card(string glyph, string title)
    {
        BackColor = Ui.CardBg; Padding = new Padding(Inset); Margin = new Padding(0, 0, Ui.Gap, Ui.Gap); Dock = DockStyle.Fill; DoubleBuffered = true;
        var head = _head = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Ui.CardBg };
        _title.Text = title.ToUpperInvariant();
        _title.SizeChanged += (_, _) => PlaceHint();       // the info icon follows the end of the title
        head.Controls.Add(_right); head.Controls.Add(_icon); head.Controls.Add(_title);
        Controls.Add(Body); Controls.Add(head);
        SetIcon(glyph);
    }

    public void SetTitle(string t) => _title.Text = t.ToUpperInvariant();
    /// <summary>"" = no icon (the title starts at the card's edge).</summary>
    public void SetIcon(string glyph)
    {
        _glyph = glyph; _icon.Visible = glyph.Length > 0;
        if (_icon.Visible) _icon.Image = Ui.GlyphCached(glyph, 18, TitleInk);
        _icon.Location = new Point(14, 13); _title.Location = new Point(_icon.Visible ? 14 + 26 : 14, 14);
        PlaceHint();
    }

    // ---- loading placeholders: grey bars with a moving shine, shown while the content is being changed ----
    Skeleton? _skel; readonly List<Control> _hidden = new();
    public static readonly (float W, int H)[] DefaultBars = { (0.45f, 26), (0.8f, 12), (0.6f, 12) };

    public void SetLoading(bool on, (float W, int H)[]? bars = null)
    {
        if (on && _skel is null)
        {
            _skel = new Skeleton(bars ?? DefaultBars, Body.Padding.Left, Body.Padding.Top) { Visible = false };
            Body.Controls.Add(_skel);
            Body.Resize += (_, _) => _skel?.SetBounds(0, 0, Body.Width, Body.Height);   // covers the whole body, whatever else is docked there
            _skel.SetBounds(0, 0, Body.Width, Body.Height);
        }
        if (_skel is null) return;
        _ = _skel.Handle;                 // realize it: DrawToBitmap (snapshots) skips controls that have no window yet
        // hide what the bars replace (instead of relying on z-order: WM_PRINT paints overlapping siblings the wrong way round)
        if (on) { foreach (Control c in Body.Controls) if (c != _skel && c.Visible) { c.Visible = false; _hidden.Add(c); } }
        else { foreach (var c in _hidden) c.Visible = true; _hidden.Clear(); }
        _skel.Visible = on;
        if (on) _skel.BringToFront();
    }

    /// <summary>Any control at the right of the header (the chart's legend).</summary>
    public void AddHeaderControl(Control c) => _right.Controls.Add(c);

    public void AddHeaderButton(string glyph, string tip, Action click)
    {
        var b = Ui.Btn(glyph, "", tip, click); b.Height = 32; b.Width = 34; b.Margin = new Padding(4, 0, 0, 0);
        _right.Controls.Add(b);
    }

    /// <summary>A labeled action button in the header (for actions that deserve a visible name).</summary>
    public void AddHeaderAction(string glyph, string text, string tip, Action click, BtnKind kind = BtnKind.Secondary)
    {
        var b = Ui.Btn(glyph, text, tip, click, kind); b.Height = 32; b.MinimumSize = new Size(0, 32); b.Margin = new Padding(6, 0, 0, 0); b.Padding = new Padding(10, 0, 12, 0);
        _right.Controls.Add(b);
    }

    Panel? _head; PictureBox? _hint;

    /// <summary>The (i) icon sits right after the title; its tooltip explains the card.</summary>
    public void SetHint(string tip)
    {
        if (_hint is null)
        {
            _hint = new PictureBox { Image = Ui.GlyphCached(Ui.Info, 16, Ui.Muted), Width = 24, Height = 24, SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Ui.CardBg, Cursor = Cursors.Help };
            _head!.Controls.Add(_hint); PlaceHint();
        }
        Ui.Tip.SetToolTip(_hint, tip);
    }

    // centred on the title line (the same middle as the card icon and the title text)
    void PlaceHint() { if (_hint is not null) _hint.Location = new Point(_title.Right + 2, _title.Top + (_title.Height - _hint.Height) / 2); }

    // the outline is built once per size, not per paint
    System.Drawing.Drawing2D.GraphicsPath? _path; Size _pathSize;
    System.Drawing.Drawing2D.GraphicsPath Path()
    {
        if (_path is null || _pathSize != Size) { _path?.Dispose(); _path = Ui.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Radius); _pathSize = Size; }
        return _path;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

    /// <summary>Rounded card: the corners outside the curve show the page behind it; the edge is drawn smooth.</summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Ui.Bg);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.FillPath(Ui.Brush(_fill ?? Ui.CardBg), Path());
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_fill is not null) return;        // a filled card needs no outline
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.DrawPath(Ui.Pen(_border, _border == Ui.Border ? 1f : 2f), Path());
    }

    protected override void Dispose(bool disposing) { if (disposing) _path?.Dispose(); base.Dispose(disposing); }
}

