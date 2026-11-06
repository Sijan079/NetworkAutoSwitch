using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NetworkAutoSwitch.Tray;

/// <summary>The kit Field look for a multi-line TextBox (the programs and folders of a rule): a muted label above a rounded, sunk box
/// (radius 8, teal border while focused). The kit Field's box is a fixed 36 px; this one takes the height given.
/// It also takes single-line inputs and a suffix, so it can fold into the kit Field once that supports a box height.</summary>
sealed class RuleField : Panel
{
    public Control Input { get; }
    readonly Label _label = new() { Dock = DockStyle.Top, Height = 22, Font = Ui.UiFont(9f, Weight.SemiBold), ForeColor = Ui.Muted, BackColor = Ui.CardBg, TextAlign = ContentAlignment.TopLeft, UseMnemonic = false };

    public RuleField(string label, Control input, string suffix = "", int boxHeight = 36)
    {
        Input = input; BackColor = Ui.CardBg; Height = 22 + boxHeight; Margin = new Padding(0);
        _label.Text = label;
        switch (input)
        {
            case TextBox t: t.BorderStyle = BorderStyle.None; if (!t.Multiline) t.Font = Ui.UiFont(9.75f); break;
            case NumericUpDown n: n.BorderStyle = BorderStyle.None; n.Font = Ui.UiFont(9.75f); break;
        }
        input.BackColor = Ui.InputBg; input.ForeColor = Ui.Text;
        Controls.Add(new Box(input, suffix) { Dock = DockStyle.Fill }); Controls.Add(_label);
    }

    sealed class Box : Panel
    {
        readonly Control _input; readonly string _suffix;
        GraphicsPath? _path; Size _pathSize;
        public Box(Control input, string suffix)
        {
            _input = input; _suffix = suffix; BackColor = Ui.CardBg;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Controls.Add(input);
            input.GotFocus += (_, _) => Invalidate(); input.LostFocus += (_, _) => Invalidate();
        }
        int SuffixW => _suffix.Length == 0 ? 0 : TextRenderer.MeasureText(_suffix, Ui.UiFont(9f), Size.Empty, TextFormatFlags.NoPadding).Width + 12;
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_input is TextBox { Multiline: true }) { _input.SetBounds(12, 8, Math.Max(10, Width - 14), Math.Max(10, Height - 12)); return; }
            int h = _input.PreferredSize.Height;
            _input.SetBounds(12, Math.Max(1, (Height - h) / 2), Math.Max(10, Width - 24 - SuffixW), h);
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
