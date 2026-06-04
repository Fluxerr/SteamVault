using SteamVault.Models;
using System.IO;

namespace SteamVault.Services;

public class DownloadService
{
    private readonly SteamApiService _steamApi;
    private readonly DepotKeyService _depotKeyService;
    private readonly LuaGeneratorService _luaGenerator;
    private readonly ManifestDownloadService _manifestDownloader;
    private readonly SettingsService _settings;

    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SteamVault", "steamvault.log");

    public DownloadService(
        SteamApiService steamApi,
        DepotKeyService depotKeyService,
        LuaGeneratorService luaGenerator,
        ManifestDownloadService manifestDownloader,
        SettingsService settings)
    {
        _steamApi = steamApi;
        _depotKeyService = depotKeyService;
        _luaGenerator = luaGenerator;
        _manifestDownloader = manifestDownloader;
        _settings = settings;
    }

    private static void LogLine(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogFilePath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.AppendAllText(LogFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { }
    }

    public async Task<DownloadResult> DownloadGameAsync(string appId, Action<string>? onStatus = null, Action<double>? onProgress = null, bool includeDlcs = false)
    {
        var result = new DownloadResult { AppId = appId };

        try
        {
            var luaOutputPath = _settings.Settings.LuaOutputPath;
            if (string.IsNullOrWhiteSpace(luaOutputPath))
            {
                result.Error = "Lua output path is not configured. Check Settings.";
                return result;
            }

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

            onStatus?.Invoke("Querying SteamCMD for depot and manifest data...");
            var depots = await _steamApi.GetDepotsFromSteamCmdAsync(appId);

            if (depots.Count == 0)
            {
                onStatus?.Invoke("SteamCMD returned no depots, checking local database...");
                if (_depotKeyService.IsLoaded)
                    depots = _depotKeyService.FindDepotsForApp(appId);
            }

            if (depots.Count == 0)
            {
                result.Error = $"No depots found for {game.Name} (App ID: {appId}). This game may not have public depot data.";
                return result;
            }

            onProgress?.Invoke(50);
            onStatus?.Invoke($"Found {depots.Count} depot(s) — Loading decryption keys...");

            if (!_depotKeyService.IsLoaded)
                await _depotKeyService.LoadAsync();

            // Use batch parallel key resolution for better coverage
            _ = await _depotKeyService.ResolveMissingKeysBatchAsync(depots, _steamApi, appIdFallback: appId, onProgress: msg => onStatus?.Invoke(msg));

            var keysAttached = depots.Count(d => !string.IsNullOrWhiteSpace(d.DecryptionKey));
            var manifestsResolved = depots.Count(d => !string.IsNullOrWhiteSpace(d.ManifestId));
            var missingKeyDepotIds = depots.Where(d => string.IsNullOrWhiteSpace(d.DecryptionKey) && !string.IsNullOrWhiteSpace(d.ManifestId)).Select(d => d.DepotId).ToList();

            game.AppAccessToken = _depotKeyService.GetAppAccessToken(appId);
            onProgress?.Invoke(70);

            var usableDepots = depots.Where(d => !string.IsNullOrWhiteSpace(d.ManifestId) && !d.IsDlcDepot).ToList();

            if (usableDepots.Count == 0)
            {
                result.Error = $"Found {depots.Count} depot(s) but none have manifest IDs resolved.";
                return result;
            }

            if (missingKeyDepotIds.Count > 0 && keysAttached == 0)
            {
                result.Error = $"Found {depots.Count} depot(s) with {manifestsResolved} manifests, but NO decryption keys available.";
                return result;
            }

            // Step 4: DLC depots
            var dlcEntries = new List<DlcLuaEntry>();
            var dlcProcessed = 0;
            var totalDlcs = 0;

            if (includeDlcs)
            {
                var allDlcAppIds = new HashSet<string>();
                var fromListOfDlc = 0;
                var fromStoreApi = 0;
                var fromDlcAppId = 0;
                var fromFullList = 0;

                foreach (var id in _steamApi.LastDlcAppIds)
                { allDlcAppIds.Add(id); fromListOfDlc++; }
                if (game.Dlc != null)
                { foreach (var dlc in game.Dlc) { allDlcAppIds.Add(dlc.AppId); fromStoreApi++; } }
                foreach (var depot in depots)
                { if (!string.IsNullOrWhiteSpace(depot.DlcAppId)) { allDlcAppIds.Add(depot.DlcAppId); fromDlcAppId++; } }

                List<string> fullDlcList;
                try { fullDlcList = await _steamApi.GetFullDlcAppIdsAsync(appId); }
                catch { fullDlcList = new List<string>(); }
                foreach (var id in fullDlcList) { allDlcAppIds.Add(id); fromFullList++; }

                var dlcDepotsInParent = depots.Where(d => d.IsDlcDepot).ToList();

                LogLine("=== DLC Discovery ===");
                LogLine($"  Sources: steamcmd listofdlc={fromListOfDlc}, Store API appdetails={fromStoreApi}, depot dlcappid={fromDlcAppId}, full DLC list API={fromFullList}, dlc-tagged depots={dlcDepotsInParent.Count}");
                LogLine($"  Total DLC App IDs collected: {allDlcAppIds.Count}");
                if (allDlcAppIds.Count > 0)
                    LogLine($"  DLC App IDs ({allDlcAppIds.Count}): {string.Join(", ", allDlcAppIds)}");

                totalDlcs = allDlcAppIds.Count;

                if (totalDlcs > 0)
                {
                    LogLine($"  Fetching depot data for {totalDlcs} DLC(s)...");
                    onProgress?.Invoke(72);
                    var dlcProcessedTotal = 0;

                    foreach (var dlcAppId in allDlcAppIds)
                    {
                        try
                        {
                            string dlcName = dlcAppId;
                            try { var info = await _steamApi.GetDlcInfoAsync(dlcAppId); if (info != null) dlcName = info.Name; } catch { }

                            LogLine($"  Processing: {dlcName} (AppID {dlcAppId})");

                            var existingDlcDepots = depots
                                .Where(d => d.DlcAppId == dlcAppId && !string.IsNullOrWhiteSpace(d.ManifestId))
                                .ToList();
                            LogLine($"    Source A (parent depots): {existingDlcDepots.Count}");

                            List<DepotInfo> dlcDepotsToUse;
                            if (existingDlcDepots.Count > 0)
                            {
                                dlcDepotsToUse = existingDlcDepots;
                            }
                            else
                            {
                                List<DepotInfo> fetched;
                                try { fetched = await _steamApi.GetDepotsFromSteamCmdAsync(dlcAppId); }
                                catch { fetched = new List<DepotInfo>(); }
                                dlcDepotsToUse = fetched.Where(d => !string.IsNullOrWhiteSpace(d.ManifestId)).ToList();
                                LogLine($"    Source B (DLC own steamcmd): {fetched.Count} total, {dlcDepotsToUse.Count} with manifest");
                            }

                            // Build depot list (may be empty for entitlement-only DLCs)
                            var usableDepotEntries = new List<DepotLuaEntry>();

                            if (dlcDepotsToUse.Count > 0)
                            {
                                var keysFound = 0;
                                foreach (var depot in dlcDepotsToUse)
                                {
                                    var key = await _depotKeyService.GetDepotKeyWithFallbackAsync(depot.DepotId, _steamApi, appIdFallback: dlcAppId);
                                    if (!string.IsNullOrWhiteSpace(key)) { depot.DecryptionKey = key; keysFound++; }
                                }
                                LogLine($"    Keys: {keysFound}/{dlcDepotsToUse.Count}");

                                usableDepotEntries = dlcDepotsToUse
                                    .Where(d => !string.IsNullOrWhiteSpace(d.ManifestId) || !string.IsNullOrWhiteSpace(d.DecryptionKey))
                                    .Select(d => new DepotLuaEntry { DepotId = d.DepotId, DecryptionKey = d.DecryptionKey, ManifestId = d.ManifestId })
                                    .ToList();
                            }

                            // ALWAYS add the DLC — entitlement/unlock DLCs with no depots still need addappid()
                            dlcEntries.Add(new DlcLuaEntry { AppId = dlcAppId, Name = dlcName, Depots = usableDepotEntries });
                            dlcProcessedTotal++;

                            if (usableDepotEntries.Count > 0)
                                LogLine($"    ✓ Added with {usableDepotEntries.Count} depot(s)");
                            else
                                LogLine($"    ✓ Added (entitlement-only, no depots) — registered via addappid({dlcAppId})");
                        }
                        catch (Exception ex)
                        {
                            LogLine($"    ✗ Failed: {ex.Message}");
                        }
                    }

                    dlcProcessed = dlcProcessedTotal;
                    LogLine($"  Result: {dlcProcessed}/{totalDlcs} DLC(s) added to Lua");
                }
                else
                {
                    LogLine("  No DLC App IDs found from any source.");
                }
            }

            // Step 5: Generate Lua
            onStatus?.Invoke("Generating Lua configuration...");
            onProgress?.Invoke(85);

            var luaEntries = usableDepots.Select(d => new DepotLuaEntry { DepotId = d.DepotId, DecryptionKey = d.DecryptionKey, ManifestId = d.ManifestId }).ToList();
            LogLine($"Generating Lua — base depots: {luaEntries.Count}, DLC entries: {dlcEntries.Count}, AppTicket: {(game.AppTicket != null ? "yes" : "no")}, ETicket: {(game.ETicket != null ? "yes" : "no")}");
            var luaFilePath = _luaGenerator.GenerateLuaFile(appId, game.Name, luaEntries, luaOutputPath, game.AppAccessToken, dlcEntries.Count > 0 ? dlcEntries : null, game.AppTicket, game.ETicket);
            onProgress?.Invoke(90);

            // Step 6: Manifest download (best-effort)
            var manifestsDownloaded = 0;
            var depotEntriesForManifest = new List<(uint DepotId, ulong ManifestGid, uint AppId)>();
            foreach (var depot in usableDepots)
            {
                if (!string.IsNullOrWhiteSpace(depot.ManifestId) && uint.TryParse(depot.DepotId, out var depId) && ulong.TryParse(depot.ManifestId, out var gid) && uint.TryParse(appId, out var aid))
                    depotEntriesForManifest.Add((depId, gid, aid));
            }
            if (depotEntriesForManifest.Count > 0)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    manifestsDownloaded = await _manifestDownloader.DownloadAllManifestsAsync(depotEntriesForManifest, cancellationToken: cts.Token);
                }
                catch (OperationCanceledException)
                { LogLine("Manifest download timed out."); }
                catch (Exception ex)
                { LogLine($"Manifest download skipped: {ex.Message}"); }
            }

            onProgress?.Invoke(100);
            result.Success = true;
            result.LuaFilePath = luaFilePath;
            result.DepotCount = usableDepots.Count;
            result.KeysAttached = keysAttached;
            result.ManifestsResolved = manifestsResolved;
            result.ManifestsDownloaded = manifestsDownloaded;
            result.DlcCount = dlcProcessed;
            result.TotalDlcs = totalDlcs;
            result.MissingKeyDepotIds = missingKeyDepotIds;
            result.MissingManifestDepotIds = depots.Where(d => string.IsNullOrWhiteSpace(d.ManifestId)).Select(d => d.DepotId).ToList();
            result.Game = game;
            result.Depots = depots;

            var parts = new List<string> { $"Lua: {usableDepots.Count} depot(s)" };
            if (manifestsDownloaded > 0) parts.Add($"{manifestsDownloaded} manifest(s)");
            if (missingKeyDepotIds.Count > 0) parts.Add($"{missingKeyDepotIds.Count} missing keys");
            if (result.MissingManifestDepotIds.Count > 0) parts.Add($"{result.MissingManifestDepotIds.Count} missing manifests");
            if (dlcProcessed > 0) parts.Add($"{dlcProcessed} DLC(s)");
            onStatus?.Invoke($"Done! {string.Join(", ", parts)} — {Path.GetFileName(luaFilePath)}");

            _settings.Settings.DownloadHistory.Add(new DownloadHistoryEntry
            {
                AppId = appId, GameName = game.Name, HeaderImageUrl = game.HeaderImageUrl,
                DownloadDate = DateTime.Now, Status = "Completed",
                DepotIds = usableDepots.Select(d => d.DepotId).ToList()
            });
            _settings.Save();
        }
        catch (Exception ex)
        { result.Error = $"Unexpected error: {ex.Message}"; }

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
    public int ManifestsDownloaded { get; set; }
    public int DlcCount { get; set; }
    public int TotalDlcs { get; set; }
    public List<string> MissingKeyDepotIds { get; set; } = new();
    public List<string> MissingManifestDepotIds { get; set; } = new();
    public GameInfo? Game { get; set; }
    public List<DepotInfo>? Depots { get; set; }
}