using SteamVault.Models;
using System.IO;
using System.Windows;

namespace SteamVault.Services;

/// <summary>
/// Background service that handles automatic game update checking and Lua regeneration.
/// When enabled, runs silently at startup in the system tray and scans all games for updates.
/// Designed to use minimal resources with throttled API calls and efficient scanning.
/// </summary>
public class AutoUpdateService
{
    private readonly SettingsService _settings;
    private readonly SteamApiService _steamApi;
    private readonly DepotKeyService _depotKeyService;
    private readonly LuaParserService _luaParser;
    private readonly DownloadService _downloadService;
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    public event Action<string>? OnStatusChanged;
    public event Action<int, int>? OnProgressChanged; // (completed, total)
    public event Action<string, bool>? OnGameUpdated; // (gameName, success)

    public bool IsRunning => _isRunning;

    public AutoUpdateService(
        SettingsService settings,
        SteamApiService steamApi,
        DepotKeyService depotKeyService,
        LuaParserService luaParser,
        DownloadService downloadService)
    {
        _settings = settings;
        _steamApi = steamApi;
        _depotKeyService = depotKeyService;
        _luaParser = luaParser;
        _downloadService = downloadService;
    }

    /// <summary>
    /// Starts the auto-update scan in the background.
    /// Scans all games and updates any that have changed manifests.
    /// </summary>
    public async Task<AutoUpdateResult> RunAutoUpdateAsync()
    {
        var resultSummary = new AutoUpdateResult();

        if (_isRunning) return resultSummary;

        _isRunning = true;
        _cts = new CancellationTokenSource();

        try
        {
            OnStatusChanged?.Invoke("Starting auto-update scan...");

            var luaPath = _settings.Settings.LuaOutputPath;
            if (string.IsNullOrWhiteSpace(luaPath) || !Directory.Exists(luaPath))
            {
                OnStatusChanged?.Invoke("Lua folder not found. Skipping auto-update.");
                resultSummary.FolderMissing = true;
                resultSummary.Summary = "Lua output folder not found.";
                return resultSummary;
            }

            // Ensure depot keys are loaded
            if (!_depotKeyService.IsLoaded)
            {
                OnStatusChanged?.Invoke("Loading depot key database...");
                await _depotKeyService.LoadAsync();
            }

            // Scan for local .lua files
            var localEntries = _luaParser.ScanLuaFolder(luaPath);
            resultSummary.TotalGames = localEntries.Count;

            if (localEntries.Count == 0)
            {
                OnStatusChanged?.Invoke("No games found. Auto-update complete.");
                resultSummary.Summary = "No games found in local vault.";
                return resultSummary;
            }

            OnStatusChanged?.Invoke($"Found {localEntries.Count} game(s). Checking for updates...");

            var updatedCount = 0;
            var failedCount = 0;
            var upToDateCount = 0;
            var semaphore = new SemaphoreSlim(3); // Limit concurrent API calls to be gentle on resources

            for (int i = 0; i < localEntries.Count; i++)
            {
                if (_cts.Token.IsCancellationRequested) break;

                var entry = localEntries[i];
                await semaphore.WaitAsync(_cts.Token);

                try
                {
                    OnProgressChanged?.Invoke(i + 1, localEntries.Count);
                    OnStatusChanged?.Invoke($"Checking {entry.Name} ({i + 1}/{localEntries.Count})...");

                    var needsUpdate = await CheckIfNeedsUpdateAsync(entry);

                    if (needsUpdate)
                    {
                        OnStatusChanged?.Invoke($"Updating {entry.Name}...");
                        var result = await _downloadService.DownloadGameAsync(entry.AppId);

                        if (result.Success)
                        {
                            updatedCount++;
                            OnGameUpdated?.Invoke(entry.Name, true);
                        }
                        else
                        {
                            failedCount++;
                            OnGameUpdated?.Invoke(entry.Name, false);
                        }
                    }
                    else
                    {
                        upToDateCount++;
                    }

                    // Small delay between checks to be kind to APIs
                    await Task.Delay(500, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    failedCount++;
                }
                finally
                {
                    semaphore.Release();
                }
            }

            resultSummary.UpdatedCount = updatedCount;
            resultSummary.UpToDateCount = upToDateCount;
            resultSummary.FailedCount = failedCount;

            var summary = $"Auto-update complete: {updatedCount} updated, {upToDateCount} up to date";
            if (failedCount > 0) summary += $", {failedCount} failed";
            OnStatusChanged?.Invoke(summary);
            
            resultSummary.Summary = summary;
        }
        catch (OperationCanceledException)
        {
            OnStatusChanged?.Invoke("Auto-update cancelled.");
            resultSummary.Summary = "Auto-update cancelled.";
        }
        catch (Exception ex)
        {
            OnStatusChanged?.Invoke($"Auto-update error: {ex.Message}");
            resultSummary.Summary = $"Auto-update error: {ex.Message}";
        }
        finally
        {
            _isRunning = false;
        }

        return resultSummary;
    }

    private async Task<bool> CheckIfNeedsUpdateAsync(LibraryEntry entry)
    {
        if (entry.Depots.Count == 0) return false;

        try
        {
            var latestDepots = await _steamApi.GetDepotsFromSteamCmdAsync(entry.AppId);
            if (latestDepots.Count == 0) return false;

            foreach (var localDepot in entry.Depots)
            {
                if (string.IsNullOrWhiteSpace(localDepot.ManifestId)) continue;

                var latest = latestDepots.FirstOrDefault(d => d.DepotId == localDepot.DepotId);
                if (latest == null) continue;

                if (!string.IsNullOrWhiteSpace(latest.ManifestId) &&
                    !string.Equals(localDepot.ManifestId, latest.ManifestId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // Check for new depots
            foreach (var latest in latestDepots)
            {
                if (string.IsNullOrWhiteSpace(latest.ManifestId)) continue;
                if (!entry.Depots.Any(d => d.DepotId == latest.DepotId))
                    return true;
            }
        }
        catch
        {
            // Fail-safe: don't update if we can't check
        }

        return false;
    }

    public void Cancel()
    {
        _cts?.Cancel();
    }

    /// <summary>
    /// Registers or unregisters the app from Windows startup (Run registry key).
    /// </summary>
    public static void SetStartupEnabled(bool enabled)
    {
        try
        {
            var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);

            if (key == null) return;

            if (enabled)
            {
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    key.SetValue("SteamVault", $"\"{exePath}\" --silent");
                }
            }
            else
            {
                key.DeleteValue("SteamVault", false);
            }

            key.Close();
        }
        catch
        {
            // Silent fail — may not have registry access
        }
    }

    /// <summary>
    /// Checks if the app is registered to start with Windows.
    /// </summary>
    public static bool IsStartupEnabled()
    {
        try
        {
            var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);

            if (key == null) return false;

            var value = key.GetValue("SteamVault");
            key.Close();
            return value != null;
        }
        catch
        {
            return false;
        }
    }
}

public class AutoUpdateResult
{
    public int TotalGames { get; set; }
    public int UpdatedCount { get; set; }
    public int UpToDateCount { get; set; }
    public int FailedCount { get; set; }
    public string Summary { get; set; } = "";
    public bool FolderMissing { get; set; }
}
