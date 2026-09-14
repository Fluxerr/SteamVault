using Newtonsoft.Json;
using SteamVault.Models;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading.Tasks;

namespace SteamVault.Services;

public class AppUpdaterService
{
    private const string GitHubRepo = "Fluxerr/SteamVault-Updater";
    private readonly HttpClient _httpClient;

    public AppUpdaterService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        // GitHub API requires a User-Agent header
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SteamVault", GetCurrentVersion().ToString()));
    }


    public static Version GetCurrentVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);
    }

    public async Task<GitHubRelease?> CheckForUpdatesAsync()
    {
        try
        {
            var url = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
            var response = await _httpClient.GetStringAsync(url);
            var release = JsonConvert.DeserializeObject<GitHubRelease>(response);

            if (release != null && !string.IsNullOrWhiteSpace(release.TagName))
            {
                var tagStr = release.TagName.TrimStart('v', 'V');
                if (Version.TryParse(tagStr, out var latestVersion))
                {
                    var currentVersion = GetCurrentVersion();
                    if (latestVersion > currentVersion)
                    {
                        return release;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to check for updates: {ex.Message}");
        }

        return null;
    }

    public async Task<string?> DownloadUpdateAsync(GitHubRelease release, Action<double> progressCallback)
    {
        try
        {
            // We assume the release asset is a .zip file containing the updated files
            var asset = release.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (asset == null) return null;

            var tempDir = Path.Combine(Path.GetTempPath(), "SteamVaultUpdate");
            Directory.CreateDirectory(tempDir);
            
            var zipPath = Path.Combine(tempDir, asset.Name);

            using var response = await _httpClient.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            var canReportProgress = totalBytes != -1;

            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var totalRead = 0L;
            var buffer = new byte[81920];
            var isMoreToRead = true;
            var lastReportedPercent = -1.0;
            var lastReportTime = DateTime.MinValue;

            do
            {
                var read = await contentStream.ReadAsync(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    isMoreToRead = false;
                }
                else
                {
                    await fileStream.WriteAsync(buffer, 0, read);

                    totalRead += read;
                    if (canReportProgress)
                    {
                        var percentage = (double)totalRead / totalBytes * 100;
                        var now = DateTime.UtcNow;
                        if (percentage - lastReportedPercent >= 0.5 || (now - lastReportTime).TotalMilliseconds >= 100)
                        {
                            lastReportedPercent = percentage;
                            lastReportTime = now;
                            progressCallback(percentage);
                        }
                    }
                }
            }
            while (isMoreToRead);

            progressCallback(100);
            return zipPath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to download update: {ex.Message}");
            return null;
        }
    }

}
