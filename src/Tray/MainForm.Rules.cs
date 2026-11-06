using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using NetworkAutoSwitch.Core;

namespace NetworkAutoSwitch.Tray;

// Backup Rules page (was Policy): the rules editor and the shared status line.
sealed partial class MainForm
{
    // ---------- Policy (the rules) ----------
    Control PolicyPage()
    {
        var page = Page();
        page.Controls.Add(_editor);
        page.Controls.Add(ActionBar(jsonButton: true));
        return page;
    }

    /// <summary>Save / Revert (and, on the Policy page, "Edit As JSON"). Saving writes the whole policy: the rules AND the global trigger.</summary>
    Control ActionBar(Action? beforeSave = null, bool jsonButton = false)
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(0, Ui.Gap, 0, 0), BackColor = Ui.Bg };
        var status = new Label { AutoSize = true, Padding = new Padding(8, 9, 0, 0), ForeColor = Ui.Muted, BackColor = Ui.Bg };
        _policyStatus.Add(status);
        bar.Controls.Add(Ui.Btn(Ui.Save, "Save", "Check everything, save it and apply it now.", () => { beforeSave?.Invoke(); SavePolicy(); }, BtnKind.Primary));
        bar.Controls.Add(Ui.Btn(Ui.Undo, "Revert", "Discard unsaved changes.", () => { _recover.Value = _engine.RecoverSeconds; _cap.Value = _engine.DailyCapMB; _backupCheck.Value = _engine.BackupCheckSeconds; _logLines.Value = _maxLogRows; LoadPolicy(); }));
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
        try { _editor.SetPolicy(BackupPolicy.Parse(dlg.Json)); Say("JSON applied here. Click Save to keep it.", false); }
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
