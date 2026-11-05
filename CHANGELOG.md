# Changelog

All notable changes to Network Auto Switch are listed here, newest first.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the version numbers follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html) (the rules for this project are in `docs/RELEASING.md`).

Every release must have its own `## [x.y.z]` section: the release workflow copies it into the GitHub release notes and refuses to run without it.

## [Unreleased]

### Added
- **Sorting on Network Activity**: every column header has a small sort button at its end (Program, Total, On Backup, Trigger, Watch List). Click a header, or its button, to sort by that column; click again to switch between ascending and descending. The table lists every program that used the network (it scrolls), not only the top ten.
- **Trigger** (was *Peak 10 s*) is the most a program downloaded in any 10 seconds today, measured like the watch trigger, and shown in amber when it is above it. The hint on the card explains it, and what *On* means (Main, Backup or Both).
- **Network Activity** (the Dashboard table that used to be *Backup Data*): what each program moved today (**Total**, download plus upload, on the **main connection and on the backup**) and **how much of it went through the backup** (*On Backup*). Hover a row for the download and upload split. It is no longer tied to the backup; the Blocked and Allowed cards still show what the watchdog did. *Backup Data Today* (the daily limit) is unchanged.
- **Game bundles**: one watch rule can now cover several folders (`folders` in the policy file; one folder per line in the Policy page), so a game's launcher, updater **and the game itself** are one entry. Picking a launcher and its game together in *Choose Programs* keeps both folders. The `folder` field still works as before.
- **Main and Backup network tables** (Mode, then Choose Connections). Sort your networks by name; the role belongs to the network, so it holds on whichever adapter that network is on. A mode applies to both tables: *Blacklist* (listed networks are barred from that role) or *Whitelist* (only listed networks may have it).
- **Ask once**: a network on neither table gets a prompt (Main, Backup or Later). Until you sort it, it is treated as a backup.
- **A backup network is protected even when it is the main connection**: watch-list downloads are blocked on it and its data counts toward the daily limit. A network that may not be the backup is never used as the backup.
- **SAME NETWORK guard**: both connections on one router (compared by the router's address and hardware address) show a badge, a log warning and a notification, because switching cannot help when both fail together.
- A *MAIN IS PHONE* badge on the Dashboard while the main connection is protected.

### Changed
- **Dashboard layout**: *Session* is a narrow card (a quarter of the width) as high as the two cards beside it; *Network Activity* sits above *Blocked*, and Blocked is now always on the dashboard (with a short note while nothing is blocked). *Backup Data Today* reads like "1.31 GB / 10 GB" so it fits.
- **A loud warning when your main connection is your phone.** A red banner appears under the status row ("Main connection is on SNcial, a backup network. Mobile data may be in use.") with a *Choose Connections* button, and a tray pop-up that **ignores the mute setting** appears. The same goes for both connections being on one network. A network that is only "not sorted yet" or "not identified yet" gets an amber banner and no pop-up.
- **Dashboard connection cards**: network name, adapter, average ping and uptime on the left; the current ping on the right with "ms" below the number. A connection that does not answer shows a red dash and nothing below it, instead of "Down".
- The connection in use is shown in full colour (green ping, bright network name); the other card is muted grey, as well as having no coloured border.
- The Dashboard banner now reads "On Main: SNcial_5G" or "On Backup: SNcial" (the adapter name if the network name is not known).
- The latency chart's legend (the two adapter names) now sits in the header row next to the title.
- Choose Connections: the adapter pickers moved under *Advanced*; they only matter when the network tables cannot decide.
- When both adapters are on one network, or the main connection is on a backup network, the engine now protects the main connection's adapter too (rules, watchdog, block rules and data count cover every protected adapter).

### Fixed
- **The download watchdog no longer drops data from programs it cannot name.** The program of a busy process is now found the way Windows' own task tools find it (it also works for protected processes, such as game anti-cheat), a failed lookup is retried after 5 seconds instead of being remembered for good, and when data still comes from a program that cannot be named, the log says so ("WARNING: ... MB came in ... from programs Windows would not name").
- **A network Windows has not identified yet is protected**, not trusted: the main connection is protected until Windows names it, and the app rechecks every 2 seconds (it used to wait up to 30 seconds with no protection). If Windows gives no names at all, the app says so once.
- The same-network check no longer reports a false alarm when the router's hardware address cannot be read and the two networks have different names. The lookup is also tried twice.
- The data tile and the Backup Data card now say the main connection counts too while it is protected as a backup network.
- The tray menu, tooltip and the Dashboard's mode button use "Main" and "Backup" (with the network name) instead of adapter names.
- Removing the app's firewall rules could run at the same time as adding new ones, deleting them. Removal now finishes first.
- Windows' stand-in network name `Identifying...` is no longer treated as a network (it was being asked about and could be saved).

## [0.2.0] - 2026-11-04

### Added
- **Daily data limit for the backup connection** (Settings → Backup Data). You get a pop-up when the backup has carried 80 % of the limit today and when it is reached; the Dashboard shows *Backup Data Today*. The count is everything that goes through the backup adapter, survives restarts, and starts again at midnight.
- **Setting: how often the idle backup is checked** (default every 10 s).
- **Setting: how many log lines the Log page keeps on screen** (Settings → Log, default 2000). The oldest lines drop off the bottom, so the newest are always there; the log file itself is not shortened.
- **Data per program is kept across restarts** (for the same day): the *Backup Data* table now shows today's totals and starts again at midnight.
- **First-run welcome** on a brand-new install: shows the main and backup connection the app found, and lets you fix them or turn on start at sign-in before anything happens.
- **Check For Updates** in Settings (About). It asks GitHub only when you click it, and offers a button to open the release page.
- **Start When I Sign In** in Settings (Startup): turn the sign-in start on or off without reinstalling. It is the same setting as the installer's option.

### Changed
- While the main connection is fine, the idle backup is now checked every 10 s instead of every second, which saves mobile data on a phone tether. Going to the backup is just as fast: it is checked at once when the main connection starts failing.
- The *VPN: Limited Checks* badge is now just *VPN*. Its tooltip, and the docs, now say how to get exact checks behind WARP (exclude `8.8.8.8` and `9.9.9.9` from the tunnel), and the Dashboard's Session card fits a fourth tile.

## [0.1.0] - 2026-11-04

First release. Everything below is new.

### Added
- Keeps a main connection (Wi-Fi) and a backup (for example a phone tether) and switches between them. An outage is noticed within seconds: at once if Windows reports the link down, in about 4 seconds if packets simply stop.
- Switching back to the main connection after 10 good checks in a row, so a flickering connection does not bounce you around.
- **Adapters are detected automatically** (Wi-Fi, Ethernet, phone tethers over USB, Bluetooth or mobile broadband; VPNs, virtual machines and driver layers are ignored) and re-detected when something is plugged in or changes. You can choose them yourself in *Choose Connections*.
- **Mode**: automatic, or locked to one connection with no automatic switching.
- Policy for the backup connection: watch rules block a program only while it downloads fast, block rules, and upload caps. Blocks last until you lift them. Add rules from a program picker, from the backup data table, or by dragging an `.exe`.
- When the main connection returns, downloads still running on the backup are moved over to it.
- Tray app with a dashboard (live status, latency chart, data used per program), a live colored log, and a policy editor. Light, dark, or follow-Windows theme.
- Installer for Windows 10 and 11 (no .NET installation needed): optional start at sign-in, upgrades in place, never downgrades, restores your network settings on uninstall.
- **Settings page**: Appearance (theme), how many seconds the main connection must work before switching back, mute notifications, and the Global Trigger (moved from the Policy page).
- **Network names** (for example `SNcial_5G`) on the connection cards, the Dashboard banner and the tray status.
- **Loading placeholders**: while the app changes the network (for example after you swap main and backup) the cards show grey bars until the first new measurement arrives.
- *Edit As JSON* on the Policy page (checked before it is accepted).
- **Works with VPNs such as Cloudflare WARP.** They refuse the internet check on a single connection, which would look like an outage. The app then checks the connection in use through the VPN and the other one by its gateway, shows a *VPN: Limited Checks* badge, and waits longer before each new try of moving back if the main connection fails again soon after.
