using System.IO;

namespace SteamVault.Models;

public class AppSettings
{
    public string SteamDirectory { get; set; } = "";
    public string LuaOutputPath { get; set; } = "";
    public string ManifestCachePath { get; set; } = "";
    public List<DownloadHistoryEntry> DownloadHistory { get; set; } = new();

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

        if (string.IsNullOrWhiteSpace(ManifestCachePath))
            ManifestCachePath = Path.Combine(SteamDirectory, "config", "depotcache");
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
