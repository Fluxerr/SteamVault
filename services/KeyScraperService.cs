using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Net.Http;

namespace SteamVault.Services;

/// <summary>
/// Scrapes additional public decryption key sources from community websites and mirrors.
/// All sources are free and publicly accessible. Keys are cached aggressively (24h).
/// </summary>
public class KeyScraperService
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly string ScraperCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamVault", "scraper_cache");

    static KeyScraperService()
    {
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "SteamVault/2.5.0 (Key Discovery)");
    }

    /// <summary>
    /// All known public community key repository mirrors (GitHub/GitLab raw URLs for depotkeys.json).
    /// These are mirrors and forks of ManifestHub-style databases.
    /// </summary>
    private static readonly List<(string Name, string KeysUrl, string TokensUrl)> CommunityRepos = new()
    {
        // Primary ManifestHub (bsinwhg)
        ("ManifestHub (primary)",
         "https://raw.githubusercontent.com/bsinwhg/ManifestHub/main/depotkeys.json",
         "https://raw.githubusercontent.com/bsinwhg/ManifestHub/main/appaccesstokens.json"),

        // Community mirrors / forks
        ("ManifestHub Mirror (backup)",
         "https://raw.githubusercontent.com/bsinwhg/ManifestHub/refs/heads/main/depotkeys.json",
         "https://raw.githubusercontent.com/bsinwhg/ManifestHub/refs/heads/main/appaccesstokens.json"),

        // Additional known mirrors (community-maintained)
        ("Mirror 2",
         "https://ghproxy.com/https://raw.githubusercontent.com/bsinwhg/ManifestHub/main/depotkeys.json",
         "https://ghproxy.com/https://raw.githubusercontent.com/bsinwhg/ManifestHub/main/appaccesstokens.json"),
    };

    /// <summary>
    /// Scrapes all community key sources and merges results. New keys fill gaps only.
    /// Returns (depotKeys, appTokens) dictionaries or empty if all sources fail.
    /// </summary>
    public async Task<(Dictionary<string, string> depotKeys, Dictionary<string, string> appTokens)>
        ScrapeAllSourcesAsync(Action<string>? onProgress = null)
    {
        var mergedKeys = new Dictionary<string, string>();
        var mergedTokens = new Dictionary<string, string>();

        Directory.CreateDirectory(ScraperCacheDir);

        foreach (var (name, keysUrl, tokensUrl) in CommunityRepos)
        {
            onProgress?.Invoke($"Scraping {name}...");

            // Check cache first
            var cacheKey = Path.Combine(ScraperCacheDir, $"depotkeys_{SanitizeFileName(name)}.json");
            var cacheToken = Path.Combine(ScraperCacheDir, $"tokens_{SanitizeFileName(name)}.json");

            var keysFromSource = await TryLoadFromCacheOrUrl(name, keysUrl, cacheKey, mergedKeys.Count == 0);
            var tokensFromSource = await TryLoadFromCacheOrUrl(name, tokensUrl, cacheToken, mergedTokens.Count == 0, keysFromSource != null);

            if (keysFromSource != null)
            {
                foreach (var kvp in keysFromSource)
                {
                    if (!mergedKeys.ContainsKey(kvp.Key))
                        mergedKeys[kvp.Key] = kvp.Value;
                }
                System.Diagnostics.Debug.WriteLine($"KeyScraper: {name} provided {keysFromSource.Count} keys ({mergedKeys.Count} total merged)");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"KeyScraper: {name} failed or returned no keys.");
            }

            if (tokensFromSource != null)
            {
                foreach (var kvp in tokensFromSource)
                {
                    if (!mergedTokens.ContainsKey(kvp.Key))
                        mergedTokens[kvp.Key] = kvp.Value;
                }
            }
        }

        onProgress?.Invoke($"Scraped {mergedKeys.Count} keys, {mergedTokens.Count} tokens from {CommunityRepos.Count} sources.");
        return (mergedKeys, mergedTokens);
    }

    private async Task<Dictionary<string, string>?> TryLoadFromCacheOrUrl(string sourceName, string url, string cachePath, bool isPrimary, bool sourceHasContent = true)
    {
        // If this is a secondary source and we already have data, skip
        if (!isPrimary && !sourceHasContent && File.Exists(cachePath))
        {
            // Use cached data for secondary sources
            try
            {
                if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath)).TotalHours < 24)
                {
                    var json = await File.ReadAllTextAsync(cachePath);
                    return JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                }
            }
            catch { }
        }

        // Try downloading
        try
        {
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return await TryLoadCacheIfFresh(cachePath);

            var data = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<Dictionary<string, string>>(data);

            // Cache to disk
            if (result != null)
            {
                try
                {
                    await File.WriteAllTextAsync(cachePath, data);
                }
                catch { }
            }

            return result;
        }
        catch
        {
            return await TryLoadCacheIfFresh(cachePath);
        }
    }

    private async Task<Dictionary<string, string>?> TryLoadCacheIfFresh(string cachePath)
    {
        try
        {
            if (File.Exists(cachePath) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath)).TotalHours < 72)
            {
                var json = await File.ReadAllTextAsync(cachePath);
                return JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            }
        }
        catch { }
        return null;
    }

    private static string SanitizeFileName(string name)
    {
        var sanitized = name.Replace(" ", "_")
                           .Replace("(", "")
                           .Replace(")", "")
                           .Replace("/", "_")
                           .Replace("\\", "_");
        return sanitized;
    }
}