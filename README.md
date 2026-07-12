<div align="center">

<img src="assets/banner.svg" alt="Patreon Archiver Bridge" width="100%" />

<br />

[![License: PolyForm Noncommercial 1.0.0](https://img.shields.io/badge/license-PolyForm%20Noncommercial--1.0.0-ff5a3c.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-0078D6.svg)](#requirements)
[![Built with .NET](https://img.shields.io/badge/.NET-9.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![UI](https://img.shields.io/badge/UI-WPF-2b2b32.svg)](#architecture)
[![Packaged with Velopack](https://img.shields.io/badge/packaged%20with-Velopack-ff5a3c.svg)](https://velopack.io/)
[![Latest Release](https://img.shields.io/github/v/release/r1kp/patreon-archiver-bridge?color=ff5a3c)](https://github.com/r1kp/patreon-archiver-bridge/releases/latest)

**A lightweight native Windows companion app that lets the Patreon Archiver Chrome extension save files directly to disk.**

[Download](#installation) · [Features](#features) · [How It Works](#how-it-works) · [Building from Source](#building-from-source) · [Related Projects](#related-projects)

</div>

---

## Table of Contents

- [About](#about)
- [Features](#features)
- [Screenshots](#screenshots)
- [Installation](#installation)
- [How It Works](#how-it-works)
- [Building from Source](#building-from-source)
- [Uninstalling](#uninstalling)
- [Related Projects](#related-projects)
- [Roadmap](#roadmap)
- [Contributing](#contributing)
- [License](#license)

---

## About

Chrome extensions cannot write directly to your file system due to security restrictions. **Patreon Archiver Bridge** is the solution: a small, modern native Windows companion application that runs in the background and handles the actual file downloading and writing on behalf of the [Patreon Archiver](#related-projects) browser extension using Chrome's native messaging API.

No external Python runtimes, no manual configuration required — just a single, self-contained setup.

---

## Features

- 🪟 **Native WPF Dashboard** — view connection status, active downloads, and media engine statistics at a glance.
- 🔌 **Native Messaging Bridge** — communicates with the Chrome extension over standard stdio pipes without opening ports.
- 📦 **Self-Contained Installer** — single executable installer containing all dependencies; runs without requiring a pre-installed .NET runtime on the host PC.
- 🔄 **Auto-Updates (Velopack)** — the application checks for updates silently in the background and installs them automatically.
- 🧹 **Clean Uninstaller** — completely removes all binaries, start menu/desktop shortcuts, registry entries, and logs.
- 🆓 **Free & Open Source** — code is open under a non-commercial license with no telemetry or tracking.

---

## Screenshots

<div align="center">

<img src="assets/screenshot-dashboard.svg" alt="Dashboard" width="49%" />
<img src="assets/screenshot-setup.svg" alt="Setup wizard" width="49%" />

<sub>Placeholder mockups — will be swapped for real screenshots/GIFs closer to the v1.0.0 release.</sub>

</div>

---

## Installation

1. Download the latest **`PatreonArchiverBridge_setup.exe`** from the [Releases page](https://github.com/r1kp/patreon-archiver-bridge/releases/latest).
2. Because the installer is freshly compiled and not code-signed yet, Windows SmartScreen will flag the installer. To proceed:
   - Right-click the downloaded file → **Properties** (Eigenschaften)
   - Tick the **Unblock** (Zulassen) checkbox at the bottom → **OK**
   - Run the installer as usual
3. Choose your preferred installation path and follow the on-screen instructions.
4. Install the [Patreon Archiver Chrome extension](#related-projects) — it will automatically connect to the Bridge.

> **Note:** The other files attached to the release (`.nupkg`, `.json`, unpackaged `.exe`) are used internally by the auto-updater and don't need to be downloaded manually.

### Requirements

- Windows 10 / 11 (64-bit)
- Google Chrome
- [Patreon Archiver Chrome Extension](#related-projects)

---

## How It Works

```
Chrome Extension  <── native messaging (stdio) ──>  PatreonArchiverBridge.exe  <──>  File System
```

The extension launches the Bridge via Chrome's native messaging host protocol. The Bridge listens on stdin/stdout, receives archive jobs (file data + destination metadata), downloads and writes them to disk using `yt-dlp` & `FFmpeg`, and reports status back to the extension — all without exposing any network port.

### Architecture

| Component | Responsibility |
|---|---|
| `PatreonArchiverBridge` | Main WPF app — dashboard UI, native messaging host, file downloader engine |
| `PatreonArchiverBridge.Setup` | Self-contained custom installer UI, wraps the silent Velopack installer (`-s --installto`) |
| `PatreonArchiverBridge.Uninstaller` | Cleans up registry entries, shortcuts (with/without spaces), and installation directories |

The setup UI is a thin, fully self-contained WPF wrapper that runs the actual Velopack-generated installer silently in the background, so users get a familiar installation experience without exposing Velopack's default UI.

---

## Building from Source

**Prerequisites:** .NET 9.0 SDK, PowerShell

```powershell
git clone https://github.com/r1kp/patreon-archiver-bridge.git
cd patreon-archiver-bridge

# Build and package with Velopack
Remove-Item -Recurse -Force "Releases" -ErrorAction SilentlyContinue
powershell -ExecutionPolicy Bypass -File pack_app.ps1 -Version <version>
```

This produces:
- `publish/PatreonArchiverBridge_setup.exe` — the custom self-contained setup wrapper
- `Releases/*` — the Velopack release assets (`.nupkg`, `-Setup.exe`, feed metadata) used for auto-updates

---

## Uninstalling

Use **Windows Settings → Apps → Patreon Archiver Bridge → Uninstall**, or run the uninstaller directly from the install directory. It removes all registry entries, shortcuts, and leftover files automatically.

---

## Related Projects

| Project | Description | Status |
|---|---|---|
| **[patreon-archiver-extension](https://github.com/r1kp/patreon-archiver-extension)** | The Chrome (Manifest V3) extension this Bridge was built for | 🚧 In development |
| **patreon-archiver-bridge** *(this repo)* | Native Windows companion app | ✅ Active |

---

## Roadmap

- [ ] Code-signed installer (removing the SmartScreen warning)
- [ ] Public release of the Chrome extension
- [ ] Real screenshots and a short demo GIF
- [ ] v1.0.0 stable release

---

## Contributing

Issues and pull requests are welcome. If you run into a bug, please open an issue with your Windows version and, if possible, the console output from running the installer via `cmd`.

---

## License

This project is licensed under the **PolyForm Noncommercial License 1.0.0**. 

You are free to view, copy, modify, and distribute this software for personal and educational purposes. **Commercial use, distribution, or sale of this software (or any modified version of it) is strictly prohibited.**

See the [LICENSE](LICENSE) file for details.
