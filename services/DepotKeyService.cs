using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SteamVault.Models;
using System.IO;
using System.Net.Http;

namespace SteamVault.Services;

/// <summary>
/// Loads and indexes the local/AppData depot keys and app access tokens from ManifestHub JSON files.
/// Also provides lookup by app ID and depot ID, and synchronization with GitHub.
/// </summary>
public class DepotKeyService
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamVault", "Data");

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5) // database can be large (16MB depotkeys)
    };

    private Dictionary<string, string> _appAccessTokens = new();
    private Dictionary<string, string> _depotKeys = new();
    private Dictionary<string, string> _cachedKeys = new();
    private bool _loaded;

    private static readonly string KeysCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamVault", "keys_cache.json");

    static DepotKeyService()
    {
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "SteamVault/1.0");
    }

    public int AppTokenCount => _appAccessTokens.Count;
    public int DepotKeyCount => _depotKeys.Count;
    public bool IsLoaded => _loaded;

    public async Task LoadAsync(bool forceReload = false)
    {
        if (_loaded && !forceReload) return;

        var tasks = new List<Task>();

        // 1. Load AppData (synced from GitHub) as the base layer
        var appDataTokens = Path.Combine(AppDataDir, "appaccesstokens.json");
        var appDataKeys = Path.Combine(AppDataDir, "depotkeys.json");

        if (File.Exists(appDataTokens))
        {
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    var json = File.ReadAllText(appDataTokens);
                    _appAccessTokens = JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new();
                    System.Diagnostics.Debug.WriteLine($"DepotKeyService: Loaded {_appAccessTokens.Count} app access tokens from AppData");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"DepotKeyService: Failed to load AppData appaccesstokens.json — {ex.Message}");
                }
            }));
        }

        if (File.Exists(appDataKeys))
        {
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    var json = File.ReadAllText(appDataKeys);
                    _depotKeys = JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new();
                    System.Diagnostics.Debug.WriteLine($"DepotKeyService: Loaded {_depotKeys.Count} depot keys from AppData");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"DepotKeyService: Failed to load AppData depotkeys.json — {ex.Message}");
                }
            }));
        }

        // Load cached keys (persisted across sessions)
        tasks.Add(Task.Run(() =>
        {
            try
            {
                if (File.Exists(KeysCachePath))
                {
                    var json = File.ReadAllText(KeysCachePath);
                    _cachedKeys = JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new();
                    System.Diagnostics.Debug.WriteLine($"DepotKeyService: Loaded {_cachedKeys.Count} cached keys");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DepotKeyService: Failed to load keys cache — {ex.Message}");
            }
        }));

        await Task.WhenAll(tasks);

        // 2. ALWAYS overlay the baked-in local database (Data/depotkeys.json from the app folder).
        //    This is where you manually add new keys — they take priority over AppData.
        var basePath = AppDomain.CurrentDomain.BaseDirectory;
        var localTokens = Path.Combine(basePath, "Data", "appaccesstokens.json");
        var localKeys = Path.Combine(basePath, "Data", "depotkeys.json");

        if (File.Exists(localTokens))
        {
            try
            {
                var json = File.ReadAllText(localTokens);
                var localToks = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                if (localToks != null)
                {
                    foreach (var kvp in localToks)
                        _appAccessTokens[kvp.Key] = kvp.Value; // overwrite with baked-in value
                    System.Diagnostics.Debug.WriteLine($"DepotKeyService: Overlaid {localToks.Count} app access tokens from local Data/");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DepotKeyService: Failed to load local appaccesstokens.json — {ex.Message}");
            }
        }

        if (File.Exists(localKeys))
        {
            try
            {
                var json = File.ReadAllText(localKeys);
                var localDepotKeys = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                if (localDepotKeys != null)
                {
                    foreach (var kvp in localDepotKeys)
                        _depotKeys[kvp.Key] = kvp.Value; // overwrite with baked-in value
                    System.Diagnostics.Debug.WriteLine($"DepotKeyService: Overlaid {localDepotKeys.Count} depot keys from local Data/");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DepotKeyService: Failed to load local depotkeys.json — {ex.Message}");
            }
        }

        _loaded = true;
        System.Diagnostics.Debug.WriteLine($"DepotKeyService: Load complete — {_depotKeys.Count} depot keys, {_appAccessTokens.Count} app tokens, {_cachedKeys.Count} cached keys");
    }

    /// <summary>
    /// Synchronizes the depot keys and app access tokens database with the latest versions from GitHub.
    /// </summary>
    public async Task<bool> SyncDatabaseAsync(Action<string>? onProgress = null)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);

            var tempTokensPath = Path.Combine(AppDataDir, "appaccesstokens.json.tmp");
            var tempKeysPath = Path.Combine(AppDataDir, "depotkeys.json.tmp");

            // 1. Download App Access Tokens
            onProgress?.Invoke("Downloading app access tokens (this is quick)...");
            var tokensUrl = "https://raw.githubusercontent.com/bsinwhg/ManifestHub/main/appaccesstokens.json";
            using (var tokensResponse = await _httpClient.GetAsync(tokensUrl))
            {
                if (!tokensResponse.IsSuccessStatusCode)
                    throw new HttpRequestException($"Failed to download tokens: {tokensResponse.ReasonPhrase}");

                var tokensData = await tokensResponse.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(tempTokensPath, tokensData);
            }

            // 2. Download Depot Keys
            onProgress?.Invoke("Downloading depot keys database (approx. 16MB, please wait)...");
            var keysUrl = "https://raw.githubusercontent.com/bsinwhg/ManifestHub/main/depotkeys.json";
            using (var keysResponse = await _httpClient.GetAsync(keysUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                if (!keysResponse.IsSuccessStatusCode)
                    throw new HttpRequestException($"Failed to download keys: {keysResponse.ReasonPhrase}");

                var totalBytes = keysResponse.Content.Headers.ContentLength ?? 16000000;
                var bytesRead = 0L;
                var buffer = new byte[8192];
                var lastProgressPct = -1;

                using (var input = await keysResponse.Content.ReadAsStreamAsync())
                using (var output = File.OpenWrite(tempKeysPath))
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await output.WriteAsync(buffer, 0, read);
                        bytesRead += read;

                        var progressPct = (int)((double)bytesRead / totalBytes * 100);
                        if (progressPct != lastProgressPct && progressPct % 10 == 0)
                        {
                            onProgress?.Invoke($"Downloading depot keys database... {progressPct}%");
                            lastProgressPct = progressPct;
                        }
                    }
                }
            }

            // 3. Swap temp files to final files
            var finalTokensPath = Path.Combine(AppDataDir, "appaccesstokens.json");
            var finalKeysPath = Path.Combine(AppDataDir, "depotkeys.json");

            if (File.Exists(finalTokensPath)) File.Delete(finalTokensPath);
            if (File.Exists(finalKeysPath)) File.Delete(finalKeysPath);

            File.Move(tempTokensPath, finalTokensPath);
            File.Move(tempKeysPath, finalKeysPath);

            // 4. Reload
            onProgress?.Invoke("Reloading database into memory...");
            await LoadAsync(forceReload: true);

            onProgress?.Invoke($"Database synchronized successfully! ({DepotKeyCount} keys, {AppTokenCount} tokens loaded)");
            return true;
        }
        catch (Exception ex)
        {
            onProgress?.Invoke($"Sync failed: {ex.Message}");
            return false;
        }
    }

    public string? GetAppAccessToken(string appId)
    {
        _appAccessTokens.TryGetValue(appId, out var token);
        return token;
    }

    public string? GetDepotKey(string depotId, string? appIdFallback = null)
    {
        // Check cache first
        if (_cachedKeys.TryGetValue(depotId, out var cached))
            return cached;

        // Then check synced database by depot ID
        if (_depotKeys.TryGetValue(depotId, out var key))
            return key;

        // Fallback: try looking up by the app ID (users often add entries keyed by App ID)
        if (!string.IsNullOrWhiteSpace(appIdFallback) && _depotKeys.TryGetValue(appIdFallback, out var appKey))
            return appKey;

        return null;
    }

    /// <summary>
    /// Multi-source key resolution with live API fallback.
    /// Tries: local cache → synced database → live api.steamcmd.net per-depot lookup.
    /// Successfully fetched live keys are saved to cache for future use.
    /// </summary>
    public async Task<string?> GetDepotKeyWithFallbackAsync(string depotId, SteamApiService steamApi, string? appIdFallback = null, CancellationToken ct = default)
    {
        // Delegate to GetDepotKey for cache + database + appId fallback lookup
        var key = GetDepotKey(depotId, appIdFallback);
        if (key != null)
            return key;

        // Live api.steamcmd.net per-depot lookup
        try
        {
            var detail = await steamApi.GetDepotDetailsAsync(depotId, ct);
            if (detail?.DecryptionKey != null)
            {
                var liveKey = detail.DecryptionKey;
                // Save to cache
                _cachedKeys[depotId] = liveKey;
                SaveCachedKeys();
                return liveKey;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Saves the keys cache to disk.
    /// </summary>
    private void SaveCachedKeys()
    {
        try
        {
            var dir = Path.GetDirectoryName(KeysCachePath);
            if (dir != null)
                Directory.CreateDirectory(dir);

            var json = JsonConvert.SerializeObject(_cachedKeys, Formatting.Indented);
            File.WriteAllText(KeysCachePath, json);
        }
        catch { }
    }

    /// <summary>
    /// Find all depot IDs that could belong to an app. Depot IDs for a game are typically
    /// appId+1, appId+2, etc. We search a reasonable range and also check the depotkeys dict.
    /// </summary>
    public List<DepotInfo> FindDepotsForApp(string appId)
    {
        var depots = new List<DepotInfo>();
        if (!int.TryParse(appId, out var appIdNum)) return depots;

        // Common pattern: depots are appId+1 through appId+30ish, plus exact appId
        for (int offset = 0; offset <= 30; offset++)
        {
            var depotId = (appIdNum + offset).ToString();
            if (_depotKeys.ContainsKey(depotId))
            {
                depots.Add(new DepotInfo
                {
                    DepotId = depotId,
                    DecryptionKey = _depotKeys[depotId]
                });
            }
        }

        // Also check some DLC ranges (typically appId + 100-999)
        for (int offset = 100; offset <= 200; offset++)
        {
            var depotId = (appIdNum + offset).ToString();
            if (_depotKeys.ContainsKey(depotId))
            {
                depots.Add(new DepotInfo
                {
                    DepotId = depotId,
                    DecryptionKey = _depotKeys[depotId]
                });
            }
        }

        return depots;
    }

    public bool HasAppToken(string appId) => _appAccessTokens.ContainsKey(appId);
    public bool HasDepotKey(string depotId) => _depotKeys.ContainsKey(depotId);

    /// <summary>
    /// Get all known app IDs from the access tokens database
    /// </summary>
    public IEnumerable<string> GetAllAppIds() => _appAccessTokens.Keys;
}

