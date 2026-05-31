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
    private bool _loaded;

    static DepotKeyService()
    {
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "SteamVault/1.0");
    }

    public int AppTokenCount => _appAccessTokens.Count;
    public int DepotKeyCount => _depotKeys.Count;
    public bool IsLoaded => _loaded;

    private (string tokensPath, string keysPath) GetPaths()
    {
        // 1. Check AppData first
        var appDataTokens = Path.Combine(AppDataDir, "appaccesstokens.json");
        var appDataKeys = Path.Combine(AppDataDir, "depotkeys.json");

        if (File.Exists(appDataTokens) && File.Exists(appDataKeys))
        {
            return (appDataTokens, appDataKeys);
        }

        // 2. Fallback to application folder
        var basePath = AppDomain.CurrentDomain.BaseDirectory;
        var localTokens = Path.Combine(basePath, "Data", "appaccesstokens.json");
        var localKeys = Path.Combine(basePath, "Data", "depotkeys.json");

        return (localTokens, localKeys);
    }

    public async Task LoadAsync(bool forceReload = false)
    {
        if (_loaded && !forceReload) return;

        var (tokensPath, keysPath) = GetPaths();
        var tasks = new List<Task>();

        if (File.Exists(tokensPath))
        {
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    var json = File.ReadAllText(tokensPath);
                    _appAccessTokens = JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new();
                }
                catch { }
            }));
        }

        if (File.Exists(keysPath))
        {
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    var json = File.ReadAllText(keysPath);
                    _depotKeys = JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new();
                }
                catch { }
            }));
        }

        await Task.WhenAll(tasks);
        _loaded = true;
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
            var tokensUrl = "https://raw.githubusercontent.com/SteamAutoCracks/ManifestHub/main/appaccesstokens.json";
            using (var tokensResponse = await _httpClient.GetAsync(tokensUrl))
            {
                if (!tokensResponse.IsSuccessStatusCode)
                    throw new HttpRequestException($"Failed to download tokens: {tokensResponse.ReasonPhrase}");

                var tokensData = await tokensResponse.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(tempTokensPath, tokensData);
            }

            // 2. Download Depot Keys
            onProgress?.Invoke("Downloading depot keys database (approx. 16MB, please wait)...");
            var keysUrl = "https://raw.githubusercontent.com/SteamAutoCracks/ManifestHub/main/depotkeys.json";
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

    public string? GetDepotKey(string depotId)
    {
        _depotKeys.TryGetValue(depotId, out var key);
        return key;
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

