using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

/// <summary>The "Backup Data Today" meter: the amount in big type, "MB of 1000 MB", a bar against the daily cap (teal, amber from 80 %,
/// red at the cap), a short note, and the session line (switches, time on backup). Strings are made in Set(), painting allocates nothing.</summary>
sealed class CapMeter : Control
{
    string _big = "0", _unit = "MB", _note = "", _session = "", _last = "";
    float _frac; Color _bar = Ui.Green;

    public CapMeter()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.CardBg;
    }

    public void Set(long bytes, long capMb, string session, string last)
    {
        bool gb = bytes >= 1_000_000_000;
        string big = gb ? $"{bytes / 1e9:0.0#}" : $"{bytes / 1_000_000}";
        string cap = capMb >= 1000 ? $"{capMb / 1000.0:0.#} GB" : $"{capMb} MB";
        string unit = capMb > 0 ? $"{(gb ? "GB" : "MB")} of {cap}" : $"{(gb ? "GB" : "MB")}, no daily limit";
        var level = DataBudget.LevelFor(bytes, capMb * 1_000_000L);
        Color bar = level == BudgetLevel.Reached ? Ui.Red : level == BudgetLevel.Warning ? Ui.Amber : Ui.Green;
        float frac = capMb > 0 ? (float)Math.Clamp(bytes / (capMb * 1e6), 0, 1) : 0;
        string note = capMb > 0 ? $"Warning at {DataBudget.WarnAt * 100:0} %. Limit set in Settings." : "Set a daily limit in Settings.";
        if (big == _big && unit == _unit && bar == _bar && Math.Abs(frac - _frac) < 0.002f && note == _note && session == _session && last == _last) return;
        _big = big; _unit = unit; _bar = bar; _frac = frac; _note = note; _session = session; _last = last;
        Invalidate();
    }

    const TextFormatFlags Line = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(BackColor);
        int w = Width;
        var bigFont = Ui.UiFont(21f, Weight.Bold);
        int bw = DashPaint.TextWidth(_big, 21f, Weight.Bold);
        TextRenderer.DrawText(g, _big, bigFont, new Rectangle(0, 0, bw + 4, 36), Ui.Text, Line | TextFormatFlags.Bottom);
        TextRenderer.DrawText(g, _unit, Ui.UiFont(9.75f), new Rectangle(bw + 8, 0, Math.Max(10, w - bw - 8), 31), Ui.Muted, Line | TextFormatFlags.Bottom);

        // the bar: 8 px, fully rounded, on the ground colour
        DashPaint.Fill(g, 0, 46, w, 8, 4, Ui.InputBg);
        int fw = (int)(w * _frac);
        if (fw >= 8) DashPaint.Fill(g, 0, 46, fw, 8, 4, _bar); else if (fw > 0) g.FillRectangle(Ui.Brush(_bar), 0, 47, fw, 6);

        TextRenderer.DrawText(g, _note, Ui.UiFont(9f), new Rectangle(0, 62, w, 18), Ui.Muted, Line);
        if (Height < 108) return;
        g.DrawLine(Ui.Pen(Ui.Border), 0, 88, w, 88);
        TextRenderer.DrawText(g, _session, Ui.UiFont(9f, Weight.SemiBold), new Rectangle(0, 98, w, 18), Ui.Text, Line);
        if (Height >= 134) TextRenderer.DrawText(g, _last, Ui.UiFont(9f), new Rectangle(0, 118, w, 18), Ui.Muted, Line);
    }
}
