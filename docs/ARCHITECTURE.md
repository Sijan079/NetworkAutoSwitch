# Architecture

How Network Auto Switch works, why it is built this way, and where to find things in the source.

## The idea in one picture

```
            every second
   ┌──────────────────────────────────────────────────────────────────────────┐
   │ SwitchEngine (Core)                                                      │
   │                                                                          │
   │  AdapterDiscovery ─► which two adapters are main and backup              │
   │          │                                                               │
   │  AdapterProber ───► is there real internet through EACH adapter?         │
   │          │              (VpnFallback steps in when a VPN refuses this)   │
   │  FailoverMonitor ─► main or backup? (pure rules, no network calls)       │
   │          │                                                               │
   │  MetricSwitcher ──► make Windows prefer that adapter (metrics + routes)  │
   │          │                                                               │
   │  BackupPolicy / DownloadGuard ─► rules for the backup, the download      │
   │          │                       watchdog (bytes counted through ETW)    │
   │  TcpMigrator ─────► on fail-back, reset backup connections so            │
   │                       downloads reconnect over the main connection       │
   └──────────────────────────────────────────────────────────────────────────┘
        ▲ events (Status, AdaptersChanged, Busy, WatchdogBlocked, Log)
        │
   Tray (WinForms window + tray icon)        Cli (diagnostics and tests, no UI)
```

The **engine is shared** by the tray app and the command-line tool, and it is the only thing that changes the network. Whatever stops it (exit, pause, crash handler, uninstall) puts the original settings back.

## Design decisions

**Check each adapter separately, without adding routes.** An earlier prototype (`docs/legacy/probe-adapters.ps1`) added temporary routes to test each adapter. That needs administrator rights and can leave routes behind if it is killed.
The app instead opens a TCP connection with a socket **pinned to one adapter** (`IP_UNICAST_IF`). Two public servers are tried at once (8.8.8.8 and 9.9.9.9), and either answering counts, so one provider's problem is never mistaken for an outage.

**Cheap on mobile data.** A check on a phone tether costs a little data, so while the main connection is fine the idle backup is checked every 10 s (`ProbeSchedule`), not every second. It is checked at once when the main connection starts failing, when the backup is in use, and when it is down (a down backup costs nothing), so a switch never rests on stale news.
Data used on the backup is counted from the adapter's own byte counter (`DataBudget`, `CounterDelta`), saved to `usage.json`, and compared with the daily limit.

**Fast to leave, slow to return.** Windows saying "link down" switches at once. A quiet failure needs 3 failed checks in a row (about 4 s, and the engine re-checks after 150 ms once the main connection starts failing). Moving back needs a number of good seconds you choose in Settings, so a flickering connection cannot bounce the app between the two.

**Metrics, not routes.** Preferring an adapter means giving it the lowest interface metric (5) and the other a high one (500), and setting the preferred adapter's default-route metric to 0 (a baked-in route metric once let the phone win by 5).
After switching, the app asks Windows which adapter a new connection really takes (`Find-NetRoute`), and if it is the wrong one it pushes the other adapter much further back. The original values are saved to `state.json` first.

**Policy only on the backup.** Rules are Windows Firewall blocks and QoS policies scoped to the backup adapter, so they never affect the main connection. They are removed the moment the main connection returns, and removed again on exit. Blocks never expire by themselves; the user decides.

**Know downloads without a driver.** Per-program bytes come from Windows' own kernel network events (ETW, `Microsoft-Windows-Kernel-Network`), so nothing has to be installed. `DownloadGuard` is pure logic (bytes in, decisions out) so it is tested without any network.

**Move running downloads.** A download that began on the backup would keep using it. On fail-back, `TcpMigrator` resets (`SetTcpEntry` DELETE_TCB) the connections of watched programs, and of anything pulling more than 1 MB per 10 s, so the program reconnects over the main connection and resumes. Games and light traffic are left alone.

<a id="vpns"></a>
### VPNs

Some VPNs (Cloudflare WARP in full-tunnel mode, for one) refuse every connection forced onto a physical adapter: the pinned check fails with `AccessDenied` although the internet works.
Treating that as an outage would switch to the backup for no reason, so `VpnFallback` takes over:

- the connection **in use** is checked the normal way, through the VPN (the VPN follows whichever adapter Windows prefers), so a real outage is still detected;
- the **idle** connection is judged by its link and a ping to its gateway (the app cannot confirm it has internet);
- because moving back is then a guess, a failed attempt soon after moving back doubles the wait before the next one (up to 5 minutes), so a Wi-Fi that is up but has no internet cannot make the app bounce.

The dashboard shows a *VPN* badge while this is active. A VPN tunnel that sits on top of the two adapters is not reported as a routing problem.

I tested the other ways to measure one adapter (pings and DNS requests sent from the adapter's address, and connections to the VPN's own servers): a full-tunnel VPN blocks all of them, so nothing on our side can get around it.
What does work is **excluding the two check addresses from the VPN** (`8.8.8.8` and `9.9.9.9`, see [CONFIGURATION.md](CONFIGURATION.md#if-something-goes-wrong)): they then bypass the tunnel, and the exact check works again. The app notices by itself and drops the badge.

## Source layout

```
NetworkAutoSwitch.sln
Directory.Build.props      the ONE place for the version number and product details
src/Core/                  everything that is not UI (net9.0-windows)
src/Tray/                  the tray app and window (WinForms)
src/Cli/                   nas-cli: diagnostics and the test runner, no UI
config/                    policy.json (default policy), policy-test.json (tiny policy for tests)
installer/                 NetworkAutoSwitch.iss (Inno Setup)
docs/                      these documents; docs/legacy holds the original prototype
.github/workflows/         ci.yml (build + tests), release.yml (installer + GitHub release)
```

### `src/Core`

| File | Role | Pure logic? |
|---|---|---|
| `SwitchEngine.cs` | The loop: detect adapters → probe → decide → switch → policy → restore. Raises the events the UI listens to. | no |
| `FailoverMonitor.cs` | The switching rules: when to leave, when to return. | **yes** |
| `AdapterDiscovery.cs` | Lists adapters, classifies them (Wi-Fi, Ethernet, tether, virtual) and picks main and backup. | mostly |
| `AdapterProber.cs` | The per-adapter health check (pinned TCP). | no |
| `VpnFallback.cs` | What to do when a VPN refuses the pinned check, and the fail-back backoff. | **yes** |
| `MetricSwitcher.cs` | Builds the PowerShell commands that set metrics and routes, and restore them. | commands are pure |
| `BackupPolicy.cs` | The policy model, parsing, validation, and the firewall/QoS commands. | mostly |
| `PolicyBuilder.cs` | Turns picked programs into a rule; says which programs a rule covers. | **yes** |
| `DownloadGuard.cs` | The download watchdog and the ETW byte counter. | guard is **yes** |
| `TcpMigrator.cs` | Lists and resets TCP connections (Windows API). | no |
| `NetworkNames.cs` | Which Wi-Fi network each adapter is on (for example `SNcial_5G`). | parsing is **yes** |
| `DataBudget.cs` | Today's data on the backup, the daily limit levels (80 %, reached), and `ProbeSchedule` (how often the idle backup is checked). | **yes** |
| `Stats.cs`, `LogClassifier.cs` | Session statistics; what kind of log line this is (for coloring). | **yes** |
| `AppSettings.cs`, `Paths.cs`, `AppInfo.cs` | Settings file, where files live, version and SemVer comparison. | mostly |

"Pure" means no network, no process, no Windows state: the function gets values and returns a result, so it is covered by the fast unit tests.

### `src/Tray`

| File | Role |
|---|---|
| `TrayProgram.cs` | Start-up, the tray icon and menu, notifications, the quit listener, theme changes. |
| `MainForm.cs` | The window: Dashboard, Log, Policy and Settings pages. |
| `Ui.cs` | The design system: theme palette, icons, buttons, cards, tabs, loading placeholders, the adapter card. |
| `PolicyEditor.cs`, `GlobalTriggerCard.cs` | The rules editor and the global trigger card. |
| `ProgramPicker.cs`, `ConnectionsDialog.cs`, `JsonDialog.cs` | Dialogs. |
| `LatencyChart.cs`, `Logo.cs`, `NoWheel.cs`, `UiSelfTest.cs` | The chart, the app icon drawn in code, controls that ignore the mouse wheel, and a UI test helper. |

Every color comes from the palette in `Ui.cs`, and the window is rebuilt when the theme changes. There are no nested cards: each card sits directly on its page.

### `src/Cli` (`nas-cli`)

One file, `Program.cs`: the diagnostic commands and `SelfTest`, the unit-test runner (`--selftest`) and the engine test (`--engine-test`). See [DEVELOPMENT.md](DEVELOPMENT.md).
