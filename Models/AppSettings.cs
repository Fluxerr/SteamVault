using System.IO;

namespace SteamVault.Models;

public class AppSettings
{
    public string SteamDirectory { get; set; } = "";
    public string LuaOutputPath { get; set; } = "";
    public List<DownloadHistoryEntry> DownloadHistory { get; set; } = new();

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
    /// Currently selected theme name. Default is "Dark" (the original premium purple theme).
    /// Valid values: "Dark", "AmoledBlack", "MidnightBlue", "SlateGray", "EmeraldNight"
    /// </summary>
    public string Theme { get; set; } = "Dark";

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

    private void EnsureDerivedPaths()
    {
        if (string.IsNullOrWhiteSpace(LuaOutputPath))
            LuaOutputPath = Path.Combine(SteamDirectory, "config", "lua");
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
