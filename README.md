# Razer Ambilight (fork)

Screen ambilight for **Razer Chroma** devices and, in this fork, for a **laptop keyboard driven through Windows Dynamic Lighting** (built and tested on a **Lenovo Legion Pro 7 Gen 10, 16AFR10H**).

Original project by [Nico Jeske](https://github.com/nicojeske/RazerAmbilight) - [Website](https://nicojeske.de/razer-ambilight/) - [Youtube Video](https://www.youtube.com/watch?v=ifXCZJyoKsw)

![](ambi_small.gif)

## What this fork adds

| | |
|---|---|
| **Laptop keyboard support** | Drives a built-in keyboard the Razer Chroma SDK cannot reach, through Windows Dynamic Lighting (HID LampArray), with brightness, effects and a color picker. |
| **Works without Razer Synapse** | Screen capture and the laptop keyboard no longer depend on Synapse or the Chroma SDK being present. |
| **New Windows 11 style UI** | One Fluent / Mica settings window (WPF-UI) replaces the old WinForms popups. |
| **Much more robust** | Fixes for crashes after sleep, hibernation, lock, screensaver and display changes, and for a long-standing random crash. |
| **Lower CPU usage** | Removed the per-pixel GDI+ calls that dominated CPU time. |

## Features

* Maps your screen to your Razer mouse, keyboard, mousepad, headset, keypad and Chroma Link devices (including Chroma Connect lights such as Govee or Yeelight) to create an "ambient effect".
* Maps your screen, or plays an effect, on a **Windows Dynamic Lighting laptop keyboard** (see below).
* Adjustable saturation, update rate, monitor selection, manual keyboard grid size, "real ambilight" and ultrawide modes.
* Settings window: double-click the tray icon, or right-click it and choose *Settings*.

## Laptop keyboard (Windows Dynamic Lighting)

Some laptop keyboards (here the Legion Pro 7 Gen 10) are not Razer devices. Razer Synapse can see them through Windows Dynamic Lighting, but it does not forward Chroma SDK effects to them, so the original app could never light them up. This fork talks to them directly with `Windows.Devices.Lights.LampArray`, and Razer devices are left to the Chroma SDK.

**Effects:** Screen ambilight, Solid color, Breathing, Color cycle, Rainbow wave, Rainbow wheel.
**Controls:** brightness (0-100 %), effect speed, reverse direction (wave and wheel), a color wheel with synchronized **R / G / B (0-255)** and hex fields, and a live status line.

Animated effects run on their own timer at the keyboard's maximum accepted rate (its reported minimum update interval, about 31 frames per second), so they keep running while the screen is static.

### One-time setup on a new PC

Windows only lets an app drive a keyboard *in the background* if it has a package identity and is registered as an "ambient" lighting app. This fork ships a small sparse package for that (the app itself is not converted to MSIX):

1. Build or copy the app to `C:\RazerAmbilight` (or pass `-AppDir` to the script).
2. In a **PowerShell opened as administrator**, run:
   ```powershell
   & "C:\RazerAmbilight\Package\Install-LightingProvider.ps1"
   ```
   It creates a self-signed certificate (`CN=RazerAmbilightDev`), trusts it on this PC, builds and signs the package, and registers it with the app folder as external location. Use `-Uninstall` to remove it.
3. Restart Razer Ambilight (always start it from that folder).
4. In Windows *Settings > Personalization > Dynamic Lighting > Background light control*, drag **Razer Ambilight** to the top.

The same steps are shown, collapsed, in the app's settings window.

Notes:
* Windows takes about **30 seconds after each start** to hand the keyboard to the app. Nothing speeds this up.
* Needs Windows 11 (background lighting control).
* The exe's manifest carries an `<msix>` element matching the package. This only works because ClickOnce manifest generation is disabled in the `.csproj`; with it on, `app.manifest` is not embedded in the exe.

## What changed compared to the original version

### Migration from the original UI to the new one
* The scattered WinForms popups (max FPS, saturation, keyboard size, monitor) and the long checkable tray menu were replaced by a single **WPF-UI Fluent window** (Mica backdrop, cards, icons, follows the Windows light/dark theme).
* The tray menu is now just *Settings* and *Exit*, with a custom dark renderer, and the old dialog classes were removed.
* Every setting has a proper public setter in `TraySettings`, saved to user settings (slider and color changes are saved with a short delay instead of on every mouse move).
* Target framework moved from .NET Framework 4.6.1 to **4.6.2** (required by WPF-UI 4.3.0); dependencies and binding redirects were updated accordingly.
* Also added: a single-instance guard, file logging to `logs\ambilight.log` (the NLog config had no target before), and a `--show-settings` command line switch that opens the settings window at start.

### Reliability
* **Random crash fixed:** the `LogicManager` (which owns the Chroma SDK instance) was created without keeping a reference to it, so the garbage collector could finalize it while the app was running, and the SDK's finalizer crashed the process (`AccessViolationException`). It is now kept alive for the whole run.
* **DXGI desktop duplication:** lost-context errors (`ACCESS_LOST`, `DEVICE_REMOVED`, `INVALID_CALL`) are detected and the capture device is recreated immediately instead of retrying against a dead device. The device is now disposed by the capture thread only (it used to be disposed from an event thread while in use).
* **Sleep, hibernation, lock, screensaver, display changes:** capture is paused during these transitions and restarted cleanly afterwards. The screensaver state is polled directly because Windows does not reliably notify a tray app about it.
* **Tray after hibernation:** the tray icon and menu are rebuilt after resume and exceptions on the tray thread are logged instead of terminating the process (this was the `0xC000041D` crash with a blank white menu).
* **Razer Synapse not running:** Chroma initialization is retried every 15 seconds in the background and no longer crashes the app; a failing device no longer stops the others.
* Multi-monitor setups with different DPI scaling are handled through the display-change recreation above.

### Performance
* `Bitmap.GetPixel` (a native GDI+ round trip per call, used in nested loops for every device) was replaced by a `LockBits` based pixel reader (`FastBitmap`).
* A frame limiter tied to the configured update rate replaces a fixed formula that could cause long, unpredictable sleeps.
* Capture does not touch DXGI at all while the machine is locked, suspended or showing the screensaver.

## Build from source

Requirements:
* Visual Studio (or Build Tools) with **MSBuild**, and the **.NET Framework 4.6.2 targeting pack**
* The **Windows 10/11 SDK** (`Windows.winmd`, `makeappx`, `signtool`). The `.csproj` references `C:\Program Files (x86)\Windows Kits\10\UnionMetadata\10.0.28000.0\Windows.winmd`; adjust the `HintPath` if your SDK version differs.
* NuGet packages (`nuget restore Ambilight.sln`)

```powershell
msbuild Ambilight\Ambilight.csproj /p:Configuration=Release
```

The output is `Ambilight\bin\Release`. Copy it to `C:\RazerAmbilight` if you use the laptop keyboard package.

## Troubleshooting

* Logs are in `logs\ambilight.log` next to the exe. For the laptop keyboard look for `under our control` lines.
* Keyboard does nothing right after start: wait about 30 seconds, and check the app is first in *Background light control*.
* Razer devices (mouse, keyboard, mousepad, headset, keypad, Chroma Link) still need Razer Synapse running.

## Note

A Windows Smart Screen message may appear during installation. This tells you that the program is not signed by a certification authority recognized by Microsoft, because such certificates cost money.

## Credits

Original application by [Nico Jeske](https://github.com/nicojeske). This fork's changes were developed by [x225franc](https://github.com/x225franc), pair-programmed with Claude (Anthropic).
