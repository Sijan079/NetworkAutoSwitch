using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

// The control kit of the redesign (docs/UI-DESIGN.md). Everything here paints itself, double-buffered, with colours from Ui,
// shared brushes/pens/fonts from Ui, and rounded paths kept per control and rebuilt only when the size changes.
// Pages use these instead of styling stock controls by hand, so the whole app reads as one system.

/// <summary>Base for kit controls: double-buffered, transparent-looking corners (paints its parent's colour first).</summary>
abstract class KitControl : Control
{
    protected KitControl()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Font = Ui.UiFont(9.75f, Weight.SemiBold); ForeColor = Ui.Text;
    }
    protected Color Behind => Parent?.BackColor ?? Ui.Bg;
    GraphicsPath? _path; Size _pathSize; float _pathRadius;
    /// <summary>The rounded outline of this control, inset half a pixel so a 1 px border is crisp. Cached until the size changes.</summary>
    protected GraphicsPath Outline(float radius)
    {
        if (_path is null || _pathSize != Size || _pathRadius != radius)
        {
            _path?.Dispose(); _path = Ui.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), radius); _pathSize = Size; _pathRadius = radius;
        }
        return _path;
    }
    protected static void Smooth(Graphics g) { g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.Half; }
    protected override void Dispose(bool disposing) { if (disposing) _path?.Dispose(); base.Dispose(disposing); }
}

/// <summary>The kit button (Ui.Btn makes these): radius 8, 36 px tall. Primary = teal fill, Secondary = raised, Danger = red outline.
/// It stays a real Button, so keyboard, Enter/Space, AcceptButton and screen readers work as usual.</summary>
sealed class KitButton : Button
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public BtnKind Kind { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Glyph { get; set; } = "";
    bool _hover, _down;
    GraphicsPath? _path; Size _pathSize;

    public KitButton()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false; Cursor = Cursors.Hand;
    }

    (Color Bg, Color Fg, Color Edge) Colors()
    {
        var parent = Parent?.BackColor ?? Ui.CardBg;
        var (bg, fg, edge) = Kind switch
        {
            BtnKind.Primary or BtnKind.Success => (Ui.Green, Ui.OnAccent, Ui.Green),
            BtnKind.Danger => (parent, Ui.Red, Ui.Red),
            _ => (Ui.Raised, Ui.Text, Ui.Border),
        };
        if (!Enabled) return (bg, Ui.Mix(fg, bg, 0.45f), Ui.Mix(edge, bg, 0.5f));
        if (_down) bg = Kind == BtnKind.Danger ? Ui.Tint(Ui.Red, 0.26f) : Ui.Mix(Ui.Text, bg, Kind == BtnKind.Secondary ? 0.14f : 0.22f);
        else if (_hover) bg = Kind == BtnKind.Danger ? Ui.Tint(Ui.Red, 0.14f) : Ui.Mix(Ui.Text, bg, Kind == BtnKind.Secondary ? 0.07f : 0.12f);
        return (bg, fg, edge);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Ui.CardBg);
        g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.Half;
        if (_path is null || _pathSize != Size) { _path?.Dispose(); _path = Ui.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8); _pathSize = Size; }
        var (bg, fg, edge) = Colors();
        g.FillPath(Ui.Brush(bg), _path);
        g.DrawPath(Ui.Pen(edge), _path);
        if (Focused && ShowFocusCues) using (var r = Ui.RoundRect(new RectangleF(2.5f, 2.5f, Width - 5.5f, Height - 5.5f), 6)) g.DrawPath(Ui.Pen(Ui.Blue, 1.5f), r);

        // icon + text, centred together
        var text = Text; const int icon = 16, gap = 8;
        bool hasIcon = Glyph.Length > 0;
        var size = text.Length > 0 ? TextRenderer.MeasureText(g, text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix) : Size.Empty;
        int total = (hasIcon ? icon : 0) + (hasIcon && text.Length > 0 ? gap : 0) + size.Width;
        int x = (Width - total) / 2;
        if (hasIcon) { g.DrawImage(Ui.GlyphCached(Glyph, icon + 2, fg), x - 1, (Height - icon - 2) / 2); x += icon + gap; }
        if (text.Length > 0) TextRenderer.DrawText(g, text, Font, new Rectangle(x, 0, size.Width + 2, Height), fg, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    public override Size GetPreferredSize(Size proposed)
    {
        if (Text.Length == 0) return new Size(Height, Height);
        var t = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        return new Size(t.Width + 28 + (Glyph.Length > 0 ? 24 : 0), Math.Max(Height, Ui.BtnH));
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void Dispose(bool disposing) { if (disposing) _path?.Dispose(); base.Dispose(disposing); }
}

/// <summary>A small rounded label: a log kind, a rule action, IN USE / STANDBY. Outlined by default, filled = the role colour behind OnAccent text.</summary>
sealed class Chip : KitControl
{
    Color _role = Ui.Muted; bool _filled;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Role { get => _role; set { _role = value; Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Filled { get => _filled; set { _filled = value; Invalidate(); } }

    public Chip(string text, Color role, bool filled = false)
    {
        _role = role; _filled = filled; Font = Ui.UiFont(8.25f, Weight.SemiBold); Text = text; Height = 22; Margin = new Padding(0, 0, 6, 0);
        AutoSize = true;
    }
    public override Size GetPreferredSize(Size proposed) => new(TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width + 20, 22);
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); if (AutoSize) Size = GetPreferredSize(Size.Empty); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Behind); Smooth(g);
        var path = Outline(Height / 2f);
        if (_filled) g.FillPath(Ui.Brush(_role), path); else g.DrawPath(Ui.Pen(_role), path);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, _filled ? Ui.OnAccent : _role, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }

    /// <summary>The same chip drawn straight onto a surface (table rows, the log), without a control per row.</summary>
    public static int Paint(Graphics g, string text, Color role, int x, int centreY, bool filled = false)
    {
        var f = Ui.UiFont(8.25f, Weight.SemiBold);
        int w = TextRenderer.MeasureText(g, text, f, Size.Empty, TextFormatFlags.NoPadding).Width + 18;
        var r = new RectangleF(x + 0.5f, centreY - 10.5f, w - 1, 21);
        var oldS = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = Ui.RoundRect(r, 10.5f)) { if (filled) g.FillPath(Ui.Brush(role), p); else g.DrawPath(Ui.Pen(role), p); }
        g.SmoothingMode = oldS;
        TextRenderer.DrawText(g, text, f, Rectangle.Round(r), filled ? Ui.OnAccent : role, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        return w;
    }
}

/// <summary>An on/off switch (38 x 22). A CheckBox underneath, so Checked, CheckedChanged, Space and screen readers behave as usual.</summary>
sealed class Toggle : CheckBox
{
    bool _hover;
    public Toggle()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        AutoSize = false; Size = new Size(38, 22); Cursor = Cursors.Hand; Text = ""; Margin = new Padding(0);
        AccessibleRole = AccessibleRole.CheckButton;    // WinForms has no "switch" role; a check button is what screen readers expect for on/off
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent?.BackColor ?? Ui.CardBg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = Checked ? Ui.Green : (_hover ? Ui.Mix(Ui.Text, Ui.Border, 0.12f) : Ui.Border);
        if (!Enabled) track = Ui.Mix(track, Parent?.BackColor ?? Ui.CardBg, 0.5f);
        using (var p = Ui.RoundRect(new RectangleF(0, 0, Width - 1, Height - 1), (Height - 1) / 2f)) g.FillPath(Ui.Brush(track), p);
        int d = Height - 6, x = Checked ? Width - d - 3 : 3;
        g.FillEllipse(Ui.Brush(Checked ? Ui.OnAccent : Ui.Text), x, 3, d, d);
        if (Focused && ShowFocusCues) using (var p = Ui.RoundRect(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), (Height - 2) / 2f)) g.DrawPath(Ui.Pen(Ui.Blue, 1.5f), p);
    }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
}

/// <summary>A segmented control (Automatic / Stay On Main / Stay On Backup; All / Switches / ...). Arrow keys move the choice.</summary>
sealed class Segmented : KitControl
{
    readonly List<string> _items = new();
    readonly List<Rectangle> _cells = new();
    int _selected, _hover = -1;
    public event Action<int>? SelectedChanged;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex { get => _selected; set { if (value == _selected || value < 0 || value >= _items.Count) return; _selected = value; Invalidate(); SelectedChanged?.Invoke(value); } }
    public string SelectedText => _selected < _items.Count ? _items[_selected] : "";

    public Segmented(params string[] items)
    {
        _items.AddRange(items); Font = Ui.UiFont(9.4f, Weight.SemiBold); Height = 36; TabStop = true; Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PageTabList; Margin = new Padding(0);
        Width = GetPreferredSize(Size.Empty).Width;
    }
    public void SetItems(params string[] items) { _items.Clear(); _items.AddRange(items); _selected = Math.Min(_selected, Math.Max(0, items.Length - 1)); Width = GetPreferredSize(Size.Empty).Width; Invalidate(); }
    /// <summary>Selects without raising SelectedChanged (to show a state that came from elsewhere).</summary>
    public void Show(int index) { if (index >= 0 && index < _items.Count) { _selected = index; Invalidate(); } }

    public override Size GetPreferredSize(Size proposed) => new(_items.Sum(t => TextRenderer.MeasureText(t, Font, Size.Empty, TextFormatFlags.NoPadding).Width + 28) + 6 + 2 * Math.Max(0, _items.Count - 1), 36);

    void Measure()
    {
        _cells.Clear(); int x = 3;
        foreach (var t in _items) { int w = TextRenderer.MeasureText(t, Font, Size.Empty, TextFormatFlags.NoPadding).Width + 28; _cells.Add(new Rectangle(x, 3, w, Height - 6)); x += w + 2; }
        // stretch to the control's width when it is wider than needed (a docked segmented control fills its row)
        int extra = Width - 3 - x + 2;
        if (extra > 0 && _cells.Count > 0) { int each = extra / _cells.Count; x = 3; for (int i = 0; i < _cells.Count; i++) { var c = _cells[i]; _cells[i] = new Rectangle(x, 3, c.Width + each, c.Height); x += c.Width + each + 2; } }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Behind); Smooth(g); Measure();
        var outline = Outline(9);
        g.FillPath(Ui.Brush(Ui.InputBg), outline); g.DrawPath(Ui.Pen(Ui.Border), outline);
        for (int i = 0; i < _cells.Count; i++)
        {
            var c = _cells[i]; bool on = i == _selected;
            if (on || i == _hover)
                using (var p = Ui.RoundRect(new RectangleF(c.X + 0.5f, c.Y + 0.5f, c.Width - 1, c.Height - 1), 7))
                {
                    g.FillPath(Ui.Brush(on ? Ui.Raised : Ui.Mix(Ui.Raised, Ui.InputBg, 0.5f)), p);
                    if (on) g.DrawPath(Ui.Pen(Ui.Border), p);
                }
            TextRenderer.DrawText(g, _items[i], Font, c, on ? Ui.Text : Ui.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (on && Focused && ShowFocusCues) using (var p = Ui.RoundRect(new RectangleF(c.X + 1.5f, c.Y + 1.5f, c.Width - 3, c.Height - 3), 6)) g.DrawPath(Ui.Pen(Ui.Blue, 1.5f), p);
        }
    }

    int Hit(Point p) { Measure(); return _cells.FindIndex(c => c.Contains(p)); }
    protected override void OnMouseMove(MouseEventArgs e) { int h = Hit(e.Location); if (h != _hover) { _hover = h; Invalidate(); } base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); int h = Hit(e.Location); if (h >= 0) SelectedIndex = h; base.OnMouseDown(e); }
    protected override bool IsInputKey(Keys k) => k is Keys.Left or Keys.Right || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left) SelectedIndex = Math.Max(0, _selected - 1);
        else if (e.KeyCode == Keys.Right) SelectedIndex = Math.Min(_items.Count - 1, _selected + 1);
        base.OnKeyDown(e);
    }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override AccessibleObject CreateAccessibilityInstance() => new Acc(this);
    sealed class Acc(Segmented o) : ControlAccessibleObject(o) { public override string? Value => o.SelectedText; }
}

/// <summary>A field: a small label above a rounded box that holds a TextBox, NumericUpDown or ComboBox (border removed), and an optional
/// suffix ("MB", "seconds") inside the box on the right. The box takes the whole width of the field.</summary>
sealed class Field : Panel
{
    public Control Input { get; }
    readonly Label _label = new() { Dock = DockStyle.Top, Height = 22, Font = Ui.UiFont(9f, Weight.SemiBold), ForeColor = Ui.Muted, TextAlign = ContentAlignment.TopLeft };
    readonly FieldBox _box;

    public Field(string label, Control input, string suffix = "")
    {
        Input = input; Height = 22 + 36; Margin = new Padding(0, 0, Ui.Gap, Ui.Gap); BackColor = Ui.CardBg;
        _label.Text = label; _label.BackColor = BackColor;
        _box = new FieldBox(input, suffix) { Dock = DockStyle.Top, Height = 36 };
        Controls.Add(_box); Controls.Add(_label);
    }
    protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e); _label.BackColor = BackColor; _box.Invalidate(); }

    sealed class FieldBox : Panel
    {
        readonly Control _input; readonly string _suffix;
        GraphicsPath? _path; Size _pathSize;
        public FieldBox(Control input, string suffix)
        {
            _input = input; _suffix = suffix;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            switch (input)
            {
                case TextBox t: t.BorderStyle = BorderStyle.None; break;
                case NumericUpDown n: n.BorderStyle = BorderStyle.None; break;
                case ComboBox c: c.FlatStyle = FlatStyle.Flat; break;
            }
            input.BackColor = Ui.InputBg; input.ForeColor = Ui.Text; input.Font = Ui.UiFont(9.75f);
            Controls.Add(input);
            input.GotFocus += (_, _) => Invalidate(); input.LostFocus += (_, _) => Invalidate();
        }
        int SuffixW => _suffix.Length == 0 ? 0 : TextRenderer.MeasureText(_suffix, Ui.UiFont(9f), Size.Empty, TextFormatFlags.NoPadding).Width + 12;
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int h = _input.PreferredSize.Height, inset = _input is ComboBox ? 2 : 12;
            _input.SetBounds(inset, Math.Max(1, (Height - h) / 2), Math.Max(10, Width - inset * 2 - SuffixW + (_input is ComboBox ? 0 : 0)), h);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent?.BackColor ?? Ui.CardBg);
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.Half;
            if (_path is null || _pathSize != Size) { _path?.Dispose(); _path = Ui.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8); _pathSize = Size; }
            g.FillPath(Ui.Brush(Ui.InputBg), _path);
            g.DrawPath(Ui.Pen(_input.ContainsFocus ? Ui.Green : Ui.Border), _path);
            if (_suffix.Length > 0) TextRenderer.DrawText(g, _suffix, Ui.UiFont(9f), new Rectangle(Width - SuffixW - 4, 0, SuffixW, Height), Ui.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding);
        }
        protected override void OnMouseDown(MouseEventArgs e) { _input.Focus(); base.OnMouseDown(e); }
        protected override void Dispose(bool disposing) { if (disposing) _path?.Dispose(); base.Dispose(disposing); }
    }
}

/// <summary>The top of every page: a title (22 px bold), a one-line subtitle, and the page's action buttons on the right.</summary>
sealed class PageHeader : Panel
{
    readonly FlowLayoutPanel _actions = new() { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 22, 0, 0) };
    readonly Label _title = new() { AutoSize = true, Font = Ui.UiFont(16.5f, Weight.Bold), ForeColor = Ui.Text, Location = new Point(0, 18) };
    readonly Label _sub = new() { AutoSize = true, Font = Ui.UiFont(9.75f), ForeColor = Ui.Muted };

    public PageHeader(string title, string subtitle = "")
    {
        Dock = DockStyle.Top; Height = 76; BackColor = Ui.Bg; Padding = new Padding(0);
        _title.Text = title; _sub.Text = subtitle; _title.BackColor = _sub.BackColor = _actions.BackColor = Ui.Bg;
        Controls.Add(_title); Controls.Add(_sub); Controls.Add(_actions);
        _title.SizeChanged += (_, _) => _sub.Location = new Point(1, _title.Bottom + 2);
        _sub.Location = new Point(1, _title.Bottom + 2);
    }
    public void SetTitle(string t) => _title.Text = t;
    public void SetSubtitle(string s) => _sub.Text = s;
    /// <summary>Adds a control (usually a KitButton) to the right side, left to right in the order added.</summary>
    public T AddAction<T>(T c) where T : Control { c.Margin = new Padding(10, 0, 0, 0); _actions.Controls.Add(c); return c; }
}

/// <summary>An uppercase, muted section label (inside a card, above a group of rows).</summary>
sealed class SectionHeader : Label
{
    public SectionHeader(string text)
    {
        Text = text.ToUpperInvariant(); AutoSize = false; Dock = DockStyle.Top; Height = 28; Font = Ui.UiFont(9.4f, Weight.Bold); ForeColor = Ui.Muted;
        TextAlign = ContentAlignment.TopLeft; BackColor = Ui.CardBg; UseMnemonic = false;
    }
}

/// <summary>The red alarm across the top of the dashboard (main is the phone, both connections are one network). Tinted red, red border,
/// a warning icon, a bold title, one line of detail, and an optional action button on the right. Amber when <see cref="Severe"/> is false.</summary>
sealed class Banner : KitControl
{
    string _title = "", _detail = "";
    bool _severe = true;
    readonly FlowLayoutPanel _right = new() { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(0, 14, 14, 0) };
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Severe { get => _severe; set { _severe = value; Recolor(); Invalidate(); } }
    Color Role => _severe ? Ui.Red : Ui.Amber;

    public Banner() { Height = 68; Dock = DockStyle.Top; Controls.Add(_right); Recolor(); }
    void Recolor() { BackColor = Ui.Tint(Role, 0.16f); _right.BackColor = BackColor; foreach (Control c in _right.Controls) c.Invalidate(); }
    public void Set(string title, string detail) { _title = title; _detail = detail; Invalidate(); }
    public T AddAction<T>(T c) where T : Control { c.Margin = new Padding(8, 0, 0, 0); _right.Controls.Add(c); return c; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Behind); Smooth(g);
        var path = Outline(12);
        g.FillPath(Ui.Brush(BackColor), path); g.DrawPath(Ui.Pen(Role), path);
        g.DrawImage(Ui.GlyphCached(Ui.Warning, 22, Role), 18, (Height - 22) / 2);
        int textW = Math.Max(10, _right.Left - 60);
        TextRenderer.DrawText(g, _title, Ui.UiFont(10.5f, Weight.Bold), new Rectangle(52, 14, textW, 22), Ui.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, _detail, Ui.UiFont(9.4f), new Rectangle(52, 36, textW, 20), Ui.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>The window's frame in the redesign: the left nav rail (logo, name, version, one item per page, the engine status box at the
/// bottom) and the page area. Same API as the old tab strip (AddPage, Select, SelectedKey, Changed).</summary>
sealed class NavShell : Panel
{
    public const int RailWidth = 216;
    readonly NavRail _rail;
    readonly Panel _body = new() { Dock = DockStyle.Fill, BackColor = Ui.Bg };
    readonly List<(string Key, Control Page)> _pages = new();
    public event Action? Changed;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string SelectedKey { get; private set; } = "";
    public List<string> Keys() => _pages.Select(p => p.Key).ToList();

    public NavShell()
    {
        Dock = DockStyle.Fill; BackColor = Ui.Bg;
        _rail = new NavRail(this) { Dock = DockStyle.Left, Width = RailWidth };
        Controls.Add(_body); Controls.Add(_rail);
    }

    public void AddPage(string key, string text, string glyph, Control page)
    {
        page.Dock = DockStyle.Fill; page.Visible = false; _body.Controls.Add(page);
        _pages.Add((key, page)); _rail.Add(key, text, glyph);
        if (_pages.Count == 1) Select(key, silent: true);
    }

    /// <summary>The box at the bottom of the rail: a coloured dot, a short state ("Engine running") and one muted line under it.</summary>
    public void SetStatus(string state, Color dot, string detail) => _rail.SetStatus(state, dot, detail);

    public void Select(string key, bool silent = false)
    {
        if (!_pages.Any(p => p.Key == key)) return;
        // only the page in view is visible, so hidden pages do no layout or painting
        foreach (var (k, page) in _pages) page.Visible = k == key;
        SelectedKey = key; _rail.Invalidate();
        if (!silent) Changed?.Invoke();
    }

    sealed class NavRail : KitControl
    {
        readonly NavShell _owner;
        readonly List<(string Key, string Text, string Glyph)> _items = new();
        string _state = "Starting", _detail = ""; Color _dot = Ui.Muted;
        int _hover = -1, _focus = 0;
        Bitmap? _logo;
        const int ItemH = 40, ItemGap = 6, Top0 = 20 + 32 + 18;

        public NavRail(NavShell owner) { _owner = owner; BackColor = Ui.CardBg; TabStop = true; AccessibleRole = AccessibleRole.PageTabList; AccessibleName = "Pages"; }
        public void Add(string key, string text, string glyph) { _items.Add((key, text, glyph)); Invalidate(); }
        public void SetStatus(string state, Color dot, string detail) { if (state == _state && dot == _dot && detail == _detail) return; _state = state; _dot = dot; _detail = detail; Invalidate(StatusRect()); }

        Rectangle ItemRect(int i) => new(14, Top0 + i * (ItemH + ItemGap), Width - 28, ItemH);
        Rectangle StatusRect() => new(14, Height - 20 - 78, Width - 28, 78);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Ui.CardBg); Smooth(g);
            g.DrawLine(Ui.Pen(Ui.Border), Width - 1, 0, Width - 1, Height);

            // logo + name + version
            _logo ??= Logo.Draw(32);
            g.DrawImage(_logo, 20, 20, 32, 32);
            TextRenderer.DrawText(g, "NetworkAutoSwitch", Ui.UiFont(10f, Weight.Bold), new Rectangle(62, 18, Width - 70, 20), Ui.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "v" + AppInfo.Version, Ui.UiFont(8.25f), new Rectangle(62, 37, Width - 70, 16), Ui.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            for (int i = 0; i < _items.Count; i++)
            {
                var (key, text, glyph) = _items[i]; var r = ItemRect(i);
                bool on = key == _owner.SelectedKey, hover = i == _hover;
                if (on || hover)
                    using (var p = Ui.RoundRect(new RectangleF(r.X, r.Y, r.Width, r.Height), 8)) g.FillPath(Ui.Brush(on ? Ui.Raised : Ui.Mix(Ui.Raised, Ui.CardBg, 0.55f)), p);
                if (on) g.FillRectangle(Ui.Brush(Ui.Green), r.X, r.Y + 6, 3, r.Height - 12);     // the active page: a 3 px teal bar on the left
                var ink = on ? Ui.Green : Ui.Muted;
                g.DrawImage(Ui.GlyphCached(glyph, 20, ink), r.X + 12, r.Y + (r.Height - 20) / 2);
                TextRenderer.DrawText(g, text, Ui.UiFont(10f, Weight.SemiBold), new Rectangle(r.X + 42, r.Y, r.Width - 46, r.Height), on ? Ui.Text : Ui.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                if (Focused && ShowFocusCues && i == _focus) using (var p = Ui.RoundRect(new RectangleF(r.X + 1.5f, r.Y + 1.5f, r.Width - 3, r.Height - 3), 7)) g.DrawPath(Ui.Pen(Ui.Blue, 1.5f), p);
            }

            // engine status box
            var s = StatusRect();
            if (s.Top > ItemRect(_items.Count).Top)
            {
                using (var p = Ui.RoundRect(new RectangleF(s.X + 0.5f, s.Y + 0.5f, s.Width - 1, s.Height - 1), 10)) { g.FillPath(Ui.Brush(Ui.Bg), p); g.DrawPath(Ui.Pen(Ui.Border), p); }
                g.FillEllipse(Ui.Brush(_dot), s.X + 12, s.Y + 16, 8, 8);
                TextRenderer.DrawText(g, _state, Ui.UiFont(9f, Weight.SemiBold), new Rectangle(s.X + 28, s.Y + 10, s.Width - 36, 20), Ui.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, _detail, Ui.UiFont(8.6f), new Rectangle(s.X + 12, s.Y + 34, s.Width - 20, s.Height - 40), Ui.Muted, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }

        int Hit(Point p) { for (int i = 0; i < _items.Count; i++) if (ItemRect(i).Contains(p)) return i; return -1; }
        protected override void OnMouseMove(MouseEventArgs e) { int h = Hit(e.Location); if (h != _hover) { _hover = h; Invalidate(); } Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { int h = Hit(e.Location); if (h >= 0) { _focus = h; _owner.Select(_items[h].Key); } base.OnMouseDown(e); }
        protected override bool IsInputKey(System.Windows.Forms.Keys k) => k is System.Windows.Forms.Keys.Up or System.Windows.Forms.Keys.Down || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (_items.Count == 0) return;
            if (e.KeyCode == System.Windows.Forms.Keys.Down) _focus = Math.Min(_items.Count - 1, _focus + 1);
            else if (e.KeyCode == System.Windows.Forms.Keys.Up) _focus = Math.Max(0, _focus - 1);
            else { base.OnKeyDown(e); return; }
            _owner.Select(_items[_focus].Key); e.Handled = true;
        }
        protected override void OnGotFocus(EventArgs e) { _focus = Math.Max(0, _items.FindIndex(i => i.Key == _owner.SelectedKey)); Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void Dispose(bool disposing) { if (disposing) _logo?.Dispose(); base.Dispose(disposing); }
    }
}
