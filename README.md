# Network Auto Switch

[![CI](https://github.com/Sijan079/NetworkAutoSwitch/actions/workflows/ci.yml/badge.svg)](https://github.com/Sijan079/NetworkAutoSwitch/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Sijan079/NetworkAutoSwitch)](https://github.com/Sijan079/NetworkAutoSwitch/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A Windows tray app that keeps **two internet connections**, a main one (for example Wi-Fi) and a backup (for example a phone tether), and moves between them for you.

- **Main connection loses internet** → it switches to the backup within seconds, and applies your **policy** so big downloads cannot eat your mobile data.
- **Main connection comes back** → it switches back, lifts every policy block, and moves downloads that were running on the backup over to the main connection.
- **You can lock it** to one connection if you do not want automatic switching.

Windows 10 and 11 (64-bit). It needs administrator rights, because it changes network metrics and firewall rules. The installer can set the app up to start already elevated when you sign in.

## Contents

- [Install](#install)
- [Using the app](#using-the-app)
- [How it works, in short](#how-it-works-in-short)
- [Documentation](#documentation)
- [Build from source](#build-from-source)
- [Known limits](#known-limits)
- [Forking and contributing](#forking-and-contributing)
- [License](#license)

## Install

Download **`NetworkAutoSwitch-Setup-<version>.exe`** from the [Releases](https://github.com/Sijan079/NetworkAutoSwitch/releases/latest) page and run it.
Nothing else needs installing: the app carries its own .NET. A `.sha256` file sits next to the installer if you want to verify the download.

- **Start when I sign in** (ticked by default): the app starts by itself 30 seconds after you sign in, already elevated, with no prompt.
- **Upgrading**: run a newer installer over the old one. It closes the running app the normal way (which puts your network settings back), replaces it, and keeps your settings and policy. It never installs an older version over a newer one.
- **Uninstalling** (Windows *Apps and features*): puts your network settings back, removes the sign-in task, and asks whether to keep your settings, policy and log.

## Using the app

The tray icon shows the state: **green** = on the main connection, **amber** = on the backup, **red** = nothing works, **grey** = paused. Double-click it to open the window.

| Page | What it is for |
|---|---|
| **Dashboard** | Live status, both connections (latency, uptime, and the Wi-Fi network each one is on), a latency chart, session numbers, and the data each program used on the backup (with one-click *Add To Watch List*). Panels for **Blocked** programs (Unblock) and **Allowed For Now** (Block Now / Watch Again) appear when needed. The **Mode** button chooses Automatic or stay on one connection; **Pause** puts Windows fully back to normal. |
| **Log** | Live, newest first, colored by type (switch, block, unblock, error, info). Select rows and press Ctrl+C to copy. |
| **Policy** | The rules for the backup connection. *Add Rule* opens a program picker (programs that used backup data first, then running ones); you can also drag an `.exe` onto the page. *Edit As JSON* edits the same rules as text. |
| **Settings** | Theme, how long the main connection must be good before switching back, mute notifications, **start when I sign in**, the **daily data limit** and check interval for the backup, the Global Trigger (how much a watched program may download before it is blocked), and **Check For Updates**. |

**Exit** (tray menu) restores everything first. A VPN such as Cloudflare WARP is supported; the dashboard then shows a *VPN* badge (see [How it works](docs/ARCHITECTURE.md#vpns)).

## How it works, in short

1. **Check.** Every second the app opens a short TCP connection to two public servers through **each adapter separately** (a socket pinned to the adapter). No routes are added.
2. **Decide.** Main link reported down by Windows → switch at once. Main merely failing → **3 failed checks in a row** (about 4 s). Back to main → after the number of seconds you set in Settings (default 10) of good checks, so a flickering Wi-Fi does not bounce you around.
3. **Switch.** The preferred adapter gets the lowest interface metric and the other the highest, then the app asks Windows which adapter new connections really use and corrects it if wrong. Original values are saved and restored on exit or pause.
4. **Protect the backup.** Watch rules block a program on the backup **only while it downloads fast**. Blocks last until you lift them. Games are never cut off unless you list them.
5. **Move downloads back.** When the main connection returns, running downloads on the backup are reset so they reconnect over the main connection.

The details, and why each choice was made, are in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Documentation

| Read this | When you want to |
|---|---|
| [docs/CONFIGURATION.md](docs/CONFIGURATION.md) | Edit the policy file, choose adapters, find the settings, log and other files |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Understand the design and find your way around the source code |
| [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) | Build, run the tests, use the diagnostic commands |
| [docs/RELEASING.md](docs/RELEASING.md) | Version numbers, the installer, and how a release is made |
| [CHANGELOG.md](CHANGELOG.md) | See what changed in each version |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Send a change |

## Build from source

Needs the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```powershell
dotnet publish src\Tray -c Release -o dist\Tray
dist\Tray\NetworkAutoSwitchTray.exe       # Windows asks for administrator rights once, then it sits in the tray
```

To stop a running copy from a script: `dist\Tray\NetworkAutoSwitchTray.exe --quit` (no administrator rights needed; it restores your network settings first).
Running the tests and building the installer: [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## Known limits

- The check is a plain connection test, so a **captive portal** that accepts connections but gives no internet is not detected.
- A **brief dropout of about 4 s** is enough to trigger a switch (the price of fast detection); switching back is deliberately slower.
- Download **speed limits** are not possible with built-in Windows tools (only upload caps), so the watchdog blocks instead.
- Connections that are not heavy and not on the watch list (for example a game session) stay on the backup until they reconnect on their own.
- IPv4 only.
- It manages **two** connections at a time. With three or more real adapters you choose which two in *Choose Connections*.
- Behind a **full-tunnel VPN** the app cannot confirm that the idle connection has internet before switching to it, only that its link and gateway are up. Excluding `8.8.8.8` and `9.9.9.9` from the VPN gives exact checks again ([how](docs/CONFIGURATION.md#if-something-goes-wrong)).

## Forking and contributing

Forks are welcome. Start with [CONTRIBUTING.md](CONTRIBUTING.md) (how to build, test, and what a good change looks like) and [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) (where things live).
The repository is laid out so the logic that decides things has **no UI and no network calls** and is covered by fast unit tests, so most changes can be checked in seconds.

If you fork and publish your own installer, change the product details in `Directory.Build.props` and a new `AppId` in `installer/NetworkAutoSwitch.iss`, so it does not upgrade (or get mistaken for) this project's installation.

## License

[MIT](LICENSE) © 2026 Sijan079. Free to use, change and share.
