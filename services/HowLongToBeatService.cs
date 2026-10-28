using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SteamVault.Services
{
    public class HowLongToBeatData
    {
        public string GameName { get; set; } = string.Empty;
        public string MainStoryTime { get; set; } = "--";
        public string MainExtraTime { get; set; } = "--";
        public string CompletionistTime { get; set; } = "--";
        public bool HasData { get; set; }
    }

    public class HowLongToBeatService
    {
        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        static HowLongToBeatService()
        {
            HttpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SteamVault/2.6.1");
            HttpClient.DefaultRequestHeaders.Add("Referer", "https://howlongtobeat.com/");
        }

        public async Task<HowLongToBeatData> GetHltbDataAsync(string gameTitle)
        {
            var result = new HowLongToBeatData { GameName = gameTitle };
            if (string.IsNullOrWhiteSpace(gameTitle)) return result;

            try
            {
                // Clean game title for search
                var cleanTitle = Regex.Replace(gameTitle, @"[^\w\s]", "").Trim();
                var searchUrl = $"https://howlongtobeat.com/api/search";

                var jsonPayload = JsonSerializer.Serialize(new
                {
                    searchType = "games",
                    searchTerms = cleanTitle.Split(' '),
                    searchPage = 1,
                    size = 5
                });

                var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");
                var response = await HttpClient.PostAsync(searchUrl, content);

                if (response.IsSuccessStatusCode)
                {
                    var jsonString = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(jsonString);

                    if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.GetArrayLength() > 0)
                    {
                        var firstGame = dataArr[0];

                        if (firstGame.TryGetProperty("comp_main", out var compMain))
                        {
                            var seconds = compMain.GetInt32();
                            if (seconds > 0) result.MainStoryTime = FormatHours(seconds);
                        }

                        if (firstGame.TryGetProperty("comp_plus", out var compPlus))
                        {
                            var seconds = compPlus.GetInt32();
                            if (seconds > 0) result.MainExtraTime = FormatHours(seconds);
                        }

                        if (firstGame.TryGetProperty("comp_100", out var comp100))
                        {
                            var seconds = comp100.GetInt32();
                            if (seconds > 0) result.CompletionistTime = FormatHours(seconds);
                        }

                        result.HasData = result.MainStoryTime != "--" || result.MainExtraTime != "--" || result.CompletionistTime != "--";
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HLTB Search Error: {ex.Message}");
            }

            return result;
        }

        private static string FormatHours(int totalSeconds)
        {
            var hours = totalSeconds / 3600.0;
            if (hours < 1) return $"{Math.Round(totalSeconds / 60.0)} Mins";
            return $"{Math.Round(hours, 1)} Hours";
        }
    }
}
