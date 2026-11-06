# Configuration

Everything the app stores lives in **`C:\ProgramData\NetworkAutoSwitch\`**. All files are plain text and safe to read. (For testing, the environment variable `NAS_DATA_DIR` points the app at another folder; `--snapshot` runs use a private copy this way, so they never touch your real settings.)

| File | What it holds |
|---|---|
| `policy.json` | The rules for the backup connection. The one you edit (first run copies `config\policy.json` there). Saving from the window keeps a `policy.json.bak`. |
| `settings.json` | Theme, mode, your Main and Backup network tables, chosen adapters, switch-back time, mute. Written by the app; edit it only while the app is closed. |
| `log.txt` | The log (rotates at 1 MB into `log.txt.old`). |
| `programs.json` | What each program downloaded and uploaded today, and on which connection (the Network Activity table on the Dashboard); starts again each day. |
| `usage.json` | How much data went through the backup today (for the daily limit); starts again each day. |
| `state.json` | The original network metrics, saved before the app changes anything. It exists **only while the app has changed your settings**; the app removes it after putting everything back. |

Old files keep loading in newer versions: unknown fields are ignored and missing fields get defaults (see [RELEASING.md](RELEASING.md#what-users-can-rely-on-in-every-version)).

## Start when I sign in

Settings → Startup (or the installer's option) creates a scheduled task called `NetworkAutoSwitchTray`: it runs the app with the highest rights 30 seconds after you sign in, so it starts already elevated with no prompt.
The task starts the copy you turned it on from; turning it on again from another copy (for example a build in `dist\Tray`) points it there.

## Your connections

Sort your networks on the **Networks** page (in the window or from the tray menu). The role belongs to the **network** (its name, for example `SNcial`), not to an adapter, so it holds on whichever adapter that network is on.

| Mode | Main table | Backup table |
|---|---|---|
| **Blacklist** | Networks barred from being main. They are always backup. | Networks barred from being backup. They are always main. |
| **Whitelist** | The only networks allowed to be main. | The only networks allowed to be backup. |

- Each mode keeps its **own** pair of tables, so switching mode never changes what a list means.
- A network on neither table is **unlabelled**. The app asks once (Main, Backup or Later) and treats it as a backup until you sort it, so a forgotten network can only cost a blocked download, never your data.
- A network that may not be main but ends up as the main connection is **protected like the backup** (the dashboard shows *MAIN IS PHONE*): watch-list downloads are blocked on it and its data counts toward the daily limit. A network that may not be the backup is never used as the backup.
- Both adapters on the **same network** (same router) show a *SAME NETWORK* badge: switching cannot help, because both fail together.
- A phone tethered over USB, Bluetooth or mobile broadband is recognised as a backup by its adapter type, because Windows gives those generic names.
- Names are read from Windows' connection profiles. While Windows is still identifying the **main** adapter's network (`Identifying...`, or no name yet) the main connection is **protected** (fail closed) and the app looks again every 2 seconds. If Windows gives no names for any adapter, the lookup itself is broken: nothing can be judged, nothing is blocked, and the log says so once.
- The *SAME NETWORK* check compares the router's address and hardware address. If the hardware address cannot be read, two networks with different names are not reported as the same.

The **adapters** (Wi-Fi, Ethernet, tethers) are found by the app; VPNs, virtual machines and the hidden driver layers Windows lists next to every adapter are ignored. They only decide the roles when the tables cannot, for example for a network you have not sorted yet.
To fix an adapter yourself, tick **Choose adapters myself** in the same window.
With only one connection there is no backup: the app keeps measuring and tells you, and changes nothing.
`nas-cli --list-adapters` shows what was found, and which network each adapter is on.

## Policy file

```json
{
  "defaults": { "mbPerWindow": 2, "windowSec": 10 },
  "backupRules": [
    { "name": "Steam", "action": "watch", "exes": ["steam.exe"] },
    { "name": "HoYoPlay", "action": "watch", "folder": "C:\\Program Files\\HoYoPlay",
      "exes": ["HYP.exe", "HYPWorker.exe", "launcher.exe"], "mbPerWindow": 5 },
    { "name": "Endfield", "action": "watch", "folders": ["C:\\Program Files\\GRYPHLINK", "D:\\Sijan\\EndField Game"],
      "exes": ["Launcher.exe", "Updater.exe", "PlatformProcess.exe", "Endfield.exe"] },
    { "name": "Updater", "action": "block", "path": "C:\\Tools\\updater.exe" },
    { "name": "Backup tool", "action": "throttle", "path": "C:\\Tools\\sync.exe", "kbps": 500 }
  ]
}
```

| Part | Meaning |
|---|---|
| `defaults` | The **global trigger**: a watched program that receives more than `mbPerWindow` megabytes within `windowSec` seconds on the backup is blocked. Every watch rule uses it unless it has its own numbers. |
| `watch` | Needs `exes` (file names, one per program). `folder` (one) or `folders` (several) is optional: use it when two launchers share a file name. `mbPerWindow` and `windowSec` are optional per-rule overrides. |
| a **game bundle** | One `watch` rule for a game: its launcher, its updater **and the game itself**, with the launcher's folder and the game's folder in `folders`. Many games download their own data from the game, not from the launcher, so the game must be in the bundle. A watch rule only blocks while the program downloads faster than the trigger (2 MB per 10 s by default), so normal gameplay is not cut off. |
| `block` | Needs `path`. Blocks that program on the backup adapter only. |
| `throttle` | Needs `path` and `kbps`. Windows can only cap **uploads** this way. |

Saving from the window checks the file first, applies it at once, and refuses a file with a mistake (the error names the rule).
`config/policy-test.json` is a tiny policy used by the test commands.

## Settings file

| Key | Default | Meaning |
|---|---|---|
| `Theme` | `System` | `System`, `Light` or `Dark` |
| `Mode` | `Auto` | `Auto`, `StayPrimary` or `StayBackup` |
| `NetworkListMode` | `Blacklist` | `Blacklist` or `Whitelist`: how the two tables are read (see *Your connections*). |
| `BlacklistMainNetworks`, `BlacklistBackupNetworks` | empty | The two tables in Blacklist mode: networks barred from main, networks barred from backup. |
| `WhitelistMainNetworks`, `WhitelistBackupNetworks` | empty | The two tables in Whitelist mode: the only networks allowed as main, as backup. |
| `PrimaryAdapter`, `BackupAdapter` | empty | Adapter names you chose under *Adapters* on the Networks page. Empty = detect automatically. Only used when the tables cannot decide. |
| `LastPrimary`, `LastBackup` | empty | What detection chose last time (used only to keep the choice stable across restarts). |
| `RecoverSeconds` | `10` | Seconds the main connection must work, without a single failed check, before moving back from the backup (3 to 600). |
| `MuteNotifications` | `false` | Hide the tray pop-ups (everything is still logged). |
| `CheckForUpdates` | `true` | Look for a newer release on GitHub at start and once a day. Installing always needs your click. |
| `Welcomed` | missing | `false` on a brand-new install until the first-run welcome has been shown, then `true`. Missing = installed before the welcome existed. |
| `LogLines` | `2000` | How many lines the Log page keeps on screen (100 to 9999 in the Settings box; the file may hold up to 20000); older lines drop off the bottom. The log file is not shortened. |
| `BackupCheckSeconds` | `10` | While the main connection is fine, the idle backup is checked this often (1 to 60 s). Every check uses a little mobile data. |
| `DailyCapMB` | `0` | Daily limit for data through the backup, in MB (`0` = no limit). The Settings page shows and edits it in **GB** (1 GB = 1000 MB, up to 9999 GB). You are told at 80 % and when it is reached. |

## Command-line options of the tray

| Option | Does |
|---|---|
| `--quit` | Asks the running copy to exit cleanly (restores the network first). No admin needed. |
| `--primary <name>` / `--backup <name>` | Fix an adapter instead of detecting it (the network tables can still override it). |
| `--dry-run` | Run without changing anything on the PC. |
| `--show` | Open the window at start. |
| `--theme light
| `--page <name>` | With `--show`: open the window on that page (`dashboard`, `activity` for Programs, `networks`, `log`, `policy` for Backup Rules, `settings`). |\|dark` | Force a theme for this run (not saved). |

## If something goes wrong

- **Errors** inside the window never stop the tray; they appear in the Log as a red *CRASHED* row with the full details.
- **Network looks odd after a crash or a forced kill**: in an administrator terminal run
  `dotnet run --project src\Cli -- --restore --execute` (puts back the saved metrics and removes the app's firewall and QoS rules).
- **A program is blocked and should not be**: Dashboard → Blocked → *Unblock* (choose how long to leave it alone), or remove it from the Policy.
- **The dashboard shows a "VPN" badge**: a VPN refuses the check on a single connection. The app still works with less precise checks (see [ARCHITECTURE.md](ARCHITECTURE.md#vpns)). **Exact checks behind WARP.** Add the two addresses the app checks, `8.8.8.8` and `9.9.9.9`, to WARP's *Exclude IPs* list (Preferences → Advanced → Split Tunnels, or from a terminal: `warp-cli tunnel ip add 8.8.8.8` and `warp-cli tunnel ip add 9.9.9.9`). They then bypass the tunnel, the per-connection check works again, and the badge disappears by itself.
  The side effect: traffic to those two addresses (for example DNS, if you use Google's or Quad9's) is no longer protected by WARP. To undo: `warp-cli tunnel ip remove 8.8.8.8` and `warp-cli tunnel ip remove 9.9.9.9`.
