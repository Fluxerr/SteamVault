using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using SharpCompress.Readers;
using System.IO;

namespace SteamVault.Services;

/// <summary>
/// Handles multiplayer fix integration with online-fix.me.
/// Parses Steam appmanifest files to find game install directories,
/// monitors the app directory for RAR files, and extracts them into the game folder.
/// Downloads must be done manually via browser due to Cloudflare WAF
/// blocking all programmatic HTTP requests.
/// </summary>
public class OnlineFixService
{
    /// <summary>
    /// RAR archives from online-fix.me are always encrypted with this password.
    /// </summary>
    private const string RarPassword = "online-fix.me";

    private readonly SettingsService _settings;
    private FileSystemWatcher? _watcher;
    private string? _lastExtractedFile;

    public OnlineFixService(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Checks whether the game is installed. Simply verifies that
    /// steamapps\appmanifest_{appId}.acf exists in the configured Steam directory.
    /// </summary>
    public bool IsGameInstalled(string appId)
    {
        var steamDir = _settings.Settings.SteamDirectory;
        if (string.IsNullOrWhiteSpace(steamDir) || !Directory.Exists(steamDir))
            return false;

        return File.Exists(Path.Combine(steamDir, "steamapps", $"appmanifest_{appId}.acf"));
    }

    /// <summary>
    /// Parses steamapps\appmanifest_{appId}.acf and extracts the "installdir" value.
    /// Returns null if the file doesn't exist or parsing fails.
    /// </summary>
    public string? ParseAppManifest(string appId)
    {
        try
        {
            var steamDir = _settings.Settings.SteamDirectory;
            if (string.IsNullOrWhiteSpace(steamDir) || !Directory.Exists(steamDir))
                return null;

            var manifestPath = Path.Combine(steamDir, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(manifestPath))
                return null;

            return ParseAppManifestFile(manifestPath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a specific appmanifest file and extracts "installdir".
    /// </summary>
    public static string? ParseAppManifestFile(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath)) return null;
            var lines = File.ReadAllLines(manifestPath);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("\"installdir\"", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.Split('"', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                        return parts[^1].Trim();
                }
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns the full game install path from the appmanifest:
    /// steamapps\common\{installdir}
    /// </summary>
    public string? GetGameInstallPath(string appId)
    {
        var installdir = ParseAppManifest(appId);
        if (string.IsNullOrWhiteSpace(installdir))
            return null;

        var steamDir = _settings.Settings.SteamDirectory;
        if (string.IsNullOrWhiteSpace(steamDir))
            return null;

        return Path.Combine(steamDir, "steamapps", "common", installdir);
    }

    /// <summary>
    /// Opens the browser to search online-fix.me for the given game.
    /// </summary>
    public void SearchOnlineFix(string gameName)
    {
        var encoded = Uri.EscapeDataString(gameName);
        var url = $"https://online-fix.me/?s={encoded}";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    /// <summary>
    /// Opens the game directory in File Explorer.
    /// </summary>
    public void OpenGameDirectory(string appId)
    {
        var path = GetGameInstallPath(appId);
        if (path != null && Directory.Exists(path))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
    }

    /// <summary>
    /// Opens the app directory in File Explorer so the user can drop RAR files there.
    /// </summary>
    public static void OpenAppDirectory()
    {
        var appDir = GetAppDirectory();
        if (Directory.Exists(appDir))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = appDir,
                UseShellExecute = true
            });
        }
    }

    /// <summary>
    /// Gets the directory where the SteamVault exe is running from.
    /// This is where users should save their RAR files.
    /// </summary>
    public static string GetAppDirectory()
    {
        return AppDomain.CurrentDomain.BaseDirectory;
    }

    /// <summary>
    /// Scans the app directory for RAR files and returns the newest one
    /// that hasn't been extracted yet. Returns null if no new RAR files are found.
    /// </summary>
    public string? FindNewestUnprocessedRar()
    {
        var appDir = GetAppDirectory();
        if (!Directory.Exists(appDir)) return null;

        var rarFile = Directory.GetFiles(appDir, "*.rar", SearchOption.TopDirectoryOnly)
            .Select(f => new FileInfo(f))
            .OrderByDescending(fi => fi.LastWriteTime)
            .FirstOrDefault();

        if (rarFile == null) return null;
        if (rarFile.FullName == _lastExtractedFile) return null;

        return rarFile.FullName;
    }

    /// <summary>
    /// Result types for extraction operations.
    /// </summary>
    public enum ExtractResult { Success, PermissionDenied, Failed }

    /// <summary>
    /// Attempts to extract a password-protected RAR archive into the game's install directory.
    /// Deletes the RAR file after extraction (even on failure, unless permission denied).
    /// Returns (result, errorMessage).
    /// </summary>
    public (ExtractResult Result, string? Error) ExtractFixToGameDir(string rarPath, string gameDir, Action<string>? onStatus = null)
    {
        try
        {
            onStatus?.Invoke("Checking RAR file...");

            if (!File.Exists(rarPath))
                return (ExtractResult.Failed, "RAR file not found.");

            var fileInfo = new FileInfo(rarPath);
            if (fileInfo.Length == 0)
                return (ExtractResult.Failed, "RAR file is empty.");

            // Check if we can write to the game directory before extracting
            if (!Directory.Exists(gameDir))
            {
                try { Directory.CreateDirectory(gameDir); }
                catch (UnauthorizedAccessException)
                {
                    return (ExtractResult.PermissionDenied,
                        "Permission denied — the game folder is in a protected location.\nThe app needs administrator rights to write files there.");
                }
            }

            // Quick write-permission check
            var testFile = Path.Combine(gameDir, ".steamvault_write_test");
            try
            {
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
            }
            catch (UnauthorizedAccessException)
            {
                return (ExtractResult.PermissionDenied,
                    "Permission denied — the game folder is in a protected location.\nThe app needs administrator rights to write files there.");
            }

            onStatus?.Invoke("Extracting fix files (password-protected)...");

            using var archive = RarArchive.Open(rarPath, new ReaderOptions { Password = RarPassword });

            var entries = archive.Entries.ToList();
            if (entries.Count == 0)
                return (ExtractResult.Failed, "RAR archive appears to be empty.");

            onStatus?.Invoke($"Extracting {entries.Count} files...");

            foreach (var entry in entries.Where(e => !e.IsDirectory))
            {
                try
                {
                    var entryKey = entry.Key ?? Path.GetFileName(entry.Key ?? "unknown");
                    entryKey = entryKey.TrimStart('\\', '/');
                    var destPath = Path.Combine(gameDir, entryKey);
                    var destDir = Path.GetDirectoryName(destPath);
                    if (destDir != null && !Directory.Exists(destDir))
                        Directory.CreateDirectory(destDir);

                    entry.WriteToFile(destPath, new ExtractionOptions
                    {
                        ExtractFullPath = false,
                        Overwrite = true
                    });
                }
                catch (UnauthorizedAccessException)
                {
                    return (ExtractResult.PermissionDenied,
                        "Permission denied — the game folder is in a protected location.\nThe app needs administrator rights to write files there.");
                }
                catch (Exception entryEx)
                {
                    return (ExtractResult.Failed, $"Failed to extract '{entry.Key}': {entryEx.Message}");
                }
            }

            onStatus?.Invoke("✓ Fix applied successfully!");

            var markerPath = Path.Combine(gameDir, ".steamvault_fix_applied");
            File.WriteAllText(markerPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            _lastExtractedFile = rarPath;
            return (ExtractResult.Success, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (ExtractResult.PermissionDenied,
                "Permission denied — the game folder is in a protected location.\nThe app needs administrator rights to write files there.");
        }
        catch (SharpCompress.Common.CryptographicException)
        {
            return (ExtractResult.Failed, "Wrong password. The RAR uses a password other than 'online-fix.me', or the file may not be a valid RAR archive.");
        }
        catch (SharpCompress.Common.InvalidFormatException ex)
        {
            return (ExtractResult.Failed, $"Invalid or corrupt RAR file: {ex.Message}. Try re-downloading it.");
        }
        catch (Exception ex)
        {
            return (ExtractResult.Failed, $"Extraction failed: {ex.Message}");
        }
        finally
        {
            // Clean up the RAR file after extraction attempt (keep it on permission denied for retry)
            CleanupTempFile(rarPath);
        }
    }

    /// <summary>
    /// Checks if an online fix has been previously applied to the game.
    /// </summary>
    public bool IsFixApplied(string appId)
    {
        var path = GetGameInstallPath(appId);
        if (path == null) return false;
        return File.Exists(Path.Combine(path, ".steamvault_fix_applied"));
    }

    /// <summary>
    /// Starts watching the app directory for RAR files.
    /// When a new RAR is detected and stabilized, the callback is invoked.
    /// </summary>
    public void StartWatching(Action<string> onRarDetected)
    {
        StopWatching();
        var appDir = GetAppDirectory();
        if (!Directory.Exists(appDir))
            Directory.CreateDirectory(appDir);

        _watcher = new FileSystemWatcher(appDir, "*.rar")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        // When a RAR is created or changed, wait for it to stabilize then notify
        _watcher.Created += (s, e) => WaitForStableFile(e.FullPath, onRarDetected);
        _watcher.Changed += (s, e) => WaitForStableFile(e.FullPath, onRarDetected);
        _watcher.Renamed += (s, e) => WaitForStableFile(e.FullPath, onRarDetected);
    }

    private static async void WaitForStableFile(string filePath, Action<string> callback)
    {
        try
        {
            // Wait for the file to stop being written to
            long lastSize = -1;
            for (int i = 0; i < 30; i++) // Max 30 seconds
            {
                await Task.Delay(1000);
                if (!File.Exists(filePath)) return;

                var currentSize = new FileInfo(filePath).Length;
                if (currentSize == lastSize && currentSize > 0)
                {
                    callback(filePath);
                    return;
                }
                lastSize = currentSize;
            }
            // Timed out waiting — file might still be usable
            if (File.Exists(filePath) && new FileInfo(filePath).Length > 0)
                callback(filePath);
        }
        catch
        {
            // If file disappears or error, just ignore
        }
    }

    /// <summary>
    /// Stops watching the app directory.
    /// </summary>
    public void StopWatching()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    /// <summary>
    /// Deletes a file if it exists.
    /// </summary>
    public static void CleanupTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}