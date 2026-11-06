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
            var ed = new PolicyEditor(new GlobalTriggerCard()) { Dock = DockStyle.Fill };
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
            // a picture of the editor (the global trigger card above it, as on the Backup Rules page), next to the result file.
            // This test does not take the app's single-instance lock, so it can render while another copy is running.
            using (var shot = new Form { StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-32000, -32000), Opacity = 0, Width = 904, Height = 800, BackColor = Ui.Bg })
            {
                var global = new GlobalTriggerCard { Dock = DockStyle.Top, Height = GlobalTriggerCard.PreferredHeight };
                var ed2 = new PolicyEditor(global) { Dock = DockStyle.Fill };
                ed2.Connect(() => seen, (t, err) => ed2.Status.Text = t);
                shot.Controls.Add(ed2); shot.Controls.Add(new Panel { Dock = DockStyle.Top, Height = Ui.Gap, BackColor = Ui.Bg }); shot.Controls.Add(global);
                shot.Show(); Ui.SkinForm(shot); ed2.SetPolicy(BackupPolicy.Parse(File.ReadAllText(Paths.DefaultPolicy))); Application.DoEvents();
                using var bmp = new System.Drawing.Bitmap(shot.Width, shot.Height);
                shot.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, shot.Width, shot.Height));
                bmp.Save(Path.ChangeExtension(outFile, ".rules.png"));
                Say("rules editor rendered");
            }
            // 3) the Networks page: choose adapters yourself, Save, and check what reached the settings file and the engine
            //    (a temporary settings file: the real one is never touched)
            {
                var tmp = Path.Combine(Path.GetTempPath(), "nas-uitest-adapters-" + Environment.ProcessId + ".json");
                File.Delete(tmp);
                var eng = new SwitchEngine("", "", Paths.DefaultPolicy, enforce: true, execute: false, intervalSec: 1, settingsFile: tmp);
                using var host3 = new Form { StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-32000, -32000), Opacity = 0, Width = 1000, Height = 900 };
                var panel = new ConnectionsPanel(eng) { Dock = DockStyle.Fill };
                host3.Controls.Add(panel); host3.Show(); Application.DoEvents();
                T F<T>(string name) => (T)typeof(ConnectionsPanel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(panel)!;
                static string NameOf(ComboBox c) => (string?)c.SelectedItem?.GetType().GetProperty("Name")?.GetValue(c.SelectedItem) ?? "";      // the page's item type is private
                var cMain = F<ComboBox>("_main"); var cBack = F<ComboBox>("_backup"); var choose = F<CheckBox>("_chooseAdapters");
                var names = cMain.Items.Cast<object>().Select(o => o.ToString()!).ToList();
                Say($"adapters offered: {string.Join(" | ", names)}");
                if (cMain.Items.Count >= 3)
                {
                    choose.Checked = true; Application.DoEvents();
                    cMain.SelectedIndex = 1; cBack.SelectedIndex = 2; Application.DoEvents();
                    string wantMain = NameOf(cMain), wantBack = NameOf(cBack);
                    typeof(ConnectionsPanel).GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(panel, null);
                    var sw3 = System.Diagnostics.Stopwatch.StartNew(); while (sw3.ElapsedMilliseconds < 2500) { Application.DoEvents(); Thread.Sleep(20); }
                    var saved = AppSettings.Load(tmp);
                    Say($"chose main '{wantMain}', backup '{wantBack}' -> file has '{saved.PrimaryAdapter}' / '{saved.BackupAdapter}', engine has '{eng.UserPrimary}' / '{eng.UserBackup}'");
                    if (saved.PrimaryAdapter != wantMain || saved.BackupAdapter != wantBack || eng.UserPrimary != wantMain || eng.UserBackup != wantBack) throw new Exception("adapter choice did not reach the settings / engine");
                    // showing the page again must show what was saved (the page reloads itself when it becomes visible and has nothing unsaved)
                    panel.Reload(); Application.DoEvents();
                    if (NameOf(cMain) != wantMain || NameOf(cBack) != wantBack || !choose.Checked) throw new Exception("after a reload the page does not show the saved adapters");
                    Say("reload shows the saved adapters, the switch is on");
                    // back to automatic: switch it off and save
                    choose.Checked = false; Application.DoEvents();
                    typeof(ConnectionsPanel).GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(panel, null);
                    sw3.Restart(); while (sw3.ElapsedMilliseconds < 2500) { Application.DoEvents(); Thread.Sleep(20); }
                    saved = AppSettings.Load(tmp);
                    Say($"automatic -> file has '{saved.PrimaryAdapter}' / '{saved.BackupAdapter}'");
                    if (saved.PrimaryAdapter != "" || saved.BackupAdapter != "") throw new Exception("switching back to automatic did not clear the saved adapters");
                }
                File.Delete(tmp);
            }
            Say("RESULT: no exception");
        }
        catch (Exception e) { Say("RESULT: EXCEPTION\r\n" + e); }
    }
}
