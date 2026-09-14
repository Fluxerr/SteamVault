using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using SteamVault.Models;

namespace SteamVault.Services;

public class ApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

public record DownloadedFile(string FilePath, string FileName);

public class LuaToolsApiClient(AuthService auth)
{
    private static readonly string InterimDownloadsFolder =
        Path.Combine(Path.GetTempPath(), "SteamVault", "downloads");

    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri(AppConfig.ApiBaseUrl),
        Timeout = TimeSpan.FromMinutes(5),
    };

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task<DenuvoListingsResponse?> GetDenuvoListingsAsync(CancellationToken ct = default)
    {
        var res = await _http.GetAsync("/api/denuvo/listings", ct);
        if (!res.IsSuccessStatusCode) return null;
        return await ReadJsonAsync<DenuvoListingsResponse>(res, ct);
    }

    public async Task<DenuvoFixesResponse?> GetDenuvoFixesAsync(string appid, CancellationToken ct = default)
    {
        var res = await _http.GetAsync($"/api/denuvo/fixes?appid={Uri.EscapeDataString(appid)}", ct);
        if (!res.IsSuccessStatusCode) return null;
        return await ReadJsonAsync<DenuvoFixesResponse>(res, ct);
    }

    public async Task<DownloadedFile> DownloadDenuvoAsync(
        string fixId, string slot, string fallbackName, IProgress<double?>? progress, CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Get,
            $"/api/denuvo/download?fix={Uri.EscapeDataString(fixId)}&slot={Uri.EscapeDataString(slot)}", ct);
        var signed = await ReadJsonAsync<DenuvoDownloadResponse>(res, ct);
        if (string.IsNullOrWhiteSpace(signed?.Url))
            throw new ApiException("Empty download link");

        return await DownloadFromUrlAsync(signed.Url, fallbackName, progress, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.GetValidAccessTokenAsync());

        var res = await _http.SendAsync(req, completion, ct);
        if (res.IsSuccessStatusCode) return res;

        throw new ApiException("Request failed: " + (int)res.StatusCode, res.StatusCode);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage res, CancellationToken ct) =>
        JsonSerializer.Deserialize<T>(await res.Content.ReadAsStringAsync(ct), JsonOpts);

    private async Task<DownloadedFile> DownloadFromUrlAsync(
        string url, string fallbackName, IProgress<double?>? progress, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
            throw new ApiException($"Download failed: {(int)res.StatusCode}", res.StatusCode);
        return await SaveResponseAsync(res, fallbackName, progress, ct);
    }

    private async Task<DownloadedFile> SaveResponseAsync(
        HttpResponseMessage res, string fallbackName, IProgress<double?>? progress, CancellationToken ct)
    {
        string fileName = res.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? fallbackName;
        foreach (char c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');

        string folder = InterimDownloadsFolder;
        Directory.CreateDirectory(folder);
        string filePath = Path.Combine(folder, fileName);

        long? total = res.Content.Headers.ContentLength;
        await using var src = await res.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(filePath);

        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
            progress?.Report(total is > 0 ? (double)written / total.Value : null);
        }

        return new DownloadedFile(filePath, fileName);
    }
}
