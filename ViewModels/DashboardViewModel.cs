using SteamVault.Models;
using SteamVault.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Application = System.Windows.Application;

namespace SteamVault.ViewModels;

public class DashboardViewModel : ViewModelBase
{
    private readonly DownloadService _downloadService;
    private readonly GameSearchService _searchService;
    private readonly SteamApiService _steamApi;
    private CancellationTokenSource? _searchCts;
    private System.Timers.Timer? _searchDebounce;

    public DashboardViewModel(
        DownloadService downloadService,
        GameSearchService searchService,
        SteamApiService steamApi,
        LuaParserService luaParser,
        SettingsService settings,
        GameManagementService gameMgmt,
        DepotKeyService depotKeyService)
    {
        _downloadService = downloadService;
        _searchService = searchService;
        _steamApi = steamApi;
        _luaParser = luaParser;
        _settings = settings;
        _gameMgmt = gameMgmt;
        _depotKeyService = depotKeyService;

        PlayFeaturedGameCommand = new RelayCommand(
            param => PlayGame(param as FeaturedGameEntry));

        UpdateFeaturedGameCommand = new RelayCommand(
            async param => await UpdateFeaturedGameAsync(param as FeaturedGameEntry),
            param => param is FeaturedGameEntry g && !g.IsUpdating);

        ViewAllCommand = new RelayCommand(_ => NavigateToLibrary?.Invoke());
        OpenLuaFolderQuickCommand = new RelayCommand(_ => OpenLuaFolderQuick());
        UpdateAllGamesCommand = new RelayCommand(_ => NavigateToLibraryAndUpdate?.Invoke());
        ToggleAutoUpdateCommand = new RelayCommand(_ => ToggleAutoUpdate());
        ResetDashboardCommand = new RelayCommand(_ => ResetDashboard());

        DownloadCommand = new RelayCommand(
            async _ => await DownloadAsync(),
            _ => !IsDownloading && !string.IsNullOrWhiteSpace(AppIdInput));

        OpenLuaFolderCommand = new RelayCommand(
            _ => OpenLuaFolder(),
            _ => !string.IsNullOrWhiteSpace(_lastLuaFilePath));

        InstallGameCommand = new RelayCommand(
            _ => InstallGame(),
            _ => IsComplete);

        SelectSearchResultCommand = new RelayCommand(
            param => SelectSearchResult(param as SearchResult));
    }

    /// <summary>
    /// Action set by MainViewModel to navigate to the My Games tab.
    /// </summary>
    public Action? NavigateToLibrary { get; set; }
    public Action? NavigateToLibraryAndUpdate { get; set; }

    // --- Properties ---
    private string _appIdInput = "";
    public string AppIdInput
    {
        get => _appIdInput;
        set
        {
            SetProperty(ref _appIdInput, value);
            CommandManager.InvalidateRequerySuggested();
            TriggerSearch(value);
        }
    }

    // Search results
    public ObservableCollection<SearchResult> SearchResults { get; } = new();

    private bool _showSearchResults;
    public bool ShowSearchResults
    {
        get => _showSearchResults;
        set => SetProperty(ref _showSearchResults, value);
    }

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        set => SetProperty(ref _isSearching, value);
    }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            SetProperty(ref _isDownloading, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private bool _isComplete;
    public bool IsComplete
    {
        get => _isComplete;
        set => SetProperty(ref _isComplete, value);
    }

    private bool _hasError;
    public bool HasError
    {
        get => _hasError;
        set => SetProperty(ref _hasError, value);
    }

    private string _statusMessage = "Search for a game by name or App ID, then download.";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    // Game info (shown after successful resolve)
    private string? _gameName;
    public string? GameName
    {
        get => _gameName;
        set
        {
            SetProperty(ref _gameName, value);
            OnPropertyChanged(nameof(HasGameInfo));
        }
    }

    private string? _gameImage;
    public string? GameImage
    {
        get => _gameImage;
        set => SetProperty(ref _gameImage, value);
    }

    private string? _gameDescription;
    public string? GameDescription
    {
        get => _gameDescription;
        set => SetProperty(ref _gameDescription, value);
    }

    private string? _gameType;
    public string? GameType
    {
        get => _gameType;
        set => SetProperty(ref _gameType, value);
    }

    private string? _gameReleaseDate;
    public string? GameReleaseDate
    {
        get => _gameReleaseDate;
        set => SetProperty(ref _gameReleaseDate, value);
    }

    private string? _estimatedSize;
    public string? EstimatedSize
    {
        get => _estimatedSize;
        set => SetProperty(ref _estimatedSize, value);
    }

    private bool _showSteamDbStats;
    public bool ShowSteamDbStats
    {
        get => _showSteamDbStats;
        set => SetProperty(ref _showSteamDbStats, value);
    }

    private string _playerCountText = "";
    public string PlayerCountText
    {
        get => _playerCountText;
        set => SetProperty(ref _playerCountText, value);
    }

    private string _reviewScoreText = "";
    public string ReviewScoreText
    {
        get => _reviewScoreText;
        set => SetProperty(ref _reviewScoreText, value);
    }

    private bool _showMultiplayerWarning;
    public bool ShowMultiplayerWarning
    {
        get => _showMultiplayerWarning;
        set => SetProperty(ref _showMultiplayerWarning, value);
    }

    private string? _resultSummary;
    public string? ResultSummary
    {
        get => _resultSummary;
        set => SetProperty(ref _resultSummary, value);
    }

    public bool HasGameInfo => !string.IsNullOrWhiteSpace(_gameName);

    private string? _lastLuaFilePath;

    // ── Featured Configs ──
    private readonly SettingsService _settings;
    private readonly LuaParserService _luaParser;
    private readonly GameManagementService _gameMgmt;
    private readonly DepotKeyService _depotKeyService;

    public ObservableCollection<FeaturedGameEntry> FeaturedGames { get; } = new();

    private bool _hasFeaturedGames;
    public bool HasFeaturedGames
    {
        get => _hasFeaturedGames;
        set => SetProperty(ref _hasFeaturedGames, value);
    }

    public ICommand PlayFeaturedGameCommand { get; }
    public ICommand UpdateFeaturedGameCommand { get; }
        public ICommand ViewAllCommand { get; }
        public ICommand OpenLuaFolderQuickCommand { get; }
        public ICommand UpdateAllGamesCommand { get; }
        public ICommand ToggleAutoUpdateCommand { get; }
        public ICommand ResetDashboardCommand { get; }
        public ICommand DownloadCommand { get; }
        public ICommand OpenLuaFolderCommand { get; }
        public ICommand InstallGameCommand { get; }
        public ICommand SelectSearchResultCommand { get; }

    // --- Search ---
    private void TriggerSearch(string query)
    {
        _searchDebounce?.Stop();
        _searchDebounce?.Dispose();

        if (string.IsNullOrWhiteSpace(query) || query.Length < 1)
        {
            ShowSearchResults = false;
            SearchResults.Clear();
            return;
        }

        // Debounce: wait 400ms after user stops typing
        _searchDebounce = new System.Timers.Timer(200);
        _searchDebounce.AutoReset = false;
        _searchDebounce.Elapsed += async (_, _) =>
        {
            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                await PerformSearchAsync(query);
            });
        };
        _searchDebounce.Start();
    }

    private async Task PerformSearchAsync(string query)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();

        IsSearching = true;

        try
        {
            var results = await _searchService.SearchAsync(query, _searchCts.Token);

            SearchResults.Clear();
            foreach (var r in results)
                SearchResults.Add(r);

            ShowSearchResults = SearchResults.Count > 0;
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsSearching = false;
        }
    }

    private void SelectSearchResult(SearchResult? result)
    {
        if (result == null) return;

        _appIdInput = result.AppId;
        OnPropertyChanged(nameof(AppIdInput));
        ShowSearchResults = false;
        SearchResults.Clear();
        CommandManager.InvalidateRequerySuggested();
    }

    // --- Main Download Flow ---
    private async Task DownloadAsync()
    {
        var appId = AppIdInput.Trim();

        // Ask user if they want to include DLCs
        bool includeDlcs = false;
        var dlcChoice = System.Windows.MessageBox.Show(
            "Do you want to include all available DLCs for this game?",
            "Include DLCs?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        includeDlcs = dlcChoice == MessageBoxResult.Yes;

        IsDownloading = true;
        IsComplete = false;
        HasError = false;
        Progress = 0;
        GameName = null;
        GameImage = null;
        GameDescription = null;
        GameType = null;
        GameReleaseDate = null;
        ResultSummary = null;
        _lastLuaFilePath = null;
        ShowSearchResults = false;
        CommandManager.InvalidateRequerySuggested();

        try
        {
            var result = await _downloadService.DownloadGameAsync(
                appId,
                onStatus: msg => Application.Current?.Dispatcher.Invoke(() => StatusMessage = msg),
                onProgress: pct => Application.Current?.Dispatcher.Invoke(() => Progress = pct),
                includeDlcs: includeDlcs);

            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (result.Success)
                {
                    IsComplete = true;
                    GameName = result.Game?.Name;
                    GameImage = result.Game?.HeaderImageUrl;
                    GameDescription = result.Game?.ShortDescription;
                    GameType = result.Game?.Type;
                    GameReleaseDate = result.Game?.ReleaseDate;
                    _lastLuaFilePath = result.LuaFilePath;
                    ShowMultiplayerWarning = result.Game?.HasMultiplayerCategories ?? false;

                    // Compute estimated size
                    var totalBytes = result.Depots?.Sum(d => d.SizeBytes) ?? 0;
                    if (totalBytes > 0)
                        EstimatedSize = FormatBytes(totalBytes);
                    else
                        EstimatedSize = null;

                    var dlcText = result.DlcCount > 0 ? $" · {result.DlcCount} DLC(s)" : "";
                    ResultSummary = $"{result.DepotCount} depot(s) · {result.KeysAttached} key(s){dlcText} · Lua saved";
                    StatusMessage = $"✓ {result.GameName} installed successfully!";
                    Progress = 100;
                }
                else
                {
                    HasError = true;
                    StatusMessage = $"✗ {result.Error}";
                }

                CommandManager.InvalidateRequerySuggested();
            });

            // Fetch SteamDB-style stats asynchronously (non-blocking)
            _ = Task.Run(async () =>
            {
                var stats = await _steamApi.GetSteamDbStatsAsync(appId);
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (stats != null)
                    {
                        if (stats.CurrentPlayers > 0)
                        {
                            PlayerCountText = stats.CurrentPlayers >= 1000
                                ? $"{stats.CurrentPlayers:N0} playing now"
                                : $"{stats.CurrentPlayers} playing now";
                        }

                        if (stats.MetacriticScore > 0)
                        {
                            ReviewScoreText += $"Metacritic: {stats.MetacriticScore} · ";
                        }

                        if (stats.PositiveReviewPercent > 0)
                        {
                            ReviewScoreText += $"{stats.PositiveReviewPercent}% positive";
                        }

                        ShowSteamDbStats = !string.IsNullOrEmpty(PlayerCountText) || !string.IsNullOrEmpty(ReviewScoreText);
                    }
                });
            });
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = $"✗ Unexpected error: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private void OpenLuaFolder()
    {
        if (!string.IsNullOrWhiteSpace(_lastLuaFilePath))
        {
            var dir = Path.GetDirectoryName(_lastLuaFilePath);
            if (dir != null && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }
    }

    /// <summary>
    /// Formats a byte count into human-readable form (e.g. "~23.4 GB").
    /// </summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824)
            return $"~{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576)
            return $"~{bytes / 1_048_576.0:F0} MB";
        if (bytes >= 1_024)
            return $"~{bytes / 1_024.0:F0} KB";
        return $"{bytes} B";
    }

    private void InstallGame()
    {
        if (IsComplete && !string.IsNullOrWhiteSpace(AppIdInput))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"steam://install/{AppIdInput.Trim()}",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to start Steam install: {ex.Message}";
            }
        }
    }

    // ── Featured Configs ──

    public async Task LoadFeaturedGamesAsync()
    {
        try
        {
            FeaturedGames.Clear();
            HasFeaturedGames = false;

            var luaPath = _settings.Settings.LuaOutputPath;
            if (string.IsNullOrWhiteSpace(luaPath) || !Directory.Exists(luaPath))
                return;

            var entries = _luaParser.ScanLuaFolder(luaPath);
            if (entries.Count == 0) return;

            // Filter to installed only, take up to 4 newest
            var installedEntries = new List<LibraryEntry>();
            foreach (var e in entries.OrderByDescending(e => e.LastUpdated))
            {
                if (_gameMgmt.IsGameInstalled(e.AppId))
                    installedEntries.Add(e);
                if (installedEntries.Count >= 4)
                    break;
            }

            if (installedEntries.Count == 0) return;

            // Add entries to UI immediately with placeholder data
            foreach (var entry in installedEntries)
            {
                FeaturedGames.Add(new FeaturedGameEntry
                {
                    AppId = entry.AppId,
                    Name = entry.Name,
                    HeaderImageUrl = entry.HeaderImageUrl,
                    IsInstalled = true,
                });
            }
            HasFeaturedGames = FeaturedGames.Count > 0;

            // Fire API calls in parallel
            var semaphore = new SemaphoreSlim(4);
            var tasks = installedEntries.Select(async entry =>
            {
                var gameEntry = FeaturedGames.First(g => g.AppId == entry.AppId);
                await semaphore.WaitAsync();
                try
                {
                    // Fetch details
                    var gameInfo = await _steamApi.GetAppDetailsAsync(entry.AppId);
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (gameInfo != null)
                        {
                            gameEntry.Name = gameInfo.Name;
                            gameEntry.HeaderImageUrl = gameInfo.HeaderImageUrl;
                        }
                    });

                    // Check update status
                    if (entry.Depots.Count > 0)
                    {
                        try
                        {
                            var latest = await _steamApi.GetDepotsFromSteamCmdAsync(entry.AppId);
                            foreach (var d in entry.Depots)
                            {
                                if (string.IsNullOrWhiteSpace(d.ManifestId)) continue;
                                var match = latest.FirstOrDefault(l => l.DepotId == d.DepotId);
                                if (match != null && !string.Equals(d.ManifestId, match.ManifestId, StringComparison.OrdinalIgnoreCase))
                                {
                                    await Application.Current.Dispatcher.InvokeAsync(() => gameEntry.NeedsUpdate = true);
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                }
                finally { semaphore.Release(); }
            });

            await Task.WhenAll(tasks);
        }
        catch { }
    }

    private void PlayGame(FeaturedGameEntry? entry)
    {
        if (entry == null || !entry.IsInstalled) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"steam://run/{entry.AppId}",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void OpenLuaFolderQuick()
    {
        var luaPath = _settings.Settings.LuaOutputPath;
        if (!string.IsNullOrWhiteSpace(luaPath) && Directory.Exists(luaPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = luaPath,
                UseShellExecute = true
            });
        }
    }

    private void ResetDashboard()
    {
        GameName = null;
        GameImage = null;
        GameDescription = null;
        GameType = null;
        GameReleaseDate = null;
        EstimatedSize = null;
        ResultSummary = null;
        ShowMultiplayerWarning = false;
        ShowSteamDbStats = false;
        PlayerCountText = "";
        ReviewScoreText = "";
        IsComplete = false;
        HasError = false;
        AppIdInput = "";
        _lastLuaFilePath = null;
        CommandManager.InvalidateRequerySuggested();
        _ = LoadFeaturedGamesAsync();
    }

    private void ToggleAutoUpdate()
    {
        _settings.Settings.AutoUpdateEnabled = !_settings.Settings.AutoUpdateEnabled;
        _settings.Save();
        OnPropertyChanged(nameof(AutoUpdateText));
    }

    public string AutoUpdateText => _settings.Settings.AutoUpdateEnabled
        ? "Auto-update: ON"
        : "Auto-update: OFF";

    private async Task UpdateFeaturedGameAsync(FeaturedGameEntry? entry)
    {
        if (entry == null || entry.IsUpdating) return;
        entry.IsUpdating = true;

        try
        {
            var result = await _downloadService.DownloadGameAsync(entry.AppId,
                onStatus: _ => { },
                onProgress: pct => Application.Current?.Dispatcher.Invoke(() => entry.UpdateProgress = pct),
                includeDlcs: true);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (result.Success)
                {
                    entry.NeedsUpdate = false;
                    entry.IsInstalled = true;
                }
            });
        }
        catch { }
        finally
        {
            entry.IsUpdating = false;
        }
    }
}

public class FeaturedGameEntry : System.ComponentModel.INotifyPropertyChanged
{
    public string AppId { get; set; } = "";

    private string _name = "Unknown";
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    private string _headerImageUrl = "";
    public string HeaderImageUrl
    {
        get => _headerImageUrl;
        set { _headerImageUrl = value; OnPropertyChanged(); }
    }

    public bool IsInstalled { get; set; }

    private bool _needsUpdate;
    public bool NeedsUpdate
    {
        get => _needsUpdate;
        set { _needsUpdate = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowUpdateButton)); }
    }

    public bool ShowUpdateButton => NeedsUpdate;

    private bool _isUpdating;
    public bool IsUpdating
    {
        get => _isUpdating;
        set { _isUpdating = value; OnPropertyChanged(); }
    }

    private double _updateProgress;
    public double UpdateProgress
    {
        get => _updateProgress;
        set { _updateProgress = value; OnPropertyChanged(); }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}