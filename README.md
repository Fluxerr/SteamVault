# 🎮 SteamVault — Get Steam Games for Free

<p align="center">
  <strong>🔓 Unlock any game on Steam — no purchase required. Download, install, and play Steam games completely free.</strong>
</p>

**SteamVault** is a Windows desktop application that enables you to download, manage, and keep your Steam game depot configurations up to date — completely offline, with **no Steam client required**. It gives you free access to Steam games by generating the necessary configuration files that let you download and play games without owning them.

![.NET 9](https://img.shields.io/badge/.NET-9.0-7C3AED?logo=dotnet)
![Platform](https://img.shields.io/badge/Platform-Windows%2010%2B-06B6D4?logo=windows)
![License](https://img.shields.io/badge/License-MIT-10B981)
![Release](https://img.shields.io/badge/Release-v2.0-8B5CF6)

---

<p align="center">
  <img src="https://img.shields.io/github/downloads/Fluxerr/SteamVault/total?color=7C3AED&label=Downloads&style=flat-square&cacheSeconds=3600" alt="Downloads" />
  &nbsp;
  <img src="https://img.shields.io/github/stars/Fluxerr/SteamVault?color=F59E0B&style=flat-square&cacheSeconds=3600" alt="Stars" />
</p>

---

## Table of Contents

- [What is SteamVault?](#what-is-steamvault)
- [Key Features](#key-features)
- [How It Works](#how-it-works)
- [Screenshots](#screenshots)
- [Installation](#installation)
- [First-Run Wizard](#first-run-wizard)
- [Usage Guide](#usage-guide)
- [System Requirements](#system-requirements)
- [Building from Source](#building-from-source)
- [Technologies Used](#technologies-used)
- [License](#license)

---

## What is SteamVault?

SteamVault provides a complete offline solution for managing **Steam depot manifests** and **Lua configuration files**. It acts as a localized vault for your game configurations, automatically generating and maintaining the manifest and depot key mappings needed by tools like **OpenSteamTool**.

**No Steam client, no Steam account, no API key required.** Everything is sourced from publicly available data.

---

## Key Features

| Feature | Description |
|---------|-------------|
| **Game Discovery** | Scan your local Steam library or download games by AppID. Instant fuzzy search across thousands of titles. |
| **One-Click Downloads** | Download depot manifests, keys, and Lua configs with a single click. Real-time progress tracking. |
| **Automatic Background Updates** | Optionally run in the system tray and automatically scan and update all game configs every 4 hours. |
| **Fully Offline** | No Steam client, no login, no API tokens needed. All data comes from public Steam CDN endpoints. |
| **Premium Dark UI** | Modern glass-morphism design with animated transitions, shimmer loading skeletons, and a polished dark theme. |
| **OpenSteamTool Auto-Install** | First-run wizard automatically detects your Steam directory, copies DLLs, and configures directories — no manual steps. |
| **Depot Key Database** | Built-in synchronized database of depot decryption keys and app access tokens, auto-updated from GitHub. |
| **Live Status Dashboard** | See all games, their update status, last-updated timestamps, and depot manifests at a glance. |
| **System Tray Support** | Run minimized to tray with balloon notifications for background updates. |

---

## How It Works

```
+---------------+     +----------------+     +-------------------+
|  Steam CDN    |---->|  SteamVault    |---->|  Your PC          |
|  (Public)     |     |  (Generates)   |     |  Steam\config\    |
+---------------+     +----------------+     +-------------------+
```

1. **SteamVault** queries the public Steam CDN for depot manifests and game metadata
2. It generates `.lua` configuration files and caches `.manifest` files
3. Files are placed directly into your `Steam\config\lua` and `Steam\config\depotcache` directories
4. **OpenSteamTool** then uses these files to enable offline mode

---

## Screenshots

> *Coming soon — screenshots of the Dashboard, My Games, and Settings views.*

---

## Installation

### Download Pre-Built Release

The easiest way to get SteamVault is to download the latest pre-built executable from the [**Releases page**](../../releases).

> **[Download Latest Release](../../releases/latest)**

Simply extract the ZIP and run `SteamVault_v2.0.0.exe`. No installer needed.

### First-Run Wizard

On first launch, SteamVault will guide you through a one-time setup:

1. **Auto-detects** your Steam installation directory via the Windows Registry
2. Displays the detected path — you can verify or browse to change it
3. **Installs OpenSteamTool** by copying 3 lightweight DLLs into your Steam folder
4. Creates the necessary `config\lua` and `config\depotcache` directories
5. If OpenSteamTool files already exist, it detects them and skips straight to the app

> Once installed, the wizard never appears again. Your settings are saved in `%APPDATA%\SteamVault\settings.json`.

---

## Usage Guide

### Dashboard
- **Download Game by AppID** — enter a Steam AppID and download its manifests and Lua configs instantly
- View your download history with game names, header images, and status badges
- All downloads are saved to `Steam\config\lua` (Lua files) and `Steam\config\depotcache` (manifest cache)

### My Games
- **Scan** your Lua folder to discover all locally stored games
- See live update status for each game: **Up to Date** or **Update Available**
- **Update All** or update individual games — fetches the latest depot manifests automatically
- Filter and search by AppID or game name with fuzzy matching

### Settings
- Configure the **Steam directory**, **Lua output path**, and **manifest cache path**
- Enable **automatic background updates** — SteamVault starts with Windows and checks for updates every 4 hours
- **Sync the depot key database** from the latest public key repository on GitHub
- Toggle the **ToggleSwitch** to enable/disable auto-start

### System Tray
- Close the window to minimize to tray (when auto-update is enabled)
- Right-click the tray icon for quick actions: Open Dashboard, Scan & Update, or Exit

---

## System Requirements

| Requirement | Minimum |
|-------------|---------|
| **Operating System** | Windows 10 (version 1809+) or Windows 11 |
| **.NET Runtime** | .NET 9.0 Desktop Runtime (included in release) |
| **Architecture** | x64 |
| **Disk Space** | ~50 MB |
| **Steam Installation** | Required (for OpenSteamTool integration) |

---

## Building from Source

### Prerequisites
- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Git

### Build Steps

```bash
# Clone the repository
git clone https://github.com/Fluxerr/SteamVault.git
cd SteamVault

# Restore packages and build
dotnet restore
dotnet build -c Release

# Run
dotnet run
```

The output executable will be at `SteamVault\bin\Release\net9.0-windows\SteamVault.exe`.

---

## Technologies Used

| Technology | Purpose |
|-----------|---------|
| **.NET 9.0** | Core runtime and SDK |
| **WPF** | Windows Presentation Foundation for the UI |
| **Windows Forms** | System tray integration and folder browser dialogs |
| **Newtonsoft.Json** | JSON serialization for settings and API responses |
| **SteamKit2** | Steam CDN protocol communication |
| **OpenSteamTool** | DLL proxy for offline Steam depot access |

### Architecture

```
SteamVault/
├── Models/          # Data models (AppSettings, GameInfo, LibraryEntry)
├── ViewModels/      # MVVM ViewModels (Main, Dashboard, MyGames, Settings, Installation)
├── Views/           # WPF XAML Views with code-behind
├── Services/        # Business logic (Steam API, Downloads, Lua parsing, Auto-update)
├── Converters/      # WPF value converters
├── Themes/          # Dark theme resource dictionary
├── Data/            # Embedded depot keys and app tokens databases
└── OpenSteamTool/   # DLLs to be copied to Steam directory
```

---

## License

This project is licensed under the **MIT License**. See the [LICENSE](LICENSE) file for details.

---

## Disclaimer

SteamVault is an unofficial tool and is **not affiliated with, endorsed by, or connected to Valve Corporation**. It operates entirely on publicly available data and does not interact with the Steam client, Steam accounts, or Steam authentication systems.

---

<p align="center">
  <sub>Built for the Steam community</sub>
</p>
