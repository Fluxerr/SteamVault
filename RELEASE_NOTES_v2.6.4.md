# SteamVault v2.6.4

> Self-contained application — available as a Windows Setup Installer or Portable Standalone executable. No .NET runtime required.

---

## What's New

### 🔧 OpenSteamTool DLL Installation and Repair
* **Installed with SteamVault:** The Windows installer detects the Steam folder containing `steam.exe` and copies the bundled OpenSteamTool DLLs there during setup.
* **Reinstall on Demand:** Troubleshoot now overwrites all bundled OpenSteamTool DLLs when repairing, even if the files have the same names or were previously present.
* **Steam Shutdown:** Setup and repair gracefully close Steam before replacing the DLLs. Steam remains closed until you launch it again.
* **Normal Startup:** Regular SteamVault launches no longer rewrite DLLs or open the setup wizard for a background refresh.

---

## 💻 Requirements

* **Windows 10** (1809+) or **Windows 11**
* Steam must be installed

---

## 📥 How to Install

1. Download `SteamVault-Setup.exe` from the Assets section below.
2. Run the installer. It will find Steam and install the OpenSteamTool DLLs.
3. Approve the Windows permission prompt if Steam is installed in a protected folder.
4. Launch SteamVault from your Desktop or Start Menu.

---

**Need help or have game requests?**
[Join our Discord Server](https://discord.gg/kxpRNzqnsX)
