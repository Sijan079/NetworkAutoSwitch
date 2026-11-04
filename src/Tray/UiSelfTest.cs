using System.Reflection;
using System.Text;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

/// <summary>--ui-test &lt;file&gt;: drives the "add a rule" screens in code (nothing is shown on screen) and writes what happened to a file.
/// It exists to reproduce UI crashes without anyone clicking.</summary>
static class UiSelfTest
{
    public static void Run(string outFile)
    {
        var sb = new StringBuilder();
        void Say(string s) { sb.AppendLine(s); File.WriteAllText(outFile, sb.ToString()); }
        try
        {
            ApplicationConfiguration.Initialize();
            Say($"theme dark={Ui.Dark}");

            // 1) the picker: open it, select the first two rows, press Add
            var seen = new[] { new SeenProgram("steam.exe", @"C:\Program Files (x86)\Steam\steam.exe", 5_000_000), new SeenProgram("HYPWorker.exe", @"C:\Program Files\HoYoPlay\HYPWorker.exe", 1_000_000) };
            using (var pk = new ProgramPicker(seen))
            {
                pk.StartPosition = FormStartPosition.Manual; pk.Location = new System.Drawing.Point(-32000, -32000); pk.Opacity = 0;
                pk.Show(); Application.DoEvents();
                var list = (ListView)typeof(ProgramPicker).GetField("_list", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pk)!;
                var sw0 = System.Diagnostics.Stopwatch.StartNew();
                Say($"picker opened at once with {list.Items.Count} rows (backup programs only), loading={pk.StillLoading()}");
                while (pk.StillLoading() && sw0.Elapsed.TotalSeconds < 60) { Application.DoEvents(); Thread.Sleep(20); }
                Say($"background load finished after {sw0.Elapsed.TotalSeconds:0.0} s: {list.Items.Count} rows, {list.Groups.Count} groups");
                list.Items[0].Selected = true; if (list.Items.Count > 1) list.Items[1].Selected = true; Application.DoEvents();
                typeof(ProgramPicker).GetMethod("Accept", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(pk, null);
                Say($"picker accepted: {pk.Selected.Count} selected -> {string.Join(", ", pk.Selected.Select(Path.GetFileName))}");
            }

            // 1b) the REAL path: a modal dialog opened from a window (exactly what the Add Rule button does), accepted by a timer
            using (var owner = new Form { StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-32000, -32000), Opacity = 0, Width = 400, Height = 300 })
            {
                owner.Show(); Application.DoEvents();
                using var modal = new ProgramPicker(seen);
                var timer = new System.Windows.Forms.Timer { Interval = 1200 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    try
                    {
                        var l = (ListView)typeof(ProgramPicker).GetField("_list", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(modal)!;
                        Say($"modal open: {l.Items.Count} rows");
                        l.Items[0].Selected = true; Application.DoEvents();
                        typeof(ProgramPicker).GetMethod("Accept", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(modal, null);
                    }
                    catch (Exception e) { Say("EXCEPTION inside modal: " + e); modal.DialogResult = DialogResult.Cancel; }
                };
                timer.Start();
                var rc = modal.ShowDialog(owner);
                Say($"modal closed: {rc}, selected {modal.Selected.Count}");
            }

            // 1c) the picker with EVERY readable process on this machine (an elevated app sees more programs than a normal one)
            using (var big = new ProgramPicker(seen))
            {
                big.StartPosition = FormStartPosition.Manual; big.Location = new System.Drawing.Point(-32000, -32000); big.Opacity = 0;
                big.Show(); Application.DoEvents();
                var swb = System.Diagnostics.Stopwatch.StartNew(); while (big.StillLoading() && swb.Elapsed.TotalSeconds < 60) { Application.DoEvents(); Thread.Sleep(20); }
                var l = (ListView)typeof(ProgramPicker).GetField("_list", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(big)!;
                var search = (TextBox)typeof(ProgramPicker).GetField("_search", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(big)!;
                search.Text = "a"; Application.DoEvents(); int a = l.Items.Count;
                search.Text = "zzzznomatch"; Application.DoEvents(); int none = l.Items.Count;
                search.Text = ""; Application.DoEvents();
                Say($"search filter ok: 'a' -> {a} rows, no match -> {none}, cleared -> {l.Items.Count}");
            }

            // 2) the rules editor: load the real policy, add the picked programs as a rule, build it back
            using var host = new Form { StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-32000, -32000), Opacity = 0, Width = 1000, Height = 700 };
            var ed = new PolicyEditor { Dock = DockStyle.Fill };
            string said = "";
            ed.Connect(() => seen, (t, err) => said = t);
            host.Controls.Add(ed); host.Show(); Application.DoEvents();
            ed.SetPolicy(BackupPolicy.Parse(File.ReadAllText(Paths.DefaultPolicy)));
            Say("editor loaded the real policy");
            int added = ed.AddPrograms(new[] { @"D:\Tools\newtool.exe", @"D:\Tools\newtool-helper.exe" });
            Application.DoEvents();
            Say($"AddPrograms -> {added} added, message: {said}");
            var built = ed.Build();
            Say($"Build ok: {built.BackupRules.Count} rules, last = {built.BackupRules[^1].Name} [{string.Join(", ", built.BackupRules[^1].Exes!)}] folder {built.BackupRules[^1].Folder}");
            int dup = ed.AddPrograms(new[] { @"C:\Program Files (x86)\Steam\steam.exe" });
            Say($"adding an already-watched program -> {dup} added, message: {said}");
            Say("RESULT: no exception");
        }
        catch (Exception e) { Say("RESULT: EXCEPTION\r\n" + e); }
    }
}
