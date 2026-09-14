using SteamVault.Models;
using SteamVault.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;

namespace SteamVault.ViewModels;

public class StatsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly LuaParserService _luaParser;
    private readonly SteamApiService _steamApi;
    private readonly GameManagementService _gameMgmt;

    public StatsViewModel(
        SettingsService settings, 
        LuaParserService luaParser, 
        SteamApiService steamApi,
        GameManagementService gameMgmt)
    {
        _settings = settings;
        _luaParser = luaParser;
        _steamApi = steamApi;
        _gameMgmt = gameMgmt;

        RefreshCommand = new RelayCommand(async _ => await RefreshStatsAsync(), _ => !IsLoading);
        CleanUpCommand = new RelayCommand(async _ => await CleanUpDiskAsync(), _ => !IsLoading);

        // Load immediately
        _ = RefreshStatsAsync();
    }

    public ICommand RefreshCommand { get; }
    public ICommand CleanUpCommand { get; }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set { SetProperty(ref _isLoading, value); CommandManager.InvalidateRequerySuggested(); }
    }

    private int _totalGamesConfigured;
    public int TotalGamesConfigured
    {
        get => _totalGamesConfigured;
        set => SetProperty(ref _totalGamesConfigured, value);
    }

    private int _totalDlcsUnlocked;
    public int TotalDlcsUnlocked
    {
        get => _totalDlcsUnlocked;
        set => SetProperty(ref _totalDlcsUnlocked, value);
    }

    private double _totalMoneySaved;
    public double TotalMoneySaved
    {
        get => _totalMoneySaved;
        set => SetProperty(ref _totalMoneySaved, value);
    }

    private string _formattedMoneySaved = "$0.00";
    public string FormattedMoneySaved
    {
        get => _formattedMoneySaved;
        set => SetProperty(ref _formattedMoneySaved, value);
    }

    private string _formattedDiskUsage = "0 MB";
    public string FormattedDiskUsage
    {
        get => _formattedDiskUsage;
        set => SetProperty(ref _formattedDiskUsage, value);
    }

    public ObservableCollection<MonthlyDataPoint> MonthlyCounts { get; } = new();
    public ObservableCollection<TopGameEntry> TopGames { get; } = new();
    public ObservableCollection<GenreDataPoint> GenreBreakdown { get; } = new();

    public async Task RefreshStatsAsync()
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            var luaPath = _settings.Settings.LuaOutputPath;
            var history = _settings.Settings.DownloadHistory;

            // 1. Basic Counts from Lua files
            var entries = _luaParser.ScanLuaFolder(luaPath);
            int gameCount = entries.Count;
            int dlcCount = entries.Sum(e => Math.Max(0, e.Depots.Count - 1)); // Rough estimate: 1 depot is base game, rest are DLC/Content

            // 2. Disk Size Calculation
            long totalManifestBytes = 0;
            var steamDir = _settings.Settings.SteamDirectory;
            if (!string.IsNullOrWhiteSpace(steamDir) && Directory.Exists(steamDir))
            {
                try
                {
                    var depotCache = Path.Combine(steamDir, "depotcache");
                    if (Directory.Exists(depotCache))
                    {
                        totalManifestBytes = Directory.GetFiles(depotCache, "*.manifest").Sum(f => new FileInfo(f).Length);
                    }
                }
                catch { }
            }

            long totalLuaBytes = 0;
            if (Directory.Exists(luaPath))
            {
                try
                {
                    totalLuaBytes = Directory.GetFiles(luaPath, "*.lua").Sum(f => new FileInfo(f).Length);
                }
                catch { }
            }

            var totalDiskMB = (totalManifestBytes + totalLuaBytes) / (1024.0 * 1024.0);

            // 3. Process Download History
            var topGamesDict = new Dictionary<string, TopGameEntry>();
            var monthlyDict = new Dictionary<string, int>();

            // Initialize last 12 months with 0
            for (int i = 11; i >= 0; i--)
            {
                var monthKey = DateTime.Now.AddMonths(-i).ToString("MMM yyyy");
                monthlyDict[monthKey] = 0;
            }

            foreach (var h in history)
            {
                // Timeline
                var monthKey = h.DownloadDate.ToString("MMM yyyy");
                if (monthlyDict.ContainsKey(monthKey))
                    monthlyDict[monthKey]++;

                // Top Games
                if (!topGamesDict.ContainsKey(h.AppId))
                {
                    topGamesDict[h.AppId] = new TopGameEntry
                    {
                        AppId = h.AppId,
                        Name = h.GameName,
                        ImageUrl = h.HeaderImageUrl,
                        DownloadCount = 0
                    };
                }
                topGamesDict[h.AppId].DownloadCount++;
            }

            var topGamesList = topGamesDict.Values.OrderByDescending(x => x.DownloadCount).Take(10).ToList();

            // 4. Async Data Fetching (Money Saved & Genres)
            // Fetch prices for the top 50 configured games to get a solid estimate
            double estimatedSavings = 0;
            var genreCounts = new Dictionary<string, int>();

            var appIdsToFetch = entries.Select(e => e.AppId).Take(50).ToList();
            
            // To not hammer the API, we'll only do full fetch if there aren't too many, 
            // or just estimate based on $29.99 average for others.
            int apiFetches = 0;
            foreach (var appId in appIdsToFetch)
            {
                if (apiFetches > 15) break; // Limit to 15 API calls per refresh to be safe
                
                var details = await _steamApi.GetAppDetailsAsync(appId);
                if (details != null)
                {
                    apiFetches++;
                    
                    if (details.IsFree)
                    {
                        estimatedSavings += 0;
                    }
                    else if (details.Price > 0)
                    {
                        estimatedSavings += (double)details.Price;
                    }
                    else
                    {
                        estimatedSavings += 19.99; // Fallback if API doesn't return price
                    }
                    
                    if (details.Genres != null)
                    {
                        foreach(var g in details.Genres)
                        {
                            if (!genreCounts.ContainsKey(g)) genreCounts[g] = 0;
                            genreCounts[g]++;
                        }
                    }
                }
            }
            
            // Extrapolate savings for the rest
            if (entries.Count > apiFetches && apiFetches > 0)
            {
                double averagePrice = estimatedSavings / apiFetches;
                estimatedSavings += averagePrice * (entries.Count - apiFetches);
            }
            else if (apiFetches == 0)
            {
                estimatedSavings = entries.Count * 19.99; // Fallback estimate
            }

            // Update UI
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                TotalGamesConfigured = gameCount;
                TotalDlcsUnlocked = dlcCount;
                TotalMoneySaved = estimatedSavings;
                FormattedMoneySaved = $"${estimatedSavings:N2}";
                FormattedDiskUsage = $"{totalDiskMB:N1} MB";

                MonthlyCounts.Clear();
                foreach (var kvp in monthlyDict)
                {
                    MonthlyCounts.Add(new MonthlyDataPoint { Month = kvp.Key, Count = kvp.Value });
                }

                TopGames.Clear();
                foreach (var g in topGamesList) TopGames.Add(g);

                GenreBreakdown.Clear();
                var topGenres = genreCounts.OrderByDescending(kvp => kvp.Value).Take(5).ToList();
                
                // Color palette for pie chart
                var colors = new[] { "#8839EF", "#CBA6F7", "#89B4FA", "#88C0D0", "#A6E3A1" };
                for (int i = 0; i < topGenres.Count; i++)
                {
                    GenreBreakdown.Add(new GenreDataPoint 
                    { 
                        Genre = topGenres[i].Key, 
                        Count = topGenres[i].Value,
                        ColorHex = colors[i % colors.Length]
                    });
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Stats error: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task CleanUpDiskAsync()
    {
        // Add cleanup logic here for orphaned manifests
        await RefreshStatsAsync();
    }
}

public class MonthlyDataPoint
{
    public string Month { get; set; } = "";
    public int Count { get; set; }
}

public class TopGameEntry
{
    public string AppId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public int DownloadCount { get; set; }
}

public class GenreDataPoint
{
    public string Genre { get; set; } = "";
    public int Count { get; set; }
    public string ColorHex { get; set; } = "#FFFFFF";
    public System.Windows.Media.Brush ColorBrush => new System.Windows.Media.SolidColorBrush(
        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(ColorHex));
}
