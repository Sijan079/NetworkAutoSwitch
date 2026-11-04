# Configuration

Everything the app stores lives in **`C:\ProgramData\NetworkAutoSwitch\`**. All files are plain text and safe to read.

| File | What it holds |
|---|---|
| `policy.json` | The rules for the backup connection. The one you edit (first run copies `config\policy.json` there). Saving from the window keeps a `policy.json.bak`. |
| `settings.json` | Theme, mode, chosen adapters, switch-back time, mute. Written by the app; edit it only while the app is closed. |
| `log.txt` | The log (rotates at 1 MB into `log.txt.old`). |
| `programs.json` | Data each program used on the backup today (the table on the Dashboard); starts again each day. |
| `usage.json` | How much data went through the backup today (for the daily limit); starts again each day. |
| `state.json` | The original network metrics, saved before the app changes anything. It exists **only while the app has changed your settings**; the app removes it after putting everything back. |

Old files keep loading in newer versions: unknown fields are ignored and missing fields get defaults (see [RELEASING.md](RELEASING.md#what-users-can-rely-on-in-every-version)).

## Start when I sign in

Settings → Startup (or the installer's option) creates a scheduled task called `NetworkAutoSwitchTray`: it runs the app with the highest rights 30 seconds after you sign in, so it starts already elevated with no prompt.
The task starts the copy you turned it on from; turning it on again from another copy (for example a build in `dist\Tray`) points it there.

## Your connections

The app **finds your connections by itself**: the main one (Wi-Fi or a wired adapter) and a backup (a phone tethered over USB, Bluetooth or mobile broadband).
VPNs, virtual machines and the hidden driver layers Windows lists next to every adapter are ignored. It looks again whenever something is plugged in or changes,
so a phone plugged in later is picked up, and a renamed adapter does not matter.

To choose yourself, use **Mode, then Choose Connections** (in the window or the tray menu), and pick the main and the backup, or leave either on *Automatic*.
With only one connection there is no backup: the app keeps measuring and tells you, and changes nothing.
`nas-cli --list-adapters` shows what was found and why.

## Policy file

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

| Part | Meaning |
|---|---|
| `defaults` | The **global trigger**: a watched program that receives more than `mbPerWindow` megabytes within `windowSec` seconds on the backup is blocked. Every watch rule uses it unless it has its own numbers. |
| `watch` | Needs `exes` (file names, one per program). `folder` is optional: use it when two launchers share a file name. `mbPerWindow` and `windowSec` are optional per-rule overrides. List launchers and downloaders, **never games**, so a game is never cut off. |
| `block` | Needs `path`. Blocks that program on the backup adapter only. |
| `throttle` | Needs `path` and `kbps`. Windows can only cap **uploads** this way. |

Saving from the window checks the file first, applies it at once, and refuses a file with a mistake (the error names the rule).
`config/policy-test.json` is a tiny policy used by the test commands.

## Settings file

| Key | Default | Meaning |
|---|---|---|
| `Theme` | `System` | `System`, `Light` or `Dark` |
| `Mode` | `Auto` | `Auto`, `StayPrimary` or `StayBackup` |
| `PrimaryAdapter`, `BackupAdapter` | empty | Adapter names you chose. Empty = detect automatically. |
| `LastPrimary`, `LastBackup` | empty | What detection chose last time (used only to keep the choice stable across restarts). |
| `RecoverSeconds` | `10` | Seconds the main connection must work, without a single failed check, before moving back from the backup (3 to 600). |
| `MuteNotifications` | `false` | Hide the tray pop-ups (everything is still logged). |
| `Welcomed` | missing | `false` on a brand-new install until the first-run welcome has been shown, then `true`. Missing = installed before the welcome existed. |
| `LogLines` | `2000` | How many lines the Log page keeps on screen (100 to 20000); older lines drop off the bottom. The log file is not shortened. |
| `BackupCheckSeconds` | `10` | While the main connection is fine, the idle backup is checked this often (1 to 60 s). Every check uses a little mobile data. |
| `DailyCapMB` | `0` | Daily limit for data through the backup, in MB (`0` = no limit). You are told at 80 % and when it is reached. |

## Command-line options of the tray

| Option | Does |
|---|---|
| `--quit` | Asks the running copy to exit cleanly (restores the network first). No admin needed. |
| `--primary <name>` / `--backup <name>` | Fix an adapter instead of detecting it. |
| `--dry-run` | Run without changing anything on the PC. |
| `--show` | Open the window at start. |
| `--theme light\|dark` | Force a theme for this run (not saved). |

## If something goes wrong

- **Errors** inside the window never stop the tray; they appear in the Log as a red *CRASHED* row with the full details.
- **Network looks odd after a crash or a forced kill**: in an administrator terminal run
  `dotnet run --project src\Cli -- --restore --execute` (puts back the saved metrics and removes the app's firewall and QoS rules).
- **A program is blocked and should not be**: Dashboard → Blocked → *Unblock* (choose how long to leave it alone), or remove it from the Policy.
- **The dashboard shows a "VPN" badge**: a VPN refuses the check on a single connection. The app still works with less precise checks (see [ARCHITECTURE.md](ARCHITECTURE.md#vpns)). **Exact checks behind WARP.** Add the two addresses the app checks, `8.8.8.8` and `9.9.9.9`, to WARP's *Exclude IPs* list (Preferences → Advanced → Split Tunnels, or from a terminal: `warp-cli tunnel ip add 8.8.8.8` and `warp-cli tunnel ip add 9.9.9.9`). They then bypass the tunnel, the per-connection check works again, and the badge disappears by itself.
  The side effect: traffic to those two addresses (for example DNS, if you use Google's or Quad9's) is no longer protected by WARP. To undo: `warp-cli tunnel ip remove 8.8.8.8` and `warp-cli tunnel ip remove 9.9.9.9`.
