using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NetworkAutoSwitch.Tray;

/// <summary>One group of networks on the Networks page (Main Networks, Backup Networks, Unlabelled, Barred): rounded rows on the card,
/// a dot in the group's role colour, the network name, where it is now (muted, on the right) and an EITHER chip for a network that may be both.
/// Painted by hand so it matches the kit; there is no scrollbar (the wheel and the arrow keys scroll it, a thin bar shows that there is more).
/// Selection lives in the page, not here: one network is selected across all groups, so a network shown in two groups lights up in both.</summary>
sealed class NetworksList : KitControl
{
    public readonly record struct Row(string Name, string Where, bool Either);

    const int RowH = 38, RowGap = 6, Radius = 8;
    readonly List<Row> _rows = new();
    readonly Color _role;
    readonly string _empty;
    int _hover = -1, _offset;
    string? _selected;

    /// <summary>A row was clicked or reached with the arrow keys.</summary>
    public event Action<string>? Picked;

    // cached outlines: one row shape (every row has the same size, drawn with a translate) and the dashed empty box
    GraphicsPath? _rowPath; int _rowPathW;
    GraphicsPath? _emptyPath; Size _emptySize;
    Pen? _dash; Color _dashColor;

    public NetworksList(Color role, string empty)
    {
        _role = role; _empty = empty;
        BackColor = Ui.CardBg; Dock = DockStyle.Fill; TabStop = true; Cursor = Cursors.Hand;
        Font = Ui.UiFont(9.75f); AccessibleRole = AccessibleRole.List;
    }

    public int Count => _rows.Count;

    public void SetRows(IEnumerable<Row> rows)
    {
        _rows.Clear(); _rows.AddRange(rows);
        _hover = -1; ClampOffset(); Invalidate();
    }

    /// <summary>The network selected on the page (null = none). Rows with that name are highlighted here.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? Selected
    {
        get => _selected;
        set { if (string.Equals(_selected, value, StringComparison.OrdinalIgnoreCase)) return; _selected = value; ScrollTo(IndexOf(value)); Invalidate(); }
    }

    int IndexOf(string? name) => name is null ? -1 : _rows.FindIndex(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    int ContentH => _rows.Count == 0 ? 0 : _rows.Count * (RowH + RowGap) - RowGap;
    bool Overflows => ContentH > Height;
    int RowW => Math.Max(10, Width - (Overflows ? 10 : 0));       // room for the thin bar when there is more to see

    void ClampOffset() => _offset = Math.Max(0, Math.Min(_offset, ContentH - Height));
    void ScrollTo(int i)
    {
        if (i < 0) return;
        int top = i * (RowH + RowGap), bottom = top + RowH;
        if (top < _offset) _offset = top; else if (bottom > _offset + Height) _offset = bottom - Height;
        ClampOffset();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); ClampOffset(); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Behind); Smooth(g);

        if (_rows.Count == 0)
        {
            // an empty group: a dashed box (like a drop area) that says how to fill it
            if (_emptyPath is null || _emptySize != Size) { _emptyPath?.Dispose(); _emptyPath = Ui.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Math.Min(Height, 76) - 1.5f), 10); _emptySize = Size; }
            if (_dash is null || _dashColor != Ui.Border) { _dash?.Dispose(); _dash = new Pen(Ui.Border) { DashStyle = DashStyle.Dash }; _dashColor = Ui.Border; }
            g.DrawPath(_dash, _emptyPath);
            TextRenderer.DrawText(g, _empty, Ui.UiFont(9f), new Rectangle(14, 0, Width - 28, Math.Min(Height, 76)), Ui.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            return;
        }

        int w = RowW;
        if (_rowPath is null || _rowPathW != w) { _rowPath?.Dispose(); _rowPath = Ui.RoundRect(new RectangleF(0.5f, 0.5f, w - 1.5f, RowH - 1.5f), Radius); _rowPathW = w; }
        var name = Ui.UiFont(9.75f, Weight.SemiBold); var small = Ui.UiFont(9f);
        const TextFormatFlags one = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;

        int first = _offset / (RowH + RowGap);
        for (int i = first; i < _rows.Count; i++)
        {
            int y = i * (RowH + RowGap) - _offset;
            if (y >= Height) break;
            var row = _rows[i];
            bool sel = row.Name.Equals(_selected ?? "", StringComparison.OrdinalIgnoreCase), hot = i == _hover;
            var fill = sel ? Ui.Tint(_role, Ui.Dark ? 0.16f : 0.12f) : hot ? Ui.Mix(Ui.Text, Ui.Raised, 0.06f) : Ui.Raised;

            g.TranslateTransform(0, y);
            g.FillPath(Ui.Brush(fill), _rowPath);
            if (sel) g.DrawPath(Ui.Pen(_role), _rowPath);
            else if (Focused && ShowFocusCues && i == Math.Max(0, IndexOf(_selected))) g.DrawPath(Ui.Pen(Ui.Blue, 1.5f), _rowPath);
            g.ResetTransform();

            g.FillEllipse(Ui.Brush(_role), 12, y + (RowH - 8) / 2, 8, 8);          // the group's role colour, so the rows read as main / backup at a glance

            // the right side first (chip, then where it is), so the name gets what is left
            int right = w - 10;
            if (row.Either)
            {
                int cw = TextRenderer.MeasureText(g, "EITHER", Ui.UiFont(8.25f, Weight.SemiBold), Size.Empty, TextFormatFlags.NoPadding).Width + 18;
                right -= cw; Chip.Paint(g, "EITHER", Ui.Blue, right, y + RowH / 2);
                right -= 10;
            }
            int nameW = TextRenderer.MeasureText(g, row.Name, name, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
            if (row.Where.Length > 0)
            {
                int room = Math.Max(0, right - 30 - nameW - 16);
                int ww = Math.Min(room, TextRenderer.MeasureText(g, row.Where, small, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + 2);
                if (ww > 24) { TextRenderer.DrawText(g, row.Where, small, new Rectangle(right - ww, y, ww, RowH), Ui.Muted, one | TextFormatFlags.Right); right -= ww + 16; }
            }
            TextRenderer.DrawText(g, row.Name, name, new Rectangle(30, y, Math.Max(0, right - 30), RowH), Ui.Text, one);
        }

        if (Overflows)
        {
            // a thin bar, not a scrollbar: it only shows where you are in a long group
            int h = Height, thumb = Math.Max(24, h * h / ContentH), span = ContentH - h;
            int ty = span <= 0 ? 0 : (int)((long)_offset * (h - thumb) / span);
            g.FillRectangle(Ui.Brush(Ui.Border), Width - 4, ty, 3, thumb);
        }
    }

    int Hit(Point p)
    {
        if (p.X < 0 || p.X >= RowW) return -1;
        int y = p.Y + _offset, i = y / (RowH + RowGap);
        return i >= 0 && i < _rows.Count && y % (RowH + RowGap) < RowH ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e) { int h = Hit(e.Location); if (h != _hover) { _hover = h; Invalidate(); } Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { if (_hover != -1) { _hover = -1; Invalidate(); } base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); int h = Hit(e.Location); if (h >= 0) Picked?.Invoke(_rows[h].Name); base.OnMouseDown(e); }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int old = _offset; _offset -= e.Delta / 120 * (RowH + RowGap); ClampOffset();
        if (old != _offset) { _hover = Hit(PointToClient(MousePosition)); Invalidate(); }
        base.OnMouseWheel(e);
    }

    protected override bool IsInputKey(Keys k) => k is Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_rows.Count > 0 && e.KeyCode is Keys.Up or Keys.Down or Keys.Home or Keys.End)
        {
            int i = IndexOf(_selected);
            i = e.KeyCode switch { Keys.Home => 0, Keys.End => _rows.Count - 1, Keys.Up => Math.Max(0, i - 1), _ => i < 0 ? 0 : Math.Min(_rows.Count - 1, i + 1) };
            Picked?.Invoke(_rows[i].Name); e.Handled = true; return;
        }
        base.OnKeyDown(e);
    }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override AccessibleObject CreateAccessibilityInstance() => new Acc(this);
    sealed class Acc(NetworksList o) : ControlAccessibleObject(o) { public override string? Value => o.Selected; }

    protected override void Dispose(bool disposing) { if (disposing) { _rowPath?.Dispose(); _emptyPath?.Dispose(); _dash?.Dispose(); } base.Dispose(disposing); }
}
