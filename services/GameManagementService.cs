using System.Diagnostics;
using System.IO;

namespace SteamVault.Services;

/// <summary>
/// Handles game management: install detection, executable finder, launch, delete.
/// Uses the configured Steam directory from settings (set during first launch).
/// </summary>
public class GameManagementService
{
    private readonly SettingsService _settings;

    public GameManagementService(SettingsService settings, OnlineFixService onlineFix)
    {
        _settings = settings;
    }

    /// <summary>
    /// Checks whether the game is installed by verifying
    /// steamapps\appmanifest_{appId}.acf exists.
    /// </summary>
    public bool IsGameInstalled(string appId)
    {
        var steamDir = _settings.Settings.SteamDirectory;
        if (string.IsNullOrWhiteSpace(steamDir) || !Directory.Exists(steamDir))
            return false;

        return File.Exists(Path.Combine(steamDir, "steamapps", $"appmanifest_{appId}.acf"));
    }

    /// <summary>
    /// Gets the game install path by reading the "installdir" from the ACF file.
    /// Returns: steamapps\common\{installdir}
    /// </summary>
    public string? GetGameInstallPath(string appId)
    {
        var steamDir = _settings.Settings.SteamDirectory;
        if (string.IsNullOrWhiteSpace(steamDir) || !Directory.Exists(steamDir))
            return null;

        var manifestPath = Path.Combine(steamDir, "steamapps", $"appmanifest_{appId}.acf");
        var installdir = OnlineFixService.ParseAppManifestFile(manifestPath);
        if (string.IsNullOrWhiteSpace(installdir))
            return null;

        return Path.Combine(steamDir, "steamapps", "common", installdir);
    }

    /// <summary>
    /// Finds the game's main executable in the install directory.
    /// </summary>
    public string? FindGameExecutable(string gameName, string appId)
    {
        var installPath = GetGameInstallPath(appId);
        if (installPath == null || !Directory.Exists(installPath)) return null;

        try
        {
            var exeFiles = Directory.GetFiles(installPath, "*.exe", SearchOption.TopDirectoryOnly);
            var candidates = exeFiles
                .Where(f =>
                {
                    var name = Path.GetFileName(f).ToLowerInvariant();
                    return !name.Contains("unins") && !name.Contains("crash") && !name.Contains("dump") &&
                           !name.Contains("redist") && !name.Contains("vcredist") &&
                           !name.Contains("dxsetup") && !name.Contains("dotnet");
                }).ToList();

            if (candidates.Count == 0) return exeFiles.FirstOrDefault();

            var nameMatch = candidates.FirstOrDefault(f =>
                Path.GetFileName(f).ToLowerInvariant().Contains(gameName.ToLowerInvariant().Split(' ')[0]));
            return nameMatch ?? candidates.FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>
    /// Launches the game executable.
    /// </summary>
    public bool LaunchGame(string gameName, string appId)
    {
        var exePath = FindGameExecutable(gameName, appId);
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
                UseShellExecute = true
            });
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Deletes the Lua config file for a game.
    /// </summary>
    public bool DeleteLuaFile(string appId)
    {
        try
        {
            var luaPath = _settings.Settings.LuaOutputPath;
            if (string.IsNullOrWhiteSpace(luaPath)) return false;
            var luaFile = Path.Combine(luaPath, $"{appId}.lua");
            if (!File.Exists(luaFile)) return false;
            File.Delete(luaFile);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Deletes the game installation directory (read from ACF file).
    /// </summary>
    public bool DeleteGameInstallation(string appId)
    {
        var installPath = GetGameInstallPath(appId);
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            return false;

        try
        {
            Directory.Delete(installPath, true);
            return true;
        }
        catch { return false; }
    }
}