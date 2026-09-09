# EarGuard — Architecture Specification & Engineering Reference

## 1. Executive Summary
EarGuard is a lightweight, zero-dependency, portable Windows application (.NET Framework 4.8 / WPF) engineered to protect users of low-impedance In-Ear Monitors (IEMs) and headphones from catastrophic volume spikes caused by USB-C DAC dongles, Windows Audio Service (`AudioSrv`) initialization amnesia, and exclusive-mode media players (Tidal, Qobuz, Foobar2000).

---

## 2. Capability Map

| Module ID | Responsibility | Depends On |
|---|---|---|
| `config-store` | Settings persistence (`%APPDATA%\EarGuard\settings.json`), schema validation, safe defaults, startup argument parser (`ShouldStartSilent`), and Windows startup registry integration (`HKCU\...\Run`). | Standard Library (.NET 4.8) |
| `audio-engine` | Native Windows CoreAudio WASAPI COM interop (`IMMDeviceEnumerator`, `IMMDevice`, `IAudioEndpointVolume`, `IAudioEndpointVolumeCallback`, `IMMNotificationClient`), sub-2ms hardware volume clamping, device connect/disconnect tracking, safe plug-in volume enforcement. | `config-store`, Win32 / OLE32 |
| `tray-manager` | System Tray icon management (`NotifyIcon`), single-instance enforcement (named Mutex), rate-limited balloon notifications on clamp, context menu (Open EarGuard, Exit). | `config-store`, `audio-engine` |
| `ui-frontend` | Single-page, compact native Windows WPF interface (Segoe UI, high contrast, transparent vector branding), dropdown device selector, device protection card (Ceiling & Safe Plug-in sliders, Live Volume gauge, Safe 2% Test Clamp), global settings toggles. | `config-store`, `audio-engine`, `tray-manager` |

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

> **Critical Safety Finding:** Omission of slots 10–13 caused C# calls to `SetMute` to dispatch directly into `SetChannelVolumeLevel(channel, 0.0f dB)`—which instructed hardware DAC chips to jump to **0 dB (100% full-scale volume)**. EarGuard completely resolved this by correcting the vtable alignment and **purging all mute operations** from the application. EarGuard is strictly an unbreachable ceiling guard, not a mixer.

### 3.2 Device Naming Architecture
Windows CoreAudio assigns generic form-factor descriptors to `PKEY_Interface_FriendlyName` (PID 2), returning ambiguous names such as `"Speakers"` or `"Headphones"`. EarGuard resolves the exact hardware endpoint via composite property resolution:
1. **Primary (`PKEY_Device_FriendlyName`):** `{a45c254e-df1c-4efd-8020-67d146a850e0}, PID 14`  
   Returns the full Windows composite label: e.g. `"Speakers (Realtek(R) Audio)"`, `"Speakers (fifine Microphone)"`, `"PA278QV (NVIDIA High Definition Audio)"`, or `"Headphones (Apple USB-C to 3.5mm Headphone Jack Adapter)"`.
2. **Fallback (`PKEY_DeviceDesc`):** `{b3f8fa53-0004-438e-9003-51a46e139bfc}, PID 6`  
   Returns the underlying hardware controller name.

### 3.3 Protection Invariants
1. **Always-On Guardian (No Global Pause):** There is no dangerous "Pause Protection" button. A hearing protection tool with a bypass defeats its own purpose.
2. **Disabling Invariant:** When a user unchecks `[ ] Protect` for a specific output device, the endpoint volume is **never touched, raised, or boosted**. It remains at its current safe level.
3. **Safe 2% Test Clamp:** Simulating a volume spike by forcing volume to 100% is strictly prohibited. The "Test Clamp" button spikes volume **strictly 2% above the configured ceiling** (`Math.Min(1.0f, MaxLimit + 0.02f)`). The COM callback intercepts this in `<2ms` and forces it back down to `MaxLimit`, confirming the hardware hook works without acoustic shock.
4. **Safe Plug-in & Wake Volume:** Whenever a new USB DAC endpoint is detected or the computer wakes from sleep/hibernate, EarGuard automatically applies `SafePlugInVol` (default 5%) before audio can play.
5. **Feedback Loop Prevention:** EarGuard maintains a unique `ContextGuid` on startup. Volume adjustments initiated by EarGuard pass `ref ContextGuid`, which the callback ignores to prevent recursion loops.
6. **Watchdog Redundancy:** A background timer inspects endpoint scalars every 1000ms as defense-in-depth against hardware sleep/wake glitches.

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
* **Validation:** Clamps `MaxLimit` between `0.01` and `1.00`, and `SafePlugInVol` between `0.00` and `MaxLimit`. Corrupted JSON files automatically recover to safe defaults.
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
* **Layout:** Single-page compact window (`Width: 500`, `Height: 540`, non-resizable, center screen).
* **Startup Behavior:**
  * If launched with a tray/startup argument, stays hidden in the system tray with zero window popups or banners.
  * If launched directly (e.g. desktop shortcut), shows the main window immediately.
* **Branding:** Seamless transparent ear-wearing-hard-hat vector icon in the header, bold `EarGuard` title, and green `• Active` status pill.
* **Device Selector:** Dropdown `ComboBox` listing all active render endpoints with rich friendly names and a refresh button (`🔄`).
* **Protection Card:**
  * Endpoint title and hardware description with text-wrapping to prevent clipping.
  * Checkbox: `[✓] Protect` toggle with dynamic `[ 🛡️ Guarded ]` / `[ ⚪ Unprotected ]` status badge.
  * Ceiling Slider: 1% to 100% with real-time numeric readout.
  * Safe Plug-in Slider: 0% to Ceiling with real-time numeric readout.
  * Live Volume Gauge: Real-time progress bar reflecting hardware endpoint volume.
  * Test Clamp Button: `⚡ Test Clamp` with green verification readout (`✓ Clamped! (32% → 30%) in <2ms`).
* **Footer:** Startup registry toggle, balloon notification toggle, and "Minimize to Tray" button.

---

## 5. Compilation & Tooling

EarGuard compiles in a single command using standard C# response file configuration:

```cmd
csc @EarGuard.rsp
```

* **Target:** .NET Framework 4.8
* **Output:** `EarGuard.exe` (Single standalone portable executable, ~65 KB)
* **Dependencies:** Zero external NuGet packages or third-party DLLs. Pure native Windows API.
