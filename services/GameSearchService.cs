using Newtonsoft.Json.Linq;
using System.Net.Http;

namespace SteamVault.Services;

/// <summary>
/// Provides fuzzy search for Steam games by name or App ID.
/// Uses the Steam Store search API for name-based searches,
/// and local depot key database for ID-based validation.
/// Implements Levenshtein distance for typo-tolerant matching.
/// </summary>
public class GameSearchService
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    static GameSearchService()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "SteamVault/1.0");
    }

    /// <summary>
    /// Searches for Steam games by name or App ID.
    /// Returns a list of search results with basic info.
    /// </summary>
    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var results = new List<SearchResult>();
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2) return results;

        query = query.Trim();

        // If the query is purely numeric, treat it as an App ID lookup
        if (int.TryParse(query, out _))
        {
            var appResult = await LookupByAppIdAsync(query, ct);
            if (appResult != null)
                results.Add(appResult);
            return results;
        }

        // Otherwise, search by name via Steam Store search API
        try
        {
            var encoded = Uri.EscapeDataString(query);
            var url = $"https://store.steampowered.com/api/storesearch/?term={encoded}&l=english&cc=US";
            var response = await _http.GetStringAsync(url, ct);
            var json = JObject.Parse(response);

            var items = json["items"] as JArray;
            if (items == null) return results;

            foreach (var item in items.Take(8)) // Limit to 8 results
            {
                var name = item["name"]?.Value<string>() ?? "";
                var appId = item["id"]?.Value<int>().ToString() ?? "";
                var imageUrl = item["tiny_image"]?.Value<string>() ?? "";

                // Calculate fuzzy match score
                var score = CalculateFuzzyScore(query, name);

                results.Add(new SearchResult
                {
                    AppId = appId,
                    Name = name,
                    ImageUrl = imageUrl,
                    MatchScore = score
                });
            }

            // Sort by match quality
            results = results.OrderByDescending(r => r.MatchScore).ToList();
        }
        catch (OperationCanceledException)
        {
            // Cancelled — expected during rapid typing
        }
        catch
        {
            // Search failed silently
        }

        return results;
    }

    private async Task<SearchResult?> LookupByAppIdAsync(string appId, CancellationToken ct)
    {
        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}";
            var response = await _http.GetStringAsync(url, ct);
            var json = JObject.Parse(response);

            var appData = json[appId];
            if (appData?["success"]?.Value<bool>() != true) return null;

            var data = appData["data"];
            return new SearchResult
            {
                AppId = appId,
                Name = data?["name"]?.Value<string>() ?? "Unknown",
                ImageUrl = data?["header_image"]?.Value<string>() ?? "",
                MatchScore = 100
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Calculates a fuzzy match score (0-100) between query and target string.
    /// Uses a combination of substring matching, prefix matching, and normalized Levenshtein distance.
    /// </summary>
    public static double CalculateFuzzyScore(string query, string target)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(target)) return 0;

        var q = query.ToLowerInvariant();
        var t = target.ToLowerInvariant();

        // Exact match
        if (q == t) return 100;

        // Starts with query
        if (t.StartsWith(q)) return 95;

        // Contains query as substring
        if (t.Contains(q)) return 85;

        // Word-level matching (any word starts with query)
        var words = t.Split(' ', '-', '_', ':', '.');
        foreach (var word in words)
        {
            if (word.StartsWith(q)) return 80;
        }

        // Normalized Levenshtein distance on the most relevant substring
        var distance = LevenshteinDistance(q, t.Length >= q.Length ? t.Substring(0, Math.Min(q.Length + 2, t.Length)) : t);
        var maxLen = Math.Max(q.Length, t.Length);
        var similarity = 1.0 - (double)distance / maxLen;

        return similarity * 70; // Scale to 0-70 range for fuzzy matches
    }

    /// <summary>
    /// Computes the Levenshtein edit distance between two strings.
    /// </summary>
    private static int LevenshteinDistance(string s, string t)
    {
        var n = s.Length;
        var m = t.Length;
        var d = new int[n + 1, m + 1];

        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }
}

public class SearchResult
{
    public string AppId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public double MatchScore { get; set; }
}
