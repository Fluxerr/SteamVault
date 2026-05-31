using System.IO;
using System.Text;

namespace SteamVault.Services;

/// <summary>
/// Generates .lua configuration files for OpenSteamTool / SteamTools stplug-in.
/// 
/// Correct format (from OpenSteamTool docs):
///   addappid(APPID)                          -- register the game
///   addtoken(APPID, "ACCESS_TOKEN")          -- app access token (if available)
///   addappid(DEPOTID, 0, "DECRYPTION_KEY")   -- register depot with key
///   setManifestid(DEPOTID, "MANIFEST_GID")   -- pin depot to specific manifest
///
/// OpenSteamTool auto-downloads manifests via steamrun/wudrm APIs,
/// so we do NOT need to separately download .manifest binary files.
/// </summary>
public class LuaGeneratorService
{
    /// <summary>
    /// Generate and save a .lua file for the given app and its depots.
    /// </summary>
    public string GenerateLuaFile(
        string appId,
        string gameName,
        List<DepotLuaEntry> depots,
        string luaOutputPath,
        string? appAccessToken = null)
    {
        Directory.CreateDirectory(luaOutputPath);

        var sb = new StringBuilder();
        sb.AppendLine($"-- {gameName}");
        sb.AppendLine($"-- AppID: {appId}");
        sb.AppendLine($"-- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        // 1. Register the game's AppID
        sb.AppendLine($"addappid({appId})");

        // 2. Add access token if we have one
        if (!string.IsNullOrWhiteSpace(appAccessToken))
        {
            sb.AppendLine($"addtoken({appId}, \"{appAccessToken}\")");
        }

        sb.AppendLine();

        // 3. Register each depot with its decryption key and manifest
        foreach (var depot in depots)
        {
            // Skip if depot ID is the same as app ID (already registered above)
            if (depot.DepotId == appId)
                continue;

            if (!string.IsNullOrWhiteSpace(depot.DecryptionKey))
            {
                sb.AppendLine($"addappid({depot.DepotId}, 0, \"{depot.DecryptionKey}\")");
            }
            else
            {
                sb.AppendLine($"addappid({depot.DepotId})");
            }

            if (!string.IsNullOrWhiteSpace(depot.ManifestId))
            {
                sb.AppendLine($"setManifestid({depot.DepotId}, \"{depot.ManifestId}\")");
            }

            sb.AppendLine();
        }

        // Save with AppID as filename
        var fileName = $"{appId}.lua";
        var filePath = Path.Combine(luaOutputPath, fileName);
        
        // Use UTF8 without BOM, as some Lua parsers (like SteamTools) fail to read files with a Byte Order Mark
        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(false));

        return filePath;
    }
}

public class DepotLuaEntry
{
    public string DepotId { get; set; } = "";
    public string? DecryptionKey { get; set; }
    public string? ManifestId { get; set; }
}
