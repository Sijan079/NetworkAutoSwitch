# Network Auto Switch

A Windows tray app that keeps **two internet connections** (a main one, e.g. Wi-Fi, and a backup, e.g. a phone tether)
and moves between them for you.

- **Wi-Fi loses internet** → it switches to the backup within seconds, and applies your **policy** so big downloads can't eat your mobile data.
- **Wi-Fi comes back** → it switches back, lifts every policy block, and moves downloads that were running on the backup over to Wi-Fi.
- **You can lock it** to one connection if you don't want automatic switching.

Windows 10/11 only. Needs administrator rights (it changes network metrics and firewall rules); the tray asks once, or runs
already elevated through the sign-in task the installer can set up.

---

## What it does

| Part | What happens |
|---|---|
| **Health check** | Every second it opens a TCP connection to two public servers (8.8.8.8, 9.9.9.9) *through each adapter separately* (a socket pinned to the adapter with `IP_UNICAST_IF`). No routes are added and no admin is needed for the check itself. |
| **Deciding** | Main link reported down by Windows → switch at once. Main merely failing → **3 failed checks in a row** (~4 s). Back to main → **10 good checks in a row** (~10 s), so a flickering Wi-Fi doesn't bounce you around. |
| **Switching** | Sets the preferred adapter's interface metric to 5 and the other to 500 (and normalizes the preferred adapter's default route). Then asks Windows which adapter new connections really use and corrects it if wrong. Originals are saved and restored on exit or pause. |
| **Policy (on the backup only)** | **Watch rules**: a launcher/downloader is blocked on the backup *only while it downloads fast* (default: more than 2 MB in 10 s). Blocks never expire by themselves; you unblock from the Dashboard. Optional **block** and **upload-cap** rules. Games should never be listed, so they are never cut off. |
| **Moving downloads** | When main returns, running connections on the backup of watched programs (and anything pulling > 1 MB / 10 s) are reset so they reconnect over Wi-Fi. Light traffic and games are left alone. |
| **Watching data** | Per-program bytes received on the backup come from Windows' own network events (ETW), not a driver. |

## Using the app

The tray icon shows the state (green = main, amber = backup, red = nothing works, grey = paused). Double-click it for the window:

- **Dashboard**: status, both connections (latency, uptime), latency chart, session numbers, data used on the backup per program
  (with a one-click *Add To Watch List*), and panels for **Blocked** programs (Unblock) and **Allowed For Now** (Block Now / Watch Again).
  The banner has the **Mode** button (Automatic, or stay on one connection), and Pause.
- **Log**: live, newest first, colored by type (switch, block, unblock, error, info). Select rows and press Ctrl+C to copy.
- **Policy**: the global trigger, a list of rules, and a rule editor. *Add Rule* opens a program picker (programs that used backup data first, then running ones). You can also drag an `.exe` onto the page.
- **Settings**: Theme (Follow System, Light or Dark), how long the main connection must be good before switching back, mute notifications, and the Global Trigger. The policy can also be edited as text (*Edit As JSON* on the Policy page).

**Pause** puts Windows fully back to normal and stops switching. **Exit** (tray menu) restores everything first.

## Install

Download **`NetworkAutoSwitch-Setup-<version>.exe`** from the [Releases](https://github.com/Sijan079/NetworkAutoSwitch/releases) page and run it
(Windows 10 or 11, 64-bit; **nothing else to install**: the app carries its own .NET). Check the `.sha256` file next to it if you want to verify the download.

- **Start when I sign in** (ticked by default): the app starts by itself 30 seconds after you sign in, already elevated, with no prompt.
- **Upgrading**: run a newer installer over the old one. It closes the running app the normal way (which puts your network settings back), replaces it, and keeps your settings and policy.
  It **never installs an older version over a newer one**.
- **Uninstalling** (Windows *Apps and features*): puts your network settings back, removes the sign-in task, and asks whether to keep your settings, policy and log.

> The first release has not been published yet. Until it is, build from source (below).

### Build from source

Needs the .NET 9 SDK.

```powershell
dotnet publish src\Tray -c Release -o dist\Tray
dist\Tray\NetworkAutoSwitchTray.exe       # Windows asks for administrator rights once, then it sits in the tray
```

To stop a running copy from a script: `dist\Tray\NetworkAutoSwitchTray.exe --quit` (no administrator rights needed; it restores your network settings first).
How releases and the installer are made, and how version numbers work: [docs/RELEASING.md](docs/RELEASING.md). What changed in each version: [CHANGELOG.md](CHANGELOG.md).

## Your connections

The app **finds your connections by itself**: your main one (Wi-Fi or a wired adapter) and a backup (a phone tethered over USB, Bluetooth or mobile broadband).
VPNs, virtual machines and the hidden driver layers Windows lists next to every adapter are ignored. It looks again whenever something is plugged in or changes,
so a phone plugged in later is picked up, and a renamed adapter does not matter.

If you have more than one candidate, or want something specific, use **Mode, then Choose Connections** (in the window or the tray menu) to pick the main and the backup,
or leave either on *Automatic*. With only one connection there is no backup: the app keeps measuring and tells you, and does not change anything.
`nas-cli --list-adapters` shows what was found and why.

## Files and locations

| What | Where |
|---|---|
| Policy (the one you edit) | `C:\ProgramData\NetworkAutoSwitch\policy.json` (first run copies `config\policy.json` there) |
| Settings (theme, mode) | `C:\ProgramData\NetworkAutoSwitch\settings.json` |
| Log | `C:\ProgramData\NetworkAutoSwitch\log.txt` (rotates at 1 MB) |
| Saved original metrics | `C:\ProgramData\NetworkAutoSwitch\state.json` (exists only while the app has changed your settings) |
| Published app | `dist\Tray\` |

### Policy format

```json
{
  "defaults": { "mbPerWindow": 2, "windowSec": 10 },
  "backupRules": [
    { "name": "Steam", "action": "watch", "exes": ["steam.exe"] },
    { "name": "HoYoPlay", "action": "watch", "folder": "C:\\Program Files\\HoYoPlay",
      "exes": ["HYP.exe", "HYPWorker.exe", "launcher.exe"], "mbPerWindow": 5 },
    { "name": "Updater", "action": "block", "path": "C:\\Tools\\updater.exe" },
    { "name": "Backup tool", "action": "throttle", "path": "C:\\Tools\\sync.exe", "kbps": 500 }
  ]
}
```

- `watch`: needs `exes`; `folder` is optional (use it when two launchers share a file name); `mbPerWindow` / `windowSec` are optional (the global `defaults` apply otherwise).
- `block`: needs `path`. Blocks the program on the backup adapter only.
- `throttle`: needs `path` and `kbps`. Windows can only cap **uploads** this way.
- Saving from the window checks the file first, keeps a `.bak`, and applies it immediately.

## If something goes wrong

- **Errors** inside the window never stop the tray; they appear in the Log tab as a red *CRASHED* row with the full details.
- **Network looks odd after a crash or a forced kill**: run in an administrator terminal
  `dotnet run --project src\Cli -- --restore --execute` (puts back the saved metrics and removes the app's firewall/QoS rules).
- **A program is blocked and shouldn't be**: Dashboard → Blocked → *Unblock* (pick how long to leave it alone).

## Project layout

```
NetworkAutoSwitch.sln
Directory.Build.props   the ONE place for the version number (and product details)
CHANGELOG.md            what changed in each version
config/        policy.json (the default policy), policy-test.json (a tiny test policy for curl)
installer/     NetworkAutoSwitch.iss: the Inno Setup installer script
docs/          RELEASING.md (version rules and how to release); legacy/probe-adapters.ps1 is the original route-based prototype, kept for reference
.github/workflows/   ci.yml (build + tests on every push) and release.yml (installer + GitHub release when a version tag is pushed)
src/
  Core/        NetworkAutoSwitch.Core: everything that is not UI
                 SwitchEngine     the loop: detect adapters -> probe -> decide -> switch -> policy -> restore
                 AdapterDiscovery finds the adapters and picks the main and the backup
                 AdapterProber    the per-adapter health check
                 FailoverMonitor  the switching rules (pure logic)
                 MetricSwitcher   metrics and route preference
                 BackupPolicy / PolicyBuilder   policy model, validation, rules from picked programs
                 DownloadGuard    the download watchdog (pure logic) + ETW byte counter
                 TcpMigrator      resets backup connections so downloads move to Wi-Fi
                 AppInfo (version and SemVer compare), Stats, LogClassifier, AppSettings, Paths
  Tray/        NetworkAutoSwitch.Tray: the tray app and window (WinForms; custom theme, tabs, cards)
  Cli/         NetworkAutoSwitch.Cli (nas-cli): diagnostics and tests, no UI
dist/          publish and installer output (not in source control)
```

## Developing and testing

```powershell
dotnet build -c Release
dotnet run -c Release --project src\Cli -- --selftest        # ~110 fast unit tests, no network changes
dotnet run -c Release --project src\Cli -- --engine-test     # the real engine in dry-run: lock mode, failover, choosing adapters (~20 s)
dotnet run -c Release --project src\Cli -- --list-adapters   # read-only: every adapter, its kind, and which two would be used
dotnet run -c Release --project src\Cli -- --list-conns      # read-only: which programs have connections over the backup
dotnet run -c Release --project src\Cli -- --enforce --ticks 0            # dry-run: prints what it WOULD do
dotnet run -c Release --project src\Cli -- --enforce --execute --ticks 0  # live (administrator terminal)
```

The tray has test helpers that never touch your network or screen:
`NetworkAutoSwitchTray.exe --dry-run --snapshot <folder>` renders every page to PNG files (`--theme dark|light`), and
`--ui-test <file>` drives the add-a-rule screens in code and writes the outcome to a file.
Passing `--primary <name>` or `--backup <name>` to the tray or the CLI fixes an adapter instead of detecting it.

## Known limits

- The check is a plain connection test, so a **captive portal** that accepts connections but gives no internet is not detected.
- A **brief dropout of about 4 s** is enough to trigger a switch (that is the price of fast detection); switching back is deliberately slower.
- Download **speed limits** are not possible with built-in Windows tools (only upload caps); the watchdog blocks instead.
- Connections that are not heavy and not on the watch list (for example a game session) stay on the backup until they reconnect on their own.
- IPv4 only.
- It manages **two** connections at a time (a main and a backup). With three or more real adapters, you choose which two in *Choose Connections*.
