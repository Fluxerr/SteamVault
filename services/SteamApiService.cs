using System.Net.Http;
using Newtonsoft.Json.Linq;
using SteamVault.Models;

namespace SteamVault.Services;

/// <summary>
/// Fetches game metadata from the Steam Store API.
/// </summary>
public class SteamApiService
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    static SteamApiService()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "SteamVault/1.0");
    }

    public async Task<GameInfo?> GetAppDetailsAsync(string appId)
    {
        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}";
            var response = await _http.GetStringAsync(url);
            var json = JObject.Parse(response);

            var appData = json[appId];
            if (appData == null || appData["success"]?.Value<bool>() != true)
                return null;

            var data = appData["data"];
            if (data == null) return null;

            var game = new GameInfo
            {
                AppId = appId,
                Name = data["name"]?.Value<string>() ?? "Unknown",
                HeaderImageUrl = data["header_image"]?.Value<string>() ?? "",
                ShortDescription = data["short_description"]?.Value<string>() ?? "",
                Type = data["type"]?.Value<string>() ?? "game",
                IsFree = data["is_free"]?.Value<bool>() ?? false,
                ReleaseDate = data["release_date"]?["date"]?.Value<string>() ?? "",
            };

            var devs = data["developers"] as JArray;
            if (devs != null)
                game.Developers = devs.Select(d => d.Value<string>() ?? "").ToList();

            var pubs = data["publishers"] as JArray;
            if (pubs != null)
                game.Publishers = pubs.Select(p => p.Value<string>() ?? "").ToList();

            var genres = data["genres"] as JArray;
            if (genres != null)
                game.Genres = genres.Select(g => g["description"]?.Value<string>() ?? "").ToList();

            var categories = data["categories"] as JArray;
            if (categories != null)
            {
                foreach (var cat in categories)
                {
                    var desc = cat["description"]?.Value<string>() ?? "";
                    if (!string.IsNullOrWhiteSpace(desc))
                        game.Categories.Add(desc);
                }
            }

            var dlc = data["dlc"] as JArray;
            if (dlc != null)
            {
                foreach (var dlcId in dlc)
                {
                    var id = dlcId.Value<string>() ?? dlcId.Value<int>().ToString();
                    game.Dlc.Add(new DlcInfo { AppId = id });
                }
            }

            return game;
        }
        catch
        {
            return null;
        }
    }

    public async Task<DlcInfo?> GetDlcInfoAsync(string appId)
    {
        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}";
            var response = await _http.GetStringAsync(url);
            var json = JObject.Parse(response);
            var appData = json[appId];
            if (appData?["success"]?.Value<bool>() != true) return null;
            var data = appData["data"];
            return new DlcInfo
            {
                AppId = appId,
                Name = data?["name"]?.Value<string>() ?? "Unknown DLC",
                HeaderImageUrl = data?["header_image"]?.Value<string>() ?? ""
            };
        }
        catch { return null; }
    }

    public async Task<SteamDbStats?> GetSteamDbStatsAsync(string appId)
    {
        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&cc=US&l=english";
            var response = await _http.GetStringAsync(url);
            var json = JObject.Parse(response);
            var appData = json[appId];
            if (appData?["success"]?.Value<bool>() != true) return null;
            var data = appData["data"];
            if (data == null) return null;
            var stats = new SteamDbStats();
            stats.MetacriticScore = data["metacritic"]?["score"]?.Value<int>() ?? 0;
            stats.TotalReviews = data["recommendations"]?["total"]?.Value<int>() ?? 0;
            var positive = data["positive"]?.Value<int>();
            var negative = data["negative"]?.Value<int>();
            if (positive != null && negative != null && positive.Value + negative.Value > 0)
                stats.PositiveReviewPercent = Math.Round((double)positive.Value / (positive.Value + negative.Value) * 100, 1);
            try
            {
                var playerUrl = $"https://api.steampowered.com/ISteamUserStats/GetNumberOfCurrentPlayers/v1/?appid={appId}";
                var playerJson = JObject.Parse(await _http.GetStringAsync(playerUrl));
                stats.CurrentPlayers = playerJson["response"]?["player_count"]?.Value<int>() ?? 0;
            }
            catch { }
            return stats;
        }
        catch { return null; }
    }

    public async Task<DepotDetail?> GetDepotDetailsAsync(string depotId, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.steamcmd.net/v1/depot/{depotId}";
            var response = await _http.GetStringAsync(url, ct);
            var json = JObject.Parse(response);
            var depotData = json["data"]?[depotId] as JObject;
            if (depotData == null) return null;
            var detail = new DepotDetail { DepotId = depotId };
            detail.DecryptionKey = depotData["DecryptionKey"]?.Value<string>()
                                ?? depotData["decryptionkey"]?.Value<string>()
                                ?? depotData["Key"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(detail.DecryptionKey))
            {
                var publicManifest = depotData["manifests"]?["public"];
                if (publicManifest != null)
                    detail.DecryptionKey = publicManifest["DecryptionKey"]?.Value<string>()
                                         ?? publicManifest["decryptionkey"]?.Value<string>();
            }
            return detail;
        }
        catch { return null; }
    }

    /// <summary>
    /// Fetches the FULL DLC list from the Steam store's DLC page AJAX endpoint.
    /// Unlike appdetails which truncates DLC lists, this returns all store-listed DLCs.
    /// Note: Some games (like 007 First Light) may have DLCs on SteamDB that aren't in the store,
    /// so we combine this with the appdetails DLC list in DownloadService.
    /// </summary>
    public async Task<List<string>> GetFullDlcAppIdsAsync(string appId)
    {
        var dlcIds = new List<string>();
        try
        {
            var url = $"https://store.steampowered.com/dlc/{appId}/ajaxgetdlclist/?l=english";
            var response = await _http.GetStringAsync(url);
            var json = JObject.Parse(response);

            if (json["success"]?.Value<int>() == 1)
            {
                var dlcArray = json["data"] as JArray;
                if (dlcArray != null && dlcArray.Count > 0)
                {
                    foreach (var dlc in dlcArray)
                    {
                        var id = dlc["appid"]?.Value<string>() ?? dlc["appid"]?.Value<long>().ToString();
                        if (!string.IsNullOrWhiteSpace(id))
                            dlcIds.Add(id);
                    }
                    System.Diagnostics.Debug.WriteLine($"SteamApiService: Got {dlcIds.Count} DLCs from store DLC AJAX API");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SteamApiService: Store DLC AJAX failed: {ex.Message}");
        }
        return dlcIds;
    }

    public async Task<List<DepotInfo>> GetDlcDepotsAsync(string dlcAppId)
    {
        return await GetDepotsFromSteamCmdAsync(dlcAppId);
    }

    /// <summary>
    /// Gets depots and their manifest IDs from api.steamcmd.net.
    /// </summary>
    public async Task<List<DepotInfo>> GetDepotsFromSteamCmdAsync(string appId)
    {
        var list = new List<DepotInfo>();
        try
        {
            var url = $"https://api.steamcmd.net/v1/info/{appId}";
            var response = await _http.GetStringAsync(url);
            var json = JObject.Parse(response);

            var appNode = json["data"]?[appId];
            if (appNode == null) return list;

            var listOfDlc = appNode["listofdlc"] as JArray;
            var dlcAppIds = new HashSet<string>();
            if (listOfDlc != null)
            {
                foreach (var dlcId in listOfDlc)
                {
                    var id = dlcId.Value<string>() ?? dlcId.Value<long>().ToString();
                    if (!string.IsNullOrWhiteSpace(id)) dlcAppIds.Add(id);
                }
            }

            var depotsNode = appNode["depots"] as JObject;
            if (depotsNode == null) return list;

            foreach (var property in depotsNode.Properties())
            {
                var depotId = property.Name;
                if (!long.TryParse(depotId, out _)) continue;

                var depotData = property.Value as JObject;
                if (depotData == null) continue;

                var osList = depotData["config"]?["oslist"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(osList) && !osList.Contains("windows", StringComparison.OrdinalIgnoreCase))
                    continue;

                var maxSize = depotData["maxsize"]?.Value<long>() ?? depotData["config"]?["maxsize"]?.Value<long>() ?? 0;
                var isSharedInstall = depotData["sharedinstall"]?.Value<int>() == 1;
                if (isSharedInstall) continue;

                string? dlcAppId = null;
                var dlcappidToken = depotData["dlcappid"];
                if (dlcappidToken != null)
                {
                    dlcAppId = dlcappidToken.Value<string>() ?? dlcappidToken.Value<long>().ToString();
                    if (!string.IsNullOrWhiteSpace(dlcAppId)) dlcAppIds.Add(dlcAppId);
                }

                string? manifestId = null;
                var publicNode = depotData["manifests"]?["public"];
                if (publicNode != null)
                {
                    manifestId = (publicNode as JObject)?["gid"]?.Value<string>()
                              ?? (publicNode as JObject)?["value"]?.Value<string>()
                              ?? publicNode.Value<string>();
                }
                if (string.IsNullOrWhiteSpace(manifestId))
                {
                    var manifestsObj = depotData["manifests"] as JObject;
                    if (manifestsObj != null)
                    {
                        foreach (var mProp in manifestsObj.Properties())
                        {
                            var val = mProp.Value;
                            if (val.Type == JTokenType.String || val.Type == JTokenType.Integer)
                            { manifestId = val.Value<string>(); break; }
                            else if (val is JObject valObj)
                            {
                                manifestId = valObj["gid"]?.Value<string>() ?? valObj["value"]?.Value<string>();
                                if (!string.IsNullOrWhiteSpace(manifestId)) break;
                            }
                        }
                    }
                }

                list.Add(new DepotInfo
                {
                    DepotId = depotId,
                    ManifestId = manifestId,
                    SizeBytes = maxSize,
                    DlcAppId = dlcAppId
                });
            }

            _lastDlcAppIds = dlcAppIds.ToList();
        }
        catch { }
        return list;
    }

    private List<string> _lastDlcAppIds = new();
    public List<string> LastDlcAppIds => _lastDlcAppIds;
}