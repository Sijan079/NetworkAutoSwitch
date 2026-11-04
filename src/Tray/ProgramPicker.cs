using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

/// <summary>Pick programs from a list instead of browsing folders: first the ones that actually used data on the backup,
/// then everything running right now. Type to filter. "Browse Files" is still there as a fallback.
/// The window opens at once with the backup programs; the running programs and the icons arrive in the background.</summary>
sealed class ProgramPicker : Form
{
    sealed record Item(string Name, string Path, bool SeenOnBackup, long Bytes);

    readonly ListView _list = new();
    readonly TextBox _search = new() { Font = Ui.UiFont(10.5f), PlaceholderText = "Search Programs", Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
    readonly Label _loading = new() { Text = "Loading...", AutoSize = true, ForeColor = Ui.Muted, Dock = DockStyle.Right, Padding = new Padding(10, 6, 0, 0), BackColor = Ui.Bg };
    readonly ImageList _icons = new() { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
    readonly Dictionary<string, int> _iconIndex = new(StringComparer.OrdinalIgnoreCase);   // path -> index in _icons (-1 = none)
    readonly List<Item> _all = new();
    readonly CancellationTokenSource _cts = new();
    readonly bool _single;
    readonly Button _ok;
    bool _running = true;    // the background load is still going

    public List<string> Selected { get; } = new();
    public bool StillLoading() => _running;

    public ProgramPicker(IEnumerable<SeenProgram> seenOnBackup, bool single = false)
    {
        _single = single;
        Text = single ? "Pick a Program" : "Pick Programs"; Icon = Ui.AppIcon(Ui.Blue);
        Size = new Size(780, 620); MinimumSize = new Size(600, 440); StartPosition = FormStartPosition.CenterParent;
        BackColor = Ui.Bg; Font = Ui.UiFont(9.5f); ShowInTaskbar = false; MinimizeBox = false;

        var searchBar = new Panel { Dock = DockStyle.Top, Height = 56, Padding = new Padding(Ui.Pad, Ui.Gap, Ui.Pad, 6), BackColor = Ui.Bg };
        searchBar.Controls.Add(_search); searchBar.Controls.Add(_loading);

        Ui.StyleList(_list);
        _list.MultiSelect = !single; _list.SmallImageList = _icons; _list.ShowGroups = true; _list.HideSelection = false;
        _list.Columns.Add("Program", 250); _list.Columns.Add("Location", 440);
        var listCard = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Ui.Pad, 6, Ui.Pad, 6), BackColor = Ui.Bg };
        _list.Dock = DockStyle.Fill; listCard.Controls.Add(_list);

        _ok = Ui.Btn(Ui.Check, single ? "Select" : "Add", "Use the selected programs.", Accept, BtnKind.Primary); _ok.Enabled = false;
        var cancel = Ui.Btn(Ui.Clear, "Cancel", "Close without adding anything.", () => DialogResult = DialogResult.Cancel);
        var browse = Ui.Btn(Ui.Folder, "Browse Files", "Pick an .exe from a folder instead.", BrowseFile);
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, BackColor = Ui.Bg, WrapContents = false };
        right.Controls.Add(_ok); right.Controls.Add(cancel);
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(Ui.Pad, Ui.Gap, Ui.Pad, Ui.Gap), BackColor = Ui.Bg };
        var left = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, BackColor = Ui.Bg, WrapContents = false };
        left.Controls.Add(browse);
        bar.Controls.Add(right); bar.Controls.Add(left);

        Controls.Add(listCard); Controls.Add(searchBar); Controls.Add(bar);
        AcceptButton = _ok; CancelButton = cancel;
        Load += (_, _) => Ui.SkinForm(this);
        FormClosed += (_, _) => _cts.Cancel();

        _search.TextChanged += (_, _) => Fill();
        _list.SelectedIndexChanged += (_, _) => _ok.Enabled = _list.SelectedItems.Count > 0;
        _list.DoubleClick += (_, _) => { if (_list.SelectedItems.Count > 0) Accept(); };

        // 1) the window opens right away with what is already known (no waiting)
        foreach (var s in seenOnBackup) _all.Add(new Item(s.Name, s.Path, true, s.Bytes));
        Fill();
        Shown += (_, _) => StartBackgroundLoad();
    }

    // ---------- background loading: running programs first, then their icons ----------
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

    /// <summary>Process.MainModule is slow (it walks every loaded DLL). This asks Windows for just the file path, which takes microseconds.</summary>
    static string? PathOf(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try { var sb = new StringBuilder(1024); int n = sb.Capacity; return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString(0, n) : null; }
        finally { CloseHandle(h); }
    }

    void StartBackgroundLoad()
    {
        var token = _cts.Token;
        var known = new HashSet<string>(_all.Select(a => a.Path), StringComparer.OrdinalIgnoreCase);
        Task.Run(() =>
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            int me = Environment.ProcessId;
            var found = new List<Item>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (token.IsCancellationRequested) return;
                    try
                    {
                        if (p.Id == me) continue;
                        string? path = PathOf(p.Id);
                        if (string.IsNullOrEmpty(path) || path.StartsWith(win, StringComparison.OrdinalIgnoreCase) || !known.Add(path)) continue;
                        found.Add(new Item(System.IO.Path.GetFileName(path), path, false, 0));
                    }
                    catch { /* not readable: not interesting */ }
                }
            }
            found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            OnUi(() => { _all.AddRange(found); Fill(); });

            // icons last, a few at a time, so the list is usable (and searchable) long before they all arrive
            var batch = new List<(string Path, Bitmap? Bmp)>();
            foreach (var path in _all.Select(a => a.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                if (token.IsCancellationRequested) return;
                Bitmap? bmp = null;
                try { using var ic = Icon.ExtractAssociatedIcon(path); bmp = ic?.ToBitmap(); } catch { }
                batch.Add((path, bmp));
                if (batch.Count >= 12) { var send = batch.ToList(); batch.Clear(); OnUi(() => ApplyIcons(send)); }
            }
            OnUi(() => { ApplyIcons(batch.ToList()); _running = false; _loading.Visible = false; });
        });
    }

    void OnUi(Action a) { if (!IsDisposed && IsHandleCreated) try { BeginInvoke(a); } catch (ObjectDisposedException) { } }

    void ApplyIcons(List<(string Path, Bitmap? Bmp)> batch)
    {
        foreach (var (path, bmp) in batch)
        {
            if (bmp is not null) { _icons.Images.Add(path, bmp); _iconIndex[path] = _icons.Images.IndexOfKey(path); } else _iconIndex[path] = -1;
        }
        _list.BeginUpdate();
        foreach (ListViewItem it in _list.Items) if (_iconIndex.TryGetValue((string)it.Tag!, out int i)) it.ImageIndex = i;
        _list.EndUpdate();
    }

    void Fill()
    {
        string q = _search.Text.Trim();
        var keep = _list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToHashSet(StringComparer.OrdinalIgnoreCase);   // keep your selection when more programs arrive
        var shown = _all.Where(a => q.Length == 0 || a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Path.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        var gSeen = new ListViewGroup("Used Data On The Backup") { HeaderAlignment = HorizontalAlignment.Left };
        var gRun = new ListViewGroup("Running Now") { HeaderAlignment = HorizontalAlignment.Left };
        _list.BeginUpdate(); _list.Items.Clear(); _list.Groups.Clear(); _list.Groups.Add(gSeen); _list.Groups.Add(gRun);
        foreach (var a in shown)
        {
            string loc = System.IO.Path.GetDirectoryName(a.Path) ?? "";
            string label = a.SeenOnBackup && a.Bytes > 0 ? $"{a.Name}   ({Fmt(a.Bytes)})" : a.Name;
            _list.Items.Add(new ListViewItem(new[] { label, loc })
            {
                Tag = a.Path, Group = a.SeenOnBackup ? gSeen : gRun, Selected = keep.Contains(a.Path),
                ImageIndex = _iconIndex.TryGetValue(a.Path, out int i) ? i : -1,
            });
        }
        _list.EndUpdate();
    }

    static string Fmt(long b) => b >= 1_000_000_000 ? $"{b / 1e9:0.0} GB" : b >= 1_000_000 ? $"{b / 1e6:0.0} MB" : $"{b / 1e3:0.0} KB";

    void BrowseFile()
    {
        using var dlg = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Pick a Program", Multiselect = !_single };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Selected.Clear(); Selected.AddRange(dlg.FileNames); DialogResult = DialogResult.OK;
    }

    void Accept()
    {
        if (_list.SelectedItems.Count == 0) return;
        Selected.Clear(); Selected.AddRange(_list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!));
        DialogResult = DialogResult.OK;
    }
}
