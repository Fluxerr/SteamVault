using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SteamVault.Models;
using System.IO;
using System.Net.Http;

namespace SteamVault.Services;

/// <summary>
/// Multi-source decryption key pipeline.
/// Sources (in priority order):
///   1. Local baked-in Data/depotkeys.json (shipped with app)
///   2. Live API cache (keys_cache.json) — keys found via api.steamcmd.net
///   3. Synced AppData from ManifestHub (GitHub)
///   4. Community repo mirrors (scraped via KeyScraperService)
///   5. Live api.steamcmd.net per-depot lookup (batch parallel)
///   6. Smart derivation (same-app cross-depot, apptoken-as-key, extended pattern matching)
/// </summary>
public class DepotKeyService
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamVault", "Data");

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    private Dictionary<string, string> _appAccessTokens = new();
    private Dictionary<string, string> _depotKeys = new();
    private Dictionary<string, string> _cachedKeys = new();
    private Dictionary<string, string> _scrapedKeys = new(); // keys from KeyScraperService
    private Dictionary<string, string> _scrapedTokens = new();
    private bool _loaded;
    private readonly KeyScraperService _keyScraper = new();

    // Multi-source stats for UI
    public int LocalKeyCount { get; private set; }
    public int AppDataKeyCount { get; private set; }
    public int CachedKeyCount { get; private set; }
    public int ScrapedKeyCount { get; private set; }
    public int LiveApiKeyCount { get; private set; }
    public int DerivedKeyCount { get; private set; }

    private static readonly string KeysCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamVault", "keys_cache.json");

    private static readonly string ScrapedKeysPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamVault", "scraped_keys_cache.json");

    private static readonly string ScrapedTokensPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamVault", "scraped_tokens_cache.json");

    static DepotKeyService()
    {
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "SteamVault/2.5.0");
    }

    public int AppTokenCount => _appAccessTokens.Count;
    public int DepotKeyCount => _depotKeys.Count;
    public int TotalKeyCount => _depotKeys.Count + _cachedKeys.Count + _scrapedKeys.Count;
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
                    AppDataKeyCount = _depotKeys.Count;
                    System.Diagnostics.Debug.WriteLine($"DepotKeyService: Loaded {_depotKeys.Count} depot keys from AppData");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"DepotKeyService: Failed to load AppData depotkeys.json — {ex.Message}");
                }
            }));
        }

        // Load cached keys (persisted across sessions from live API lookups)
        tasks.Add(Task.Run(() =>
        {
            try
            {
                if (File.Exists(KeysCachePath))
                {
                    var json = File.ReadAllText(KeysCachePath);
                    _cachedKeys = JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new();
                    CachedKeyCount = _cachedKeys.Count;
                    System.Diagnostics.Debug.WriteLine($"DepotKeyService: Loaded {_cachedKeys.Count} cached keys");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DepotKeyService: Failed to load keys cache — {ex.Message}");
            }
        }));

        // Load scraped keys (from community mirrors)
        tasks.Add(Task.Run(() =>
        {
            try
            {
                if (File.Exists(ScrapedKeysPath))
                {
                    var json = File.ReadAllText(ScrapedKeysPath);
                    _scrapedKeys = JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new();
                    ScrapedKeyCount = _scrapedKeys.Count;
                    System.Diagnostics.Debug.WriteLine($"DepotKeyService: Loaded {_scrapedKeys.Count} scraped keys");
                }
                if (File.Exists(ScrapedTokensPath))
                {
                    var json = File.ReadAllText(ScrapedTokensPath);
                    _scrapedTokens = JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new();
                    System.Diagnostics.Debug.WriteLine($"DepotKeyService: Loaded {_scrapedTokens.Count} scraped tokens");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DepotKeyService: Failed to load scraped keys — {ex.Message}");
            }
        }));

        await Task.WhenAll(tasks);

        // 2. Overlay the baked-in local database (highest priority)
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
                        _appAccessTokens[kvp.Key] = kvp.Value;
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
                    LocalKeyCount = localDepotKeys.Count;
                    foreach (var kvp in localDepotKeys)
                        _depotKeys[kvp.Key] = kvp.Value;
                    System.Diagnostics.Debug.WriteLine($"DepotKeyService: Overlaid {localDepotKeys.Count} depot keys from local Data/");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DepotKeyService: Failed to load local depotkeys.json — {ex.Message}");
            }
        }

        _loaded = true;
        System.Diagnostics.Debug.WriteLine($"DepotKeyService: Load complete — " +
            $"{DepotKeyCount} synced + {_cachedKeys.Count} cached + {_scrapedKeys.Count} scraped + {LocalKeyCount} local keys, " +
            $"{_appAccessTokens.Count} app tokens");
    }

    /// <summary>
    /// Synchronizes the depot keys and app access tokens database with all known sources.
    /// Primary: ManifestHub GitHub + all community mirrors.
    /// </summary>
    public async Task<bool> SyncDatabaseAsync(Action<string>? onProgress = null)
    {
        var anySuccess = false;

        try
        {
            Directory.CreateDirectory(AppDataDir);

            // 1. Download from primary ManifestHub GitHub
            var primarySuccess = await DownloadPrimarySourceAsync(onProgress);

            // 2. Scrape community mirrors (fills gaps only)
            onProgress?.Invoke("Checking community key mirrors...");
            try
            {
                var (scrapedKeys, scrapedTokens) = await _keyScraper.ScrapeAllSourcesAsync(onProgress);

                // Merge scraped into local cache (don't overwrite primary)
                foreach (var kvp in scrapedKeys)
                {
                    if (!_scrapedKeys.ContainsKey(kvp.Key))
                        _scrapedKeys[kvp.Key] = kvp.Value;
                }
                foreach (var kvp in scrapedTokens)
                {
                    if (!_scrapedTokens.ContainsKey(kvp.Key))
                        _scrapedTokens[kvp.Key] = kvp.Value;
                }

                ScrapedKeyCount = _scrapedKeys.Count;
                SaveScrapedCache();

                var newKeys = scrapedKeys.Count(kvp => !_depotKeys.ContainsKey(kvp.Key));
                onProgress?.Invoke($"Community mirrors: {newKeys} new keys found ({_scrapedKeys.Count} total scraped)");
                anySuccess = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DepotKeyService: Community scrape failed — {ex.Message}");
                onProgress?.Invoke($"Community mirrors unavailable ({ex.Message}) — using cached data.");
            }

            anySuccess = anySuccess || primarySuccess;

            // Reload
            onProgress?.Invoke("Reloading database into memory...");
            await LoadAsync(forceReload: true);

            if (anySuccess)
            {
                var totalUnique = TotalKeyCount;
                onProgress?.Invoke($"Database synchronized successfully! ({totalUnique} unique keys, {AppTokenCount} tokens loaded)");
            }
            else
            {
                onProgress?.Invoke("All sync sources failed — using existing database.");
            }

            return anySuccess;
        }
        catch (Exception ex)
        {
            onProgress?.Invoke($"Sync failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> DownloadPrimarySourceAsync(Action<string>? onProgress)
    {
        try
        {
            var tempTokensPath = Path.Combine(AppDataDir, "appaccesstokens.json.tmp");
            var tempKeysPath = Path.Combine(AppDataDir, "depotkeys.json.tmp");

            // 1. Download App Access Tokens
            onProgress?.Invoke("Downloading app access tokens from GitHub...");
            var tokensUrl = "https://raw.githubusercontent.com/bsinwhg/ManifestHub/main/appaccesstokens.json";
            using (var tokensResponse = await _httpClient.GetAsync(tokensUrl))
            {
                if (!tokensResponse.IsSuccessStatusCode)
                    throw new HttpRequestException($"Failed to download tokens: {tokensResponse.ReasonPhrase}");

                var tokensData = await tokensResponse.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(tempTokensPath, tokensData);
            }

            // 2. Download Depot Keys
            onProgress?.Invoke("Downloading depot keys database from GitHub (approx. 16MB, please wait)...");
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

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DepotKeyService: Primary source download failed — {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Forces a refresh of community scraped keys (useful for "Rescan Keys" button).
    /// </summary>
    public async Task<bool> RefreshScrapedKeysAsync(Action<string>? onProgress = null)
    {
        try
        {
            onProgress?.Invoke("Refreshing community key mirrors...");
            var (scrapedKeys, scrapedTokens) = await _keyScraper.ScrapeAllSourcesAsync(onProgress);

            _scrapedKeys = scrapedKeys;
            _scrapedTokens = scrapedTokens;
            ScrapedKeyCount = _scrapedKeys.Count;

            SaveScrapedCache();

            onProgress?.Invoke($"Community mirrors refreshed: {_scrapedKeys.Count} keys, {_scrapedTokens.Count} tokens.");
            return true;
        }
        catch (Exception ex)
        {
            onProgress?.Invoke($"Refresh failed: {ex.Message}");
            return false;
        }
    }

    public string? GetAppAccessToken(string appId)
    {
        // Check local/synced first
        if (_appAccessTokens.TryGetValue(appId, out var token))
            return token;

        // Check scraped tokens
        if (_scrapedTokens.TryGetValue(appId, out var scrapedToken))
            return scrapedToken;

        return null;
    }

    public string? GetDepotKey(string depotId, string? appIdFallback = null)
    {
        // Priority order: cache > local/synced > scraped > fallbacks

        // 1. Check live API cache
        if (_cachedKeys.TryGetValue(depotId, out var cached))
            return cached;

        // 2. Check main database (synced + local)
        if (_depotKeys.TryGetValue(depotId, out var key))
            return key;

        // 3. Check scraped community mirrors
        if (_scrapedKeys.TryGetValue(depotId, out var scraped))
            return scraped;

        // 4. Fallback: try looking up by the app ID
        if (!string.IsNullOrWhiteSpace(appIdFallback))
        {
            if (_depotKeys.TryGetValue(appIdFallback, out var appKey))
                return appKey;
            if (_scrapedKeys.TryGetValue(appIdFallback, out var appScrapedKey))
                return appScrapedKey;
        }

        return null;
    }

    /// <summary>
    /// Multi-source key resolution with live API fallback and smart derivation.
    /// Tries: local cache → synced database → scraped → live api.steamcmd.net → derivation.
    /// Successfully fetched live keys are saved to cache for future use.
    /// </summary>
    public async Task<string?> GetDepotKeyWithFallbackAsync(string depotId, SteamApiService steamApi, string? appIdFallback = null, CancellationToken ct = default)
    {
        // Delegate to GetDepotKey for all local/synced/scraped sources
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
                CachedKeyCount = _cachedKeys.Count;
                LiveApiKeyCount++;
                SaveCachedKeys();
                return liveKey;
            }
        }
        catch { }

        // Smart derivation: try AppID-based patterns
        var derived = TryDeriveKey(depotId, appIdFallback);
        if (derived != null)
        {
            _cachedKeys[depotId] = derived;
            CachedKeyCount = _cachedKeys.Count;
            DerivedKeyCount++;
            SaveCachedKeys();
            return derived;
        }

        return null;
    }

    /// <summary>
    /// Batch resolve missing depot keys using parallel api.steamcmd.net lookups.
    /// </summary>
    public async Task<int> ResolveMissingKeysBatchAsync(List<DepotInfo> depots, SteamApiService steamApi, string? appIdFallback = null, int maxConcurrency = 8, Action<string>? onProgress = null)
    {
        var missingDepots = depots
            .Where(d => string.IsNullOrWhiteSpace(d.DecryptionKey))
            .ToList();

        if (missingDepots.Count == 0) return 0;

        onProgress?.Invoke($"Looking up {missingDepots.Count} missing depot keys...");
        var resolved = 0;
        var semaphore = new SemaphoreSlim(maxConcurrency);

        var tasks = missingDepots.Select(async depot =>
        {
            await semaphore.WaitAsync();
            try
            {
                var key = await GetDepotKeyWithFallbackAsync(depot.DepotId, steamApi, appIdFallback);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    depot.DecryptionKey = key;
                    Interlocked.Increment(ref resolved);
                }
            }
            catch { }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        if (resolved > 0)
            onProgress?.Invoke($"Resolved {resolved} of {missingDepots.Count} missing keys via live API.");
        else
            onProgress?.Invoke($"No keys found from live API for {missingDepots.Count} missing depots.");

        SaveCachedKeys();
        return resolved;
    }

    /// <summary>
    /// Smart derivation: tries common patterns to derive a decryption key.
    /// - AppAccessToken as key (some games use their app access token)
    /// - Same-app cross-depot (if another depot for this app has a known key, try it)
    /// </summary>
    private string? TryDeriveKey(string depotId, string? appIdFallback)
    {
        // 5a. Try AppAccessToken as the decryption key
        if (!string.IsNullOrWhiteSpace(appIdFallback))
        {
            var appToken = GetAppAccessToken(appIdFallback);
            if (!string.IsNullOrWhiteSpace(appToken) && IsValidKey(appToken))
            {
                System.Diagnostics.Debug.WriteLine($"DepotKeyService: Derived key for depot {depotId} using AppAccessToken of app {appIdFallback}");
                return appToken;
            }

            // 5b. Same-app cross-depot: if any sibling depot has a known key, try it
            if (int.TryParse(appIdFallback, out var appNum))
            {
                var knownSiblingKeys = FindKnownKeysForApp(appIdFallback);
                if (knownSiblingKeys.Count > 0)
                {
                    // If ALL sibling depots share the same key, that's likely the key for this depot too
                    var distinctKeys = knownSiblingKeys.Distinct().ToList();
                    if (distinctKeys.Count == 1)
                    {
                        System.Diagnostics.Debug.WriteLine($"DepotKeyService: All {knownSiblingKeys.Count} known depots for app {appIdFallback} share key — applying to depot {depotId}");
                        return distinctKeys[0];
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Finds all known decryption keys for a given app ID across all sources.
    /// </summary>
    private List<string> FindKnownKeysForApp(string appId)
    {
        var keys = new List<string>();
        if (!int.TryParse(appId, out var appIdNum)) return keys;

        // Search depotkeys and scraped keys for depot IDs that match this app's pattern range
        for (int offset = 0; offset <= 100; offset++)
        {
            var depotId = (appIdNum + offset).ToString();
            if (_depotKeys.TryGetValue(depotId, out var key))
                keys.Add(key);
            else if (_scrapedKeys.TryGetValue(depotId, out var skey))
                keys.Add(skey);
            else if (_cachedKeys.TryGetValue(depotId, out var ckey))
                keys.Add(ckey);
        }

        // DLC ranges
        for (int offset = 100; offset <= 999; offset++)
        {
            var depotId = (appIdNum + offset).ToString();
            if (_depotKeys.TryGetValue(depotId, out var key))
                keys.Add(key);
            else if (_scrapedKeys.TryGetValue(depotId, out var skey))
                keys.Add(skey);
            else if (_cachedKeys.TryGetValue(depotId, out var ckey))
                keys.Add(ckey);
        }

        return keys;
    }

    /// <summary>
    /// Validates that a string looks like a hex decryption key (64 hex chars).
    /// </summary>
    private static bool IsValidKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32) return false;
        return key.All(c => char.IsAsciiHexDigit(c));
    }

    /// <summary>
    /// Saves the live API keys cache to disk.
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
    /// Saves the scraped keys/tokens cache to disk.
    /// </summary>
    private void SaveScrapedCache()
    {
        try
        {
            var dir = Path.GetDirectoryName(ScrapedKeysPath);
            if (dir != null)
                Directory.CreateDirectory(dir);

            File.WriteAllText(ScrapedKeysPath, JsonConvert.SerializeObject(_scrapedKeys, Formatting.Indented));
            File.WriteAllText(ScrapedTokensPath, JsonConvert.SerializeObject(_scrapedTokens, Formatting.Indented));
        }
        catch { }
    }

    /// <summary>
    /// Find all depot IDs that could belong to an app. Searches all sources.
    /// </summary>
    public List<DepotInfo> FindDepotsForApp(string appId)
    {
        var depots = new List<DepotInfo>();
        if (!int.TryParse(appId, out var appIdNum)) return depots;

        // Extended range: appId + 0 through appId + 99 (was 0-30)
        for (int offset = 0; offset <= 99; offset++)
        {
            var depotId = (appIdNum + offset).ToString();
            var key = GetDepotKey(depotId, appId);
            if (key != null)
            {
                depots.Add(new DepotInfo
                {
                    DepotId = depotId,
                    DecryptionKey = key
                });
            }
        }

        // DLC ranges: appId + 100-999 (was 100-200)
        for (int offset = 100; offset <= 999; offset++)
        {
            var depotId = (appIdNum + offset).ToString();
            var key = GetDepotKey(depotId, appId);
            if (key != null)
            {
                depots.Add(new DepotInfo
                {
                    DepotId = depotId,
                    DecryptionKey = key
                });
            }
        }

        return depots;
    }

    public bool HasAppToken(string appId) => _appAccessTokens.ContainsKey(appId) || _scrapedTokens.ContainsKey(appId);
    public bool HasDepotKey(string depotId) => _depotKeys.ContainsKey(depotId) || _scrapedKeys.ContainsKey(depotId) || _cachedKeys.ContainsKey(depotId);

    /// <summary>
    /// Get all known app IDs from the access tokens database (synced + scraped).
    /// </summary>
    public IEnumerable<string> GetAllAppIds() => _appAccessTokens.Keys.Concat(_scrapedTokens.Keys).Distinct();
}