using System.IO;

namespace SteamVault.Models;

public class AppSettings
{
    public string SteamDirectory { get; set; } = "";
    public string LuaOutputPath { get; set; } = "";
    public string DepotcachePath { get; set; } = "";

    public string GetEffectiveDepotcachePath()
    {
        if (!string.IsNullOrWhiteSpace(DepotcachePath))
            return DepotcachePath;

        if (!string.IsNullOrWhiteSpace(SteamDirectory))
            return Path.Combine(SteamDirectory, "config", "depotcache");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SteamVault", "depotcache");
    }

    public List<string> GetAllDepotcachePaths()
    {
        var paths = new List<string>();

        // Primary location: config/depotcache (or custom DepotcachePath)
        var primary = GetEffectiveDepotcachePath();
        if (!string.IsNullOrWhiteSpace(primary))
            paths.Add(primary);

        // Secondary location: depotcache (directly under Steam directory)
        if (!string.IsNullOrWhiteSpace(SteamDirectory))
        {
            var secondary = Path.Combine(SteamDirectory, "depotcache");
            if (!paths.Contains(secondary, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(secondary);
            }
        }

        return paths;
    }

    private void EnsureDerivedPaths()
    {
        if (string.IsNullOrWhiteSpace(LuaOutputPath))
            LuaOutputPath = Path.Combine(SteamDirectory, "config", "lua");
        if (string.IsNullOrWhiteSpace(DepotcachePath))
            DepotcachePath = Path.Combine(SteamDirectory, "config", "depotcache");
    }
    public List<DownloadHistoryEntry> DownloadHistory { get; set; } = new();
    public List<string> SearchHistory { get; set; } = new();

    /// <summary>
    /// Whether OpenSteamTool has been installed (DLLs copied to Steam directory).
    /// Once true, the installation wizard is never shown again.
    /// </summary>
    public bool IsInstalled { get; set; } = false;

    /// <summary>
    /// When enabled, SteamVault will start with Windows, run in the system tray,
    /// and automatically scan + update all game Lua configs on launch.
    /// </summary>
    public bool AutoUpdateEnabled { get; set; } = false;

    /// <summary>
    /// User profile name displayed in Family Vault and sharing.
    /// </summary>
    public string Username { get; set; } = "VaultUser";

    /// <summary>
    /// The last version of the application that was run. Used to show "What's New" on updates.
    /// </summary>
    public string LastRunVersion { get; set; } = "1.0.0.0";

    /// <summary>
    /// Tracks which version's "What's New" dialog has actually been displayed and dismissed by the user.
    /// </summary>
    public string ShownWhatsNewVersion { get; set; } = "";

    /// <summary>
    /// Enables Windows 11 Dark Glassmorphism (Acrylic/Mica backdrop blur) for Steam windows.
    /// </summary>
    public bool SteamGlassThemeEnabled { get; set; } = true;

    /// <summary>
    /// Enables Turbo Downloader speed accelerator for Steam game depot downloads.
    /// </summary>
    public bool TurboDownloaderEnabled { get; set; } = true;

    /// <summary>
    /// Currently selected theme name. Default is "Gruvbox" (retro warm dark theme).
    /// </summary>
    public string Theme { get; set; } = "Gruvbox";

    /// <summary>
    /// Custom accent color overrides the theme's primary accent. Stored as hex (#RRGGBB).
    /// If null or empty, the theme's default accent is used.
    /// </summary>
    public string? CustomAccentColor { get; set; }

    /// <summary>
    /// Default export / backup directory. If empty, the user is prompted.
    /// </summary>
    public string LastExportDirectory { get; set; } = "";

    /// <summary>
    /// Auto-detects Steam directory from common paths if not already set.
    /// Returns true if a valid path was found or already configured.
    /// </summary>
    public bool AutoDetectPaths()
    {
        if (!string.IsNullOrWhiteSpace(SteamDirectory) && Directory.Exists(SteamDirectory))
        {
            EnsureDerivedPaths();
            return true;
        }

        // Common Steam install locations
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            @"D:\Steam",
            @"D:\STEAM",
            @"E:\Steam",
            @"E:\STEAM",
            @"C:\Steam",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steam"),
        };

        foreach (var path in candidates)
        {
            if (Directory.Exists(path))
            {
                SteamDirectory = path;
                EnsureDerivedPaths();
                return true;
            }
        }

        // Default fallback
        SteamDirectory = @"C:\Program Files (x86)\Steam";
        EnsureDerivedPaths();
        return false;
    }
}

public class DownloadHistoryEntry
{
    public string AppId { get; set; } = "";
    public string GameName { get; set; } = "";
    public string HeaderImageUrl { get; set; } = "";
    public DateTime DownloadDate { get; set; }
    public string Status { get; set; } = "Completed";
    public List<string> DepotIds { get; set; } = new();
}
