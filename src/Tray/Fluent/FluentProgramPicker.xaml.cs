using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetworkAutoSwitch.Core;
using Wpf.Ui.Controls;

namespace NetworkAutoSwitch.Tray.Fluent;

/// <summary>Pick programs from a list: first the ones that used data on the backup, then everything running now. Type to filter.
/// Opens at once; running programs and icons arrive in the background.</summary>
public partial class FluentProgramPicker : FluentWindow
{
    sealed class Item : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Name { get; init; } = ""; public string Path { get; init; } = ""; public bool Seen { get; init; } public long Bytes { get; init; }
        public string Group => Seen ? "Used Data On The Backup" : "Running Now";
        public string Label => Seen && Bytes > 0 ? $"{Name}   ({Fmt(Bytes)})" : Name;
        public string Location => System.IO.Path.GetDirectoryName(Path) ?? "";
        ImageSource? _icon; public ImageSource? Icon { get => _icon; set { _icon = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon))); } }
    }

    readonly List<Item> _all = new();
    readonly System.Collections.ObjectModel.ObservableCollection<Item> _shown = new();
    readonly CancellationTokenSource _cts = new();
    readonly bool _single;
    public List<string> Selected { get; } = new();

    internal FluentProgramPicker(IEnumerable<SeenProgram> seenOnBackup, bool single = false)
    {
        _single = single;
        InitializeComponent();
        Title = Bar.Title = single ? "Pick a Program" : "Pick Programs"; OkBtn.Content = single ? "Select" : "Add";
        Grid.SelectionMode = single ? System.Windows.Controls.DataGridSelectionMode.Single : System.Windows.Controls.DataGridSelectionMode.Extended;
        var view = CollectionViewSource.GetDefaultView(_shown); view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Item.Group)));
        Grid.ItemsSource = view;
        foreach (var s in seenOnBackup) _all.Add(new Item { Name = s.Name, Path = s.Path, Seen = true, Bytes = s.Bytes });
        Fill();
        Search.TextChanged += (_, _) => Fill();
        Grid.SelectionChanged += (_, _) => OkBtn.IsEnabled = Grid.SelectedItems.Count > 0;
        Grid.MouseDoubleClick += (_, _) => { if (Grid.SelectedItems.Count > 0) Accept(); };
        OkBtn.Click += (_, _) => Accept();
        BrowseBtn.Click += (_, _) => BrowseFile();
        Closed += (_, _) => _cts.Cancel();
        ContentRendered += (_, _) => StartBackgroundLoad();
    }

    static string Fmt(long b) => b >= 1_000_000_000 ? $"{b / 1e9:0.0} GB" : b >= 1_000_000 ? $"{b / 1e6:0.0} MB" : $"{b / 1e3:0.0} KB";

    void Fill()
    {
        string q = Search.Text.Trim();
        var keep = Grid.SelectedItems.Cast<Item>().Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);   // keep your selection when more programs arrive
        _shown.Clear();
        foreach (var a in _all.Where(a => q.Length == 0 || a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Path.Contains(q, StringComparison.OrdinalIgnoreCase)).OrderByDescending(a => a.Seen).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            _shown.Add(a);
        foreach (var it in _shown) if (keep.Contains(it.Path)) Grid.SelectedItems.Add(it);
    }

    void StartBackgroundLoad()
    {
        var token = _cts.Token; var known = new HashSet<string>(_all.Select(a => a.Path), StringComparer.OrdinalIgnoreCase);
        Task.Run(() =>
        {
            var found = ProcessPaths.Running(token).Where(known.Add).Select(p => new Item { Name = System.IO.Path.GetFileName(p), Path = p }).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (token.IsCancellationRequested) return;
            Dispatcher.BeginInvoke(() => { _all.AddRange(found); Fill(); });
            // icons last, so the list is usable long before they all arrive
            foreach (var item in _all.ToList().Concat(found).Distinct())
            {
                if (token.IsCancellationRequested) return;
                try
                {
                    using var ic = System.Drawing.Icon.ExtractAssociatedIcon(item.Path);
                    if (ic is null) continue;
                    var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); src.Freeze();
                    Dispatcher.BeginInvoke(() => item.Icon = src);
                }
                catch { }
            }
            Dispatcher.BeginInvoke(() => LoadingText.Visibility = Visibility.Collapsed);
        });
    }

    void BrowseFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Pick a Program", Multiselect = !_single };
        if (dlg.ShowDialog(this) != true) return;
        Selected.Clear(); Selected.AddRange(dlg.FileNames); DialogResult = true;
    }

    void Accept()
    {
        if (Grid.SelectedItems.Count == 0) return;
        Selected.Clear(); Selected.AddRange(Grid.SelectedItems.Cast<Item>().Select(i => i.Path));
        DialogResult = true;
    }
}
