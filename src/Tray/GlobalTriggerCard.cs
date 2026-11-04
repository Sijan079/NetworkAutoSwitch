using System.Drawing;
using System.Windows.Forms;

namespace NetworkAutoSwitch.Tray;

/// <summary>The global trigger (X MB within Y seconds) as its own card, so it can live on the Settings page while the rules editor on the Policy page reads it.</summary>
sealed class GlobalTriggerCard : Card
{
    readonly NoWheelNumeric _mb = new() { DecimalPlaces = 1, Minimum = 0.1m, Maximum = 10000, Increment = 0.5m, Value = 2, Width = 84 };
    readonly NoWheelNumeric _win = new() { Minimum = 1, Maximum = 600, Value = 10, Width = 68 };
    readonly Label _rate = new() { AutoSize = true, ForeColor = Ui.Muted, Padding = new Padding(4, 6, 0, 0), BackColor = Ui.CardBg };
    bool _quiet;

    /// <summary>Raised when YOU change a number (not when the values are loaded).</summary>
    public event Action? Changed;

    public GlobalTriggerCard() : base(Ui.Download, "Global Trigger")
    {
        SetHint("Every watch rule uses this unless you give it its own.\nA watched program that receives more than this is blocked until you unblock it on the Dashboard.");
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Ui.CardBg };
        Label L(string t) => new() { Text = t, AutoSize = true, Padding = new Padding(2, 6, 8, 0), ForeColor = Ui.Muted, BackColor = Ui.CardBg };
        flow.Controls.AddRange(new Control[] { L("Block Above"), _mb, L("MB Per"), _win, L("s"), _rate });
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

    void UpdateRate() => _rate.Text = $"≈ {(double)_mb.Value * 8 / (double)_win.Value:0.0} Mbit/s";
}
