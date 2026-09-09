# 🛡️ EarGuard for Windows

> **Hardware Volume Spike Protector for IEM & Headphone Audiophiles**

<p align="center">
  <img src="EarGuard.png" alt="EarGuard Logo" width="140" height="140" />
</p>

<p align="center">
  <a href="https://github.com/AmrZriek/EarGuard/releases/latest"><img src="https://img.shields.io/github/v/release/AmrZriek/EarGuard?style=flat-square&color=blue" alt="Release" /></a>
  <a href="https://github.com/AmrZriek/EarGuard/releases/latest/download/EarGuard.exe"><img src="https://img.shields.io/badge/Download-EarGuard.exe-brightgreen?style=flat-square&logo=windows" alt="Download" /></a>
  <img src="https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011-0078D6?style=flat-square&logo=windows" alt="Platform" />
  <img src="https://img.shields.io/badge/.NET%20Framework-4.8%20(Native)-512BD4?style=flat-square" alt=".NET" />
  <img src="https://img.shields.io/badge/Binary%20Size-65%20KB-success?style=flat-square" alt="Size" />
  <img src="https://img.shields.io/badge/Latency-%3C2ms%20Hardware%20Clamp-orange?style=flat-square" alt="Latency" />
  <img src="https://img.shields.io/badge/License-MIT-blue?style=flat-square" alt="License" />
  <a href="https://ko-fi.com/amrzriek"><img src="https://img.shields.io/badge/Support-Ko--fi-FF5E5B?style=flat-square&logo=kofi&logoColor=white" alt="Ko-fi" /></a>
</p>

<p align="center">
  <a href="https://github.com/AmrZriek/EarGuard/releases/latest/download/EarGuard.exe">
    <img src="https://img.shields.io/badge/⬇%20Download-EarGuard.exe-brightgreen?style=for-the-badge&logo=windows&logoColor=white" height="40" alt="Download EarGuard" />
  </a>
</p>

---

## 🎧 The Problem: The Shotgun in Your Ear Canal

Fellas with IEMs and dongle DACs, I have a gift for you. 

I’m sure you value your hearing, so I built **EarGuard**. Because it only takes **one** accidental 100% volume blast with low-impedance, ultra-sensitive IEMs to realize you have a literal 12-gauge shotgun pointed directly inside your ear canal.

If you own a dongle without a physical gain knob, you already know the terror:

1. You plug in your dongle or wake your PC from sleep.
2. Windows Audio Service (`AudioSrv`) suffers amnesia, forgets your previous volume, and cheerfully initializes the endpoint at **1.0 (100% master volume)**.
3. Tidal in Exclusive Mode grabs hardware exclusive access and demands full digital line-level output.
4. You hit play, and 125 dB of raw acoustic violence blows out your delicate balanced-armature drivers and gives you instant tinnitus.

**EarGuard solves this permanently.** It runs quietly in your system tray, hooks directly into the physical audio hardware via low-level WASAPI COM callbacks, and clamps any volume spike down in **under 2 milliseconds** before your eardrums register the pressure wave.

---

## 📸 The Interface

No bloated Electron. No web views. No 300 MB installer. Just clean, native Windows desktop UI that does one job flawlessly:

<p align="center">
  <img src="screenshot.png" alt="EarGuard UI Screenshot" width="500" />
</p>

---

## ✨ Features That Save Your Hearing

* **⚡ Sub-2ms Hardware Clamping:** Uses native Windows CoreAudio `IAudioEndpointVolumeCallback`. The moment any rogue process or OS bug attempts to push volume past your ceiling, the COM callback forces the UAC2 hardware volume registers back down in `<2ms`.
* **🏷️ Unambiguous Hardware Labels:** Queries the full composite friendly name (`PKEY_Device_FriendlyName`) and controller descriptor (`PKEY_DeviceDesc`). You'll always see the exact hardware device (e.g. `Speakers (Realtek(R) Audio)`, `Speakers (fifine Microphone)`, `Headphones (Apple USB-C Adapter)`) rather than generic, identical "Speakers" entries.
* **🔌 Safe Plug-in & Wake Volume:** Whenever you plug in a USB-C dongle DAC or your laptop resumes from sleep/hibernate, EarGuard automatically drops the hardware endpoint to a whisper-quiet **5%** before anything can play.
* **🛡️ Selective Device Guarding:** Toggle protection per device with `[✓] Protect`. **Inviolable Invariant:** Disabling protection for an endpoint *never* boosts or increases volume—the scalar remains exactly where it was.
* **🔒 Zero Dangerous "Pause" Modes:** We removed global pause bypasses entirely. A safety app with a "disable protection" toggle defeats the purpose—EarGuard guards your ears 24/7.
* **⚡ Tested Safe-Clamp Button:** The "Test Clamp" button spikes volume **strictly 2% above your ceiling** (e.g. 30% → 32%). It proves your hardware clamp works with zero risk of deafening you.
* **🎯 Tidal Exclusive Mode Tamed:** Operates at the physical endpoint layer (`IAudioEndpointVolume`), meaning it clamps even when bit-perfect players bypass the Windows software mixer.
* **🍃 Featherweight Footprint:** 
  * Single standalone portable executable (**~64 KB**).
  * **0.0% CPU** usage (100% event-driven, zero polling loops).
  * Built into .NET Framework 4.8—runs out-of-the-box on every Windows 10 and 11 PC with zero prerequisites.
* **🤫 Silent Background Startup:** When "Start EarGuard automatically with Windows" is enabled, EarGuard launches silently into your system tray (`--tray`) on PC boot with zero intrusive popups or window flashes. Double-clicking the app or clicking the tray icon brings up the interface instantly.

---

## 🚀 Quick Start

1. Download [**EarGuard.exe**](https://github.com/AmrZriek/EarGuard/releases/latest/download/EarGuard.exe) from the [Releases](https://github.com/AmrZriek/EarGuard/releases/latest) page.
2. Run `EarGuard.exe`. It will automatically detect your audio outputs and sit in your system tray.
3. Select your headphone/DAC dongle from the dropdown, verify `[✓] Protect` is checked, and set your **Hard Volume Ceiling** (e.g. `30%`) and **Safe Plug-in Volume** (e.g. `5%`).
4. Click **Test Clamp** to watch EarGuard slap a volume spike down in real-time.
5. Check **"Start EarGuard automatically with Windows"** and minimize to tray. Your ears are now bulletproof.

---

## 🛠️ Compiling from Source

No complicated build scripts, Node modules, or package managers required. C# compilation is literally a two-word command in your terminal:

```cmd
csc @EarGuard.rsp
```

The included `EarGuard.rsp` response file supplies the framework references and embeds the transparent `EarGuard.ico`. In under 3 seconds, you have a fresh, optimized `EarGuard.exe`.

---

## 🎵 Tips for Audiophile Players (Tidal, Foobar2000, Qobuz)

* **Tidal Hi-Fi / Master:** You can safely keep Tidal in **WASAPI Exclusive Mode** for bit-perfect streaming. In Tidal's output settings (`More Settings` next to your DAC), uncheck **"Force Volume"**. When unchecked, Tidal will gracefully synchronize with EarGuard's hardware clamp.
* **Foobar2000:** Set output to *WASAPI (event)* or *WASAPI (exclusive)*. EarGuard protects the hardware volume regardless of stream mode.

---

## ☕ Support

If EarGuard saved your hearing or IEMs from an accidental 100% blast, consider buying me a coffee!

[![Support on Ko-fi](https://img.shields.io/badge/Support%20on-Ko--fi-FF5E5B?style=for-the-badge&logo=kofi&logoColor=white)](https://ko-fi.com/amrzriek)

👉 **[https://ko-fi.com/amrzriek](https://ko-fi.com/amrzriek)**

---

## 📜 License

MIT License. Free and open source for the audio community. Share it with anyone who owns sensitive IEMs and values their hearing.
