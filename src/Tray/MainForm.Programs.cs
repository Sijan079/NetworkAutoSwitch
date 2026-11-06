using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

// Programs page (was Activity): the Network Activity and Blocked tables with their actions.
sealed partial class MainForm
{
    static readonly int[] GraceMinutes = { 10, 30, 60, 180 };

    void OverrideSelected(bool all)
    {
        var names = (all ? _blockedList.Items.Cast<ListViewItem>() : _blockedList.SelectedItems.Cast<ListViewItem>()).Where(i => i.Name == "blocked").Select(i => (string)i.Tag!).ToList();
        if (names.Count == 0) return;
        int minutes = GraceMinutes[Math.Max(0, _grace.SelectedIndex)];
        Task.Run(() => { foreach (var n in names) _engine.OverrideBlock(n, minutes); OnUi(RefreshBlocked); });   // runs PowerShell: keep off the UI thread
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowScrollBar(IntPtr hWnd, int bar, bool show);
    static bool DemoBlocked = Environment.GetCommandLineArgs().Contains("--demo-blocked");

    void RefreshBlocked()
    {
        var list = _engine.Blocked; var allowed = _engine.Allowed;
        var selected = _blockedList.SelectedItems.Cast<ListViewItem>().Select(i => i.Name + "|" + (string)i.Tag!).ToHashSet();
        _blockedList.BeginUpdate(); _blockedList.Items.Clear();
        foreach (var b in list)
        {
            var since = DateTime.UtcNow - b.Since; if (since < TimeSpan.Zero) since = TimeSpan.Zero;
            string ago = since.TotalHours >= 1 ? $"{(int)since.TotalHours} h {since.Minutes} m" : $"{(int)since.TotalMinutes} min";
            _blockedList.Items.Add(new ListViewItem(new[] { b.Path, b.Rule, "Blocked " + ago }) { Name = "blocked", Tag = b.DisplayName, Selected = selected.Contains("blocked|" + b.DisplayName) });
        }
        foreach (var a in allowed)
        {
            var left = a.Until - DateTime.UtcNow; if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            string t = left.TotalHours >= 1 ? $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}" : $"{(int)left.TotalMinutes}:{left.Seconds:00}";
            _blockedList.Items.Add(new ListViewItem(new[] { a.Path, a.Rule, "Allowed, watched in " + t }) { Name = "allowed", Tag = a.DisplayName, Selected = selected.Contains("allowed|" + a.DisplayName) });
        }
        int shown = list.Count + allowed.Count, blocked = list.Count;
        if (DemoBlocked && !_engine.Live)      // dev only (--dry-run --demo-blocked): sample rows to see the card
        {
            _blockedList.Items.Add(new ListViewItem(new[] { @"C:\Program Files (x86)\Steam\steam.exe", "Steam", "Blocked 3 min" }) { Name = "blocked", Tag = "demo-steam" });
            _blockedList.Items.Add(new ListViewItem(new[] { @"D:\Games\Endfield\Endfield.exe", "Endfield", "Blocked 12 min" }) { Name = "blocked", Tag = "demo-endfield" });
            _blockedList.Items.Add(new ListViewItem(new[] { @"C:\Program Files\HoYoPlay\HYPWorker.exe", "HoYoPlay", "Allowed, watched in 9:42" }) { Name = "allowed", Tag = "demo-hyp" });
            shown += 3; blocked += 2;
        }
        _blockedList.EndUpdate();
        _blockedView.BeginUpdate(); _blockedView.Items.Clear();
        foreach (ListViewItem it in _blockedList.Items) { var c = (ListViewItem)it.Clone(); c.Selected = false; _blockedView.Items.Add(c); }
        _blockedView.EndUpdate();
        _blockedCard.SetTitle(blocked > 0 ? $"Blocked ({blocked})" : "Blocked"); _blockedViewCard.SetTitle(blocked > 0 ? $"Blocked ({blocked})" : "Blocked");
        ShowBlockedList(shown);
    }

    void AllowedAction(bool block)
    {
        var names = _blockedList.SelectedItems.Cast<ListViewItem>().Where(i => i.Name == "allowed").Select(i => (string)i.Tag!).ToList();
        if (names.Count == 0) return;
        Task.Run(() => { foreach (var n in names) { if (block) _engine.BlockAgain(n); else _engine.WatchAgain(n); } OnUi(RefreshBlocked); });
    }

    /// <summary>What the sort button at the end of a Network Activity column header shows: 0 idle, 1 ascending, 2 descending.</summary>
    int UsageSortState(int column)
    {
        if (column < 0 || column >= UsageKeys.Length) return -1;
        if (_sortKey != UsageKeys[column]) return 0;
        bool desc = _sortKey != "program"; if (_sortFlip) desc = !desc;
        return desc ? 2 : 1;
    }

    /// <summary>A click on a column header presses its sort button: a new column sorts by it, the same column again flips ascending and descending.</summary>
    void SortByColumn(int column)
    {
        if (column < 0 || column >= UsageKeys.Length) return;
        if (_sortKey == UsageKeys[column]) _sortFlip = !_sortFlip; else { _sortKey = UsageKeys[column]; _sortFlip = false; }
        Ui.RedrawHeader(_usage);
        RefreshUsage();
    }

    void RefreshUsage()
    {
        RefreshBlocked();
        var top = UsageSort.Apply(_engine.Usage.Rows(200), _sortKey, _sortFlip, r => _engine.IsWatched(_engine.Usage.PathFor(r.Name) ?? r.Name));
        long triggerBytes = (long)(_engine.TriggerMb * 1_000_000);
        // this table is rebuilt every 2 s, so remember what is selected (by program name) and select it again
        var selected = _usage.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _usage.BeginUpdate(); _usage.Items.Clear();
        foreach (var r in top)
        {
            string path = _engine.Usage.PathFor(r.Name) ?? r.Name;
            bool watched = _engine.IsWatched(path);
            var item = new ListViewItem(new[] { r.Name, Fmt(r.Total), r.BackupBytes > 0 ? Fmt(r.BackupBytes) : "", $"{r.PeakDown / 1e6:0.0} MB", watched ? "Watched" : "" })
            { Tag = r.Name, Selected = selected.Contains(r.Name), UseItemStyleForSubItems = false,
              ToolTipText = $"{r.Name}\nDown {Fmt(r.Down)}  \u00b7  Up {Fmt(r.Up)}" + (r.On.Length > 0 ? $"\nSeen on: {r.On}" : "") };
            item.SubItems[2].ForeColor = Ui.Amber;                                                 // data that went through your phone stands out
            item.SubItems[3].ForeColor = r.PeakDown >= triggerBytes ? Ui.Amber : Ui.Text;           // above the watch trigger: this is what would get a watched program blocked
            item.SubItems[4].ForeColor = Ui.Green;
            _usage.Items.Add(item);
        }
        _usage.EndUpdate();
        _usageView.BeginUpdate(); _usageView.Items.Clear();
        foreach (ListViewItem it in _usage.Items) { var c = (ListViewItem)it.Clone(); c.Selected = false; _usageView.Items.Add(c); }
        _usageView.EndUpdate();
    }

    /// <summary>One click from the "Network Activity" table: the selected programs become a watch rule, saved and applied at once.</summary>
    void AddWatchFromUsage()
    {
        var paths = _usage.SelectedItems.Cast<ListViewItem>().Select(i => _engine.Usage.PathFor((string)i.Tag!)).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
        if (paths.Count == 0) { Ui.Tip.Show("Select a program in the list first.", _usage, 10, 10, 2500); return; }
        ShowWindow("Policy");
        if (_editor.AddPrograms(paths) > 0) SavePolicy();   // validates, saves and applies right away
    }

    static string Fmt(long b) => b >= 1_000_000_000 ? $"{b / 1e9:0.00} GB" : b >= 1_000_000 ? $"{b / 1e6:0.0} MB" : $"{b / 1e3:0.0} KB";

    // ---------- Activity: the full tables with their actions ----------
    Control ActivityPage()
    {
        var page = Page();
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Ui.Bg };
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 58)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        var usageCard = new Card(Ui.Download, "Network Activity");
        usageCard.SetHint("Total = what each program downloaded plus uploaded today, on the main connection and the backup (kept across restarts, starts again at midnight). Hover a row for the split. Click a column header, or its small button, to sort; click again to reverse.\nOn Backup = how much of that went through your backup (your phone's data).\nTrigger = the most it downloaded in any 10 seconds today. It is measured the same way as your watch trigger (set in Settings): amber means it went above it, so a watched program like this is blocked on the backup. Set a rule's trigger between a program's quiet value and its download value.");
        Ui.StyleList(_usage, UsageSortState);
        _usage.ColumnClick += (_, e) => SortByColumn(e.Column);
        _usage.ShowItemToolTips = true;
        _usage.Columns.Add("Program", 196); _usage.Columns.Add("Total", 104, HorizontalAlignment.Right); _usage.Columns.Add("On Backup", 140, HorizontalAlignment.Right);
        _usage.Columns.Add("Trigger", 108, HorizontalAlignment.Right); _usage.Columns.Add("Watch List", 112);
        // the number columns keep their size; the program name gives way first in a narrow window, and Watch List takes what is left (so nothing is pushed out of view)
        FitUsageColumns(_usage);
        // the actions live in the card's header, right of the title (the first one added is the rightmost)
        usageCard.AddHeaderAction(Ui.Refresh, "Reset", "Reset the counters.", () => { _engine.Usage.Reset(); RefreshUsage(); });
        usageCard.AddHeaderAction(Ui.Eye, "Add To Watch List", "Make the selected programs a watch rule (saved and applied at once).", AddWatchFromUsage, BtnKind.Primary);
        usageCard.Body.Controls.Add(_usage);

        // one table for what is blocked and what you let through for now (Status says which)
        Ui.StyleList(_blockedList);
        _blockedList.Columns.Add("Program", 330); _blockedList.Columns.Add("Rule", 120); _blockedList.Columns.Add("Status", 170);
        foreach (var g in new[] { "10 Min", "30 Min", "1 Hour", "3 Hours" }) _grace.Items.Add(g);
        _grace.SelectedIndex = 0;
        Ui.Tip.SetToolTip(_grace, "How long to leave the program alone after you unblock it.");
        _unblockBtn = Ui.Btn(Ui.Unlock, "Unblock", "Unblock the selected blocked programs. The watchdog leaves them alone for the time on the left.\nBlocks never expire by themselves; you decide.", () => OverrideSelected(all: false), BtnKind.Success);
        _unblockAllBtn = Ui.Btn(Ui.Unlock, "Unblock All", "Unblock everything that is blocked.", () => OverrideSelected(all: true));
        _blockNowBtn = Ui.Btn(Ui.Lock, "Block", "Block the selected allowed programs again immediately.", () => AllowedAction(block: true), BtnKind.Danger);
        _watchAgainBtn = Ui.Btn(Ui.Eye, "Watch", "End the allowed time of the selected programs now. They are blocked only if they download too fast.", () => AllowedAction(block: false));
        foreach (var bt in new[] { _unblockBtn, _unblockAllBtn, _blockNowBtn, _watchAgainBtn }) { bt.AutoSize = true; bt.Margin = new Padding(0, 0, 6, 0); }
        _grace.Width = 112; _grace.Margin = new Padding(0, 2, 6, 0);
        var side = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Ui.CardBg, Padding = new Padding(0, 8, 0, 0) };
        side.Controls.Add(_grace); side.Controls.Add(_unblockBtn); side.Controls.Add(_unblockAllBtn); side.Controls.Add(_blockNowBtn); side.Controls.Add(_watchAgainBtn);
        _blockedSide = side;
        _blockedList.SelectedIndexChanged += (_, _) => UpdateBlockedButtons();
        FitFirstColumn(_blockedList);
        _blockedCard.Body.Controls.Add(_blockedList); _blockedCard.Body.Controls.Add(_blockedEmpty); _blockedCard.Body.Controls.Add(side);
        ShowBlockedList(0);
        _blockedCard.SetHint("Programs the watchdog stopped because they downloaded too fast on a protected connection (Status: Blocked). Blocks never expire by themselves: you decide.\nPrograms you unblocked stay here with a countdown (Status: Allowed): the watchdog leaves them alone until it ends. Block (again) or Watch act on those.");

        grid.Controls.Add(usageCard, 0, 0); grid.Controls.Add(_blockedCard, 0, 1);
        page.Controls.Add(grid);
        return page;
    }

    /// <summary>The number columns keep their size, the program name gives way first in a narrow window, and the last column takes what is left,
    /// so the header never shows an empty (white) strip when a divider is dragged and there is no horizontal bar.</summary>
    /// <summary>The first column (the program path) takes what the other columns leave, so the table never scrolls sideways.</summary>
    static void FitFirstColumn(ListView lv)
    {
        void Fit()
        {
            int w = lv.ClientSize.Width; if (w < 200) return;
            int others = 0; for (int i = 1; i < lv.Columns.Count; i++) others += lv.Columns[i].Width;
            int first = Math.Max(120, w - others);
            if (lv.Columns[0].Width != first) lv.Columns[0].Width = first;
            ShowScrollBar(lv.Handle, 0, false);
        }
        lv.Resize += (_, _) => Fit();
    }

    static void FitUsageColumns(ListView lv)
    {
        bool fitting = false;
        void FillLast()
        {
            int w = lv.ClientSize.Width; if (w < 300 || fitting) return;
            fitting = true;
            try
            {
                int others = 0; for (int i = 0; i < 4; i++) others += lv.Columns[i].Width;
                int last = Math.Max(110, w - others);
                if (lv.Columns[4].Width != last) lv.Columns[4].Width = last;
                ShowScrollBar(lv.Handle, 0, false);      // an exact fit can make Windows add a horizontal bar: never wanted here
            }
            finally { fitting = false; }
        }
        lv.Resize += (_, _) =>
        {
            int w = lv.ClientSize.Width; if (w < 300) return;
            int numbers = lv.Columns[1].Width + lv.Columns[2].Width + lv.Columns[3].Width;
            fitting = true; lv.Columns[0].Width = Math.Max(100, w - numbers - 110); fitting = false;
            FillLast();
        };
        lv.ColumnWidthChanged += (_, _) => FillLast();
    }

}
