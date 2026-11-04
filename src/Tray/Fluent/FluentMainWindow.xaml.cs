using System.ComponentModel;
using NetworkAutoSwitch.Core;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;
using NetworkAutoSwitch.Tray.Fluent.Pages;

namespace NetworkAutoSwitch.Tray.Fluent;

/// <summary>The Fluent window: a navigation pane with Dashboard, Log, Policy and Settings. Pages are built once and kept, so the Log keeps its rows.</summary>
public partial class FluentMainWindow : FluentWindow
{
    readonly PageCache _pages;
    internal bool ReallyClose;

    internal FluentMainWindow(FluentContext cx)
    {
        _pages = new PageCache(cx);
        InitializeComponent();
        Nav.SetPageProviderService(_pages);
        Title = $"Network Auto Switch {AppInfo.Version}"; Bar.Title = Title;
        Loaded += (_, _) => { if (Nav.SelectedItem is null || _want is not null) Go(_want ?? "Dashboard"); };
    }

    /// <summary>Opens a page by name (Dashboard, Log, Policy, Settings).</summary>
    string? _want;      // the page asked for before the window finished loading
    public void Go(string page)
    {
        _want = IsLoaded ? null : page;
        Type t = page.ToLowerInvariant() switch { "log" => typeof(LogPage), "policy" => typeof(PolicyPage), "settings" => typeof(SettingsPage), _ => typeof(DashboardPage) };
        Nav.Navigate(t);
    }

    public T Page<T>() where T : class => (T)_pages.GetPage(typeof(T))!;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!ReallyClose) { e.Cancel = true; Hide(); return; }       // closing the window only hides it (the tray keeps running)
        base.OnClosing(e);
    }

    sealed class PageCache : INavigationViewPageProvider
    {
        readonly FluentContext _cx; readonly Dictionary<Type, object> _made = new();
        public PageCache(FluentContext cx) => _cx = cx;
        public object? GetPage(Type pageType)
        {
            if (_made.TryGetValue(pageType, out var p)) return p;
            return _made[pageType] = Activator.CreateInstance(pageType, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, new object[] { _cx }, null)!;
        }
    }
}
