using System.Drawing;
using System.Windows.Forms;

namespace NetworkAutoSwitch.Tray;

/// <summary>One cell: text, or a chip when <see cref="Chip"/> is set (the text is drawn inside it in that colour).</summary>
readonly record struct DashCell(string Text, Color? Chip = null);

/// <summary>One row. <see cref="Action"/> = what a small icon button at the end of the row does ("Allow" on a blocked row, "Block Now" on an allowed one; null = no button), named in <see cref="ActionTip"/>; <see cref="Key"/> is handed back on a click.</summary>
sealed record DashRow(DashCell[] Cells, string? Action, string Key, string Tip, bool Danger = false, string? ActionTip = null)
{
    public bool Same(DashRow o)
    {
        if (Action != o.Action || Key != o.Key || Tip != o.Tip || Danger != o.Danger || ActionTip != o.ActionTip || Cells.Length != o.Cells.Length) return false;
        for (int i = 0; i < Cells.Length; i++) if (Cells[i] != o.Cells[i]) return false;
        return true;
    }
}

/// <summary>A column: header text, width in px (0 = takes what is left; one such column), right-aligned or not, and whether it may be
/// dropped when the table is narrow (so the name column never shrinks to nothing in a small window).</summary>
sealed record DashColumn(string Header, int Width, bool Right = false, bool Optional = false);

/// <summary>The dashboard's quick-view table (Network Activity, Watchdog): a muted uppercase header, 40 px rows with dividers, chips and
/// an optional small button per row. Painted in one control, as many rows as fit (the rest is counted on the last line), repainted only
/// when the rows really changed. The full tables, with sorting and selection, are on the Programs page.</summary>
sealed class DashTable : Control
{
    readonly DashColumn[] _cols;
    readonly string _empty;
    readonly int _actionW;
    DashRow[] _rows = Array.Empty<DashRow>();
    readonly int[] _x, _w; readonly bool[] _shown;
    int _fillShown = -1;
    int _hoverRow = -1; bool _hoverBtn;
    string _more = "";
    const int HeadH = 30, RowH = 40, Gap = 12, BtnH = 28;
    public event Action<DashRow>? ActionClicked;

    public DashTable(DashColumn[] cols, string empty, int actionWidth = 0)
    {
        _cols = cols; _empty = empty; _actionW = actionWidth;
        _x = new int[cols.Length]; _w = new int[cols.Length]; _shown = new bool[cols.Length];
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.CardBg; AccessibleRole = AccessibleRole.Table;
    }

    /// <summary>New rows: repaints only when something differs from what is on screen (the source refreshes every 2 s).</summary>
    public void SetRows(DashRow[] rows)
    {
        bool same = rows.Length == _rows.Length;
        for (int i = 0; same && i < rows.Length; i++) same = rows[i].Same(_rows[i]);
        if (same) return;
        _rows = rows; _hoverRow = -1; Layout2(); Invalidate();
    }

    int Fits => Math.Max(0, (Height - HeadH) / RowH);

    // column positions, worked out on resize (not per paint); optional columns go first when the name column gets narrow
    void Layout2()
    {
        int avail = Width - (_actionW > 0 ? _actionW + Gap : 0);
        for (int i = 0; i < _cols.Length; i++) _shown[i] = true;
        int Fixed() { int s = 0, n = 0; for (int i = 0; i < _cols.Length; i++) if (_shown[i]) { s += _cols[i].Width; n++; } return s + Gap * (n - 1); }
        for (int i = _cols.Length - 1; i >= 0 && avail - Fixed() < 96; i--) if (_cols[i].Optional) _shown[i] = false;
        int fill = Math.Max(40, avail - Fixed()), x = 0;
        for (int i = 0; i < _cols.Length; i++)
        {
            if (!_shown[i]) continue;
            _x[i] = x; _w[i] = _cols[i].Width == 0 ? fill : _cols[i].Width; x += _w[i] + Gap;
            if (_cols[i].Width == 0) _fillShown = i;
        }
        int hidden = _rows.Length - Fits;
        _more = hidden > 0 && Fits > 0 ? $"{hidden + 1} more on the Programs page" : "";
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Layout2(); }

    Rectangle BtnRect(int row) => new(Width - _actionW, HeadH + row * RowH + (RowH - BtnH) / 2, _actionW, BtnH);

    const TextFormatFlags Cell = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(BackColor);
        var head = Ui.UiFont(8.25f, Weight.Bold); var body = Ui.UiFont(9.75f);
        for (int i = 0; i < _cols.Length; i++)
            if (_shown[i]) TextRenderer.DrawText(g, _cols[i].Header, head, new Rectangle(_x[i], 0, _w[i], HeadH - 8), Ui.Muted, Cell | (_cols[i].Right ? TextFormatFlags.Right : TextFormatFlags.Left));
        g.DrawLine(Ui.Pen(Ui.Border), 0, HeadH - 1, Width, HeadH - 1);

        if (_rows.Length == 0)
        {
            TextRenderer.DrawText(g, _empty, body, new Rectangle(0, HeadH, Width, RowH), Ui.Muted, Cell);
            return;
        }
        int fits = Fits, n = Math.Min(_rows.Length, fits);
        if (_more.Length > 0) n = fits - 1;       // the last line says how many are not shown
        for (int r = 0; r < n; r++)
        {
            var row = _rows[r]; int y = HeadH + r * RowH;
            for (int i = 0; i < _cols.Length && i < row.Cells.Length; i++)
            {
                if (!_shown[i]) continue;
                var c = row.Cells[i];
                if (c.Chip is Color role && c.Text.Length > 0) DashPaint.Chip(g, c.Text, role, _x[i], y + RowH / 2, false);
                else TextRenderer.DrawText(g, c.Text, body, new Rectangle(_x[i], y, _w[i], RowH), Ui.Text, Cell | (_cols[i].Right ? TextFormatFlags.Right : TextFormatFlags.Left));
            }
            if (row.Action is string a)
            {
                var b = BtnRect(r); bool hot = r == _hoverRow && _hoverBtn;
                Color edge = row.Danger ? Ui.Red : Ui.Border, ink = row.Danger ? Ui.Red : Ui.Text;
                Color bg = row.Danger ? (hot ? Ui.Tint(Ui.Red, 0.14f) : BackColor) : hot ? Ui.Mix(Ui.Text, Ui.Raised, 0.07f) : Ui.Raised;
                DashPaint.Fill(g, b.X, b.Y, b.Width, b.Height, 7, bg); DashPaint.Outline(g, b.X, b.Y, b.Width, b.Height, 7, edge);
                // the icon shows the row's STATE, not the action: a blocked program has a closed lock, an allowed one an open lock; the tooltip names what a click does
                g.DrawImage(Ui.GlyphCached(a.StartsWith("Allow", StringComparison.Ordinal) ? Ui.Lock : Ui.Unlock, 18, ink), b.X + (b.Width - 18) / 2, b.Y + (b.Height - 18) / 2);
            }
            g.DrawLine(Ui.Pen(Ui.Border), 0, y + RowH - 1, Width, y + RowH - 1);
        }
        if (_more.Length > 0 && fits > 0)
            TextRenderer.DrawText(g, _more, Ui.UiFont(9f), new Rectangle(0, HeadH + n * RowH, Width, RowH), Ui.Muted, Cell);
    }

    int RowAt(Point p)
    {
        if (p.Y < HeadH) return -1;
        int r = (p.Y - HeadH) / RowH, shown = _more.Length > 0 ? Fits - 1 : Math.Min(_rows.Length, Fits);
        return r < shown ? r : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int r = RowAt(e.Location);
        bool btn = r >= 0 && _rows[r].Action is not null && BtnRect(r).Contains(e.Location);
        if (r != _hoverRow || btn != _hoverBtn)
            Ui.Tip.SetToolTip(this, r < 0 ? "" : btn ? (_rows[r].ActionTip ?? _rows[r].Action ?? "") : _rows[r].Tip);        // over the button: what it does; elsewhere: the full path / details of the row
        if (r != _hoverRow || btn != _hoverBtn) { _hoverRow = r; _hoverBtn = btn; Cursor = btn ? Cursors.Hand : Cursors.Default; Invalidate(); }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e) { if (_hoverRow >= 0) { _hoverRow = -1; _hoverBtn = false; Invalidate(); } base.OnMouseLeave(e); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        int r = RowAt(e.Location);
        if (e.Button == MouseButtons.Left && r >= 0 && _rows[r].Action is not null && BtnRect(r).Contains(e.Location)) ActionClicked?.Invoke(_rows[r]);
        base.OnMouseUp(e);
    }
}
