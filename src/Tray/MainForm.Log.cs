using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

// Log page: everything the engine wrote, newest first, filtered by kind and by a search, in one monospace list.
sealed partial class MainForm
{
    // every row held in memory, newest first (the list on screen shows the ones that match the filter and the search).
    // Filtering works on this list, so it never reads the file again.
    readonly List<LogRow> _logAll = new();
    int _logFilter;
    string _logQuery = "";
    Segmented _logKinds = null!;
    readonly Label _logCount = new() { Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft };
    readonly System.Windows.Forms.Timer _logSearchTimer = new() { Interval = 120 };    // typing fast re-filters once, after a short pause
    static readonly string LogPath = Paths.LogFile;       // the getter creates the folder every call: read it once

    const int RowPad = 14, TimeW = 76, ChipW = 92, ColGap = 12;

    // ---------- Log ----------
    Control LogPage()
    {
        var page = new Panel { BackColor = Ui.Bg, Padding = new Padding(28, 0, 28, 20) };

        var header = new PageHeader("Log", "Everything the engine changed, in order.");
        header.AddAction(Ui.Btn("", "Copy", "Copy the selected lines (or every line in view when none is selected). Ctrl+C works too.", CopyLog));
        header.AddAction(Ui.Btn("", "Open Log File", "Open the log file in Notepad (newest first).", OpenLogNewestFirst));
        header.AddAction(Ui.Btn("", "Clear View", "Clear this view. The file is kept.", ClearLogView));

        // the filter on the left, the search on the right
        var bar = new Panel { Dock = DockStyle.Top, Height = 6 + 38 + 16, BackColor = Ui.Bg };
        _logKinds = new Segmented("All", "Switches", "Blocks", "Errors", "Commands") { Location = new Point(0, 6) };
        Ui.Tip.SetToolTip(_logKinds, "Show only one kind of line. Blocks includes unblocks.");
        _logKinds.SelectedChanged += i => { _logFilter = i; RefilterLog(); };
        var search = new TextBox { PlaceholderText = "Search the log…" };
        var field = SearchField(search);
        field.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        bar.Controls.Add(_logKinds); bar.Controls.Add(field);
        bar.Resize += (_, _) => field.Location = new Point(Math.Max(_logKinds.Right + 12, bar.Width - field.Width), 6 + (_logKinds.Height - field.Height) / 2);
        search.TextChanged += (_, _) => { _logSearchTimer.Stop(); _logSearchTimer.Start(); };
        search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { search.Clear(); e.SuppressKeyPress = true; } };
        _logSearchTimer.Tick += (_, _) => { _logSearchTimer.Stop(); var q = search.Text.Trim(); if (q != _logQuery) { _logQuery = q; RefilterLog(); } };

        // the list: one card without a header, the rows reach its edges
        var card = new Card("", "") { Dock = DockStyle.Fill, Margin = Padding.Empty };
        foreach (Control c in card.Controls) if (c != card.Body) c.Visible = false;     // the card's title row: this card has none
        card.Body.Padding = new Padding(0, 2, 0, 2); card.Body.Controls.Add(_log);
        _log.ItemHeight = 38; _log.HorizontalScrollbar = false; _log.Font = Ui.MonoFont(9.4f); _log.BackColor = Ui.CardBg;
        _log.DrawItem += DrawLogRow;
        _log.Resize += (_, _) => _log.Invalidate();     // the message ends in an ellipsis at the new width
        _log.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.C) { CopyLog(); e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.A) { _log.BeginUpdate(); for (int i = 0; i < _log.Items.Count; i++) _log.SetSelected(i, true); _log.EndUpdate(); e.SuppressKeyPress = true; }
        };

        // footer: how many lines are in view, where the file is, and when it rotates
        var foot = new Panel { Dock = DockStyle.Bottom, Height = 12 + 18, Padding = new Padding(2, 12, 2, 0), BackColor = Ui.Bg };
        var rotate = new Label { Dock = DockStyle.Right, AutoSize = true, Text = "Rotates at 1 MB", UseMnemonic = false, TextAlign = ContentAlignment.MiddleRight };
        Ui.Tip.SetToolTip(rotate, "At 1 MB the file is renamed to log.txt.old and a new one is started.");
        foreach (var l in new[] { _logCount, rotate }) { l.Font = Ui.UiFont(9f); l.ForeColor = Ui.Muted; l.BackColor = Ui.Bg; }
        foot.Controls.Add(_logCount); foot.Controls.Add(rotate);

        // docking: Fill first, then the edges; the last one added docks outermost
        page.Controls.Add(card); page.Controls.Add(foot); page.Controls.Add(bar); page.Controls.Add(header);
        UpdateLogCount();
        return page;
    }

    /// <summary>The kit Field without its label, so the box lines up with the segmented control beside it.</summary>
    static Control SearchField(TextBox box)
    {
        var f = new Field("", box) { Width = 260, Height = 36, BackColor = Ui.Bg, Margin = Padding.Empty };
        foreach (Control c in f.Controls) if (c is Label) c.Visible = false;
        return f;
    }

    /// <summary>All lines of the log file (or the last <paramref name="n"/>), oldest first. Opened shared because the tray is writing to it.</summary>
    static List<string> ReadLines(int n = int.MaxValue)
    {
        var lines = new Queue<string>();
        if (!File.Exists(LogPath)) return new();
        using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        for (string? l; (l = sr.ReadLine()) is not null;) { lines.Enqueue(l); if (lines.Count > n) lines.Dequeue(); }
        return lines.ToList();
    }

    /// <summary>Reads the end of the file once (at start): the view shows the NEWEST line at the top and the oldest at the bottom.</summary>
    void LoadLogTail()
    {
        _logAll.Clear();
        try
        {
            var lines = ReadLines(_maxLogRows);
            for (int i = lines.Count - 1; i >= 0; i--) if (lines[i].Length > 0) _logAll.Add(new LogRow(lines[i]));
        }
        catch (Exception e) { _logAll.Add(new LogRow("could not read log file: " + e.Message)); }
        RefilterLog();
    }

    /// <summary>Shows the rows that match the filter and the search: from memory, in one batch.</summary>
    void RefilterLog()
    {
        var shown = _logAll.Where(r => r.Matches(_logFilter, _logQuery)).ToArray();
        _log.BeginUpdate();
        _log.Items.Clear(); _log.Items.AddRange(shown);
        _log.EndUpdate();
        UpdateLogCount();
    }

    void UpdateLogCount() => _logCount.Text = $"Showing {_log.Items.Count:N0} of {_logAll.Count:N0} lines · {LogPath}";

    void ClearLogView() { _logAll.Clear(); _log.Items.Clear(); UpdateLogCount(); }

    /// <summary>The selected lines, or every line in view when none is selected, in the file's format.</summary>
    void CopyLog()
    {
        var rows = _log.SelectedItems.Count > 0 ? _log.SelectedItems.Cast<LogRow>() : _log.Items.Cast<LogRow>();
        var text = string.Join("\r\n", rows);
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); } catch { }      // the clipboard can be held by another program for a moment
    }

    /// <summary>The setting changed: remember it, and shorten what is on screen right away if it is now too long.</summary>
    void SetLogLines(int n)
    {
        n = Math.Clamp(n, 100, 20000);
        if (n == _maxLogRows) return;
        _maxLogRows = n; AppSettings.Update(x => x.LogLines = n); TrimLog();
    }

    /// <summary>Drops the OLDEST rows (the bottom of the list) beyond the limit set in Settings.</summary>
    void TrimLog()
    {
        if (_logAll.Count <= _maxLogRows) return;
        _logAll.RemoveRange(_maxLogRows, _logAll.Count - _maxLogRows);
        RefilterLog();
    }

    /// <summary>Called from any thread for every log line. New lines go on top; the view keeps its place if you scrolled down.</summary>
    public void AddLog(string line) => OnUi(() =>
    {
        var row = new LogRow($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}");   // same timestamp format as the file, so old and new lines line up
        _logAll.Insert(0, row);
        if (_logAll.Count > _maxLogRows)
        {
            // the oldest row goes; it is also the last row on screen when it matches the filter
            var old = _logAll[^1]; _logAll.RemoveAt(_logAll.Count - 1);
            int last = _log.Items.Count - 1;
            if (last >= 0 && ReferenceEquals(_log.Items[last], old)) _log.Items.RemoveAt(last);
        }
        if (row.Matches(_logFilter, _logQuery))
        {
            int top = _log.TopIndex;
            _log.Items.Insert(0, row);
            if (top > 0) _log.TopIndex = top + 1;
        }
        UpdateLogCount();
    });

    static Color RoleColor(LogRole r) => r switch
    {
        LogRole.Main => Ui.Green, LogRole.Backup => Ui.Amber, LogRole.Red => Ui.Red, LogRole.Blue => Ui.Blue, _ => Ui.Muted,
    };

    // a row: time (muted), the kind chip, the message (ends in an ellipsis), a line under it
    void DrawLogRow(object? sender, DrawItemEventArgs ev)
    {
        if (ev.Index < 0 || ev.Index >= _log.Items.Count) return;
        var row = (LogRow)_log.Items[ev.Index];
        var g = ev.Graphics; var r = ev.Bounds;
        bool selected = (ev.State & DrawItemState.Selected) != 0;

        g.FillRectangle(Ui.Brush(selected ? Ui.SelectedRow : Ui.CardBg), r);
        g.DrawLine(Ui.Pen(Ui.Border), r.X, r.Bottom - 1, r.Right, r.Bottom - 1);

        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        var font = Ui.MonoFont(9.4f);
        int x = r.X + RowPad, mid = r.Y + r.Height / 2;
        TextRenderer.DrawText(g, row.Clock, font, new Rectangle(x, r.Y, TimeW, r.Height - 1), Ui.Muted, flags);
        x += TimeW + ColGap;
        if (row.Label.Length > 0) Chip.Paint(g, row.Label, RoleColor(row.Role), x, mid - 1);
        x += ChipW + ColGap;
        TextRenderer.DrawText(g, row.Message, font, new Rectangle(x, r.Y, Math.Max(0, r.Right - RowPad - x), r.Height - 1), Ui.Text, flags | TextFormatFlags.EndEllipsis);
    }

    /// <summary>The file on disk stays oldest-first (the normal way log files are written); this opens a newest-first copy of it.</summary>
    static void OpenLogNewestFirst()
    {
        try
        {
            var all = ReadLines(); all.Reverse();
            string tmp = Path.Combine(Path.GetTempPath(), "NetworkAutoSwitch-log-newest-first.txt");
            File.WriteAllLines(tmp, all);
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{tmp}\"") { UseShellExecute = true });
        }
        catch { }
    }
}
