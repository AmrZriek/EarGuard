# EarGuard — CONTEXT.md

## What it is
EarGuard for Windows: hardware volume spike protector for IEM/headphone audiophiles. Runs in the system tray, hooks physical audio endpoints via WASAPI `IAudioEndpointVolumeCallback`, and brings any volume spike past a user ceiling back down. Its one volume-writing routine is lower-only: it can never raise volume. Protects against Windows Audio Service init-at-100% amnesia, USB-C dongle DAC plug-in blasts, and exclusive-mode players (Tidal/Qobuz/Foobar2000) that bypass the software mixer. Enforcement is reactive: it cannot block playback before a change or guarantee zero-latency clamping, but enforces limits via hardware callbacks and a 100ms watchdog.

## Stack
- C# / .NET Framework 4.8 (WPF, WinForms NotifyIcon tray), zero dependencies, no installer
- Build: `csc @EarGuard.rsp` (~0.5s) or `build.ps1` (runs test suite then compiles)
- Layout: `src/Audio/` (CoreAudio COM interop, engine, guarded device), `src/Config/` (settings.json in %APPDATA%\EarGuard, Task Scheduler deterministic logon startup), `src/Tray/` (tray, single-instance mutex, recovery), `src/UI/` (MainWindow.cs), `tests/` (AudioEngine, ConfigStore, TrayIconRecovery, E2E)
- Docs: `README.md`, `SPEC.md` (architecture + safety invariants), `LICENSE` (MIT)

## State on 2026-09-19
- Version: v1.1.1 (tags v1.0.0, v1.1.0, v1.1.1). HEAD `ca87851` "Add the MIT license file the README already claimed", release commit `5cb4fa6` "1.1.1: Fix volume limits after reconnecting or waking".
- Ships as single portable `EarGuard.exe` (82,432 bytes / ~80.5 KB), Windows 10/11, 0.0% idle CPU (event-driven + 100ms watchdog timer), MIT license.
- Autostart: Deterministic logon startup registered via Windows Task Scheduler (`EarGuardStartup` running `EarGuard.exe --tray`).
- Repo: https://github.com/AmrZriek/EarGuard — Releases page distributes `EarGuard.exe`; Ko-fi: https://ko-fi.com/amrzriek
- Pricing: free + open source (Ko-fi donations).

## v1.1.1 Release Highlights
- **Sleep & Reconnect Grace Window:** Keeps `SafePlugInVol` (5% default) limit active for 3 seconds (`SafePlugInGraceMs`) after device arrival or system resume from sleep/hibernate. Handles Windows Audio Service late restores where volume is raised hundreds of milliseconds after device detection.
- **Publish On Change Gating:** Writes `settings.json` and triggers UI rebuilds only when endpoint topology or default device actually changes, reducing ~30 redundant disk writes and UI sweeps to a handful of scans per event.
- **Quiet Devices Unmodified & Lower-Only:** Never touches endpoints already below limit. Removed the volume-raising test button.
- **Silent Balloon Notifications:** Clamp alerts use `NIIF_NOSOUND` so notifications never produce loud Windows chimes.
- **Dynamic Device Tracking:** Window follows Windows default playback endpoint until user manually selects an output; respects explicit manual choices.
- **Build & Layout Polish:** Pipe separator in window and tray titles; `build.ps1` produces byte-identical binaries with Roslyn; documented `csc @EarGuard.rsp` paths fixed.

## ASSETS (verified paths, repo root)
- `screenshot.png` (29.6 KB) — main UI screenshot, used in README; primary website hero/demo asset
- `EarGuard.png` (15.6 KB) — logo, used in README header; website icon/product tile
- `EarGuard-transparent.png` (15.6 KB) — transparent logo variant; website/dark-background use
- `EarGuard.ico` (15.6 KB) — embedded app icon; favicon source candidate
- `EarGuard.exe` (82.4 KB, built 2026-09-18) — current portable binary (canonical distribution via GitHub Releases)
- `LICENSE` (1 KB) — official MIT license file
- `archive/` — legacy assets and design prototypes

## Session log
- 2026-09-13 (Hermes recon): First inventory. Wrote CONTEXT.md + `docs/website-brief.md`.
- 2026-09-18 (Amr / Hermes): v1.1.1 release and license addition. Sleep/wake volume limits, quiet reconnects, build script and rsp fixes, MIT license file added.
- 2026-09-19 (Hermes maintenance): Validated full test suite (ConfigStore, AudioEngine, TrayIconRecovery, E2E all pass). Updated CONTEXT.md with v1.1.1 specs, Task Scheduler details, and asset metrics.
