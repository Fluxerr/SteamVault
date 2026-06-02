using SteamKit2;
using SteamKit2.CDN;
using System.IO;

namespace SteamVault.Services;

/// <summary>
/// Downloads .manifest binary files from Steam's CDN using SteamKit2.
/// These .manifest files go into Steam\config\depotcache and are required
/// by OpenSteamTool (or Steam itself) to know which chunks to request for a depot.
/// 
/// Always fetches the latest manifest for each depot to ensure downloads work
/// with newly released games and updates.
/// 
/// NOTE: OpenSteamTool does NOT require these .manifest files — it fetches manifests
/// on its own through Steam's client connection. This service is best-effort only.
/// </summary>
public class ManifestDownloadService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly SteamClient _steamClient;
    private readonly SteamContent? _steamContent;
    private readonly Client _cdnClient;
    private readonly CallbackManager _callbackManager;
    private bool _connected;
    private TaskCompletionSource<bool>? _connectTcs;
    private volatile bool _isDisposed;

    public ManifestDownloadService(SettingsService settings)
    {
        _settings = settings;
        _steamClient = new SteamClient();
        _callbackManager = new CallbackManager(_steamClient);
        _steamContent = _steamClient.GetHandler<SteamContent>()!;
        _cdnClient = new Client(_steamClient);

        _callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
    }

    /// <summary>
    /// Downloads the latest manifest binary for a depot and saves it to the depotcache directory.
    /// Returns the file path of the saved manifest, or null on failure.
    /// </summary>
    public async Task<string?> DownloadManifestAsync(uint depotId, ulong manifestGid, uint appId, CancellationToken ct = default)
    {
        if (_isDisposed) return null;

        try
        {
            ct.ThrowIfCancellationRequested();

            await ConnectAndLogonAsync(ct);

            var depotcachePath = GetDepotcachePath();
            Directory.CreateDirectory(depotcachePath);

            // Manifest filename format: {depotId}_{manifestGid}.manifest
            var manifestFileName = $"{depotId}_{manifestGid}.manifest";
            var manifestFilePath = Path.Combine(depotcachePath, manifestFileName);

            // Check if we already have this exact manifest
            if (File.Exists(manifestFilePath) && new FileInfo(manifestFilePath).Length > 0)
            {
                System.Diagnostics.Debug.WriteLine($"ManifestDownloadService: Already have {manifestFileName}, skipping download");
                return manifestFilePath;
            }

            // Delete any old manifests for this depot (keep only the latest)
            CleanOldManifests(depotcachePath, depotId);

            System.Diagnostics.Debug.WriteLine($"ManifestDownloadService: Requesting manifest for depot {depotId}, gid {manifestGid}, app {appId}");

            ct.ThrowIfCancellationRequested();

            // Step 1: Get CDN servers for this depot
            var servers = await _steamContent!.GetServersForSteamPipe(cellId: depotId);
            if (servers == null || !servers.Any())
            {
                System.Diagnostics.Debug.WriteLine($"ManifestDownloadService: No CDN servers available for depot {depotId}");
                return null;
            }

            // Pick the best server (lowest weighted load)
            var server = servers.OrderBy(s => s.WeightedLoad).First();

            ct.ThrowIfCancellationRequested();

            // Step 2: Get the manifest request code (depot key)
            var manifestRequestCode = await _steamContent!.GetManifestRequestCode(depotId, appId, manifestGid, "public", "");
            if (manifestRequestCode == 0)
            {
                System.Diagnostics.Debug.WriteLine($"ManifestDownloadService: Failed to get manifest request code for depot {depotId}");
                return null;
            }

            ct.ThrowIfCancellationRequested();

            // Step 3: Get CDN auth token
            byte[]? authToken = null;
            try
            {
                var tokenResult = await _steamContent!.GetCDNAuthToken(appId, depotId, server.Host ?? "");
                if (tokenResult != null && tokenResult.Result == EResult.OK)
                {
                    authToken = tokenResult.Token != null ? System.Text.Encoding.UTF8.GetBytes(tokenResult.Token) : null;
                }
            }
            catch
            {
                // Auth token may not be required for all depots
            }

            ct.ThrowIfCancellationRequested();

            // Step 4: Download the depot manifest (returns DepotManifest object)
            var manifest = await _cdnClient.DownloadManifestAsync(
                depotId,
                manifestGid,
                manifestRequestCode,
                server,
                authToken,
                server,
                null);

            if (manifest != null && manifest.Files != null && manifest.Files.Count > 0)
            {
                // Save the manifest binary to disk using SteamKit2's built-in serialization
                manifest.SaveToFile(manifestFilePath);
                System.Diagnostics.Debug.WriteLine($"ManifestDownloadService: Saved manifest to {manifestFileName} ({manifest.Files.Count} files)");
                return manifestFilePath;
            }

            System.Diagnostics.Debug.WriteLine($"ManifestDownloadService: Empty/null manifest for depot {depotId}");
            return null;
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"ManifestDownloadService: Cancelled downloading manifest for depot {depotId}");
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ManifestDownloadService: Error downloading manifest for depot {depotId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Downloads manifests for all depots in the list. Returns count of successfully downloaded manifests.
    /// Supports cancellation via CancellationToken.
    /// </summary>
    public async Task<int> DownloadAllManifestsAsync(
        List<(uint DepotId, ulong ManifestGid, uint AppId)> depots,
        Action<string>? onStatus = null,
        Action<double>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (_isDisposed) return 0;

        var successCount = 0;
        var total = depots.Count;
        var completed = 0;

        foreach (var (depotId, manifestGid, appId) in depots)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            onStatus?.Invoke($"Downloading manifest for depot {depotId}...");
            var result = await DownloadManifestAsync(depotId, manifestGid, appId, cancellationToken);
            if (result != null) successCount++;

            completed++;
            onProgress?.Invoke((double)completed / total * 100);
        }

        return successCount;
    }

    private async Task ConnectAndLogonAsync(CancellationToken ct = default)
    {
        if (_connected || _isDisposed) return;

        ct.ThrowIfCancellationRequested();

        _connectTcs = new TaskCompletionSource<bool>();

        _steamClient.Connect();

        // Wait for connection with 15 second timeout (or cancellation)
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(15_000);
        var timeout = Task.Delay(Timeout.Infinite, timeoutCts.Token);

        try
        {
            var completedTask = await Task.WhenAny(_connectTcs.Task, timeout);

            if (completedTask == timeout)
            {
                System.Diagnostics.Debug.WriteLine("ManifestDownloadService: Timed out connecting to Steam CDN");
                return;
            }
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("ManifestDownloadService: Connection cancelled");
            return;
        }

        if (!_connected)
        {
            System.Diagnostics.Debug.WriteLine("ManifestDownloadService: Failed to connect to Steam CDN");
            return;
        }

        // Give CDN auth a moment to initialize
        await Task.Delay(1000, ct);
    }

    private string GetDepotcachePath()
    {
        var steamDir = _settings.Settings.SteamDirectory;
        if (!string.IsNullOrWhiteSpace(steamDir) && Directory.Exists(steamDir))
            return Path.Combine(steamDir, "config", "depotcache");

        // Fallback to AppData
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SteamVault", "depotcache");
    }

    private static void CleanOldManifests(string depotcachePath, uint depotId)
    {
        try
        {
            var pattern = $"{depotId}_*.manifest";
            foreach (var oldFile in Directory.GetFiles(depotcachePath, pattern))
            {
                try { File.Delete(oldFile); }
                catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
    }

    private void OnConnected(SteamClient.ConnectedCallback callback)
    {
        _connected = true;
        _connectTcs?.TrySetResult(true);
        System.Diagnostics.Debug.WriteLine("ManifestDownloadService: Connected to Steam");
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        _connected = false;
        _connectTcs?.TrySetResult(false);
        System.Diagnostics.Debug.WriteLine("ManifestDownloadService: Disconnected from Steam");
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            if (_connected)
                _steamClient.Disconnect();
        }
        catch { }
        _cdnClient?.Dispose();
    }
}