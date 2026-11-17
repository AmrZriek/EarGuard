# EarGuard — Project Context

## Current state

The working version is **v1.1.3**: assembly/file version `1.1.3.0`, informational version `1.1.3`. Tagged and released on 2026-10-09 with a clean working tree; it is the latest published GitHub release (`EarGuard.exe`, 94,208 bytes, SHA-256 `109166E5B50690570AF410F70C00A2402060C18F448C8A92347FE3B92959BEC7`). The previous published version is **v1.1.2**.

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
- [docs/README.md](docs/README.md): documentation router, contributor entry points, retained-history policy, and existing semantic retrieval setup.
- [docs/SPEC.md](docs/SPEC.md): architecture, safety constraints, and build/test procedures.

## Startup and tray behavior

Settings live at `%APPDATA%\EarGuard\settings.json`. Enabling Start with Windows installs a durable executable at `%LOCALAPPDATA%\EarGuard\EarGuard.exe` and registers the `EarGuardStartup` logon task. Moving the original download does not break this registration.

Task Scheduler startup does not appear in Task Manager's Startup apps list. Windows controls tray-icon placement. Keep the durable executable path and initial tooltip stable because they affect the shell's stored icon identity.

The installed copy contains only the executable. Logo loading must support the embedded icon, and owned icon handles must remain alive while the tray uses them.

## Project links

Repository: https://github.com/AmrZriek/EarGuard

License: MIT. Optional donations: https://ko-fi.com/amrzriek

## Organization verification — 2026-10-26

Moved the active specification and unpublished Gumroad draft to `docs/SPEC.md` and `docs/marketing/GUMROAD.md`, with byte-identical contents and routing through `docs/README.md`, preserving the pre-existing README edits. Resume verification confirmed unchanged source, tests, build files, assets and root executable, valid live local links, and a clean `git diff --check`; earlier logs record Framework success and Roslyn success on retry after an icon-handle assertion failure, not a new build or hardware validation in this resume. `AGENTS.md` creation remains denied; retrieval guidance lives in the docs router and shared-index refresh belongs to its separate owner.

### External knowledge graph generation — 2026-10-26
Generated a fresh code-only AST knowledge graph (612 nodes, 1,691 edges, 26 communities via graphify 0.9.96) externally under `E:/AI/graphify-index/projects/earguard/`, keeping the repository clean of local graph artifacts. Diagnostics confirmed zero dangling endpoints, zero archive leakage, and functional read-only symbol query retrieval without LLM calls. Timestamp recorded (2026-10-26T21:05:00Z) for parent coordinator awareness.
