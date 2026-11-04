# SteamVault v2.6.3

> Self-contained application — available as a Windows Setup Installer or Portable Standalone executable. No .NET runtime required.

---

## 📦 Installer vs Standalone Portable

> [!TIP]
> **Windows Setup Installer (`SteamVault-Setup.exe`)**
> Installs SteamVault with Start Menu and Desktop shortcuts and a Windows uninstaller.

> [!NOTE]
> **Portable Standalone (`SteamVault.exe`)**
> A single self-contained executable. No separate .NET installation is needed.

---

## What's New

### 🛠️ OpenSteamTool Repair and Startup Improvements
* **Safer DLL Refresh:** SteamVault checks whether the installed DLLs differ from the bundled files and skips writes when they already match.
* **Steam-Aware Updates:** When a DLL replacement is needed while Steam is running, SteamVault defers the update and opens the Troubleshoot page instead of interrupting with the setup wizard.
* **One-Click Repair:** Use Troubleshoot to gracefully close Steam, restore the bundled DLLs, and recreate missing Lua and depot cache folders. Steam remains closed until you launch it again.
* **Better Steam Detection:** SteamVault validates the saved Steam path and falls back to the Windows registry when that path is no longer valid.

---

## 💻 Requirements

* **Windows 10** (1809+) or **Windows 11**
* Steam must be installed

---

## 📥 How to Install

### Option A: Windows Setup Installer
1. Download `SteamVault-Setup.exe` from the Assets section below.
2. Run the installer and follow the setup wizard.
3. Launch SteamVault from your Desktop or Start Menu.

### Option B: Portable Standalone
1. Download `SteamVault.exe` from the Assets section below.
2. Place it in any folder and double-click to run.

---

**Need help or have game requests?**
[Join our Discord Server](https://discord.gg/kxpRNzqnsX)
