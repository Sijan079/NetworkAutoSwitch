using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

/// <summary>Shown once, the first time the app runs on a PC, in three steps: 1 the networks it found (main and backup) and the chance to
/// fix the choice, 2 programs to watch on the backup, 3 the daily cap, "start when I sign in" and what it changes in Windows.
/// The caller reads <see cref="StartAtSignIn"/> after it closes, however it was closed; it starts as the current setting, so Skip or the X change nothing.</summary>
sealed class WelcomeDialog : Form
{
    const int W = 680, H = 560, Side = DialogParts.Side, BodyW = W - 2 * Side;
    static readonly string[] StepNames = { "Pick Your Networks", "Add Programs To Watch", "Set A Daily Cap" };

    readonly SwitchEngine _engine;
    readonly Toggle _startup = new() { AccessibleName = "Start When I Sign In" };
    readonly Label _mainValue, _backupValue;
    readonly Panel[] _steps = new Panel[3];
    readonly StepStrip _strip = new(StepNames);
    readonly KitButton _back, _next, _skip;
    readonly System.Windows.Forms.Timer _refresh = new() { Interval = 1500 };
    int _step;

    public bool StartAtSignIn => _startup.Checked;

    public WelcomeDialog(SwitchEngine engine)
    {
        _engine = engine;
        DialogParts.Prepare(this, "Welcome", new Size(W, H), taskbar: true);

        var title = DialogParts.Title("Welcome To NetworkAutoSwitch"); title.SetBounds(Side, 20, BodyW, 32);
        var sub = DialogParts.Text("Three quick things to set up.", Ui.UiFont(9.75f), Ui.Muted); sub.SetBounds(Side, 54, BodyW, 22);
        _strip.SetBounds(Side, 90, BodyW, 34);
        _strip.StepClicked += Go;

        _mainValue = Value(""); _backupValue = Value("");
        _steps[0] = NetworksStep(); _steps[1] = ProgramsStep(); _steps[2] = CapStep();
        foreach (var p in _steps) p.SetBounds(Side, 142, BodyW, H - 64 - 142 - 8);

        _skip = DialogParts.Button("Skip", "Close this and start using the app. Everything here can be changed later.", () => DialogResult = DialogResult.OK);
        _back = DialogParts.Button("Back", "The step before.", () => Go(_step - 1));
        _next = DialogParts.Button("Next", "The next step.", () => { if (_step < 2) Go(_step + 1); else DialogResult = DialogResult.OK; }, BtnKind.Primary);
        var footer = new DialogFooter();
        footer.AddLeft(_skip); footer.AddRight(_back); footer.AddRight(_next);

        Controls.AddRange(new Control[] { title, sub, _strip, footer });
        Controls.AddRange(_steps);
        AcceptButton = _next;
        _startup.Checked = StartupTask.IsEnabled();

        // the adapters (and the Wi-Fi network names) are still being read in the first seconds: keep the text fresh
        _refresh.Tick += (_, _) => Show_(); _refresh.Start(); FormClosed += (_, _) => _refresh.Stop();
        Show_();
        Go(0);
    }
    internal void GoTemp(int n) => Go(n); // TEMP

    /// <summary>Shows one step (0-2): its panel, the strip, and the footer buttons for it.</summary>
    void Go(int step)
    {
        _step = Math.Clamp(step, 0, 2);
        for (int i = 0; i < _steps.Length; i++) _steps[i].Visible = i == _step;
        _strip.Current = _step;
        _back.Visible = _step > 0; _skip.Visible = _step < 2;
        _next.Text = _step < 2 ? "Next" : "Get Started";
        Ui.Tip.SetToolTip(_next, _step < 2 ? "The next step." : "Close this and start using the app.");
        if (IsHandleCreated) _next.Focus();
    }

    // ---------- the steps ----------

    Panel NetworksStep()
    {
        var p = StepPanel();
        // the one-line pitch next to the app's mark, as in the mockup
        var hero = new HeroMark(); hero.Location = new Point(0, 0);
        var pitch = DialogParts.Text("Keeps you online when your main connection drops, without letting the backup spend your mobile data.", Ui.UiFont(9.75f), Ui.Muted);
        pitch.SetBounds(66, 0, BodyW - 66, 52); pitch.TextAlign = ContentAlignment.MiddleLeft;
        p.Controls.Add(hero); p.Controls.Add(pitch);

        AddHeading(p, 70, "Pick your networks", "Tell it which Wi-Fi is main and which is your phone. It asks once per new network.");

        var well = new DialogWell(); well.SetBounds(0, 128, BodyW, 128);
        AddFound(well, 14, Ui.Home, Ui.Green, "Main Connection", _mainValue);
        AddFound(well, 68, Ui.Phone, Ui.Amber, "Backup Connection", _backupValue);
        p.Controls.Add(well);

        var choose = Ui.Btn(Ui.Settings, "Choose Connections", "Pick the main and the backup connection yourself.", () => { using var d = new ConnectionsDialog(_engine); d.ShowDialog(this); Show_(); });
        choose.Location = new Point(0, 270);
        p.Controls.Add(choose);
        return p;
    }

    Panel ProgramsStep()
    {
        var p = StepPanel();
        AddHeading(p, 0, "Add programs to watch", "Big downloaders get blocked on the backup, then moved back when main returns.");

        var well = new DialogWell(); well.SetBounds(0, 62, BodyW, 156);
        AddRule(well, 16, "Watch", Ui.Blue, "Blocked only while it pulls a lot of data on the backup. The usual choice for games and launchers.");
        AddRule(well, 62, "Block", Ui.Red, "Never allowed on the backup at all.");
        AddRule(well, 108, "Upload Cap", Ui.Amber, "Allowed, but its uploads are held to a speed you choose.");
        p.Controls.Add(well);

        var where = DialogParts.Text("Add them on the Backup Rules page of the main window (double-click the tray icon to open it).",
            Ui.UiFont(9.4f), Ui.Muted);
        where.SetBounds(0, 234, BodyW, 24);
        p.Controls.Add(where);
        return p;
    }

    Panel CapStep()
    {
        var p = StepPanel();
        AddHeading(p, 0, "Set a daily cap", "Get warned at 80 % of the data you allow the backup, and again when it is reached.");

        long cap = _engine.DailyCapMB;
        var well = new DialogWell(); well.SetBounds(0, 62, BodyW, 62);
        well.Controls.Add(new DialogGlyph(Ui.Data, 22, Ui.Amber) { Location = new Point(16, 20) });
        var capLbl = DialogParts.Text(cap > 0 ? $"{cap:N0} MB a day on the backup" : "No limit yet", Ui.UiFont(10.5f, Weight.SemiBold), Ui.Text, Ui.Bg);
        capLbl.SetBounds(52, 11, BodyW - 68, 22);
        var capHint = DialogParts.Text(cap > 0 ? "Change it any time on the Settings page." : "Set one on the Settings page when you know your mobile plan.", Ui.UiFont(9f), Ui.Muted, Ui.Bg);
        capHint.SetBounds(52, 33, BodyW - 68, 20);
        well.Controls.Add(capLbl); well.Controls.Add(capHint);
        p.Controls.Add(well);

        // start at sign-in: the one choice this dialog makes (the caller applies it after the dialog closes)
        _startup.Location = new Point(0, 146);
        var sLbl = DialogParts.Text("Start When I Sign In", Ui.UiFont(10.5f, Weight.SemiBold), Ui.Text); sLbl.SetBounds(52, 140, BodyW - 52, 22);
        var sHint = DialogParts.Text("Opens with Windows, so the switching is on from the start.", Ui.UiFont(9f), Ui.Muted); sHint.SetBounds(52, 162, BodyW - 52, 20);
        sLbl.Click += (_, _) => _startup.Checked = !_startup.Checked; sLbl.Cursor = Cursors.Hand;
        p.Controls.Add(_startup); p.Controls.Add(sLbl); p.Controls.Add(sHint);

        var admin = new DialogWell(); admin.SetBounds(0, 206, BodyW, 64);
        admin.Controls.Add(new DialogGlyph(Ui.Shield, 20, Ui.Muted) { Location = new Point(16, 22) });
        var adminText = DialogParts.Text("Needs administrator rights to change metrics, firewall and QoS rules. Your original settings are saved first and restored on exit.",
            Ui.UiFont(9f), Ui.Muted, Ui.Bg);
        adminText.SetBounds(50, 10, BodyW - 64, 44); adminText.TextAlign = ContentAlignment.MiddleLeft;
        admin.Controls.Add(adminText);
        p.Controls.Add(admin);
        return p;
    }

    // ---------- small builders ----------

    static Panel StepPanel() => new() { BackColor = Ui.CardBg };

    static Label Value(string text) => DialogParts.Text(text, Ui.UiFont(10f, Weight.SemiBold), Ui.Text, Ui.Bg);

    /// <summary>The step's bold heading and its one muted line (14 px / 12.5 px in the mockup).</summary>
    static void AddHeading(Panel p, int y, string heading, string detail)
    {
        var h = DialogParts.Text(heading, Ui.UiFont(10.5f, Weight.Bold), Ui.Text); h.SetBounds(0, y, BodyW, 22);
        var d = DialogParts.Text(detail, Ui.UiFont(9.4f), Ui.Muted); d.SetBounds(0, y + 24, BodyW, 22);
        p.Controls.Add(h); p.Controls.Add(d);
    }

    /// <summary>One "what I found" row: the role icon in its colour, a small muted caption, the adapter on the line below (two lines for the long "none found" hint).</summary>
    static void AddFound(Panel well, int y, string glyph, Color role, string caption, Label value)
    {
        well.Controls.Add(new DialogGlyph(glyph, 22, role) { Location = new Point(16, y + 4) });
        var cap = DialogParts.Text(caption, Ui.UiFont(8.6f, Weight.SemiBold), Ui.Muted, Ui.Bg); cap.SetBounds(52, y, BodyW - 68, 18);
        value.SetBounds(52, y + 18, BodyW - 68, 40); value.AutoEllipsis = true;
        well.Controls.Add(cap); well.Controls.Add(value);
    }

    /// <summary>One rule action: its chip and what it does.</summary>
    static void AddRule(Panel well, int y, string action, Color role, string what)
    {
        var chip = new Chip(action, role) { AutoSize = false, Width = 96, Location = new Point(16, y + 2) };
        var t = DialogParts.Text(what, Ui.UiFont(9.4f), Ui.Muted, Ui.Bg); t.SetBounds(128, y, BodyW - 144, 26); t.TextAlign = ContentAlignment.MiddleLeft;
        well.Controls.Add(chip); well.Controls.Add(t);
    }

    static string Describe(string name, AdapterKind kind, string? network) =>
        $"{name}  ·  {AdapterDiscovery.KindLabel(kind)}" + (string.IsNullOrEmpty(network) ? "" : $"  ·  {network}");

    /// <summary>Refreshes the found connections (every 1.5 s); a label is only touched when its text changed, so nothing flickers.</summary>
    void Show_()
    {
        string main = _engine.PrimaryName.Length > 0 ? Describe(_engine.PrimaryName, _engine.PrimaryKind, _engine.PrimaryNetwork) : "None found";
        string backup = _engine.BackupName.Length > 0
            ? Describe(_engine.BackupName, _engine.BackupKind, _engine.BackupNetwork)
            : "None found yet. Plug in your phone with USB tethering (or connect Bluetooth tethering) and it is picked up by itself.";
        if (_mainValue.Text != main) _mainValue.Text = main;
        if (_backupValue.Text != backup) { _backupValue.Text = backup; _backupValue.ForeColor = _engine.BackupName.Length > 0 ? Ui.Text : Ui.Muted; _backupValue.Font = Ui.UiFont(_engine.BackupName.Length > 0 ? 10f : 9.4f, _engine.BackupName.Length > 0 ? Weight.SemiBold : Weight.Regular); }
    }

    /// <summary>The 52 px teal square with the Wi-Fi mark (the mockup's welcome badge).</summary>
    sealed class HeroMark : Control
    {
        GraphicsPath? _path;
        public HeroMark()
        {
            Size = new Size(52, 52);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent?.BackColor ?? Ui.CardBg);
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.Half;
            _path ??= Ui.RoundRect(new RectangleF(0, 0, Width, Height), 14);
            g.FillPath(Ui.Brush(Ui.Green), _path);
            g.DrawImage(Ui.GlyphCached(Ui.Wifi, 30, Ui.OnAccent), (Width - 30) / 2, (Height - 30) / 2);
        }
        protected override void Dispose(bool disposing) { if (disposing) _path?.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>The step indicator: a numbered circle and a name per step, joined by a line. Done = teal outline with a check, current = teal fill,
    /// still to come = grey outline. A click on a step goes there.</summary>
    sealed class StepStrip : Control
    {
        readonly string[] _names;
        int _current, _hover = -1;
        public event Action<int>? StepClicked;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Current { get => _current; set { _current = value; AccessibleDescription = $"Step {value + 1} of {_names.Length}: {_names[value]}"; Invalidate(); } }

        public StepStrip(string[] names)
        {
            _names = names; Height = 34; AccessibleRole = AccessibleRole.ProgressBar; AccessibleName = "Steps";
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        int CellW => Width / _names.Length;
        int Hit(Point p) => p.X >= 0 && p.X < CellW * _names.Length && p.Y >= 0 && p.Y < Height ? p.X / CellW : -1;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent?.BackColor ?? Ui.CardBg);
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.Half;
            const int d = 30; int cy = Height / 2, cw = CellW;
            var label = Ui.UiFont(9.75f, Weight.SemiBold); var num = Ui.UiFont(9.75f, Weight.Bold);
            for (int i = 0; i < _names.Length; i++)
            {
                int x = i * cw; bool done = i < _current, on = i == _current;
                var circle = new RectangleF(x + 0.75f, cy - d / 2f + 0.75f, d - 1.5f, d - 1.5f);
                if (on) g.FillEllipse(Ui.Brush(Ui.Green), circle);
                else g.DrawEllipse(Ui.Pen(done ? Ui.Green : (i == _hover ? Ui.Muted : Ui.Border), 1.5f), circle);
                var ink = on ? Ui.OnAccent : done ? Ui.Green : Ui.Muted;
                if (done) g.DrawImage(Ui.GlyphCached(Ui.Check, 16, ink), x + (d - 16) / 2, cy - 8);
                else TextRenderer.DrawText(g, (i + 1).ToString(), num, new Rectangle(x, cy - d / 2, d, d), ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                int tx = x + d + 10;
                var size = TextRenderer.MeasureText(g, _names[i], label, Size.Empty, TextFormatFlags.NoPadding);
                int tw = Math.Min(size.Width + 2, cw - d - 10 - (i < _names.Length - 1 ? 12 : 0));
                TextRenderer.DrawText(g, _names[i], label, new Rectangle(tx, 0, tw, Height), on || done || i == _hover ? Ui.Text : Ui.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                // the line to the next step: teal once this step is behind you
                if (i < _names.Length - 1)
                {
                    int lx0 = tx + tw + 12, lx1 = (i + 1) * cw - 12;
                    if (lx1 > lx0) g.DrawLine(Ui.Pen(done ? Ui.Green : Ui.Border, 1.5f), lx0, cy, lx1, cy);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e) { int h = Hit(e.Location); if (h != _hover) { _hover = h; Cursor = h >= 0 && h != _current ? Cursors.Hand : Cursors.Default; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e) { int h = Hit(e.Location); if (h >= 0 && h != _current) StepClicked?.Invoke(h); base.OnMouseClick(e); }
    }
}
