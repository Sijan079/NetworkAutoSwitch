using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

// Backup Rules page (was Policy): the global trigger, the rules editor and the shared status line.
sealed partial class MainForm
{
    // ---------- Backup Rules ----------
    /// <summary>Header (with the program picker), the global trigger card, then the rule list and the rule editor side by side.
    /// The page scrolls (without a scrollbar) when the window is too small for the editor.</summary>
    Control PolicyPage()
    {
        var page = new ScrollSurface { MinContentHeight = 900 };
        page.Content.Padding = Page().Padding;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Ui.Bg, Margin = new Padding(0) };
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));                                        // header
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, GlobalTriggerCard.PreferredHeight + Ui.Gap)); // global trigger
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));                                        // rule list + editor

        var header = new PageHeader("Backup Rules") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Ui.Gap, 0) };
        header.AddAction(Ui.Btn("", "Programs Seen On Backup", "Pick from the programs that used data on the backup (or anything running now) and make a rule for them.", _editor.PickAndAdd));

        // the editor reads the global trigger, so they share this page (a control has one parent: Settings no longer shows it)
        _global.Dock = DockStyle.Fill; _global.Margin = new Padding(0, 0, Ui.Gap, Ui.Gap);

        _editor.Dock = DockStyle.Fill; _editor.Margin = new Padding(0);
        _editor.AddAction(Ui.Btn(Ui.Check, "Save And Apply", "Check every rule and the global trigger, save them and apply them now.", SavePolicy, BtnKind.Primary));
        _editor.AddAction(Ui.Btn(Ui.Code, "", "Edit As JSON: the same rules as text (for advanced use). Nothing is saved until you click Save And Apply.", EditJson));
        _editor.AddAction(Ui.Btn(Ui.Undo, "", "Revert: discard unsaved changes.", RevertAll));
        _policyStatus.Add(_editor.Status);

        grid.Controls.Add(header, 0, 0); grid.Controls.Add(_global, 0, 1); grid.Controls.Add(_editor, 0, 2);
        page.Content.Controls.Add(grid);
        return page;
    }

    /// <summary>Discards unsaved changes: the rules and the global trigger from the file, the Settings numbers from the engine.</summary>
    void RevertAll() { _recover.Value = _engine.RecoverSeconds; _cap.Value = _engine.DailyCapMB; _backupCheck.Value = _engine.BackupCheckSeconds; _logLines.Value = _maxLogRows; LoadPolicy(); }

    /// <summary>Save / Revert (and, on the Policy page, "Edit As JSON"). Saving writes the whole policy: the rules AND the global trigger.</summary>
    Control ActionBar(Action? beforeSave = null, bool jsonButton = false)
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(0, Ui.Gap, 0, 0), BackColor = Ui.Bg };
        var status = new Label { AutoSize = true, Padding = new Padding(8, 9, 0, 0), ForeColor = Ui.Muted, BackColor = Ui.Bg };
        _policyStatus.Add(status);
        bar.Controls.Add(Ui.Btn(Ui.Save, "Save", "Check everything, save it and apply it now.", () => { beforeSave?.Invoke(); SavePolicy(); }, BtnKind.Primary));
        bar.Controls.Add(Ui.Btn(Ui.Undo, "Revert", "Discard unsaved changes.", RevertAll));
        if (jsonButton) bar.Controls.Add(Ui.Btn(Ui.Code, "Edit As JSON", "Edit the same rules as text (for advanced use). Nothing is saved until you click Save.", EditJson));
        bar.Controls.Add(status);
        return bar;
    }

    /// <summary>The rules as text, in a dialog. The text is checked before it is accepted, and only changes the editor: Save still decides.</summary>
    void EditJson()
    {
        string json;
        try { json = BackupPolicy.ToJson(_editor.Build()); }
        catch (InvalidDataException e) { Say("Fix this first: " + e.Message, true); return; }
        using var dlg = new JsonDialog(json);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { _editor.SetPolicy(BackupPolicy.Parse(dlg.Json)); Say("JSON applied here. Click Save And Apply to keep it.", false); }
        catch (InvalidDataException e) { Say(e.Message, true); }
    }

    void LoadPolicy()
    {
        try { _editor.SetPolicy(BackupPolicy.Parse(File.ReadAllText(_policyPath))); Say("", false); }
        catch (Exception e) { Say("Could not read policy: " + e.Message, true); }
    }

    void SavePolicy()
    {
        string json;
        try
        {
            // validate first: a typo or an incomplete rule must never reach the running engine
            json = BackupPolicy.ToJson(_editor.Build());
            BackupPolicy.Parse(json);
        }
        catch (InvalidDataException e) { Say("Not saved. " + e.Message, true); return; }
        try
        {
            if (File.Exists(_policyPath)) File.Copy(_policyPath, _policyPath + ".bak", true);
            File.WriteAllText(_policyPath, json);
            LoadPolicy();                       // show what was really saved
            Say("Saving...", false);
            Task.Run(() =>
            {
                try { _engine.ReloadPolicy(); OnUi(() => Say("Saved and applied.", false, ok: true)); }
                catch (Exception e) { OnUi(() => Say("Saved, but applying failed: " + e.Message, true)); }
            });
        }
        catch (Exception e) { Say("Could not save: " + e.Message, true); }
    }

    // ---------- Settings ----------
    void Say(string text, bool error, bool ok = false)
    {
        foreach (var l in _policyStatus) { l.Text = text; l.ForeColor = error ? Ui.Red : ok ? Ui.Green : Ui.Muted; }
    }

}
