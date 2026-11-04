using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace NetworkAutoSwitch.Tray.Fluent;

/// <summary>Grey placeholder bars with a gentle pulse, shown while the engine changes the network. Kind: Card (default), Banner or Chart.</summary>
sealed class SkeletonHost : StackPanel
{
    string _kind = "Card";
    public SkeletonHost() { IsHitTestVisible = false; Build(); }

    public string Kind { get => _kind; set { _kind = value; Build(); } }

    void Build()
    {
        Children.Clear();
        (double W, double H)[] bars = _kind switch
        {
            "Banner" => new[] { (300.0, 26.0) },
            "Chart" => new[] { (700.0, 14.0), (480.0, 14.0), (620.0, 14.0), (380.0, 14.0) },
            _ => new[] { (150.0, 28.0), (300.0, 12.0), (220.0, 12.0) },
        };
        foreach (var (w, h) in bars)
        {
            var bar = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(Math.Min(6, h / 2)), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
            bar.SetResourceReference(Border.BackgroundProperty, "ControlStrokeColorDefaultBrush");
            bar.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            Children.Add(bar);
        }
    }
}
