using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

// Log page: the live, colour-tagged log.
sealed partial class MainForm
{
    // ---------- Log ----------
    Control LogPage()
    {
        var page = Page();
        var card = new Card(Ui.Document, "Log") { Margin = new Padding(0, 0, Ui.Gap, Ui.Gap) };
        card.SetHint("Newest line first. Select rows and press Ctrl+C to copy.");
        card.AddHeaderAction(Ui.OpenFile, "Open File", "Open the log file in Notepad (newest first).", OpenLogNewestFirst);
        card.AddHeaderAction(Ui.Refresh, "Reload", "Reload the history from the file.", () => LoadLogTail());
        card.AddHeaderAction(Ui.Clear, "Clear", "Clear this view (the file is kept).", () => _log.Items.Clear());
        card.Body.Padding = new Padding(Ui.Gap - 4, 0, Ui.Gap - 4, Ui.Gap - 4); card.Body.Controls.Add(_log);
        _log.DrawItem += DrawLogRow;
        _log.KeyDown += (_, e) =>   // Ctrl+C copies the selected rows as plain text
        {
            if (e.Control && e.KeyCode == Keys.C && _log.SelectedItems.Count > 0)
                Clipboard.SetText(string.Join("\r\n", _log.SelectedItems.Cast<LogEntry>().Select(x => $"{x.Time}  {x.Message}")));
        };
        page.Controls.Add(card);
        return page;
    }

    /// <summary>All lines of the log file (or the last <paramref name="n"/>), oldest first. Opened shared because the tray is writing to it.</summary>
    static List<string> ReadLines(int n = int.MaxValue)
    {
        var lines = new Queue<string>();
        if (!File.Exists(Paths.LogFile)) return new();
        using var fs = new FileStream(Paths.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        for (string? l; (l = sr.ReadLine()) is not null;) { lines.Enqueue(l); if (lines.Count > n) lines.Dequeue(); }
        return lines.ToList();
    }

    static LogEntry ToEntry(string line)
    {
        var (time, msg) = LogClassifier.Split(line);
        return new LogEntry(time, msg, LogClassifier.Classify(msg));
    }

    /// <summary>The log view shows the NEWEST line at the top and the oldest at the bottom.</summary>
    void LoadLogTail()
    {
        try
        {
            var lines = ReadLines(Math.Min(300, _maxLogRows)); lines.Reverse();
            _log.BeginUpdate(); _log.Items.Clear();
            foreach (var l in lines) { var e = ToEntry(l); _log.Items.Add(e); GrowWidth(e); }
            _log.EndUpdate();
        }
        catch (Exception e) { _log.Items.Clear(); _log.Items.Add(new LogEntry("", "could not read log file: " + e.Message, LogKind.Error)); }
    }

    /// <summary>The setting changed: remember it, and shorten what is on screen right away if it is now too long.</summary>
    void SetLogLines(int n)
    {
        n = Math.Clamp(n, 100, 20000);
        if (n == _maxLogRows) return;
        _maxLogRows = n; AppSettings.Update(x => x.LogLines = n); TrimLog();
    }

    /// <summary>Drops the OLDEST rows (the bottom of the list) beyond the limit set in Settings.</summary>
    void TrimLog() { while (_log.Items.Count > _maxLogRows) _log.Items.RemoveAt(_log.Items.Count - 1); }

    /// <summary>Called from any thread for every log line. New lines go on top; the view keeps its place if you scrolled down.</summary>
    public void AddLog(string line) => OnUi(() =>
    {
        var e = ToEntry($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}");   // same timestamp format as the file, so old and new lines line up
        int top = _log.TopIndex;
        _log.Items.Insert(0, e);
        if (top > 0) _log.TopIndex = top + 1;
        TrimLog();
        GrowWidth(e);
    });

    void GrowWidth(LogEntry e)
    {
        int w = 290 + TextRenderer.MeasureText(e.Message, _logFont).Width + 20;
        if (w > _logWidth) { _logWidth = w; _log.HorizontalExtent = w; }
    }

    static readonly Font _logFont = new("Consolas", 9.5f), _tagFont = new("Segoe UI Semibold", 8.5f), _timeFont = new("Consolas", 9f);

    // tint of the whole row, stripe/pill color, and whether the text is dimmed (tints are darker in the dark theme)
    static (Color Tint, Color Accent, bool Dim) Style(LogKind k)
    {
        bool d = Ui.Dark;
        return k switch
        {
            LogKind.Switch  => (d ? Color.FromArgb(58, 46, 24) : Color.FromArgb(255, 244, 225), Ui.AmberFill, false),
            LogKind.Block   => (d ? Color.FromArgb(62, 32, 36) : Color.FromArgb(253, 235, 237), Ui.RedFill, false),
            LogKind.Unblock => (d ? Color.FromArgb(26, 54, 40) : Color.FromArgb(230, 247, 237), Ui.GreenFill, false),
            LogKind.Error   => (d ? Color.FromArgb(84, 28, 36) : Color.FromArgb(251, 220, 224), Color.FromArgb(160, 20, 35), false),
            LogKind.Info    => (d ? Color.FromArgb(28, 42, 70) : Color.FromArgb(234, 241, 254), Ui.BlueFill, false),
            LogKind.Command => (Ui.CardBg, Ui.Border, true),
            _               => (Ui.CardBg, Ui.Border, false),
        };
    }

    void DrawLogRow(object? sender, DrawItemEventArgs ev)
    {
        if (ev.Index < 0 || ev.Index >= _log.Items.Count) return;
        var e = (LogEntry)_log.Items[ev.Index];
        var (tint, accent, dim) = Style(e.Kind);
        var g = ev.Graphics; var r = ev.Bounds;
        bool selected = (ev.State & DrawItemState.Selected) != 0;

        using (var back = new SolidBrush(selected ? (Ui.Dark ? Color.FromArgb(52, 72, 112) : Color.FromArgb(215, 227, 252)) : tint)) g.FillRectangle(back, r);
        using (var stripe = new SolidBrush(accent)) g.FillRectangle(stripe, r.X, r.Y, 4, r.Height);
        using (var line = new Pen(Ui.Dark ? Color.FromArgb(44, 48, 58) : Color.FromArgb(235, 236, 240))) g.DrawLine(line, r.X, r.Bottom - 1, r.Right, r.Bottom - 1);

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        int x = r.X + 16;
        string time = e.Time.Length >= 19 ? e.Time[11..19] : e.Time;      // the date is in the file; the row shows the clock time
        TextRenderer.DrawText(g, time, _timeFont, new Rectangle(x, r.Y, 72, r.Height), Ui.Muted, flags);
        x += 80;

        string tag = LogClassifier.Tag(e.Kind);
        if (tag.Length > 0)
        {
            tag = Ui.T(tag.ToLowerInvariant());     // Title Case tags: Switch, Block, Unblock, Error, Info
            var sz = TextRenderer.MeasureText(g, tag, _tagFont, new Size(200, 20), TextFormatFlags.NoPadding);
            var pill = new Rectangle(x, r.Y + (r.Height - 18) / 2, sz.Width + 16, 18);
            using (var pb = new SolidBrush(accent)) g.FillRectangle(pb, pill);
            TextRenderer.DrawText(g, tag, _tagFont, pill, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        x += 92;   // fixed column so messages line up whether or not the row has a tag

        TextRenderer.DrawText(g, e.Message, _logFont, new Rectangle(x, r.Y, 100000, r.Height),
            dim ? Ui.Muted : e.Kind == LogKind.Error ? (Ui.Dark ? Color.FromArgb(255, 140, 150) : Color.FromArgb(160, 20, 35)) : Ui.Text, flags);
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
