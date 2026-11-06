# Development

## What you need

- Windows 10 or 11 (the app uses Windows networking, firewall and ETW)
- The [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php), only to build the installer

## Build and run

```powershell
dotnet build -c Release
dotnet publish src\Tray -c Release -o dist\Tray
dist\Tray\NetworkAutoSwitchTray.exe          # asks for administrator rights once
```

While developing, use `--dry-run` to run the whole app without changing anything on the PC (it prints what it **would** do):

```powershell
dist\Tray\NetworkAutoSwitchTray.exe --dry-run --show
```

Stop a running copy cleanly (it restores the network first) with `dist\Tray\NetworkAutoSwitchTray.exe --quit`. If you rebuild while a copy is running, quit it first.

## Tests

There is no test framework to install. The tests are in `src/Cli/Program.cs` and run through the command-line tool:

```powershell
dotnet run -c Release --project src\Cli -- --selftest        # about 130 unit tests; fast, no network changes
dotnet run -c Release --project src\Cli -- --engine-test     # the real engine in dry-run: lock mode, failover, choosing adapters (about 20 s)
```

**Both must pass before you send a change.** CI runs the unit tests on every push and pull request. The engine test needs real adapters, so it is run by hand.

When you change logic, add a test next to the ones for the same code. The logic that decides things (`FailoverMonitor`, `VpnFallback`, `PolicyBuilder`, `DownloadGuard`, `LogClassifier`, adapter classification) is pure, so a test is a few lines with no network.

## Diagnostic commands (`nas-cli`)

```powershell
dotnet run -c Release --project src\Cli -- --list-adapters   # read-only: every adapter, its kind, and which two would be used
dotnet run -c Release --project src\Cli -- --list-conns      # read-only: which programs have connections over the backup
dotnet run -c Release --project src\Cli -- --enforce --ticks 0            # dry-run: prints what it WOULD do
dotnet run -c Release --project src\Cli -- --enforce --execute --ticks 0  # live (needs an administrator terminal)
dotnet run -c Release --project src\Cli -- --restore --execute            # put saved metrics back and remove the app's rules
```

Other options: `--primary <name>`, `--backup <name>`, `--interval <s>`, `--ticks <n>`, `--policy <file>`, `--policy-apply`, `--policy-remove`, `--etw-debug`.
Without `--execute` nothing on the PC is changed. **Always leave the PC as you found it**: anything you add while testing must be removed again (the app does this on exit and `--restore` does it after a crash).

## Checking the window without looking at the screen

The tray has helpers that never touch your network or capture your screen:

```powershell
NetworkAutoSwitchTray.exe --dry-run --snapshot <folder> --theme dark    # saves a PNG of every page (and the dialogs), then quits
NetworkAutoSwitchTray.exe --ui-test <file> [--theme dark]               # drives screens in code (add-a-rule, choosing adapters and saving, the cap's MB/GB conversion) and writes the outcome to a file, with a few pictures next to it
NetworkAutoSwitchTray.exe --dry-run --demo-blocked --show --page settings   # a dry-run window opened on one page, with sample blocked programs
```

The snapshots are drawn by the window itself (`DrawToBitmap`), so they work with the window hidden. Use them to review a UI change in both themes.
A snapshot (and `--ui-test`) runs on a **private copy of the data folder** (the `NAS_DATA_DIR` environment variable, see [CONFIGURATION.md](CONFIGURATION.md)), so it can never change your real settings, and it always quits by itself.
Set `NAS_DATA_DIR` yourself, pointing at a folder with copies of `settings.json` and `policy.json`, when you run a dry-run window you are going to click around in.

`DrawToBitmap` has two blind spots: it skips hidden controls and paints overlapping controls in the wrong order. For details such as these, look at the real window (`--show --page <name>`), not only at a snapshot.

## The installer

```powershell
dotnet publish src\Tray -c Release -r win-x64 --self-contained true -p:DebugType=none -o dist\publish
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" "/DAppVersion=0.1.0" installer\NetworkAutoSwitch.iss
# -> dist\installer\NetworkAutoSwitch-Setup-0.1.0.exe
```

The version is passed in, never typed in the script; it comes from `Directory.Build.props`. Adding `/DTestInstall` builds a per-user variant for automated checks (no sign-in task, never touches a running copy).
**The `AppId` in the script must never change** for this project (see [RELEASING.md](RELEASING.md#the-installer)); a fork that publishes its own installer should give it a new one.

## Conventions

- **Labels use Title Case** (`Add To Watch List`); program and product names (`HoYoPlay`, `Wi-Fi`) are left alone.
- **Explanations go in tooltips** and short hints, not in long paragraphs on the page.
- **No nested cards.** Every card sits directly on its page; colors come from the palette in `Ui.cs`, never typed into a control.
- **Anything that talks to Windows (PowerShell, firewall, adapters) must stay off the UI thread.**
- **The app changes the network only through `SwitchEngine`**, and it must be able to put everything back from `state.json` alone.
- Comments explain *why*, not *what*.
