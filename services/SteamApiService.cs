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

            // Developers
            var devs = data["developers"] as JArray;
            if (devs != null)
                game.Developers = devs.Select(d => d.Value<string>() ?? "").ToList();

            // Publishers
            var pubs = data["publishers"] as JArray;
            if (pubs != null)
                game.Publishers = pubs.Select(p => p.Value<string>() ?? "").ToList();

            // Genres
            var genres = data["genres"] as JArray;
            if (genres != null)
                game.Genres = genres.Select(g => g["description"]?.Value<string>() ?? "").ToList();

            // DLC list
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

    /// <summary>
    /// Fetch basic info for a DLC (just name and image)
    /// </summary>
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
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets depots and their manifest IDs directly from api.steamcmd.net.
    /// Returns a list of DepotInfo objects, or empty if not found/error.
    /// </summary>
    public async Task<List<DepotInfo>> GetDepotsFromSteamCmdAsync(string appId)
    {
        var list = new List<DepotInfo>();
        try
        {
            var url = $"https://api.steamcmd.net/v1/info/{appId}";
            var response = await _http.GetStringAsync(url);
            var json = JObject.Parse(response);

            // Structure: data -> <appId> -> depots
            var appNode = json["data"]?[appId];
            if (appNode == null) return list;

            var depotsNode = appNode["depots"] as JObject;
            if (depotsNode == null) return list;

            foreach (var property in depotsNode.Properties())
            {
                var depotId = property.Name;
                if (!long.TryParse(depotId, out _)) continue;

                var depotData = property.Value as JObject;
                if (depotData == null) continue;

                // 1. Filter out depots that are specifically for other OS's (e.g. macOS, Linux) and not Windows
                var osList = depotData["config"]?["oslist"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(osList))
                {
                    var hasWindows = osList.Contains("windows", StringComparison.OrdinalIgnoreCase);
                    if (!hasWindows) continue; // Skip macOS/Linux-only depots
                }

                // 2. Filter out shared redists or depots from other apps (e.g. Steamworks Common Redists)
                if (depotData["depotfromapp"] != null || depotData["sharedinstall"]?.Value<string>() == "1" || depotData["sharedinstall"]?.Value<int>() == 1)
                {
                    continue; // Skip shared redistributables
                }

                // 2. Parse Manifest ID
                string? manifestId = null;

                // Check "public" branch first
                var publicNode = depotData["manifests"]?["public"];
                if (publicNode != null)
                {
                    if (publicNode is JObject publicObj)
                    {
                        manifestId = publicObj["gid"]?.Value<string>() 
                                  ?? publicObj["value"]?.Value<string>() 
                                  ?? publicObj["id"]?.Value<string>();
                    }
                    else
                    {
                        manifestId = publicNode.Value<string>();
                    }
                }

                // Fallback to search any other branch if public manifest isn't found
                if (string.IsNullOrWhiteSpace(manifestId))
                {
                    var manifestsObj = depotData["manifests"] as JObject;
                    if (manifestsObj != null)
                    {
                        foreach (var mProp in manifestsObj.Properties())
                        {
                            var val = mProp.Value;
                            if (val.Type == JTokenType.String || val.Type == JTokenType.Integer)
                            {
                                manifestId = val.Value<string>();
                                break;
                            }
                            else if (val is JObject valObj)
                            {
                                manifestId = valObj["gid"]?.Value<string>()
                                          ?? valObj["value"]?.Value<string>()
                                          ?? valObj["id"]?.Value<string>();
                                if (!string.IsNullOrWhiteSpace(manifestId)) break;
                            }
                        }
                    }
                }

                list.Add(new DepotInfo
                {
                    DepotId = depotId,
                    ManifestId = manifestId
                });
            }
        }
        catch
        {
            // Fail-silent, fallback to local discovery
        }
        return list;
    }
}
