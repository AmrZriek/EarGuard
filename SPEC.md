# EarGuard — Architecture Specification & Engineering Reference

## 1. Executive Summary
EarGuard is a lightweight, zero-dependency, portable Windows application (.NET Framework 4.8 / WPF) that detects and lowers endpoint volume above configured limits. It targets unexpected volume changes during device connection, Windows Audio Service initialization, and playback. Enforcement is reactive: it cannot block playback before a change, guarantee no brief spikes, or guarantee hearing safety. Device and driver support determine whether endpoint-volume changes affect exclusive-mode playback.

---

## 2. Capability Map

| Module ID | Responsibility | Depends On |
|---|---|---|
| `config-store` | Settings persistence (`%APPDATA%\EarGuard\settings.json`), schema validation, safe defaults, startup argument parser (`ShouldStartSilent`), and Windows startup registry integration (`HKCU\...\Run`). | Standard Library (.NET 4.8) |
| `audio-engine` | Native Windows CoreAudio WASAPI COM interop (`IMMDeviceEnumerator`, `IMMDevice`, `IAudioEndpointVolume`, `IAudioEndpointVolumeCallback`, `IMMNotificationClient`), hardware volume clamping through a single lower-only write path, device connect/disconnect tracking, safe plug-in limit enforcement. | `config-store`, Win32 / OLE32 |
| `tray-manager` | System Tray icon management (`NotifyIcon`), single-instance enforcement (named Mutex), rate-limited balloon notifications on clamp, context menu (Open EarGuard, Exit). | `config-store`, `audio-engine` |
| `ui-frontend` | Single-page, compact native Windows WPF interface (Segoe UI, high contrast, transparent vector branding), dropdown device selector, device protection card (Ceiling & Safe Plug-in sliders, Live Volume gauge), global settings toggles. | `config-store`, `audio-engine`, `tray-manager` |

---

## 3. Core Audio Interop & Safety Invariants

### 3.1 `IAudioEndpointVolume` COM Vtable Alignment
In Windows CoreAudio (`endpointvolume.h`), the `IAudioEndpointVolume` interface vtable is strictly ordered. All 14 slots must be explicitly defined in managed COM interfaces:

```
[Slot 03] RegisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify)
[Slot 04] UnregisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify)
[Slot 05] GetChannelCount(out uint pnChannelCount)
[Slot 06] SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext)
[Slot 07] SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext)
[Slot 08] GetMasterVolumeLevel(out float pfLevelDB)
[Slot 09] GetMasterVolumeLevelScalar(out float pfLevel)
[Slot 10] SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext)
[Slot 11] SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext)
[Slot 12] GetChannelVolumeLevel(uint nChannel, out float pfLevelDB)
[Slot 13] GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel)
[Slot 14] SetMute(bool bMute, ref Guid pguidEventContext)
[Slot 15] GetMute(out bool pbMute)
```

> **Critical Safety Finding:** Omission of slots 10–13 caused C# calls to `SetMute` to dispatch into `SetChannelVolumeLevel(channel, 0.0f dB)` rather than muting. The corrected vtable and removal of mute operations address that dispatch error. EarGuard lowers endpoint volume; it is not a mixer or an unbreachable playback limiter.

### 3.2 Device Naming Architecture
Windows CoreAudio assigns generic form-factor descriptors to `PKEY_Interface_FriendlyName` (PID 2), returning ambiguous names such as `"Speakers"` or `"Headphones"`. EarGuard resolves the exact hardware endpoint via composite property resolution:
1. **Primary (`PKEY_Device_FriendlyName`):** `{a45c254e-df1c-4efd-8020-67d146a850e0}, PID 14`  
   Returns the full Windows composite label: e.g. `"Speakers (Realtek(R) Audio)"`, `"Speakers (fifine Microphone)"`, `"PA278QV (NVIDIA High Definition Audio)"`, or `"Headphones (Apple USB-C to 3.5mm Headphone Jack Adapter)"`.
2. **Fallback (`PKEY_DeviceDesc`):** `{b3f8fa53-0004-438e-9003-51a46e139bfc}, PID 6`  
   Returns the underlying hardware controller name.

### 3.3 Protection Invariants
1. **No Global Pause:** Enabled endpoints are monitored while EarGuard runs. Protection can be disabled per device.
2. **Disabling:** Unchecking `[ ] Protect` does not request a volume change. The engine stops limiting that endpoint; other applications and the user remain free to change it.
3. **Plug-in & Wake Limit:** Detecting a new endpoint or resuming from sleep/hibernate starts a `SafePlugInVol` limit (default 5%). This is a cap, not a target: an endpoint observed at or below it needs no write. It does not delay or block playback.
4. **Plug-in Grace Window:** The plug-in limit applies for `SafePlugInGraceMs` (3000 ms) after detection or resume. Callbacks and watchdog ticks lower louder values, including late volume restores by Windows. Resume renews this window for existing endpoints. Afterward, the normal ceiling applies, so a restored value below the ceiling is left alone even if it exceeds the plug-in limit.
5. **Lower-Only Requests:** One routine writes endpoint volume. It reads the current scalar and requests a lower value only when that observation exceeds the active limit; it does not deliberately raise quiet volume to a target. EarGuard serializes its own read/compare/write transactions. These are not atomic against Windows or other applications: an external writer can lower volume between EarGuard's read and write, and EarGuard's pending request can then exceed that newer value. This path cannot provide an absolute never-raises guarantee.
6. **Feedback Loop Prevention:** EarGuard maintains a unique `ContextGuid` on startup. Volume adjustments initiated by EarGuard pass `ref ContextGuid`, which the callback ignores to prevent recursion loops.
7. **Watchdog Redundancy:** A background timer inspects endpoint scalars every `WatchdogIntervalMs` (100 ms) as defense-in-depth against hardware sleep/wake glitches.
8. **Discovery Retries:** Windows finishes re-indexing a newly reported endpoint slightly after it raises the notification, so discovery retries run at `DiscoveryRetryIntervalMs` (500 ms) until `SafePlugInGraceMs` expires. Retries stop early once a scan observes a structural change, so one notification costs a handful of scans rather than one per tick.
9. **Publish On Change:** A scan re-asserts every cap, but writes `settings.json` and raises `DevicesChanged` only when the guarded device set or the default endpoint actually changed. Without that gate each scan rewrites the settings file and rebuilds the UI device list, which turned a single device change into dozens of file writes and UI rebuilds.

---

## 4. Module Specifications

### 4.1 Module: `config-store`
* **Path:** `%APPDATA%\EarGuard\settings.json`
* **Schema:**
  ```json
  {
    "GlobalMaxLimit": 0.30,
    "GlobalSafePlugInVol": 0.05,
    "LaunchOnStartup": false,
    "ShowNotificationOnBlock": true,
    "Devices": [
      {
        "DeviceId": "{0.0.0.00000000}.{guid}",
        "DeviceName": "Speakers (Realtek(R) Audio)",
        "Enabled": true,
        "MaxLimit": 0.30,
        "SafePlugInVol": 0.05
      }
    ]
  }
  ```
* **Validation:** Clamps `MaxLimit` between `0.01` and `1.00`, and `SafePlugInVol` between `0.00` and `MaxLimit`. The ceiling is the user's own choice: any value from 1% to 100% is accepted and honoured exactly. Non-finite values are replaced with safe defaults. Corrupted JSON files automatically recover to safe defaults.
* **Silent Windows Startup:**
  * When enabled, writes: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\EarGuard = "C:\...\EarGuard.exe" --tray`.
  * `MigrateStartupRegistryIfNeeded()`: Automatically detects and updates legacy registry values lacking `--tray`.
  * `ShouldStartSilent(string[] args)`: Recognizes `--tray`, `-tray`, `/tray`, `-t`, `--minimized`, `-minimized`, `/minimized`, `-m`, `--silent`, `-silent`, `/silent`, `-s`, `--startup`, `-startup`, `/startup`, `--autostart`, `-autostart`, and `/autostart`.

### 4.2 Module: `tray-manager`
* **Single Instance & Background Message Dispatch:**
  * Enforces process exclusivity via `Global\EarGuard_SingleInstance_Mutex`.
  * `new WindowInteropHelper(mainWindow).EnsureHandle()` is invoked during startup before showing any UI. This ensures the Win32 `HWND` is created and hooked into `WndProc`, allowing single-instance restore broadcasts (`RegisterWindowMessage`) from secondary launches to restore and focus the window even when EarGuard starts silently in the tray.
* **Tray Icon:** Generated with full 32-bit alpha transparency from `EarGuard-transparent.png` (or `EarGuard.ico`), blending seamlessly with light and dark Windows taskbars.
* **Context Menu:**
  * `[🛡️] Open EarGuard` *(Default bold action with app logo)*
  * `──────────────`
  * `Exit EarGuard`

### 4.3 Module: `ui-frontend`
* **Layout:** Compact resizable window (default `500 × 540`, minimum `480 × 520`, center screen). The protection card scrolls vertically when its wrapped content exceeds the available height; header, device selector, and footer stay visible.
* **Startup Behavior:**
  * If launched with a tray/startup argument, stays hidden in the system tray with zero window popups or banners.
  * If launched directly (e.g. desktop shortcut), shows the main window immediately.
* **Branding:** Seamless transparent ear-wearing-hard-hat vector icon in the header, bold `EarGuard` title, and green `• Active` status pill.
* **Device Selector:** Dropdown `ComboBox` lists active render endpoints by friendly name, with a refresh button. It follows the Windows default until an explicit user commitment, including mouse or keyboard commitment of the already-selected item. Opening or canceling the dropdown and programmatic refresh do not take manual ownership.
* **Protection Card:**
  * Endpoint title uses ellipsis; description and helper text wrap to fit.
  * Checkbox: `[✓] Protect` toggle with dynamic `[ 🛡️ Guarded ]` / `[ ⚪ Unprotected ]` status badge.
  * Ceiling Slider: 1% to 100% with real-time numeric readout.
  * Safe Plug-in Slider: 0% to Ceiling with real-time numeric readout.
  * Live Volume Gauge: Real-time progress bar reflecting hardware endpoint volume.
  * Protection status follows the enabled state. Disabling clears the last-lowering message; queued clamp events do not replace the disabled status. Clamp messages describe historical lowering, not prevented audio.
  * There is no "test" control. Any feature that writes a louder value to the hardware is prohibited, so protection is verified by using the machine rather than by deliberately provoking a spike.
* **Footer:** "Start with Windows", "Notify when volume is lowered", and "Minimize to Tray". Optional tray notifications report the observed old volume and requested lower volume after a successful write, with the system notification sound suppressed.

---

## 5. Compilation & Tooling

Two entry points. The response file is the minimal path:

```cmd
csc @EarGuard.rsp
```

The globs in that file must use backslashes (`src\Audio\*.cs`). With forward slashes the compiler resolves each wildcard against the current directory and reports all 17 sources as missing, which looks like a broken checkout rather than a path bug.

The full pipeline runs the test suite first, then builds the app, then prints its SHA-256:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Under Roslyn from Visual Studio Build Tools the script passes `/deterministic+`, so identical sources yield byte-identical binaries and a published hash can be verified locally. The flag is applied conditionally because the in-box .NET Framework compiler predates it and exits with `CS2007` on an unrecognized option.

**Compiler compatibility:** `src/Audio/AudioEngine.cs` holds `VolumeAdjustmentResult` with explicit readonly fields and read-only properties rather than auto-properties with private setters. The C# 5 compiler shipped with .NET Framework cannot assign an auto-property backing field from a struct constructor (`CS0843`), and the response-file path is documented as working with that compiler.

* **Target:** .NET Framework 4.8
* **Output:** `EarGuard.exe` (Single standalone portable executable, 82,432 bytes under Roslyn; 84,992 bytes under the in-box .NET Framework compiler)
* **Dependencies:** Zero external NuGet packages or third-party DLLs. Pure native Windows API.
