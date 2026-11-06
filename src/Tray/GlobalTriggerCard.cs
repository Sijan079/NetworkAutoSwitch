using System.Drawing;
using System.Windows.Forms;

namespace NetworkAutoSwitch.Tray;

/// <summary>The global trigger (X MB within Y seconds) as its own card at the top of the Backup Rules page; the rules editor below reads it.</summary>
sealed class GlobalTriggerCard : Card
{
    /// <summary>The height the card needs: header, one row of kit fields, padding.</summary>
    public const int PreferredHeight = 4 + 48 + 4 + 58 + 14 + 4;

    readonly NoWheelNumeric _mb = new() { DecimalPlaces = 1, Minimum = 0.1m, Maximum = 10000, Increment = 0.5m, Value = 2 };
    readonly NoWheelNumeric _win = new() { Minimum = 1, Maximum = 600, Value = 10 };
    readonly Label _note = new() { AutoSize = true, ForeColor = Ui.Muted, BackColor = Ui.CardBg, Font = Ui.UiFont(9f), Margin = new Padding(4, 32, 0, 0), UseMnemonic = false };
    bool _quiet;

    /// <summary>Raised when YOU change a number (not when the values are loaded).</summary>
    public event Action? Changed;

    public GlobalTriggerCard() : base("", "Global Trigger")
    {
        SetHint("Every watch rule uses this unless you give it its own.\nA watched program that receives more than this is blocked until you unblock it on the Dashboard.");
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Ui.CardBg, Margin = new Padding(0) };
        flow.Controls.Add(new Field("Block When Downloading More Than", _mb, "MB") { Width = 290, Margin = new Padding(0, 0, 14, 0) });
        flow.Controls.Add(new Field("Within", _win, "seconds") { Width = 230, Margin = new Padding(0, 0, 14, 0) });      // the box needs the unit, the two buttons and the ~20 px a hidden spinner keeps
        flow.Controls.Add(_note);
        Body.Controls.Add(flow);
        foreach (var n in new[] { _mb, _win }) n.ValueChanged += (_, _) => { UpdateRate(); if (!_quiet) Changed?.Invoke(); };
        UpdateRate();
    }

    public double MbValue() => (double)_mb.Value;
    public int WindowSeconds() => (int)_win.Value;

    /// <summary>Loads values without raising <see cref="Changed"/>.</summary>
    public void SetValues(double mb, int seconds)
    {
        _quiet = true;
        _mb.Value = Math.Min(_mb.Maximum, Math.Max(_mb.Minimum, (decimal)mb));
        _win.Value = Math.Min(_win.Maximum, Math.Max(_win.Minimum, seconds));
        _quiet = false; UpdateRate();
    }

    void UpdateRate() => _note.Text = $"≈ {(double)_mb.Value * 8 / (double)_win.Value:0.0} Mbit/s";
}
