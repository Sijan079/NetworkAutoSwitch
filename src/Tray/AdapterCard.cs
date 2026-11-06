using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

/// <summary>One connection on the dashboard. Left: network name, adapter, average ping, uptime. Right: the current ping, number on top and "ms" below it.
/// The card of the connection in use is filled with its role colour.</summary>
sealed class AdapterCard : Card
{
    static Color Blend(Color a, Color b, float t) => Color.FromArgb((int)(a.R * t + b.R * (1 - t)), (int)(a.G * t + b.G * (1 - t)), (int)(a.B * t + b.B * (1 - t)));

    /// <summary>Loading placeholders that match the layout: four lines on the left (the right side stays quiet).</summary>
    public static readonly (float W, int H)[] Bars = { (0.45f, 18), (0.28f, 12), (0.40f, 12), (0.30f, 12) };

    static int Fit(float pt, bool bold, int pad) => Ui.UiFont(pt, bold).Height + pad;       // a line is as tall as its text needs, whatever the font size or display scaling
    static Label Line(float size, bool bold, Color color, int height) => new() { Dock = DockStyle.Top, Height = Math.Max(height, Fit(size, bold, 6)), AutoEllipsis = true, ForeColor = color, Font = Ui.UiFont(size, bold: bold), BackColor = Ui.CardBg, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label _net = Line(12f, true, Ui.Text, 28), _adapter = Line(10f, false, Ui.Muted, 22), _avg = Line(10f, false, Ui.Muted, 22), _up = Line(10f, false, Ui.Muted, 22);
    readonly Label _ms = new() { Dock = DockStyle.Top, Height = Math.Max(54, Fit(28f, true, 8)), Font = Ui.UiFont(28f, bold: true), TextAlign = ContentAlignment.BottomCenter, BackColor = Ui.CardBg, Text = "..." };
    readonly Label _unit = new() { Dock = DockStyle.Top, Height = Math.Max(26, Fit(11f, false, 6)), Font = Ui.UiFont(11f), ForeColor = Ui.Muted, TextAlign = ContentAlignment.TopCenter, BackColor = Ui.CardBg };

    // a symbol in front of the average ping and the uptime, instead of the words
    readonly PictureBox _avgIcon = new() { Dock = DockStyle.Left, Width = 30, SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Ui.CardBg, Image = Ui.GlyphBold(Ui.Speed, 22, Ui.Muted) };
    readonly PictureBox _upIcon = new() { Dock = DockStyle.Left, Width = 30, SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Ui.CardBg, Image = Ui.GlyphBold(Ui.Up, 22, Ui.Muted) };
    Color _iconInk = Ui.Muted;
    static Panel Row(Label text, PictureBox icon)
    {
        var p = new Panel { Dock = DockStyle.Top, Height = text.Height, BackColor = Ui.CardBg };
        text.Dock = DockStyle.Fill; p.Controls.Add(text); p.Controls.Add(icon);
        return p;
    }
    void InkIcons(Color c) { if (c == _iconInk) return; _iconInk = c; _avgIcon.Image = Ui.GlyphBold(Ui.Speed, 22, c); _upIcon.Image = Ui.GlyphBold(Ui.Up, 22, c); }

    public AdapterCard(string glyph, string title) : base(glyph, title)
    {
        var right = new Panel { Dock = DockStyle.Right, Width = 104, BackColor = Ui.CardBg };
        right.Controls.Add(_unit); right.Controls.Add(_ms);                       // docked to the top: the last added is the first shown
        var left = new Panel { Dock = DockStyle.Fill, BackColor = Ui.CardBg };
        left.Controls.Add(Row(_up, _upIcon)); left.Controls.Add(Row(_avg, _avgIcon)); left.Controls.Add(_adapter); left.Controls.Add(_net);
        Ui.Tip.SetToolTip(_avg, "Average ping"); Ui.Tip.SetToolTip(_avgIcon, "Average ping");
        Ui.Tip.SetToolTip(_up, "Uptime: the share of checks that got an answer"); Ui.Tip.SetToolTip(_upIcon, "Uptime: the share of checks that got an answer");
        Body.Controls.Add(left); Body.Controls.Add(right);
    }

    bool _known, _selected;

    /// <summary>The network name and the adapter it is on.</summary>
    public void SetIdentity(string network, string adapter)
    {
        _known = network.Length > 0;
        _net.Text = _known ? network : "Unknown network";
        _net.ForeColor = Fill is not null ? Color.White : _known ? Ui.Text : Ui.Muted;
        _adapter.Text = adapter;
    }

    /// <summary>Shows one measurement. <paramref name="selected"/> = this is the connection in use: its colors are shown in full.
    /// The other card is muted (grey ping, grey name), so the one in use stands out even without looking at the border.</summary>
    public void Show(ProbeResult r, double uptimePct, int? avgMs, Color? inUse, bool selected)
    {
        _selected = selected;
        _net.ForeColor = _known ? Ui.Text : Ui.Muted;
        _avg.Text = avgMs is int a ? a + " ms" : "-";
        _up.Text = $"{uptimePct:0.#}%";
        if (r.Healthy) { _ms.Font = Ui.UiFont(28f, bold: true); _ms.Text = r.Ms.ToString(); _ms.ForeColor = Ui.Text; _unit.Text = "ms"; Ui.Tip.SetToolTip(_ms, ""); }
        else { _ms.Font = Ui.UiFont(28f, bold: true); _ms.Text = "\u2014"; _ms.ForeColor = Ui.Red; _unit.Text = ""; Ui.Tip.SetToolTip(_ms, r.Detail); }   // a ping has no number when nothing answers: 0 would read as "perfect"
        // the connection in use is painted in its role colour (green = Main, amber = Backup), with white text on it
        Color? fill = inUse is Color u ? (u == Ui.Green ? Ui.GreenFill : Ui.AmberFill) : null;
        if (fill is Color f)
        {
            Color soft = Blend(Color.White, f, 0.8f);
            SetFill(f, soft);
            _net.ForeColor = Color.White; _ms.ForeColor = Color.White;
            _adapter.ForeColor = _avg.ForeColor = _up.ForeColor = _unit.ForeColor = soft; InkIcons(Color.White);
        }
        else
        {
            SetFill(null, null);
            _adapter.ForeColor = _avg.ForeColor = _up.ForeColor = _unit.ForeColor = Ui.Muted; InkIcons(Ui.Text);
        }
    }
}
