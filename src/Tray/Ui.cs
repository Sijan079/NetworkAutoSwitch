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

    // ---- theme palette ----
    public static bool Dark { get; private set; }
    public static Color Bg { get; private set; }
    public static Color CardBg { get; private set; }
    public static Color InputBg { get; private set; }
    public static Color Border { get; private set; }
    public static Color Text { get; private set; }
    public static Color Muted { get; private set; }
    public static Color Hover { get; private set; }
    public static Color Green { get; private set; }     // "ink" colors: used for text, icons and stripes
    public static Color Amber { get; private set; }
    public static Color Red { get; private set; }
    public static Color Blue { get; private set; }
    // "fill" colors: backgrounds of filled buttons and badges (same in both themes, always readable with white text)
    public static readonly Color BlueFill = Color.FromArgb(37, 99, 235), GreenFill = Color.FromArgb(22, 135, 70), RedFill = Color.FromArgb(200, 36, 48), AmberFill = Color.FromArgb(200, 112, 0);

    static Ui() { SetTheme(false); }

    public static void SetTheme(bool dark)
    {
        Dark = dark;
        if (!dark)
        {
            Bg = Color.FromArgb(243, 244, 247); CardBg = Color.White; InputBg = Color.White; Border = Color.FromArgb(222, 225, 232);
            Text = Color.FromArgb(28, 30, 36); Muted = Color.FromArgb(100, 105, 118); Hover = Color.FromArgb(236, 239, 245);
            Green = Color.FromArgb(22, 135, 70); Amber = Color.FromArgb(190, 105, 0); Red = Color.FromArgb(200, 36, 48); Blue = Color.FromArgb(37, 99, 235);
        }
        else
        {
            Bg = Color.FromArgb(21, 23, 28); CardBg = Color.FromArgb(32, 35, 42); InputBg = Color.FromArgb(42, 46, 55); Border = Color.FromArgb(58, 63, 74);
            Text = Color.FromArgb(232, 234, 240); Muted = Color.FromArgb(150, 156, 170); Hover = Color.FromArgb(48, 52, 62);
            Green = Color.FromArgb(62, 202, 122); Amber = Color.FromArgb(246, 172, 52); Red = Color.FromArgb(255, 108, 118); Blue = Color.FromArgb(112, 162, 255);
        }
    }

    // ---- icons: Segoe Fluent Icons (Windows 11) with Segoe MDL2 Assets (Windows 10) as the fallback ----
    public const string Wifi = "", Ethernet = "", Pause = "", Play = "", Add = "", Delete = "",
        Refresh = "", OpenFile = "", Save = "", Undo = "", Unlock = "", Lock = "", Swap = "",
        Clock = "", Home = "", Document = "", Settings = "", Download = "", Warning = "",
        Info = "", Chart = "", Data = "", Folder = "", Code = "", Shield = "", Check = "",
        Clear = "", Dot = "", Eye = "", Block = "", List = "", Brightness = "", Phone = "", Moon = "", Monitor = "", Mute = "";

    static readonly string IconFont = PickFont();
    static string PickFont()
    {
        using var fc = new InstalledFontCollection();
        return fc.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    }

    public static readonly ToolTip Tip = new() { InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 12000 };

    public static Font IconFnt(float px) => new(IconFont, px, FontStyle.Regular, GraphicsUnit.Pixel);
    public static Font UiFont(float pt, bool bold = false) => new(bold ? "Segoe UI Semibold" : "Segoe UI", pt);

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
    /// <summary>Flat button with an icon. Primary/Success/Danger are filled with color so the main action stands out;
    /// Secondary is outlined. Empty text = icon-only square button (the tooltip says what it does).</summary>
    public static Button Btn(string glyph, string text, string tip, Action click, BtnKind kind = BtnKind.Secondary)
    {
        Color fill = kind switch { BtnKind.Primary => BlueFill, BtnKind.Success => GreenFill, BtnKind.Danger => RedFill, _ => CardBg };
        bool filled = kind != BtnKind.Secondary;
        var fg = filled ? Color.White : Text;
        var b = new Button
        {
            FlatStyle = FlatStyle.Flat, BackColor = fill, ForeColor = fg, Image = Glyph(glyph, 18, fg), Text = T(text), Cursor = Cursors.Hand,
            Height = BtnH, Margin = new Padding(0, 0, 8, 0), UseVisualStyleBackColor = false,
            TextImageRelation = TextImageRelation.ImageBeforeText, ImageAlign = ContentAlignment.MiddleCenter, Font = UiFont(9.5f, bold: filled),
        };
        if (text.Length == 0) b.Width = BtnH + 2; else { b.AutoSize = true; b.AutoSizeMode = AutoSizeMode.GrowAndShrink; b.Padding = new Padding(12, 0, 16, 0); b.MinimumSize = new Size(96, BtnH); }
        b.FlatAppearance.BorderColor = filled ? fill : Border; b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.MouseOverBackColor = filled ? ControlPaint.Dark(fill, 0.12f) : Hover;
        b.FlatAppearance.MouseDownBackColor = filled ? ControlPaint.Dark(fill, 0.25f) : Border;
        Tip.SetToolTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    /// <param name="sortState">null = a plain list. Otherwise every column header gets a small sort button at its end and can be clicked:
    /// it returns -1 (this column cannot be sorted), 0 (not the sort column), 1 (ascending) or 2 (descending).</param>
    public static void StyleList(ListView lv, Func<int, int>? sortState = null)
    {
        lv.View = View.Details; lv.FullRowSelect = true; lv.HeaderStyle = ColumnHeaderStyle.Nonclickable; lv.BorderStyle = BorderStyle.None;
        lv.BackColor = CardBg; lv.ForeColor = Text; lv.Dock = DockStyle.Fill; lv.GridLines = false; lv.Font = UiFont(9.5f);
        lv.SmallImageList ??= new ImageList { ImageSize = new Size(1, 30) };   // a 1x30 image list is the standard trick to get taller rows

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

    public static Color SelectedRow => Dark ? Color.FromArgb(52, 72, 112) : Color.FromArgb(215, 227, 252);

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

/// <summary>Our own tab strip (the built-in one can't be themed): icon + title buttons, the selected one underlined in the accent color.</summary>
sealed class TabHost : Panel
{
    sealed class TabBtn : Button
    {
        public bool Selected;
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Selected) { using var b = new SolidBrush(Ui.Blue); e.Graphics.FillRectangle(b, 10, Height - 3, Width - 20, 3); }
        }
    }

    readonly FlowLayoutPanel _strip = new() { Dock = DockStyle.Top, Height = 48, BackColor = Ui.Bg, Padding = new Padding(Ui.Pad - 4, 4, 0, 0), WrapContents = false };
    readonly Panel _body = new() { Dock = DockStyle.Fill, BackColor = Ui.Bg };
    readonly List<(string Key, TabBtn Btn, Control Page)> _tabs = new();
    public event Action? Changed;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string SelectedKey { get; private set; } = "";
    public List<string> Keys() => _tabs.Select(t => t.Key).ToList();

    public TabHost()
    {
        BackColor = Ui.Bg; Dock = DockStyle.Fill;
        Controls.Add(_body); Controls.Add(_strip);
    }

    public void AddPage(string key, string text, string glyph, Control page)
    {
        var b = new TabBtn
        {
            FlatStyle = FlatStyle.Flat, BackColor = Ui.Bg, ForeColor = Ui.Muted, Text = Ui.T(text), Image = Ui.Glyph(glyph, 18, Ui.Muted), Cursor = Cursors.Hand,
            TextImageRelation = TextImageRelation.ImageBeforeText, Height = 42, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 0, 16, 4), Margin = new Padding(0, 0, 4, 0), Font = Ui.UiFont(10f), UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = Ui.Hover; b.FlatAppearance.MouseDownBackColor = Ui.Border;
        b.Click += (_, _) => Select(key);
        page.Dock = DockStyle.Fill; page.Visible = false; _body.Controls.Add(page); _strip.Controls.Add(b);
        _tabs.Add((key, b, page));
        if (_tabs.Count == 1) Select(key, silent: true);
    }

    public void Select(string key, bool silent = false)
    {
        foreach (var (k, btn, page) in _tabs)
        {
            bool on = k == key;
            page.Visible = on; btn.Selected = on;
            btn.ForeColor = on ? Ui.Text : Ui.Muted; btn.Font = Ui.UiFont(10f, bold: on);
            btn.Invalidate();
        }
        SelectedKey = key;
        if (!silent) Changed?.Invoke();
    }
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

/// <summary>A panel with a small icon + title header, optional icon buttons and an info tooltip on the right. Border color can mark state.
/// Cards are never placed inside other cards.</summary>
class Card : Panel
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Panel Body { get; } = new() { Dock = DockStyle.Fill, BackColor = Ui.CardBg, Padding = new Padding(Ui.Pad, 6, Ui.Pad, Ui.Gap) };
    readonly FlowLayoutPanel _right = new() { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Ui.CardBg, Padding = new Padding(0, 4, 8, 0) };
    readonly PictureBox _icon = new() { Width = 22, Height = 22, SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Ui.CardBg };
    readonly Label _title = new() { AutoSize = true, ForeColor = Ui.Muted, Font = Ui.UiFont(10f, bold: true), BackColor = Ui.CardBg };
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get => _border; set { _border = value; Invalidate(); } }
    Color _border = Ui.Border;

    public Card(string glyph, string title)
    {
        BackColor = Ui.CardBg; Padding = new Padding(1); Margin = new Padding(0, 0, Ui.Gap, Ui.Gap); Dock = DockStyle.Fill;
        _icon.Image = Ui.Glyph(glyph, 18, Ui.Muted); _title.Text = Ui.T(title);
        var head = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = Ui.CardBg };
        _icon.Location = new Point(Ui.Pad, 10); _title.Location = new Point(Ui.Pad + 30, 11);
        head.Controls.Add(_right); head.Controls.Add(_icon); head.Controls.Add(_title);
        Controls.Add(Body); Controls.Add(head);
    }

    public void SetTitle(string t) => _title.Text = Ui.T(t);
    public void SetIcon(string glyph) => _icon.Image = Ui.Glyph(glyph, 18, Ui.Muted);

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

    public void SetHint(string tip)
    {
        var i = new PictureBox { Image = Ui.Glyph(Ui.Info, 18, Ui.Muted), Width = 30, Height = 32, SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Ui.CardBg, Cursor = Cursors.Help };
        Ui.Tip.SetToolTip(i, tip); _right.Controls.Add(i);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var p = new Pen(_border, _border == Ui.Border ? 1f : 2f);
        e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>One connection on the dashboard. Left: network name, adapter, average ping, uptime. Right: the current ping, number on top and "ms" below it.
/// The border color marks the connection in use.</summary>
sealed class AdapterCard : Card
{
    /// <summary>Loading placeholders that match the layout: four lines on the left (the right side stays quiet).</summary>
    public static readonly (float W, int H)[] Bars = { (0.45f, 18), (0.28f, 12), (0.40f, 12), (0.30f, 12) };

    static Label Line(float size, bool bold, Color color, int height) => new() { Dock = DockStyle.Top, Height = height, AutoEllipsis = true, ForeColor = color, Font = Ui.UiFont(size, bold: bold), BackColor = Ui.CardBg, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label _net = Line(12f, true, Ui.Text, 28), _adapter = Line(10f, false, Ui.Muted, 22), _avg = Line(10f, false, Ui.Muted, 22), _up = Line(10f, false, Ui.Muted, 22);
    readonly Label _ms = new() { Dock = DockStyle.Top, Height = 54, Font = Ui.UiFont(28f, bold: true), TextAlign = ContentAlignment.BottomCenter, BackColor = Ui.CardBg, Text = "..." };
    readonly Label _unit = new() { Dock = DockStyle.Top, Height = 26, Font = Ui.UiFont(11f), ForeColor = Ui.Muted, TextAlign = ContentAlignment.TopCenter, BackColor = Ui.CardBg };

    public AdapterCard(string glyph, string title) : base(glyph, title)
    {
        var right = new Panel { Dock = DockStyle.Right, Width = 104, BackColor = Ui.CardBg };
        right.Controls.Add(_unit); right.Controls.Add(_ms);                       // docked to the top: the last added is the first shown
        var left = new Panel { Dock = DockStyle.Fill, BackColor = Ui.CardBg };
        left.Controls.Add(_up); left.Controls.Add(_avg); left.Controls.Add(_adapter); left.Controls.Add(_net);
        Body.Controls.Add(left); Body.Controls.Add(right);
    }

    bool _known, _selected;

    /// <summary>The network name and the adapter it is on.</summary>
    public void SetIdentity(string network, string adapter)
    {
        _known = network.Length > 0;
        _net.Text = _known ? network : "Unknown network";
        _net.ForeColor = _known && _selected ? Ui.Text : Ui.Muted;
        _adapter.Text = adapter;
    }

    /// <summary>Shows one measurement. <paramref name="selected"/> = this is the connection in use: its colors are shown in full.
    /// The other card is muted (grey ping, grey name), so the one in use stands out even without looking at the border.</summary>
    public void Show(ProbeResult r, double uptimePct, int? avgMs, Color? inUse, bool selected)
    {
        _selected = selected;
        _net.ForeColor = _known && selected ? Ui.Text : Ui.Muted;
        _avg.Text = $"Average ping  \u00b7  {(avgMs is int a ? a + " ms" : "-")}";
        _up.Text = $"Uptime  \u00b7  {uptimePct:0.#}%";
        if (r.Healthy) { _ms.Font = Ui.UiFont(28f, bold: true); _ms.Text = r.Ms.ToString(); _ms.ForeColor = selected ? Ui.Green : Ui.Muted; _unit.Text = "ms"; Ui.Tip.SetToolTip(_ms, ""); }
        else { _ms.Font = Ui.UiFont(28f, bold: true); _ms.Text = "\u2014"; _ms.ForeColor = selected ? Ui.Red : Ui.Muted; _unit.Text = ""; Ui.Tip.SetToolTip(_ms, r.Detail); }   // a ping has no number when nothing answers: 0 would read as "perfect"
        BorderColor = inUse ?? Ui.Border;
    }
}
