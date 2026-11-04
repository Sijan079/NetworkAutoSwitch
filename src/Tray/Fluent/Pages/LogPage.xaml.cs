using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray.Fluent.Pages;

public sealed record LogItem(string Time, string Tag, string Message, string Kind);

/// <summary>The live log, newest line at the top. The page is built once, so it keeps its rows while you look at other pages.</summary>
public partial class LogPage : Page
{
    const int MaxRows = 2000;
    readonly ObservableCollection<LogItem> _rows = new();

    internal LogPage(FluentContext cx)
    {
        InitializeComponent();
        Grid.ItemsSource = _rows;
        Load();
        cx.LogLine += line => Dispatcher.BeginInvoke(() => Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}"));
        OpenBtn.Click += (_, _) => OpenNewestFirst();
        ReloadBtn.Click += (_, _) => Load();
        ClearBtn.Click += (_, _) => _rows.Clear();
        Grid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && Grid.SelectedItems.Count > 0)
            { Clipboard.SetText(string.Join("\r\n", Grid.SelectedItems.Cast<LogItem>().Select(x => $"{x.Time}  {x.Message}"))); e.Handled = true; }
        };
    }

    static LogItem ToItem(string line)
    {
        var (time, msg) = LogClassifier.Split(line);
        var kind = LogClassifier.Classify(msg);
        return new LogItem(time, LogClassifier.Tag(kind), msg, kind.ToString());
    }

    void Add(string line) { _rows.Insert(0, ToItem(line)); while (_rows.Count > MaxRows) _rows.RemoveAt(_rows.Count - 1); }   // drop the OLDEST rows (bottom)

    /// <summary>The last lines of the log file, oldest first. Opened shared because the tray is writing to it.</summary>
    static List<string> ReadLines(int? last = null)
    {
        var lines = new List<string>();
        using var fs = new FileStream(Paths.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        string? l; while ((l = sr.ReadLine()) is not null) lines.Add(l);
        return last is int n && lines.Count > n ? lines.GetRange(lines.Count - n, n) : lines;
    }

    void Load()
    {
        try
        {
            var lines = ReadLines(300); lines.Reverse();
            _rows.Clear();
            foreach (var l in lines) _rows.Add(ToItem(l));
        }
        catch (Exception e) { _rows.Clear(); _rows.Add(new LogItem("", "ERROR", "could not read log file: " + e.Message, "Error")); }
    }

    static void OpenNewestFirst()
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
