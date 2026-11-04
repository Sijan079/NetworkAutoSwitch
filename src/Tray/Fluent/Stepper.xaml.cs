using System.Windows;
using System.Windows.Controls;

namespace NetworkAutoSwitch.Tray.Fluent;

/// <summary>A number field with a - at its start and a + at its end. One control, used everywhere a number is typed.</summary>
public partial class Stepper : System.Windows.Controls.UserControl
{
    public Stepper()
    {
        InitializeComponent();
        // the mouse wheel must never change a number by accident: pass it on so the page scrolls instead
        PreviewMouseWheel += (_, e) => { e.Handled = true; var up = new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = this }; (Parent as UIElement)?.RaiseEvent(up); };
        Minus.Click += (_, _) => Nudge(-Step);
        Plus.Click += (_, _) => Nudge(Step);
        Box.ValueChanged += (_, _) => ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when the number changes (typed, + / - or set from code).</summary>
    public event EventHandler? ValueChanged;

    public double Minimum { get => Box.Minimum; set => Box.Minimum = value; }
    public double Maximum { get => Box.Maximum; set => Box.Maximum = value; }
    public double Step { get; set; } = 1;
    public int Decimals { get => Box.MaxDecimalPlaces; set => Box.MaxDecimalPlaces = value; }
    public string? Placeholder { get => Box.PlaceholderText; set => Box.PlaceholderText = value ?? ""; }
    public double Value { get => Box.Value ?? Minimum; set => Box.Value = value; }

    void Nudge(double by) => Value = Math.Round(Math.Min(Maximum, Math.Max(Minimum, Value + by)), Math.Max(Decimals, 1));
}
