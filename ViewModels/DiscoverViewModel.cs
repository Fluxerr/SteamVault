using SteamVault.Models;
using SteamVault.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace SteamVault.ViewModels;

public class DiscoverViewModel : ViewModelBase
{
    private readonly SteamApiService _steamApi;
    private readonly DownloadService _downloadService;

    // Known hardware/non-game App IDs that Steam's API misclassifies as type 0
    private static readonly HashSet<string> HardwareAppIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "1675200", // Steam Deck
        "1675180", // Steam Deck OLED
        "1675190", // Steam Deck LCD
        "2241930", // Steam Controller
        "353370",  // Steam Controller
        "353380",  // Steam Link
        "968770",  // Steam Link app
    };

    public DiscoverViewModel(SteamApiService steamApi, DownloadService downloadService)
    {
        _steamApi = steamApi;
        _downloadService = downloadService;

        RefreshCommand = new RelayCommand(async _ => await LoadAllAsync(), _ => !IsLoading);
        SelectGameCommand = new RelayCommand(async param => await SelectGameAsync(param as SearchResult));
        DownloadDetailGameCommand = new RelayCommand(async _ => await DownloadSelectedGameAsync(), _ => ShowGameDetail && !IsDownloading);
        InstallDetailGameCommand = new RelayCommand(_ => InstallSelectedGame(), _ => ShowGameDetail && IsDownloadComplete);
        BackToDiscoverCommand = new RelayCommand(_ => BackToBrowse());
    }

    public ObservableCollection<SearchResult> TrendingGames { get; } = new();
    public ObservableCollection<SearchResult> TopSellers { get; } = new();
    public ObservableCollection<SearchResult> NewReleases { get; } = new();
    public ObservableCollection<SearchResult> FreeGames { get; } = new();

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }

    private string _statusMessage = "Click Refresh to browse trending Steam games.";
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    private bool _isDownloading;
    public bool IsDownloading { get => _isDownloading; set { SetProperty(ref _isDownloading, value); CommandManager.InvalidateRequerySuggested(); } }

    private bool _isDownloadComplete;
    public bool IsDownloadComplete { get => _isDownloadComplete; set { SetProperty(ref _isDownloadComplete, value); CommandManager.InvalidateRequerySuggested(); } }

    private bool _showGameDetail;
    public bool ShowGameDetail { get => _showGameDetail; set { SetProperty(ref _showGameDetail, value); OnPropertyChanged(nameof(ShowBrowseGrid)); } }
    public bool ShowBrowseGrid => !ShowGameDetail;

    private string _detailAppId = "";
    public string DetailAppId { get => _detailAppId; set => SetProperty(ref _detailAppId, value); }

    private string _detailGameName = "";
    public string DetailGameName { get => _detailGameName; set => SetProperty(ref _detailGameName, value); }

    private string _detailGameImage = "";
    public string DetailGameImage { get => _detailGameImage; set => SetProperty(ref _detailGameImage, value); }

    private string _detailDescription = "";
    public string DetailDescription { get => _detailDescription; set => SetProperty(ref _detailDescription, value); }

    private string _detailType = "";
    public string DetailType { get => _detailType; set => SetProperty(ref _detailType, value); }

    private string _detailReleaseDate = "";
    public string DetailReleaseDate { get => _detailReleaseDate; set => SetProperty(ref _detailReleaseDate, value); }

    private string _detailPlayerCount = "";
    public string DetailPlayerCount { get => _detailPlayerCount; set => SetProperty(ref _detailPlayerCount, value); }

    private string _detailReviewScore = "";
    public string DetailReviewScore { get => _detailReviewScore; set => SetProperty(ref _detailReviewScore, value); }

    private bool _detailHasStats;
    public bool DetailHasStats { get => _detailHasStats; set => SetProperty(ref _detailHasStats, value); }

    private bool _detailShowMultiplayerWarning;
    public bool DetailShowMultiplayerWarning { get => _detailShowMultiplayerWarning; set => SetProperty(ref _detailShowMultiplayerWarning, value); }

    private string _detailStatusMessage = "";
    public string DetailStatusMessage { get => _detailStatusMessage; set => SetProperty(ref _detailStatusMessage, value); }

    private double _detailProgress;
    public double DetailProgress { get => _detailProgress; set => SetProperty(ref _detailProgress, value); }

    private string? _lastLuaFilePath;

    public ICommand RefreshCommand { get; }
    public ICommand SelectGameCommand { get; }
    public ICommand DownloadDetailGameCommand { get; }
    public ICommand InstallDetailGameCommand { get; }
    public ICommand BackToDiscoverCommand { get; }

    public async Task LoadAllAsync()
    {
        IsLoading = true;
        StatusMessage = "Loading featured games from Steam...";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Add("User-Agent", "SteamVault/2.0");
            var json = Newtonsoft.Json.Linq.JObject.Parse(
                await http.GetStringAsync("https://store.steampowered.com/api/featuredcategories?l=english&cc=US"));

            // Parse all sections: filter to games-only, take exactly multiples of 3 for clean grid
            var trendingItems = ParseCategoryItems(json, "specials", gamesOnly: true)
                .Concat(ParseCategoryItems(json, "coming_soon", gamesOnly: true))
                .DistinctBy(r => r.AppId)
                .Take(9).ToList();

            var topSellerItems = ParseCategoryItems(json, "top_sellers", gamesOnly: true)
                .DistinctBy(r => r.AppId)
                .Take(9).ToList();

            var newReleaseItems = ParseCategoryItems(json, "new_releases", gamesOnly: true)
                .DistinctBy(r => r.AppId)
                .Take(9).ToList();

            // Popular: use specials again but offset
            var freeItems = ParseCategoryItems(json, "specials", gamesOnly: true)
                .Skip(9).Take(9).ToList();

            Application.Current.Dispatcher.Invoke(() =>
            {
                Populate(TrendingGames, trendingItems);
                Populate(TopSellers, topSellerItems);
                Populate(NewReleases, newReleaseItems);
                Populate(FreeGames, freeItems);
            });

            StatusMessage = "Browse complete! Click any game card to see details and download.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static void Populate(ObservableCollection<SearchResult> target, List<SearchResult> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private async Task SelectGameAsync(SearchResult? result)
    {
        if (result == null) return;

        DetailAppId = result.AppId;
        DetailGameName = result.Name;
        DetailGameImage = result.ImageUrl;
        DetailDescription = "";
        DetailType = "";
        DetailReleaseDate = "";
        DetailPlayerCount = "";
        DetailReviewScore = "";
        DetailHasStats = false;
        DetailStatusMessage = $"Loading details for {result.Name}...";
        DetailProgress = 0;
        IsDownloadComplete = false;
        ShowGameDetail = true;

        var gameInfo = await _steamApi.GetAppDetailsAsync(result.AppId);
        var stats = await _steamApi.GetSteamDbStatsAsync(result.AppId);

        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (gameInfo != null)
            {
                DetailGameName = gameInfo.Name;
                DetailGameImage = gameInfo.HeaderImageUrl;
                DetailDescription = gameInfo.ShortDescription;
                DetailType = gameInfo.Type;
                DetailReleaseDate = gameInfo.ReleaseDate;
                DetailShowMultiplayerWarning = gameInfo.HasMultiplayerCategories;
            }
            if (stats != null)
            {
                DetailHasStats = stats.HasScore || stats.HasPlayers;
                if (stats.CurrentPlayers > 0)
                    DetailPlayerCount = stats.CurrentPlayers >= 1000 ? $"{stats.CurrentPlayers:N0} playing now" : $"{stats.CurrentPlayers} playing now";
                var parts = new List<string>();
                if (stats.MetacriticScore > 0) parts.Add($"Metacritic: {stats.MetacriticScore}");
                if (stats.PositiveReviewPercent > 0) parts.Add($"{stats.PositiveReviewPercent}% positive");
                DetailReviewScore = string.Join(" · ", parts);
            }
            DetailStatusMessage = "Ready to download. Click 'Download Config' to get depot manifests and Lua files.";
        });
    }

    private async Task DownloadSelectedGameAsync()
    {
        if (string.IsNullOrWhiteSpace(DetailAppId)) return;
        bool includeDlcs = MessageBox.Show("Do you want to include all available DLCs?", "Include DLCs?", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        IsDownloading = true; IsDownloadComplete = false;
        DetailStatusMessage = "Fetching game details from Steam..."; DetailProgress = 0;
        try
        {
            var result = await _downloadService.DownloadGameAsync(DetailAppId,
                onStatus: msg => Application.Current?.Dispatcher.Invoke(() => DetailStatusMessage = msg),
                onProgress: pct => Application.Current?.Dispatcher.Invoke(() => DetailProgress = pct),
                includeDlcs: includeDlcs);
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (result.Success) { IsDownloadComplete = true; _lastLuaFilePath = result.LuaFilePath; DetailStatusMessage = $"✓ {result.GameName} installed!"; DetailProgress = 100; }
                else DetailStatusMessage = $"✗ {result.Error}";
            });
        }
        catch (Exception ex) { DetailStatusMessage = $"✗ Error: {ex.Message}"; }
        finally { IsDownloading = false; }
    }

    private void InstallSelectedGame()
    {
        if (IsDownloadComplete && !string.IsNullOrWhiteSpace(DetailAppId))
            try { Process.Start(new ProcessStartInfo { FileName = $"steam://install/{DetailAppId}", UseShellExecute = true }); }
            catch (Exception ex) { DetailStatusMessage = $"Failed: {ex.Message}"; }
    }

    private void BackToBrowse() => ShowGameDetail = false;

    private static List<SearchResult> ParseCategoryItems(Newtonsoft.Json.Linq.JObject json, string category, bool gamesOnly = false)
    {
        var results = new List<SearchResult>();
        var items = json[category]?["items"] as Newtonsoft.Json.Linq.JArray;
        if (items == null) return results;
        foreach (var item in items)
        {
            var r = ParseItem(item, gamesOnly);
            if (r != null) results.Add(r);
        }
        return results;
    }

    /// <summary>
    /// Parses a store item, filtering out non-games.
    /// - Hardware: blacklisted by App ID (Steam Deck, etc.) even if API says type 0
    /// - DLCs/packs: filtered by requiring header_image + name doesn't contain "Pack"/"Bundle"/"Edition" keywords
    ///   (actual DLCs/redeemables from this API typically have no header_image or have giveaway names)
    /// </summary>
    private static SearchResult? ParseItem(Newtonsoft.Json.Linq.JToken item, bool gamesOnly = false)
    {
        var id = item["id"];
        if (id == null) return null;
        var appId = id.ToString();

        // Hardware blacklist
        if (HardwareAppIds.Contains(appId)) return null;

        var name = item["name"]?.ToString();
        if (string.IsNullOrWhiteSpace(name)) return null;

        // If gamesOnly, require a header_image (real games always have one in this API)
        // and skip items that look like DLC/currency/redeemable packs
        var headerImage = item["header_image"]?.ToString();
        if (gamesOnly && string.IsNullOrWhiteSpace(headerImage))
            return null;

        // Filter known DLC patterns from the name when gamesOnly
        if (gamesOnly)
        {
            var lowerName = name.ToLowerInvariant();
            if (lowerName.Contains(" - ") && (
                lowerName.Contains("edition") ||
                lowerName.Contains("pack") ||
                lowerName.Contains("bundle") ||
                lowerName.Contains("supporter") ||
                lowerName.Contains(" upgrade") ||
                lowerName.Contains(" dlc") ||
                lowerName.Contains(" premium") ||
                lowerName.Contains(" deluxe") ||
                lowerName.Contains(" welcome pack") ||
                lowerName.Contains(" battle pass")))
                return null;
        }

        return new SearchResult
        {
            AppId = appId,
            Name = name,
            ImageUrl = headerImage
                    ?? item["capsule_image"]?.ToString()
                    ?? item["tiny_image"]?.ToString()
                    ?? ""
        };
    }
}