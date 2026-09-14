using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SteamVault.Services;

/// <summary>
/// Submits game requests to the SteamVault website when decryption keys are missing.
/// Uses the Supabase REST API (same instance as AuthService) to upsert requests.
/// Fire-and-forget — failures are silently logged, never blocking the user.
/// </summary>
public class GameRequestService
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    // Rate limit: don't submit more than once every 30 seconds
    private static DateTime _lastSubmission = DateTime.MinValue;
    private static readonly TimeSpan RateLimit = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Submits a game request to Supabase when decryption keys are missing.
    /// Safe to call fire-and-forget — all exceptions are caught and logged.
    /// </summary>
    public static async Task SubmitRequestAsync(
        string appId,
        string gameName,
        string headerImageUrl,
        List<string>? missingDepotIds = null)
    {
        try
        {
            // Rate limit check
            if (DateTime.Now - _lastSubmission < RateLimit)
            {
                Debug.WriteLine($"GameRequestService: Rate limited — skipping request for {appId}");
                return;
            }

            if (string.IsNullOrWhiteSpace(appId)) return;

            _lastSubmission = DateTime.Now;

            var payload = new
            {
                p_app_id = appId,
                p_name = gameName ?? "Unknown",
                p_header_image = headerImageUrl ?? "",
                p_missing_depots = missingDepotIds ?? new List<string>(),
                p_source = "app"
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var req = new HttpRequestMessage(HttpMethod.Post, $"{AppConfig.SupabaseUrl}/rest/v1/rpc/upsert_game_request")
            {
                Content = content
            };
            req.Headers.Add("apikey", AppConfig.SupabaseAnonKey);
            req.Headers.Add("Authorization", $"Bearer {AppConfig.SupabaseAnonKey}");

            var response = await _http.SendAsync(req);

            if (response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"GameRequestService: Successfully submitted request for {gameName} ({appId})");
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                Debug.WriteLine($"GameRequestService: Request failed ({response.StatusCode}): {error}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GameRequestService: Exception submitting request for {appId} — {ex.Message}");
        }
    }
}
