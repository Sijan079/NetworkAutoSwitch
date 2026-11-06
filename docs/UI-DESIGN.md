# NetworkAutoSwitch: UI redesign brief (hand-off for Claude Code)

Status: design approved by the user (2026-11-06). Implementation not started. Design mockups (8 screens, private canvas): https://claude.ai/artifact/PxPx6rry9q5AiZ8sSJNcJo Repo: https://github.com/Sijan079/NetworkAutoSwitch. See `repo-docs-digest.md` and `function-index.md` for the code layout.

## Decisions

- Stay on **WinForms** (.NET 9) with custom-drawn (GDI+) controls. WPF was tried and rejected as "not my style". WebView2 rejected: RAM cost and size.
- **Do not touch** **`SwitchEngine`** **or Core behaviour.** UI only (`src/Tray`).
- Hard limits: total app **under 50 MB**; performance matters (tray app, mostly hidden).
- **Font: Inter**, bundled. Weights: Regular, SemiBold, Bold (static TTF, SIL OFL, ship license text). Embed as assembly resources. Register at startup with `AddFontMemResourceEx` (process-private, so GDI/`TextRenderer` sees it) and also load into a `PrivateFontCollection` for GDI+. Fall back to Segoe UI in `Ui.UiFont` if loading fails. Inter looks lighter at 11-12 px: use SemiBold or +0.5 pt for small labels if thin.

## Palette (put in `Ui.cs`; the repo rule is colours only from there)

Dark: ground `#0f1419`, surface `#171d25`, raised `#1f2731`, line `#2b3541`, text `#e8edf3`, muted `#9aa7b6`. Roles: main/healthy teal `#3ddc97`, backup/standby amber `#f5b04c`, blocked/error red `#ff6b6b`, info blue `#6cb2ff`. Text on teal fill `#0b1a12`. Light theme still needed (Theme setting is System/Light/Dark): derive a light palette with the same roles, check contrast.

## Layout

Window about 1120 px wide. Left nav rail (216 px): logo + name + version, 4 items (Dashboard, Activity Log, Backup Rules, Settings), engine status box at the bottom. Replaces the current tab strip. Header: title, one-line subtitle, action buttons on the right. Content: cards (radius 12, 1 px line border), no nested cards.

## Control kit to build first (all self-painted, double-buffered)

Card, Chip (outlined or filled), Button (primary / secondary / danger), Segmented control, Toggle switch (role=switch for accessibility), Field (label + value box + suffix), Section header (uppercase, muted), Table row/grid, Nav item (active = raised bg + 3 px teal left bar), Banner (red alarm), LatencyChart restyle.

## Screens (see the mockups)

1. **Dashboard**: Main and Backup connection cards (kind label, network, adapter, status/latency/uptime, IN USE or STANDBY chip), mode segmented (Automatic / Stay On Main / Stay On Backup), latency chart + daily data cap meter, Network Activity table, Watchdog table (Blocked/Allowed/Watching with Allow / Block Now buttons and countdown).
2. **Dashboard, alarm state**: red banner when main is the phone or both are one network (ignores mute); main card shows protections active.
3. **Activity Log**: filter segmented (All, Switches, Blocks, Errors, Commands), search, mono list with time, coloured kind chip (from `LogClassifier` kinds), message.
4. **Backup Rules**: global trigger (MB per window), rule list with action chips (Watch / Block / Upload Cap), rule editor (name, action, programs, folders, own trigger, Save And Apply, Edit As JSON, Remove).
5. **Settings**: adapters, switching timings (RecoverSeconds, BackupCheckSeconds, DailyCapMB), theme, start at sign-in, mute notifications, check for updates, update card.
6. **Choose Connections** dialog: Blacklist/Whitelist, Main and Backup network lists, saved Wi-Fi chips.
7. **What Is This Network?** prompt (Main / Backup / Decide Later).
8. **Welcome** dialog (3 steps).

## Performance rules

- No UI updates while the window is hidden; engine keeps running.
- Repaint only when data changed; invalidate dirty rectangles; chart redraws only on a new sample.
- Cache brushes, pens, fonts, rounded `GraphicsPath`s; never allocate per paint.
- Anything that talks to Windows stays off the UI thread (existing rule).
- Handle DPI scaling (per-monitor) for custom-painted controls.

## Repo conventions to keep

Title Case labels; explanations in tooltips; colours only from `Ui.cs`; network changes only via `SwitchEngine`; comments explain why. `--selftest` and `--engine-test` must pass; `UiSelfTest` / `--snapshot` can render screens for review.

## Suggested order

1. Font loader + palette in `Ui.cs` + control kit.
2. Dashboard (both states). 3. Nav rail shell. 4. Log, Rules, Settings. 5. Dialogs. 6. Light theme pass, DPI pass, size and memory check. Ask the user for current installer size and memory use (window open) before and after.
