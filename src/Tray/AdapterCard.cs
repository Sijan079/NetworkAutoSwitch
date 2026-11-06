using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

/// <summary>One connection on the dashboard (docs/ui-mockups Main.dc.html): an icon box, the kind (MAIN / BACKUP) in its role colour,
/// the network and the adapter, an IN USE or STANDBY chip, then Status / Latency / Uptime and one muted note under a divider.
/// The card of the connection in use is filled with its role colour, with every text on it in Ui.OnAccent (or a mix of it).
/// Everything is painted by one control, from strings prepared in Show(), so a paint allocates nothing.</summary>
sealed class AdapterCard : Card
{
    /// <summary>Loading placeholders that match the layout: kind, network, adapter, then the stats line.</summary>
    public static readonly (float W, int H)[] Bars = { (0.22f, 12), (0.45f, 18), (0.30f, 12), (0.85f, 22) };

    readonly Face _face;
    readonly Color _role;
    readonly string _kind;
    string _glyph;
    string _net = "", _adapter = "", _status = "-", _latency = "-", _uptime = "-", _note = "", _chip = "STANDBY", _statusTip = "";
    Color _statusInk;
    bool _known, _inUse;
    Skeleton? _skel;

    public AdapterCard(string glyph, string title) : base("", title)
    {
        _glyph = glyph; _kind = title.ToUpperInvariant();
        _role = title == "Main" ? Ui.Green : Ui.Amber;       // Main is teal and Backup amber everywhere in the app
        _statusInk = Ui.Muted;
        // the kind label is drawn inside the face (in the role colour), so the card's own header row is not used
        foreach (Control c in Controls) if (c != Body) c.Visible = false;
        Body.Padding = new Padding(14, 14, 14, 10);
        _face = new Face(this) { Dock = DockStyle.Fill, BackColor = Ui.CardBg };
        Body.Controls.Add(_face);
        // Card.SetLoading puts a Skeleton (painted in the card colour) into the body: while it shows, drop the fill so the
        // bars do not sit in a plain rectangle in the middle of a teal card; the fill comes back when the bars go.
        Body.ControlAdded += (_, e) => { if (e.Control is Skeleton s) { _skel = s; s.VisibleChanged += (_, _) => ApplyFill(); } };
    }

    /// <summary>The icon in the box follows the adapter kind (Wi-Fi, cable, phone).</summary>
    public void SetGlyph(string glyph) { if (glyph == _glyph) return; _glyph = glyph; _face.Invalidate(); }

    /// <summary>The network name and the adapter it is on.</summary>
    public void SetIdentity(string network, string adapter)
    {
        bool known = network.Length > 0; string net = known ? network : "Unknown Network";
        if (net == _net && adapter == _adapter && known == _known) return;      // called on every probe: repaint only when it changed
        _known = known; _net = net; _adapter = adapter; _face.Invalidate();
    }

    /// <summary>Shows one measurement. <paramref name="inUse"/> = this is the connection in use (filled card, IN USE chip).</summary>
    public void Show(ProbeResult r, bool hasData, double uptimePct, bool inUse, bool paused, string note)
    {
        string status = paused ? "Paused" : !hasData ? "None" : r.Healthy ? "Healthy" : "Down";
        Color ink = paused || !hasData ? Ui.Muted : r.Healthy ? _role : Ui.Red;
        string latency = hasData && r.Healthy ? r.Ms + " ms" : "—";        // no number when nothing answers: 0 would read as "perfect"
        string uptime = hasData ? $"{uptimePct:0.#} %" : "—";
        string chip = paused ? "PAUSED" : inUse ? "IN USE" : "STANDBY";
        string tip = hasData && !r.Healthy ? r.Detail : "";
        if (status == _status && ink == _statusInk && latency == _latency && uptime == _uptime && chip == _chip && note == _note && inUse == _inUse && tip == _statusTip) return;
        _status = status; _statusInk = ink; _latency = latency; _uptime = uptime; _chip = chip; _note = note;
        if (tip != _statusTip) { _statusTip = tip; Ui.Tip.SetToolTip(_face, tip.Length > 0 ? tip : (_kind == "MAIN" ? "Main connection" : "Backup connection")); }
        if (inUse != _inUse) { _inUse = inUse; ApplyFill(); }
        _face.Invalidate();
    }

    void ApplyFill()
    {
        bool fill = _inUse && _skel is not { Visible: true };
        SetFill(fill ? _role : null, fill ? Ui.OnAccent : null);
    }

    /// <summary>The painted content of the card.</summary>
    sealed class Face : Control
    {
        readonly AdapterCard _o;
        public Face(AdapterCard o)
        {
            _o = o;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Ui.Tip.SetToolTip(this, o._kind == "MAIN" ? "Main connection" : "Backup connection");
        }

        const TextFormatFlags Line = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(BackColor);
            bool filled = _o.Fill is not null;
            Color text = filled ? Ui.OnAccent : Ui.Text;
            Color muted = filled ? Ui.Mix(Ui.OnAccent, BackColor, 0.72f) : Ui.Muted;
            Color role = filled ? Ui.OnAccent : _o._role;
            Color line = filled ? Ui.Mix(Ui.OnAccent, BackColor, 0.25f) : Ui.Border;
            int w = Width;

            // icon box: 40 x 40, radius 10, role-coloured outline and glyph
            DashPaint.Outline(g, 0, 0, 40, 40, 10, role);
            g.DrawImage(Ui.GlyphCached(_o._glyph, 22, role), 9, 9);

            // chip on the right: the card in use gets an OnAccent outline on its fill; standby is a muted outline
            int chipW = DashPaint.ChipWidth(_o._chip);
            DashPaint.Chip(g, _o._chip, filled ? Ui.OnAccent : Ui.Muted, w - chipW - 1, 12, false);

            int tx = 52, tw = Math.Max(10, w - tx - chipW - 10);
            TextRenderer.DrawText(g, _o._kind, Ui.UiFont(8.25f, Weight.Bold), new Rectangle(tx, -1, tw, 16), role, Line);
            TextRenderer.DrawText(g, _o._net, Ui.UiFont(12f, Weight.Bold), new Rectangle(tx, 14, tw, 24), _o._known ? text : muted, Line);
            TextRenderer.DrawText(g, _o._adapter, Ui.UiFont(9f), new Rectangle(tx, 38, tw, 18), muted, Line);

            // three equal stat columns
            int col = (w - 24) / 3, sy = 70;
            for (int i = 0; i < 3; i++)
            {
                int x = i * (col + 12);
                string label = i == 0 ? "Status" : i == 1 ? "Latency" : "Uptime";
                string value = i == 0 ? _o._status : i == 1 ? _o._latency : _o._uptime;
                Color ink = i == 0 ? (filled ? Ui.OnAccent : _o._statusInk) : text;      // red on teal fails contrast: on a fill it is OnAccent
                TextRenderer.DrawText(g, label, Ui.UiFont(8.25f), new Rectangle(x, sy, col, 16), muted, Line);
                TextRenderer.DrawText(g, value, Ui.UiFont(11.25f, Weight.Bold), new Rectangle(x, sy + 16, col, 24), ink, Line);
            }

            // divider + note
            int dy = sy + 48;
            if (Height > dy + 8)
            {
                g.DrawLine(Ui.Pen(line), 0, dy, w, dy);
                TextRenderer.DrawText(g, _o._note, Ui.UiFont(9f), new Rectangle(0, dy + 10, w, 18), muted, Line);
            }
        }
    }
}
