using System.Windows.Controls;
namespace NetworkAutoSwitch.Tray.Fluent.Pages;
public class LogPage : Page
{
    internal LogPage(FluentContext cx) { Content = new TextBlock { Text = "Log (coming)", Margin = new System.Windows.Thickness(32) }; }
}
