using Newtonsoft.Json;
using System.Collections.Generic;

namespace SteamVault.Models;

public class GitHubRelease
{
    [JsonProperty("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("body")]
    public string Body { get; set; } = string.Empty; // Release notes

    [JsonProperty("assets")]
    public List<GitHubAsset> Assets { get; set; } = new();
}

public class GitHubAsset
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;
}
