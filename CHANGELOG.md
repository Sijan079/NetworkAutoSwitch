# Changelog

All notable changes to Network Auto Switch are listed here, newest first.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the version numbers follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html) (the rules for this project are in `docs/RELEASING.md`).

Every release must have its own `## [x.y.z]` section: the release workflow copies it into the GitHub release notes and refuses to run without it.

## [Unreleased]

## [0.3.0] - 2026-11-05

This release stops the app from trusting an adapter to be "the backup": **the role now belongs to the network.** It came from a real mistake: a PC with both adapters on a phone's hotspot used several GB of mobile data on what the app believed was the main connection.

### Added
- **Main and Backup network tables** (Mode, then Choose Connections). Sort your networks by name; the role belongs to the network, so it holds on whichever adapter that network is on, and swapped adapters do no harm. One mode applies to both tables: *Blacklist* (a table lists the networks barred from that role: barred from main = always backup) or *Whitelist* (a table lists the only networks that may have that role). Each mode keeps its own tables.
- **Ask once**: a network on neither table gets a prompt (Main, Backup or Later). Until you sort it, it is treated as a backup.
- **A backup network is protected even when it is the main connection**: heavy downloads on your watch list are blocked on it, the download watchdog covers it, and its data counts toward the daily limit. A network that may not be the backup is never used as the backup.
- **A loud warning when your main connection is your phone**: a red banner under the status row ("Main connection is on SNcial, a backup network. Mobile data may be in use.") with a *Choose Connections* button, and a tray pop-up that **ignores the mute setting**. A network that is only "not sorted yet" or "not identified yet" gets an amber banner and no pop-up.
- **Same-network guard**: when both connections are on one router (compared by the router's address and hardware address), the dashboard, the log and a notification say so, because switching cannot help when both fail together.
- **Network Activity** (the dashboard table that used to be *Backup Data*): what each program moved today (**Total**, download plus upload, on the main connection and on the backup), **how much of it went through the backup** (*On Backup*), and **Trigger**: the most it downloaded in any 10 seconds, measured like the watch trigger and shown in amber when it is above it. Hover a row for the download and upload split. *Backup Data Today* (the daily limit) is unchanged.
- **Sorting**: every Network Activity header has a small sort button at its end (Program, Total, On Backup, Trigger, Watch List); click again to switch between ascending and descending. The table lists every program, not only the top ten.
- **Game bundles**: one watch rule can cover several folders (`folders` in the policy file; one folder per line in the Policy page), so a game's launcher, updater **and the game itself** are one entry. Picking a launcher and its game together in *Choose Programs* keeps both folders. The `folder` field still works as before.
- **The dashboard and the Settings page scroll without a scrollbar**: with the mouse wheel, a touchpad, or PageUp / PageDown / Home / End. A window that is too small slides the content instead of squeezing it.

### Changed
- **After upgrading from 0.2.0 your networks start unsorted.** The first time the app sees each network it asks once whether it is Main or Backup. Until you answer, an unsorted network is treated as a backup, so downloads on your watch list are blocked on it (also on your main connection). Answer the prompt, or open *Choose Connections*, to sort them. Your saved adapter choice still works when the tables cannot decide.
- **Dashboard redesign**: the connection cards show the network name, adapter, average ping and uptime, with the current ping on the right ("ms" under the number; a connection that does not answer shows a red dash); the connection in use is in full colour and the other one is muted. The status line reads "On Main: SNcial_5G" or "On Backup: SNcial". *Session* is a narrow card as tall as the two beside it, *Network Activity* sits above *Blocked*, and *Blocked* is always on the dashboard (with a short note while nothing is blocked). The latency legend sits in the header row.
- Choose Connections: the adapter pickers moved under *Advanced*; they only matter when the network tables cannot decide.
- The tray menu, tooltip and the mode button say "Main" and "Backup" (with the network name) instead of adapter names.

### Fixed
- **The download watchdog no longer drops data from programs it cannot name.** A program's path is now found the way Windows' own task tools find it (protected processes, such as game anti-cheat, too), a failed lookup is retried after 5 seconds instead of being remembered for good, and data that still comes from a program that cannot be named is reported in the log.
- **A network Windows has not identified yet is protected**, not trusted: the main connection is protected until Windows names it, and the app rechecks every 2 seconds.
- Removing the app's firewall rules could run at the same time as adding new ones and delete them. Removal now finishes first.
- Windows' stand-in network name `Identifying...` is no longer treated as a network.
- The same-network check no longer raises a false alarm when the router's hardware address cannot be read and the two networks have different names.
- Text cut off in the *Session* card and the connection cards (small windows, other font sizes or display scaling).

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
