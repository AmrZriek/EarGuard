# EarGuard — Project Context

## Current state

The working version is **v1.1.3**: assembly/file version `1.1.3.0`, informational version `1.1.3`. The last committed public version is **v1.1.2**. The v1.1.3 changes are uncommitted and have not been published.

This is a reliability patch. It improves audio-device discovery and callback recovery, settings persistence and backup recovery, startup registration, live-volume readings, and logo handling.

## Application

EarGuard is a portable Windows 10/11 tray app for limiting unexpected output-volume changes. It uses C# and .NET Framework 4.8, WPF for the window, WinForms for the tray icon, and Windows CoreAudio for endpoint control. There are no external dependencies.

Protection is reactive. Callbacks and a 100 ms watchdog lower volume above the chosen ceiling; they cannot prevent every brief spike or guarantee hearing safety. Exclusive-mode behavior depends on hardware and driver support. Windows provides no atomic read-and-set against other volume writers.

A three-second grace period applies the Safe Plug-in Limit (5% by default) after device arrival or resume. Protection is per device. The window follows the default output until the user chooses a device for the session.

## Code and documentation

- `src/Audio/`: CoreAudio interop, discovery, device protection, and event publication.
- `src/Config/`: settings persistence, per-user installation, and Task Scheduler startup registration.
- `src/Tray/`: tray icon, notifications, single-instance handling, and shell recovery.
- `src/UI/`: the main window and its settings controls.
- `tests/`: regression tests; real audio integration is opt-in.
- `README.md`: public product description, setup, and user-facing release notes. Keep implementation and review details out of it.
- `SPEC.md`: architecture, safety constraints, and build/test procedures.

## Startup and tray behavior

Settings live at `%APPDATA%\EarGuard\settings.json`. Enabling Start with Windows installs a durable executable at `%LOCALAPPDATA%\EarGuard\EarGuard.exe` and registers the `EarGuardStartup` logon task. Moving the original download does not break this registration.

Task Scheduler startup does not appear in Task Manager's Startup apps list. Windows controls tray-icon placement. Keep the durable executable path and initial tooltip stable because they affect the shell's stored icon identity.

The installed copy contains only the executable. Logo loading must support the embedded icon, and owned icon handles must remain alive while the tray uses them.

## Project links

Repository: https://github.com/AmrZriek/EarGuard

License: MIT. Optional donations: https://ko-fi.com/amrzriek
