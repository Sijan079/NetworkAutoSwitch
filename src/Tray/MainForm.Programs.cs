using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

// Programs page (nav key "activity"): the Network Activity and Blocked tables with their actions, laid out like the mockups
// (page header, then two cards). The Dashboard shows read-only copies of both tables: RefreshUsage / RefreshBlocked fill them too.
sealed partial class MainForm
{
    static readonly int[] GraceMinutes = { 10, 30, 60, 180 };
    static readonly string[] GraceLabels = { "10 Min", "30 Min", "1 Hour", "3 Hours" };

    // Blocked table columns. Subitems 0-2 (path, rule, "Blocked 3 min" / "Allowed, watched in 9:42") are what the Dashboard's
    // 3-column copy shows, so they stay as they were; 3 (time) and 4 (the row button's cell) are appended for this page only.
    const int BlockedColLocation = 1, BlockedColState = 3, BlockedColTime = 4;
    const int UsageColWatch = 4;
    TableHeader? _usageHeader;
    int _usageHover = -1, _blockedHover = -1;

    void OverrideSelected()
    {
        var names = _blockedList.SelectedItems.Cast<ListViewItem>().Where(i => i.Name == "blocked").Select(i => (string)i.Tag!).ToList();
        Allow(names);
    }

    /// <summary>Unblocks the programs for the grace time chosen in the dropdown.</summary>
    void Allow(List<string> names)
    {
        if (names.Count == 0) return;
        int minutes = GraceMinutes[Math.Max(0, _grace.SelectedIndex)];
        Task.Run(() => { foreach (var n in names) _engine.OverrideBlock(n, minutes); OnUi(RefreshBlocked); });   // runs PowerShell: keep off the UI thread
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowScrollBar(IntPtr hWnd, int bar, bool show);
    static bool DemoBlocked = Environment.GetCommandLineArgs().Contains("--demo-blocked");

    static string Ago(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} m" : $"{(int)t.TotalMinutes} min";
    static string Clock(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    /// <summary>One row of the Blocked table. The file name and folder are split here (every 2 s), not in the paint code.</summary>
    static ListViewItem BlockedRow(string kind, string path, string rule, string status, string time, string tag, bool selected)
    {
        var it = new ListViewItem(new[] { path, "", rule, status, time }) { Name = kind, Tag = tag, Selected = selected, ToolTipText = path };
        string file = Path.GetFileName(path), dir = Path.GetDirectoryName(path) ?? "";
        it.SubItems[0].Tag = file.Length > 0 ? file : path;
        it.SubItems[BlockedColLocation].Text = file.Length > 0 ? dir : "";
        return it;
    }

    void RefreshBlocked()
    {
        if (!Visible) return;             // nothing on screen: the window catches up when it is shown (see ActivityPage)
        var list = _engine.Blocked; var allowed = _engine.Allowed;
        var selected = _blockedList.SelectedItems.Cast<ListViewItem>().Select(i => i.Name + "|" + (string)i.Tag!).ToHashSet();
        _blockedList.BeginUpdate(); _blockedList.Items.Clear();
        foreach (var b in list)
        {
            var since = DateTime.UtcNow - b.Since; if (since < TimeSpan.Zero) since = TimeSpan.Zero;
            string ago = Ago(since);
            _blockedList.Items.Add(BlockedRow("blocked", b.Path, b.Rule, "Blocked " + ago, "for " + ago, b.DisplayName, selected.Contains("blocked|" + b.DisplayName)));
        }
        foreach (var a in allowed)
        {
            var left = a.Until - DateTime.UtcNow; if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            string t = Clock(left);
            _blockedList.Items.Add(BlockedRow("allowed", a.Path, a.Rule, "Allowed, watched in " + t, t + " left", a.DisplayName, selected.Contains("allowed|" + a.DisplayName)));
        }
        int shown = list.Count + allowed.Count, blocked = list.Count;
        if (DemoBlocked && !_engine.Live)      // dev only (--dry-run --demo-blocked): sample rows to see the card
        {
            _blockedList.Items.Add(BlockedRow("blocked", @"C:\Program Files (x86)\Steam\steam.exe", "Steam", "Blocked 3 min", "for 3 min", "demo-steam", selected.Contains("blocked|demo-steam")));
            _blockedList.Items.Add(BlockedRow("blocked", @"D:\Games\Endfield\Endfield.exe", "Endfield", "Blocked 12 min", "for 12 min", "demo-endfield", selected.Contains("blocked|demo-endfield")));
            _blockedList.Items.Add(BlockedRow("allowed", @"C:\Program Files\HoYoPlay\HYPWorker.exe", "HoYoPlay", "Allowed, watched in 9:42", "9:42 left", "demo-hyp", selected.Contains("allowed|demo-hyp")));
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
        BlockOrWatch(names, block);
    }

    void BlockOrWatch(List<string> names, bool block)
    {
        if (names.Count == 0) return;
        Task.Run(() => { foreach (var n in names) { if (block) _engine.BlockAgain(n); else _engine.WatchAgain(n); } OnUi(RefreshBlocked); });   // runs PowerShell: keep off the UI thread
    }

    /// <summary>What the sort arrow of a Network Activity column shows: 0 idle, 1 ascending, 2 descending.</summary>
    int UsageSortState(int column)
    {
        if (column < 0 || column >= UsageKeys.Length) return -1;
        if (_sortKey != UsageKeys[column]) return 0;
        bool desc = _sortKey != "program"; if (_sortFlip) desc = !desc;
        return desc ? 2 : 1;
    }

    /// <summary>A click on a column title sorts by it: a new column sorts by it, the same column again flips ascending and descending.</summary>
    void SortByColumn(int column)
    {
        if (column < 0 || column >= UsageKeys.Length) return;
        if (_sortKey == UsageKeys[column]) _sortFlip = !_sortFlip; else { _sortKey = UsageKeys[column]; _sortFlip = false; }
        Ui.RedrawHeader(_usage); _usageHeader?.Invalidate();
        RefreshUsage();
    }

    void RefreshUsage()
    {
        if (!Visible) return;             // the 2 s timer keeps ticking while the window is hidden; there is nothing to show then
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

    // ---------- the page ----------
    Control ActivityPage()
    {
        // the mockup's page: 28 px sides, the header (title, subtitle, actions) on top, the cards below it with 16 px between them
        var page = new Panel { BackColor = Ui.Bg, Padding = new Padding(28, 0, 28, 24) };
        var header = new PageHeader("Programs");

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Ui.Bg, Margin = new Padding(0), Padding = new Padding(0, 6, 0, 0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 56)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
        var usageCard = UsageCard(); usageCard.Margin = new Padding(0, 0, 0, 16);
        var blockedCard = BlockedCard(); blockedCard.Margin = new Padding(0);
        grid.Controls.Add(usageCard, 0, 0); grid.Controls.Add(blockedCard, 0, 1);

        page.Controls.Add(grid); page.Controls.Add(header);       // docking runs in reverse order: the header takes the top first
        // the tables are not refreshed while the window is hidden; catch up the moment it is shown
        VisibleChanged += (_, _) => { if (Visible) RefreshUsage(); };
        return page;
    }

    /// <summary>A muted note at the right of a card header (the mockup's "Today, per program").</summary>
    static Label ProgramsNote(string text) => new()
    {
        Text = text, AutoSize = true, Font = Ui.UiFont(9f), ForeColor = Ui.Muted, BackColor = Ui.CardBg, Margin = new Padding(10, 9, 4, 0), UseMnemonic = false,
    };

    Card UsageCard()
    {
        var card = new Card("", "Network Activity");
        card.SetHint("Total = what each program downloaded plus uploaded today, on the main connection and the backup (kept across restarts, starts again at midnight). Hover a row for the split. Click a column title to sort; click it again to reverse.\nOn Backup = how much of that went through your backup (your phone's data).\nTrigger = the most it downloaded in any 10 seconds today. It is measured the same way as your watch trigger (set in Settings): amber means it went above it, so a watched program like this is blocked on the backup. Set a rule's trigger between a program's quiet value and its download value.\nWatch List = the program is covered by a backup rule: the watchdog is watching it.");
        // the actions live in the card's header, right of the title (the first one added is the rightmost)
        card.AddHeaderAction(Ui.Refresh, "Reset", "Reset the counters.", () => { _engine.Usage.Reset(); RefreshUsage(); });
        _addWatchBtn = card.AddHeaderAction(Ui.Eye, "Add To Watch List", "Make the selected programs a watch rule (saved and applied at once).", AddWatchFromUsage, BtnKind.Primary);

        ProgramsTable.Prepare(_usage);
        _usage.ShowItemToolTips = true; _usage.MultiSelect = true;
        _usage.Columns.Add("Program", 200); _usage.Columns.Add("Total", 100, HorizontalAlignment.Right); _usage.Columns.Add("On Backup", 110, HorizontalAlignment.Right);
        _usage.Columns.Add("Trigger", 100, HorizontalAlignment.Right); _usage.Columns.Add("Watch List", 124);
        FitFirstColumn(_usage);          // the numbers keep their width; the program name takes what is left, so nothing scrolls sideways
        _usage.DrawSubItem += (_, e) => DrawUsageCell(e);
        ProgramsTable.TrackHover(_usage, () => _usageHover, v => _usageHover = v);
        _usageHeader = new TableHeader(_usage, UsageSortState, SortByColumn);
        _addWatchBtn.Enabled = false;                                   // like Save: dull until there is something to act on
        _usage.SelectedIndexChanged += (_, _) => _addWatchBtn.Enabled = _usage.SelectedItems.Count > 0;

        // the table's left edge lines up with the card title; the 6 px leave room for the row tint around the text
        card.Body.Padding = new Padding(6, 0, 6, 10);
        card.Body.Controls.Add(_usage); card.Body.Controls.Add(_usageHeader);
        return card;
    }

    void DrawUsageCell(DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null || e.SubItem is null) return;
        var g = e.Graphics; var cell = ProgramsTable.Cell(_usage, e);
        ProgramsTable.Background(g, cell, e.Item.Selected, e.ItemIndex == _usageHover, e.ColumnIndex < _usage.Columns.Count - 1);
        var area = ProgramsTable.TextArea(cell, e.ColumnIndex);
        if (e.ColumnIndex == UsageColWatch)
        {
            if (e.SubItem.Text.Length > 0) Chip.Paint(g, "Watching", Ui.Muted, area.Left, cell.Top + (cell.Height - 1) / 2);
            return;
        }
        bool right = _usage.Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right;
        ProgramsTable.Text(g, e.SubItem.Text, area, e.ColumnIndex == 0 ? Ui.Text : e.SubItem.ForeColor, right);
    }

    Card BlockedCard()
    {
        _blockedCard.SetIcon("");
        _blockedCard.SetHint("Programs the watchdog stopped because they downloaded too fast on a protected connection (State: Blocked). Blocks never expire by themselves: you decide.\nPrograms you allowed stay here with a countdown (State: Allowed): the watchdog leaves them alone until it ends.\nAllow, Block Now and Watch act on the selected rows; the button in a row acts on that row only.");

        ProgramsTable.Prepare(_blockedList);
        _blockedList.ShowItemToolTips = true;
        _blockedList.Columns.Add("Program", 200); _blockedList.Columns.Add("Location", 220); _blockedList.Columns.Add("Rule", 116); _blockedList.Columns.Add("State", 96);
        _blockedList.Columns.Add("Time", 96);
        FitFirstColumn(_blockedList);
        _blockedList.DrawSubItem += (_, e) => DrawBlockedCell(e);
        ProgramsTable.TrackHover(_blockedList, () => _blockedHover, v => _blockedHover = v);
        _blockedList.SelectedIndexChanged += (_, _) => UpdateBlockedButtons();
        var header = new TableHeader(_blockedList);

        // the action bar above the table: the grace time on the left; Watch (allowed rows only) and the one action button on the right
        foreach (var g in GraceLabels) _grace.Items.Add(g);
        _grace.SelectedIndex = 0;
        _grace.ItemHeight = 30; _grace.Width = 112; _grace.Margin = new Padding(0, 0, 8, 0);       // 36 px tall, like the buttons next to it
        Ui.Tip.SetToolTip(_grace, "How long to leave a program alone after you allow it. Then the watchdog watches it again.");
        _unblockBtn = Ui.Btn(Ui.Unlock, "Allow", "", () => { if (_blockedList.SelectedItems.Cast<ListViewItem>().Any(i => i.Name == "allowed")) AllowedAction(block: true); else OverrideSelected(); }, BtnKind.Primary);   // one button: Allow on a blocked row, Block Now on an allowed one (see UpdateBlockedButtons)
        _watchAgainBtn = Ui.Btn(Ui.Eye, "Watch", "End the allowed time of the selected programs now. They are blocked only if they download too fast.", () => AllowedAction(block: false));
        var left = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = Ui.CardBg, Padding = new Padding(0), Margin = new Padding(0) };
        left.Controls.Add(_grace);
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = Ui.CardBg, Padding = new Padding(0), Margin = new Padding(0) };
        right.Controls.Add(_watchAgainBtn); right.Controls.Add(_unblockBtn);       // far right: Watch (allowed rows only) beside the one action button
        _watchAgainBtn.Margin = new Padding(0, 0, 8, 0); _unblockBtn.Margin = new Padding(0);
        var bar = new Panel { Dock = DockStyle.Top, Height = 36 + 12, BackColor = Ui.CardBg, Padding = new Padding(ProgramsTable.CellPad, 0, ProgramsTable.CellPad, 12) };   // above the table the buttons act on
        bar.Controls.Add(left); bar.Controls.Add(right);
        _blockedSide = left;

        _blockedEmpty.Text = "Nothing is blocked. Programs the watchdog stops show up here.";
        _blockedEmpty.TextAlign = ContentAlignment.MiddleCenter; _blockedEmpty.Font = Ui.UiFont(9.75f); _blockedEmpty.Padding = new Padding(0);

        _blockedCard.Body.Padding = new Padding(6, 0, 6, 12);
        _blockedCard.Body.Controls.Add(_blockedList); _blockedCard.Body.Controls.Add(_blockedEmpty); _blockedCard.Body.Controls.Add(header); _blockedCard.Body.Controls.Add(bar);   // docks in reverse: the bar is on top, then the column header
        ShowBlockedList(0);
        return _blockedCard;
    }

    void DrawBlockedCell(DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null || e.SubItem is null) return;
        var g = e.Graphics; var cell = ProgramsTable.Cell(_blockedList, e);
        ProgramsTable.Background(g, cell, e.Item.Selected, e.ItemIndex == _blockedHover, e.ColumnIndex < _blockedList.Columns.Count - 1);
        var area = ProgramsTable.TextArea(cell, e.ColumnIndex);
        bool isBlocked = e.Item.Name == "blocked";
        int mid = cell.Top + (cell.Height - 1) / 2;
        switch (e.ColumnIndex)
        {
            case 0:
                ProgramsTable.Text(g, e.SubItem.Tag as string ?? e.SubItem.Text, area, Ui.Text, extra: TextFormatFlags.PathEllipsis);       // the file name; the folder has its own column
                break;
            case BlockedColLocation:
                ProgramsTable.Text(g, e.SubItem.Text, area, Ui.Muted, extra: TextFormatFlags.PathEllipsis);
                break;
            case BlockedColState:
                Chip.Paint(g, isBlocked ? "Blocked" : "Allowed", isBlocked ? Ui.Red : Ui.Blue, area.Left, mid);
                break;
            case BlockedColTime:
                ProgramsTable.Text(g, e.SubItem.Text, area, isBlocked ? Ui.Muted : Ui.Text);
                break;
            default:
                ProgramsTable.Text(g, e.SubItem.Text, area, Ui.Text);
                break;
        }
    }

    /// <summary>The first column (the program) takes what the other columns leave, so the table never scrolls sideways.</summary>
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

    /// <summary>The number columns keep their size, the program name gives way first in a narrow window, and the last column takes what is left,
    /// so the header never shows an empty (white) strip when a divider is dragged and there is no horizontal bar. (Used by the Dashboard's copy.)</summary>
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
