using SteamVault.Models;
using System.IO;

namespace SteamVault.Services;

/// <summary>
/// Orchestrates the full download and installation flow:
/// 1. Fetches game info from Steam Store API
/// 2. Resolves depots + manifest IDs from SteamCMD API (free, no key)
/// 3. Attaches decryption keys from local database
/// 4. Generates a .lua file for OpenSteamTool / SteamTools
/// </summary>
public class DownloadService
{
    private readonly SteamApiService _steamApi;
    private readonly DepotKeyService _depotKeyService;
    private readonly LuaGeneratorService _luaGenerator;
    private readonly SettingsService _settings;

    public DownloadService(
        SteamApiService steamApi,
        DepotKeyService depotKeyService,
        LuaGeneratorService luaGenerator,
        SettingsService settings)
    {
        _steamApi = steamApi;
        _depotKeyService = depotKeyService;
        _luaGenerator = luaGenerator;
        _settings = settings;
    }

    /// <summary>
    /// One-click pipeline: enter App ID, get everything done.
    /// </summary>
    public async Task<DownloadResult> DownloadGameAsync(string appId, Action<string>? onStatus = null, Action<double>? onProgress = null)
    {
        var result = new DownloadResult { AppId = appId };

        try
        {
            // Validate paths
            var luaOutputPath = _settings.Settings.LuaOutputPath;
            if (string.IsNullOrWhiteSpace(luaOutputPath))
            {
                result.Error = "Lua output path is not configured. Check Settings.";
                return result;
            }

            // Step 1: Fetch game info
            onStatus?.Invoke("Fetching game details from Steam...");
            onProgress?.Invoke(10);

            var game = await _steamApi.GetAppDetailsAsync(appId);
            if (game == null)
            {
                result.Error = "Game not found. Check the App ID and try again.";
                return result;
            }

            result.GameName = game.Name;
            result.HeaderImageUrl = game.HeaderImageUrl;
            onStatus?.Invoke($"Found: {game.Name} — Resolving depots...");
            onProgress?.Invoke(25);

            // Step 2: Get depots + manifest IDs from SteamCMD API (free, no key needed!)
            onStatus?.Invoke("Querying SteamCMD for depot and manifest data...");
            var depots = await _steamApi.GetDepotsFromSteamCmdAsync(appId);

            if (depots.Count == 0)
            {
                // Fallback: try local database discovery
                onStatus?.Invoke("SteamCMD returned no depots, checking local database...");
                if (_depotKeyService.IsLoaded)
                {
                    depots = _depotKeyService.FindDepotsForApp(appId);
                }
            }

            if (depots.Count == 0)
            {
                result.Error = $"No depots found for {game.Name} (App ID: {appId}). This game may not have public depot data.";
                return result;
            }

            onProgress?.Invoke(50);
            onStatus?.Invoke($"Found {depots.Count} depot(s) — Loading decryption keys...");

            // Step 3: Ensure depot key database is loaded, attach keys
            if (!_depotKeyService.IsLoaded)
            {
                await _depotKeyService.LoadAsync();
            }

            // Attach decryption keys
            var keysAttached = 0;
            var manifestsResolved = 0;
            foreach (var depot in depots)
            {
                var key = _depotKeyService.GetDepotKey(depot.DepotId);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    depot.DecryptionKey = key;
                    keysAttached++;
                }

                if (!string.IsNullOrWhiteSpace(depot.ManifestId))
                {
                    manifestsResolved++;
                }
            }

            // Attach app access token
            game.AppAccessToken = _depotKeyService.GetAppAccessToken(appId);

            onProgress?.Invoke(70);

            // Filter to only depots with manifest IDs
            var usableDepots = depots.Where(d => !string.IsNullOrWhiteSpace(d.ManifestId)).ToList();

            if (usableDepots.Count == 0)
            {
                result.Error = $"Found {depots.Count} depot(s) but none have manifest IDs resolved. The game data may not be publicly accessible.";
                return result;
            }

            // Step 4: Generate .lua file
            onStatus?.Invoke("Generating Lua configuration...");
            onProgress?.Invoke(85);

            var luaEntries = usableDepots.Select(d => new DepotLuaEntry
            {
                DepotId = d.DepotId,
                DecryptionKey = d.DecryptionKey,
                ManifestId = d.ManifestId
            }).ToList();

            var luaFilePath = _luaGenerator.GenerateLuaFile(appId, game.Name, luaEntries, luaOutputPath, game.AppAccessToken);

            onProgress?.Invoke(100);

            result.Success = true;
            result.LuaFilePath = luaFilePath;
            result.DepotCount = usableDepots.Count;
            result.KeysAttached = keysAttached;
            result.ManifestsResolved = manifestsResolved;
            result.Game = game;
            result.Depots = depots;

            onStatus?.Invoke($"✓ Done! Generated Lua for {usableDepots.Count} depot(s) — {Path.GetFileName(luaFilePath)}");

            // Save to history
            _settings.Settings.DownloadHistory.Add(new DownloadHistoryEntry
            {
                AppId = appId,
                GameName = game.Name,
                HeaderImageUrl = game.HeaderImageUrl,
                DownloadDate = DateTime.Now,
                Status = "Completed",
                DepotIds = usableDepots.Select(d => d.DepotId).ToList()
            });
            _settings.Save();
        }
        catch (Exception ex)
        {
            result.Error = $"Unexpected error: {ex.Message}";
        }

        return result;
    }
}

public class DownloadResult
{
    public string AppId { get; set; } = "";
    public string GameName { get; set; } = "";
    public string HeaderImageUrl { get; set; } = "";
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? LuaFilePath { get; set; }
    public int DepotCount { get; set; }
    public int KeysAttached { get; set; }
    public int ManifestsResolved { get; set; }
    public GameInfo? Game { get; set; }
    public List<DepotInfo>? Depots { get; set; }
}
