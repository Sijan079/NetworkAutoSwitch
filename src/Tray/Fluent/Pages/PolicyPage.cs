using System.Windows.Controls;
namespace NetworkAutoSwitch.Tray.Fluent.Pages;
public class PolicyPage : Page
{
    internal PolicyPage(FluentContext cx) { Content = new TextBlock { Text = "Policy (coming)", Margin = new System.Windows.Thickness(32) }; }
}
