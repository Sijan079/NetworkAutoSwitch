using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;

namespace NetworkAutoSwitch.Tray;

// The tables of the Programs page, drawn like the dashboard mockup's tables: an uppercase muted header line, 40 px rows with a
// 1 px divider under each, chips for states and a small button inside a row. The ListView stays underneath (selection, keyboard,
// tooltips, screen readers); only its painting is ours. Everything here uses the cached brushes, pens and fonts from Ui.

/// <summary>Shared painting for the Programs tables. Paint code calls these on every cell, so nothing here allocates GDI objects.</summary>
static class ProgramsTable
{
    public const int RowHeight = 40, CellPad = 8;
    public static Font CellFont => Ui.UiFont(9.75f);                    // 13 px, the mockup's row text
    public static Font HeaderFont => Ui.UiFont(8.25f, Weight.Bold);     // 11 px uppercase column titles

    /// <summary>Turns a ListView into a Programs table: details view, no native header (the TableHeader above it draws the titles,
    /// which also renders correctly in --snapshot pictures), taller rows, owner-drawn cells, double-buffered so the 2 s refresh never flickers.</summary>
    public static void Prepare(ListView lv)
    {
        lv.View = View.Details; lv.FullRowSelect = true; lv.HeaderStyle = ColumnHeaderStyle.None; lv.BorderStyle = BorderStyle.None;
        lv.BackColor = Ui.CardBg; lv.ForeColor = Ui.Text; lv.Dock = DockStyle.Fill; lv.GridLines = false; lv.Font = CellFont;
        lv.SmallImageList ??= new ImageList { ImageSize = new Size(1, RowHeight) };      // a 1 px wide image list is the standard trick for taller rows
        lv.OwnerDraw = true;
        lv.DrawItem += (_, _) => { };              // every cell is drawn by the page's DrawSubItem handler
        // DoubleBuffered is protected on ListView; turning it on sets LVS_EX_DOUBLEBUFFER, so rebuilding the rows does not flash
        typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(lv, true);
    }

    /// <summary>The cell rectangle to paint in. Column 0 can report the whole row's width, so it is clamped to its column.</summary>
    public static Rectangle Cell(ListView lv, DrawListViewSubItemEventArgs e)
    {
        var r = e.Bounds;
        if (e.ColumnIndex == 0 && lv.Columns.Count > 0) r.Width = Math.Min(r.Width, lv.Columns[0].Width);
        return r;
    }

    /// <summary>The row background (selected, hovered or plain) and the divider line under it.</summary>
    public static void Background(Graphics g, Rectangle r, bool selected, bool hover, bool columnLine = false)
    {
        var bg = selected ? Ui.SelectedRow : hover ? Ui.Mix(Ui.Raised, Ui.CardBg, 0.6f) : Ui.CardBg;
        g.FillRectangle(Ui.Brush(bg), r);
        g.DrawLine(Ui.Pen(Ui.Border), r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
        if (columnLine) g.DrawLine(Ui.Pen(Ui.Border), r.Right - 1, r.Top, r.Right - 1, r.Bottom - 1);      // the right border of this column: shows where it ends (and where to drag)
    }

    /// <summary>The text area of a cell: CellPad on both sides. With the card body's 6 px padding, the first column's text lines up with the card title.</summary>
    public static Rectangle TextArea(Rectangle cell, int column) =>
        new(cell.Left + CellPad, cell.Top, Math.Max(0, cell.Width - CellPad * 2), cell.Height - 1);

    public static void Text(Graphics g, string text, Rectangle area, Color color, bool right = false, Font? font = null, TextFormatFlags extra = TextFormatFlags.EndEllipsis) =>
        TextRenderer.DrawText(g, text, font ?? CellFont, area, color, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | extra | (right ? TextFormatFlags.Right : TextFormatFlags.Left));

    /// <summary>The row under the mouse, so it can be tinted. Repaints only the rows whose state changed.</summary>
    public static void TrackHover(ListView lv, Func<int> get, Action<int> set)
    {
        void Move(int now)
        {
            int old = get(); if (old == now) return;
            set(now);
            if (old >= 0 && old < lv.Items.Count) lv.Invalidate(lv.Items[old].Bounds);
            if (now >= 0 && now < lv.Items.Count) lv.Invalidate(lv.Items[now].Bounds);
        }
        lv.MouseMove += (_, e) => Move(lv.GetItemAt(e.X, e.Y)?.Index ?? -1);
        lv.MouseLeave += (_, _) => Move(-1);
    }
}

/// <summary>The column titles above a Programs table: uppercase, muted, 11 px, a line under them (the mockup's table header).
/// Sortable tables show the sort column in full text colour with an arrow; a click on a title sorts by it.</summary>
sealed class TableHeader : Control
{
    readonly ListView _lv;
    readonly Func<int, int>? _sortState;       // -1 not sortable, 0 idle, 1 ascending, 2 descending
    readonly Action<int>? _sortBy;
    string[] _titles = Array.Empty<string>();
    readonly PointF[] _arrow = new PointF[3];   // reused for every paint

    public TableHeader(ListView lv, Func<int, int>? sortState = null, Action<int>? sortBy = null)
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _lv = lv; _sortState = sortState; _sortBy = sortBy;
        Dock = DockStyle.Top; Height = 30; BackColor = Ui.CardBg;
        lv.ColumnWidthChanged += (_, _) => Invalidate();
        lv.VisibleChanged += (_, _) => Visible = lv.Visible;        // the empty state hides the table: its titles go with it
        if (sortBy is not null) Ui.Tip.SetToolTip(this, "Click a column title to sort by it. Click it again to reverse the order.");
    }

    void SyncTitles()
    {
        if (_titles.Length == _lv.Columns.Count) return;
        _titles = _lv.Columns.Cast<ColumnHeader>().Select(c => c.Text.ToUpperInvariant()).ToArray();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Ui.CardBg); SyncTitles();
        int x = 0;
        for (int i = 0; i < _titles.Length; i++)
        {
            int w = _lv.Columns[i].Width;
            var cell = new Rectangle(x, 0, w, Height - 1); x += w;
            if (i < _titles.Length - 1) g.DrawLine(Ui.Pen(Ui.Border), x - 1, 0, x - 1, Height - 1);
            if (_titles[i].Length == 0) continue;
            int state = _sortState?.Invoke(i) ?? -1;
            bool right = _lv.Columns[i].TextAlign == HorizontalAlignment.Right, on = state > 0;
            var area = ProgramsTable.TextArea(cell, i);
            if (on) area.Width = Math.Max(0, area.Width - 14);       // room for the arrow at the right edge of the column
            var ink = on ? Ui.Text : Ui.Muted;
            ProgramsTable.Text(g, _titles[i], area, ink, right, ProgramsTable.HeaderFont);
            if (on)
            {
                float cx = cell.Right - ProgramsTable.CellPad - 4, cy = Height / 2f;      // the sort arrow sits at the right side of the column
                bool up = state == 1;
                _arrow[0] = new PointF(cx - 3.5f, up ? cy + 2 : cy - 2); _arrow[1] = new PointF(cx + 3.5f, up ? cy + 2 : cy - 2); _arrow[2] = new PointF(cx, up ? cy - 2.5f : cy + 2.5f);
                var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPolygon(Ui.Brush(ink), _arrow);
                g.SmoothingMode = old;
            }
        }
        g.DrawLine(Ui.Pen(Ui.Border), 0, Height - 1, Width, Height - 1);
    }

    int Hit(int px) { int x = 0; for (int i = 0; i < _lv.Columns.Count; i++) { x += _lv.Columns[i].Width; if (px < x) return i; } return -1; }

    // Resizing: drag the line between two column titles. The column on the left grows by what the one on the right gives up (and the other way round),
    // so the table keeps its width and never scrolls sideways.
    const int Grip = 5, MinCol = 60;
    int _drag = -1, _dragX, _wLeft, _wRight;
    bool _dragged;
    /// <summary>The column whose right edge is under the pointer (not the last one: its edge is the table's edge), or -1.</summary>
    int EdgeAt(int px) { int x = 0; for (int i = 0; i < _lv.Columns.Count - 1; i++) { x += _lv.Columns[i].Width; if (Math.Abs(px - x) <= Grip) return i; } return -1; }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && EdgeAt(e.X) is var c && c >= 0)
        { _drag = c; _dragX = e.X; _wLeft = _lv.Columns[c].Width; _wRight = _lv.Columns[c + 1].Width; _dragged = false; Capture = true; }
        base.OnMouseDown(e);
    }
    protected override void OnMouseUp(MouseEventArgs e) { _drag = -1; Capture = false; base.OnMouseUp(e); }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag >= 0)
        {
            int d = Math.Clamp(e.X - _dragX, MinCol - _wLeft, _wRight - MinCol);
            if (d != 0) _dragged = true;
            _lv.BeginUpdate();
            _lv.Columns[_drag].Width = _wLeft + d; _lv.Columns[_drag + 1].Width = _wRight - d;
            _lv.EndUpdate(); _lv.Invalidate();
            return;
        }
        int c = Hit(e.X);
        Cursor = EdgeAt(e.X) >= 0 ? Cursors.VSplit : _sortBy is not null && c >= 0 && (_sortState?.Invoke(c) ?? -1) >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (_dragged || EdgeAt(e.X) >= 0) { _dragged = false; return; }       // a drag (or a click on a divider) is not a sort click
        int c = Hit(e.X);
        if (_sortBy is not null && c >= 0 && (_sortState?.Invoke(c) ?? -1) >= 0) { _sortBy(c); Invalidate(); }
        base.OnMouseClick(e);
    }
}
