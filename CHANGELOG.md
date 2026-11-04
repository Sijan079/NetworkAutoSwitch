# Changelog

All notable changes to Network Auto Switch are listed here, newest first.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the version numbers follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html) (the rules for this project are in `docs/RELEASING.md`).

Every release must have its own `## [x.y.z]` section: the release workflow copies it into the GitHub release notes and refuses to run without it.

## [Unreleased]

### Added
- **Settings page**: Appearance (theme), how many seconds the main connection must work before switching back, mute notifications, and the Global Trigger (moved from the Policy page).
- **Network names** (for example `SNcial_5G`) on the connection cards, the Dashboard banner and the tray status.
- **Loading placeholders**: while the app changes the network (for example after you swap main and backup) the cards show grey bars until the first new measurement arrives.
- *Edit As JSON* on the Policy page (checked before it is accepted).

### Changed
- The JSON tab is gone from the navigation; the theme button left the Dashboard banner (it is in Settings now).
- *Choose Connections* closes at once and applies the change in the background.

## [0.1.0] - 2026-11-04

First release.

### Added
- Keeps a main connection (Wi-Fi) and a backup (for example a phone tether) and switches between them. An outage is noticed within seconds: at once if Windows reports the link down, in about 4 seconds if packets simply stop.
- Switching back to the main connection after 10 good checks in a row, so a flickering connection does not bounce you around.
- **Adapters are detected automatically** (Wi-Fi, Ethernet, phone tethers over USB, Bluetooth or mobile broadband; VPNs, virtual machines and driver layers are ignored) and re-detected when something is plugged in or changes. You can choose them yourself in *Choose Connections*.
- **Mode**: automatic, or locked to one connection with no automatic switching.
- Policy for the backup connection: watch rules block a program only while it downloads fast, block rules, and upload caps. Blocks last until you lift them. Add rules from a program picker, from the backup data table, or by dragging an `.exe`.
- When the main connection returns, downloads still running on the backup are moved over to it.
- Tray app with a dashboard (live status, latency chart, data used per program), a live colored log, and a policy editor. Light, dark, or follow-Windows theme.
- Installer for Windows 10 and 11 (no .NET installation needed): optional start at sign-in, upgrades in place, never downgrades, restores your network settings on uninstall.
