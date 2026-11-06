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

    // Busy: a spinner takes the icon's place and the button ignores clicks (a save is running). It keeps its colours, so it does not
    // look "dead"; the spinner is the sign that work is going on. Flash(): a short tick in the icon's place when the work is done.
    bool _busy; float _spin;
    string _flash = "";
    readonly System.Windows.Forms.Timer _spinTimer = new() { Interval = 30 };
    readonly System.Windows.Forms.Timer _flashTimer = new() { Interval = 1200 };
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Busy
    {
        get => _busy;
        set { if (_busy == value) return; _busy = value; _hover = _down = false; if (value) { _flash = ""; _flashTimer.Stop(); _spinTimer.Start(); } else _spinTimer.Stop(); Invalidate(); }
    }
    /// <summary>Shows <paramref name="glyph"/> in the icon's place for a moment (a tick after a save), then goes back to the normal icon.</summary>
    public void Flash(string glyph, int ms = 1200) { if (_busy) return; _flash = glyph; _flashTimer.Stop(); _flashTimer.Interval = ms; _flashTimer.Start(); Invalidate(); }

    public KitButton()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false; Cursor = Cursors.Hand;
        _spinTimer.Tick += (_, _) => { _spin = (_spin + 16) % 360; Invalidate(); };
        _flashTimer.Tick += (_, _) => { _flashTimer.Stop(); _flash = ""; Invalidate(); };
        VisibleChanged += (_, _) => { if (!Visible) _spinTimer.Stop(); else if (_busy) _spinTimer.Start(); };      // nothing animates while it is hidden
    }

    protected override void OnClick(EventArgs e) { if (!_busy) base.OnClick(e); }

    /// <summary>When set, the button wears this colour: an outline and text in it on the surface behind (a button on a tinted banner).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color? Accent { get; set; }

    (Color Bg, Color Fg, Color Edge) Colors()
    {
        var parent = Parent?.BackColor ?? Ui.CardBg;
        var (bg, fg, edge) = Kind switch
        {
            BtnKind.Primary or BtnKind.Success => (Ui.Green, Ui.OnAccent, Ui.Green),
            BtnKind.Danger => (parent, Ui.Red, Ui.Red),
            _ => (Ui.Raised, Ui.Text, Ui.Border),
        };
        if (Accent is Color a)
        {
            if (!Enabled) return (parent, Ui.Mix(a, parent, 0.45f), Ui.Mix(a, parent, 0.5f));
            return (_busy ? parent : _down ? Ui.Mix(a, parent, 0.30f) : _hover ? Ui.Mix(a, parent, 0.16f) : parent, a, a);
        }
        if (!Enabled)
        {
            // inactive: the fill is pulled toward the surface behind as well (a bright filled button turns into a dull, quiet one), then the text and edge follow
            var quiet = Kind == BtnKind.Secondary ? Ui.Mix(bg, parent, 0.55f) : Kind == BtnKind.Danger ? bg : Ui.Mix(bg, parent, 0.22f);
            bool filled = Kind is BtnKind.Primary or BtnKind.Success;
            return (quiet, Ui.Mix(filled ? Ui.Muted : fg, quiet, filled ? 0.6f : 0.45f), filled ? quiet : Ui.Mix(edge, quiet, 0.5f));
        }
        if (_busy) return (bg, fg, edge);       // no hover or press effect while it works
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
        bool hasIcon = Glyph.Length > 0 || _busy;
        var size = text.Length > 0 ? TextRenderer.MeasureText(g, text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix) : Size.Empty;
        int total = (hasIcon ? icon : 0) + (hasIcon && text.Length > 0 ? gap : 0) + size.Width;
        int x = (Width - total) / 2;
        if (_busy)
        {
            // a 270 degree arc turning round: same 16 px box and place as the icon, so the label does not move
            g.DrawArc(Ui.Pen(fg, 2f), new RectangleF(x + 1, (Height - icon) / 2f + 1, icon - 2, icon - 2), _spin, 270);
            x += icon + gap;
        }
        else if (hasIcon) { g.DrawImage(Ui.GlyphCached(_flash.Length > 0 ? _flash : Glyph, icon + 2, fg), x - 1, (Height - icon - 2) / 2); x += icon + gap; }
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
    protected override void Dispose(bool disposing) { if (disposing) { _path?.Dispose(); _spinTimer.Dispose(); _flashTimer.Dispose(); } base.Dispose(disposing); }
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
    public static new int Paint(Graphics g, string text, Color role, int x, int centreY, bool filled = false)
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
    /// <summary>Optional: the colour the SELECTED segment is filled with (text on it uses OnAccent). Null = the quiet raised look.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<int, Color?>? SelectedFill { get; set; }
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
            var c = _cells[i]; bool on = i == _selected; Color? accent = on ? SelectedFill?.Invoke(i) : null;
            if (on || i == _hover)
                using (var p = Ui.RoundRect(new RectangleF(c.X + 0.5f, c.Y + 0.5f, c.Width - 1, c.Height - 1), 7))
                {
                    g.FillPath(Ui.Brush(accent ?? (on ? Ui.Raised : Ui.Mix(Ui.Raised, Ui.InputBg, 0.5f))), p);
                    if (on && accent is null) g.DrawPath(Ui.Pen(Ui.Border), p);
                }
            TextRenderer.DrawText(g, _items[i], Font, c, on ? (accent is null ? Ui.Text : Ui.OnAccent) : Ui.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
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
    // the constructor sets BackColor before _box exists, so it can be null here
    protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e); _label.BackColor = BackColor; _box?.Invalidate(); }

    sealed class FieldBox : Panel
    {
        readonly Control _input; readonly string _suffix;
        readonly NumericUpDown? _num;                                   // a number field gets our own − and + buttons (the native arrows are hidden)
        GraphicsPath? _path; Size _pathSize;
        int _hoverBtn, _downBtn;                                        // 0 none, 1 minus, 2 plus
        readonly System.Windows.Forms.Timer _repeat = new() { Interval = 420 };
        const int StepBtn = 24, StepGap = 4, StepEdge = 6;

        public FieldBox(Control input, string suffix)
        {
            _input = input; _suffix = suffix;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            switch (input)
            {
                case TextBox t: t.BorderStyle = BorderStyle.None; break;
                case NumericUpDown n:
                    n.BorderStyle = BorderStyle.None; _num = n;
                    if (n.Controls.Count > 0) n.Controls[0].Visible = false;      // the native spin arrows: replaced by the buttons drawn here
                    n.ValueChanged += (_, _) => Invalidate();
                    break;
                case ComboBox c: c.FlatStyle = FlatStyle.Flat; break;
            }
            input.BackColor = Ui.InputBg; input.ForeColor = Ui.Text; input.Font = Ui.UiFont(9.75f);
            Controls.Add(input);
            input.GotFocus += (_, _) => Invalidate(); input.LostFocus += (_, _) => Invalidate();
            _repeat.Tick += (_, _) => { _repeat.Interval = 70; Step(_downBtn); };      // holding a button keeps stepping
        }
        int SuffixW => _suffix.Length == 0 ? 0 : TextRenderer.MeasureText(_suffix, Ui.UiFont(9f), Size.Empty, TextFormatFlags.NoPadding).Width + 12;
        int StepSide => _num is null ? 0 : StepEdge + StepBtn + 8;      // room one button takes at each end of the box (the minus at the start, the plus at the end)
        Rectangle MinusRect => new(StepEdge, (Height - StepBtn) / 2, StepBtn, StepBtn);
        Rectangle PlusRect => new(Width - StepEdge - StepBtn, (Height - StepBtn) / 2, StepBtn, StepBtn);
        int HitBtn(Point p) => _num is null ? 0 : MinusRect.Contains(p) ? 1 : PlusRect.Contains(p) ? 2 : 0;
        bool CanStep(int btn) => _num is not null && _num.Enabled && (btn == 1 ? _num.Value > _num.Minimum : _num.Value < _num.Maximum);
        void Step(int btn)
        {
            if (_num is null || !CanStep(btn)) { _repeat.Stop(); return; }
            if (btn == 1) _num.DownButton(); else _num.UpButton();     // these respect the minimum, the maximum and the step
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_input is NoWheelComboBox cb)   // draws its own rounded box: it takes the whole field, and the field draws nothing around it
            {
                cb.ItemHeight = Math.Max(16, Height - 6);   // a combo's height follows its item height (+6 for its frame)
                cb.SetBounds(0, Math.Max(0, (Height - cb.Height) / 2), Width, cb.Height);
                return;
            }
            int h = _input.PreferredSize.Height, inset = _input is ComboBox ? 2 : 12;
            if (_num is not null)
            {
                // Between the two buttons, left of the unit. A NumericUpDown keeps a slot for its up/down spinner at its right end, and on screen
                // that slot is drawn even when the spinner is "hidden" (a dark box beside the unit). So the number is made that much wider and a clip
                // region cuts the slot off: it is neither painted nor clicked, and the digits get the whole visible width.
                int visible = Math.Max(10, Width - StepSide * 2 - SuffixW);
                int slot = Math.Max(_num.Controls.Count > 0 ? _num.Controls[0].Width : 0, SystemInformation.VerticalScrollBarWidth + 8);      // too big is safe, too small would shorten the digits
                _input.SetBounds(StepSide, Math.Max(1, (Height - h) / 2), visible + slot, h);
                var old = _num.Region; _num.Region = new Region(new Rectangle(0, 0, visible, _num.Height)); old?.Dispose();
            }
            else _input.SetBounds(inset, Math.Max(1, (Height - h) / 2), Math.Max(10, Width - inset * 2 - SuffixW), h);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent?.BackColor ?? Ui.CardBg);
            if (_input is NoWheelComboBox) return;     // the dropdown draws its own box; a second border here would double it
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.Half;
            if (_path is null || _pathSize != Size) { _path?.Dispose(); _path = Ui.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8); _pathSize = Size; }
            g.FillPath(Ui.Brush(Ui.InputBg), _path);
            g.DrawPath(Ui.Pen(_input.ContainsFocus ? Ui.Green : Ui.Border), _path);
            if (_suffix.Length > 0) TextRenderer.DrawText(g, _suffix, Ui.UiFont(9f), new Rectangle(Width - StepSide - SuffixW - 4, 0, SuffixW, Height), Ui.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding);
            if (_num is not null) { PaintStep(g, MinusRect, 1); PaintStep(g, PlusRect, 2); }
        }

        /// <summary>One of the two round-cornered buttons: raised colour, a "−" or "+" drawn as lines, dimmed at the limit.</summary>
        void PaintStep(Graphics g, Rectangle r, int btn)
        {
            bool can = CanStep(btn), hot = can && (_hoverBtn == btn), down = can && _downBtn == btn;
            var fill = down ? Ui.Mix(Ui.Text, Ui.Raised, 0.16f) : hot ? Ui.Mix(Ui.Text, Ui.Raised, 0.08f) : Ui.Raised;
            using (var p = Ui.RoundRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), 6)) { g.FillPath(Ui.Brush(fill), p); g.DrawPath(Ui.Pen(Ui.Border), p); }
            var ink = can ? (hot || down ? Ui.Text : Ui.Muted) : Ui.Mix(Ui.Muted, Ui.Raised, 0.4f);
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            var pen = Ui.Pen(ink, 1.8f);
            g.DrawLine(pen, cx - 5, cy, cx + 5, cy);
            if (btn == 2) g.DrawLine(pen, cx, cy - 5, cx, cy + 5);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitBtn(e.Location); if (h != _hoverBtn) { _hoverBtn = h; Cursor = h != 0 && CanStep(h) ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { _hoverBtn = 0; _downBtn = 0; _repeat.Stop(); Cursor = Cursors.Default; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            int b = HitBtn(e.Location);
            if (b != 0 && e.Button == MouseButtons.Left)
            {
                _input.Focus(); _downBtn = b; Step(b); Invalidate();
                _repeat.Interval = 420; if (CanStep(b)) _repeat.Start();      // a short pause, then it repeats
            }
            else _input.Focus();
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e) { _downBtn = 0; _repeat.Stop(); Invalidate(); base.OnMouseUp(e); }
        protected override void Dispose(bool disposing) { if (disposing) { _path?.Dispose(); _repeat.Dispose(); } base.Dispose(disposing); }
    }
}

/// <summary>A search box: a rounded box with the hint painted by us (so it is always centred in the box's height, in both themes, which the
/// native placeholder is not) and an X at the end that clears the text. The text itself is a borderless TextBox (<see cref="Input"/>).
/// While the box is empty and not being typed in, the TextBox is moved out of sight, so the hint is painted on a clear surface and nothing
/// overlaps it; it comes back the moment the box is clicked or reached with Tab.</summary>
sealed class SearchBox : KitControl
{
    public TextBox Input { get; } = new() { BorderStyle = BorderStyle.None };
    readonly string _hint;
    bool _hoverX;
    const int XBox = 22, XEdge = 8;

    public SearchBox(string hint, int width = 260)
    {
        _hint = hint; Size = new Size(width, Ui.BtnH); Margin = Padding.Empty; Font = Ui.UiFont(9.75f);
        Input.BackColor = Ui.InputBg; Input.ForeColor = Ui.Text; Input.Font = Ui.UiFont(9.75f); Input.AccessibleName = hint.TrimEnd('…', '.');
        Controls.Add(Input);
        Input.TextChanged += (_, _) => PlaceInput();
        Input.GotFocus += (_, _) => PlaceInput(); Input.LostFocus += (_, _) => PlaceInput();
    }

    Rectangle XRect => new(Width - XEdge - XBox, (Height - XBox) / 2, XBox, XBox);
    bool HasText => Input.Text.Length > 0;

    /// <summary>In place while there is text or the box has focus; out of sight otherwise (the hint shows instead).</summary>
    void PlaceInput()
    {
        bool show = HasText || Input.Focused;
        int h = Input.PreferredSize.Height;
        Input.SetBounds(show ? 12 : -4000, (Height - h) / 2, Math.Max(10, Width - 12 - XBox - XEdge - 6), h);     // ends before the X
        Invalidate();
    }
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); PlaceInput(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Behind); Smooth(g);
        var path = Outline(8);
        g.FillPath(Ui.Brush(Ui.InputBg), path);
        g.DrawPath(Ui.Pen(Input.ContainsFocus ? Ui.Green : Ui.Border), path);
        if (!HasText && !Input.Focused)
            TextRenderer.DrawText(g, _hint, Font, new Rectangle(13, 0, Width - 13 - XBox - XEdge, Height), Ui.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        else if (HasText)
        {
            var r = XRect;
            if (_hoverX) using (var p = Ui.RoundRect(new RectangleF(r.X, r.Y, r.Width, r.Height), 6)) g.FillPath(Ui.Brush(Ui.Raised), p);
            var pen = Ui.Pen(_hoverX ? Ui.Text : Ui.Muted, 1.7f);
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, d = 4.5f;
            g.DrawLine(pen, cx - d, cy - d, cx + d, cy + d); g.DrawLine(pen, cx - d, cy + d, cx + d, cy - d);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool over = HasText && XRect.Contains(e.Location);
        if (over != _hoverX) { _hoverX = over; Cursor = over ? Cursors.Hand : Cursors.IBeam; Ui.Tip.SetToolTip(this, over ? "Clear The Search" : ""); Invalidate(); }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e) { if (_hoverX) { _hoverX = false; Invalidate(); } base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (HasText && XRect.Contains(e.Location)) { Input.Clear(); _hoverX = false; }
        Input.Focus(); base.OnMouseDown(e);
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
    readonly List<Control> _actions = new();
    int _actionsLeft;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Severe { get => _severe; set { _severe = value; Recolor(); Invalidate(); } }
    Color Role => _severe ? Ui.Red : Ui.Amber;

    public Banner() { Height = 68; Dock = DockStyle.Top; Recolor(); }
    void Recolor()
    {
        BackColor = Ui.Tint(Role, 0.16f);
        foreach (var c in _actions) { if (c is KitButton b) b.Accent = Role; c.Invalidate(); }     // the buttons wear the banner's colour, not the dark "secondary" look
    }
    public void Set(string title, string detail) { _title = title; _detail = detail; Invalidate(); }

    /// <summary>Adds a button on the right. The buttons are placed by hand (not in a docked panel), so nothing sits on top of the border or the rounded corners.</summary>
    public T AddAction<T>(T c) where T : Control
    {
        if (c is KitButton b) b.Accent = Role;
        _actions.Add(c); Controls.Add(c); PlaceActions();
        return c;
    }

    void PlaceActions()
    {
        int x = Width - 16;
        for (int i = _actions.Count - 1; i >= 0; i--)
        {
            var c = _actions[i]; int w = c.PreferredSize.Width;
            c.SetBounds(x - w, (Height - c.PreferredSize.Height) / 2, w, c.PreferredSize.Height);
            x -= w + 8;
        }
        _actionsLeft = _actions.Count == 0 ? Width - 16 : x + 8;
    }
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); PlaceActions(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Behind); Smooth(g);
        var path = Outline(12);
        g.FillPath(Ui.Brush(BackColor), path); g.DrawPath(Ui.Pen(Role), path);
        g.DrawImage(Ui.GlyphCached(Ui.Warning, 22, Role), 18, (Height - 22) / 2);
        int textW = Math.Max(10, _actionsLeft - 60 - 12);
        TextRenderer.DrawText(g, _title, Ui.UiFont(10.5f, Weight.Bold), new Rectangle(52, 14, textW, 22), Ui.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, _detail, Ui.UiFont(9.4f), new Rectangle(52, 36, textW, 20), Ui.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>The window's frame in the redesign: the left nav rail (logo, name, version, one item per page) and the page area. Same API as the old tab strip (AddPage, Select, SelectedKey, Changed).</summary>
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

    /// <param name="minHeight">The least height the page needs. A page that does not scroll by itself is wrapped in a ScrollSurface
    /// (no scrollbar; wheel, touchpad and PageUp/PageDown), so a short window scrolls it instead of squeezing or cutting it.</param>
    public void AddPage(string key, string text, string glyph, Control page, int minHeight = 0)
    {
        Control host = page;
        if (minHeight > 0 && page is not ScrollSurface && !Contains<ScrollSurface>(page))
        {
            var s = new ScrollSurface { MinContentHeight = minHeight };
            page.Dock = DockStyle.Fill; s.Content.Controls.Add(page);
            host = s;
        }
        host.Dock = DockStyle.Fill; host.Visible = false; _body.Controls.Add(host);
        _pages.Add((key, host)); _rail.Add(key, text, glyph);
        if (_pages.Count == 1) Select(key, silent: true);
    }

    static bool Contains<T>(Control c) where T : Control { foreach (Control k in c.Controls) if (k is T || Contains<T>(k)) return true; return false; }


    // opening a page shows a skeleton shaped like a page for a moment, while the page lays itself out behind it
    readonly PageSkeleton _skeleton = new() { Dock = DockStyle.Fill, Visible = false };
    /// <summary>False while pages are rendered to pictures (--snapshot), so the skeleton never covers them.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowLoading { get; set; } = true;
    readonly System.Windows.Forms.Timer _reveal = new() { Interval = 260 };
    bool _skeletonReady;

    public void Select(string key, bool silent = false)
    {
        if (!_pages.Any(p => p.Key == key)) return;
        bool changed = key != SelectedKey && SelectedKey.Length > 0;
        // only the page in view is visible, so hidden pages do no layout or painting
        foreach (var (k, page) in _pages) page.Visible = k == key;
        SelectedKey = key; _rail.Invalidate();
        if (changed && ShowLoading && Visible && FindForm() is { Visible: true }) ShowSkeleton();
        if (!silent) Changed?.Invoke();
    }

    void ShowSkeleton()
    {
        if (!_skeletonReady) { _body.Controls.Add(_skeleton); _reveal.Tick += (_, _) => { _reveal.Stop(); _skeleton.Visible = false; }; _skeletonReady = true; }
        _reveal.Stop();
        _skeleton.Visible = true; _skeleton.BringToFront(); _skeleton.Restart();
        _reveal.Start();
    }

    protected override void Dispose(bool disposing) { if (disposing) _reveal.Dispose(); base.Dispose(disposing); }

    /// <summary>Grey blocks in the page's frame (a title bar, then cards) with a moving shine. Painted from cached paths; the animation
    /// timer only runs while it is visible.</summary>
    sealed class PageSkeleton : KitControl
    {
        readonly System.Windows.Forms.Timer _t = new() { Interval = 33 };
        float _phase;
        public PageSkeleton() { BackColor = Ui.Bg; _t.Tick += (_, _) => { _phase += 0.04f; Invalidate(); }; VisibleChanged += (_, _) => _t.Enabled = Visible; }
        public void Restart() { _phase = 0; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Ui.Bg); Smooth(g);
            int x = 28, w = Width - 56, gap = 16;
            var block = Ui.Mix(Ui.Text, Ui.CardBg, Ui.Dark ? 0.06f : 0.05f);
            void Bar(RectangleF r, float rad) { using var p = Ui.RoundRect(r, rad); g.FillPath(Ui.Brush(block), p); }
            void CardBox(RectangleF r)
            {
                using (var p = Ui.RoundRect(r, 12)) { g.FillPath(Ui.Brush(Ui.CardBg), p); g.DrawPath(Ui.Pen(Ui.Border), p); }
                Bar(new RectangleF(r.X + 18, r.Y + 18, Math.Min(140, r.Width * 0.4f), 12), 6);
                for (int i = 0; i < 3 && r.Y + 52 + i * 26 < r.Bottom - 20; i++) Bar(new RectangleF(r.X + 18, r.Y + 50 + i * 26, (r.Width - 36) * (i == 2 ? 0.55f : 0.85f), 12), 6);
            }
            Bar(new RectangleF(x, 22, 180, 26), 8);                                                 // the page title
            float top = 76, half = (w - gap) / 2f, h1 = Math.Max(120, (Height - top - 24 - gap) * 0.42f), h2 = Math.Max(120, Height - top - 24 - gap - h1);
            CardBox(new RectangleF(x, top, half, h1)); CardBox(new RectangleF(x + half + gap, top, half, h1));
            CardBox(new RectangleF(x, top + h1 + gap, w, h2));
            // the shine: a soft band sweeping left to right across everything
            float band = Math.Max(160, Width * 0.25f), bx = -band + (Width + band * 2) * (_phase % 1.4f / 1.4f);
            using var lg = new System.Drawing.Drawing2D.LinearGradientBrush(new RectangleF(bx, 0, band, Height), Color.Transparent, Color.Transparent, System.Drawing.Drawing2D.LinearGradientMode.Horizontal)
            { InterpolationColors = new System.Drawing.Drawing2D.ColorBlend { Colors = new[] { Color.FromArgb(0, Ui.Text), Color.FromArgb(Ui.Dark ? 14 : 22, Ui.Dark ? Ui.Text : Color.White), Color.FromArgb(0, Ui.Text) }, Positions = new[] { 0f, 0.5f, 1f } } };
            g.FillRectangle(lg, bx, 0, band, Height);
        }
        protected override void Dispose(bool disposing) { if (disposing) _t.Dispose(); base.Dispose(disposing); }
    }

    sealed class NavRail : KitControl
    {
        readonly NavShell _owner;
        readonly List<(string Key, string Text, string Glyph)> _items = new();
        int _hover = -1, _focus = 0;
        Bitmap? _logo;
        const int ItemH = 40, ItemGap = 6, Top0 = 20 + 32 + 18;

        public NavRail(NavShell owner) { _owner = owner; BackColor = Ui.CardBg; TabStop = true; AccessibleRole = AccessibleRole.PageTabList; AccessibleName = "Pages"; }
        public void Add(string key, string text, string glyph) { _items.Add((key, text, glyph)); Invalidate(); }

        Rectangle ItemRect(int i) => new(14, Top0 + i * (ItemH + ItemGap), Width - 28, ItemH);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Ui.CardBg); Smooth(g);
            g.DrawLine(Ui.Pen(Ui.Border), Width - 1, 0, Width - 1, Height);

            // logo + name + version
            _logo ??= Logo.Draw(32);
            g.DrawImage(_logo, 20, 20, 32, 32);
            TextRenderer.DrawText(g, "NetworkAutoSwitch", NameFont(g), new Rectangle(60, 18, Width - 62, 20), Ui.Text, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
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

        }

        // the largest bold size at which the app name fits the rail, worked out once (the rail width is fixed)
        Font? _nameFont;
        Font NameFont(Graphics g)
        {
            if (_nameFont is not null) return _nameFont;
            float pt = 10.5f;
            while (pt > 7.5f && TextRenderer.MeasureText(g, "NetworkAutoSwitch", Ui.UiFont(pt, Weight.Bold), Size.Empty, TextFormatFlags.NoPadding).Width > Width - 66) pt -= 0.25f;
            return _nameFont = Ui.UiFont(pt, Weight.Bold);
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
