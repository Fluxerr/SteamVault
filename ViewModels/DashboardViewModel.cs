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
    private readonly SteamApiService _steamApi;
    private readonly ImportService _importService;
    private readonly GameSearchService _searchService;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _debounceCts;

    public DashboardViewModel(
        DownloadService downloadService,
        GameSearchService searchService,
        SteamApiService steamApi,
        LuaParserService luaParser,
        SettingsService settings,
        GameManagementService gameMgmt,
        DepotKeyService depotKeyService,
        ImportService importService)
    {
        _downloadService = downloadService;
        _searchService = searchService;
        _steamApi = steamApi;
        _luaParser = luaParser;
        _settings = settings;
        _gameMgmt = gameMgmt;
        _depotKeyService = depotKeyService;
        _importService = importService;

        LoadRecentActivity();
        SearchHistoryList = new ObservableCollection<string>(_settings.Settings.SearchHistory);

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

        ImportFilesCommand = new RelayCommand(
            async param => await SmartImportFilesAsync(param as string[]));

        DismissMultiImportCommand = new RelayCommand(_ => DismissMultiImport());
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

    public ObservableCollection<SearchResult> SearchResults { get; } = new();
    
    public ObservableCollection<DownloadHistoryEntry> RecentActivity { get; } = new();
    public ObservableCollection<string> SearchHistoryList { get; } = new();

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

    // Hardware & Compatibility Properties
    private string _compatibilityRating = "";
    public string CompatibilityRating
    {
        get => _compatibilityRating;
        set => SetProperty(ref _compatibilityRating, value);
    }

    private string _compatibilityBadgeColor = "#4CAF50";
    public string CompatibilityBadgeColor
    {
        get => _compatibilityBadgeColor;
        set => SetProperty(ref _compatibilityBadgeColor, value);
    }

    private string _compatibilityText = "";
    public string CompatibilityText
    {
        get => _compatibilityText;
        set => SetProperty(ref _compatibilityText, value);
    }

    private string _userCpuName = "";
    public string UserCpuName
    {
        get => _userCpuName;
        set => SetProperty(ref _userCpuName, value);
    }

    private string _userGpuName = "";
    public string UserGpuName
    {
        get => _userGpuName;
        set => SetProperty(ref _userGpuName, value);
    }

    private string _userRamText = "";
    public string UserRamText
    {
        get => _userRamText;
        set => SetProperty(ref _userRamText, value);
    }

    private string _gameReqRam = "";
    public string GameReqRam
    {
        get => _gameReqRam;
        set => SetProperty(ref _gameReqRam, value);
    }

    private string _gameReqCpu = "";
    public string GameReqCpu
    {
        get => _gameReqCpu;
        set => SetProperty(ref _gameReqCpu, value);
    }

    private string _gameReqGpu = "";
    public string GameReqGpu
    {
        get => _gameReqGpu;
        set => SetProperty(ref _gameReqGpu, value);
    }

    private bool _hasCompatibilityData;
    public bool HasCompatibilityData
    {
        get => _hasCompatibilityData;
        set => SetProperty(ref _hasCompatibilityData, value);
    }

    // HowLongToBeat Properties
    private string _hltbMainStory = "--";
    public string HltbMainStory
    {
        get => _hltbMainStory;
        set => SetProperty(ref _hltbMainStory, value);
    }

    private string _hltbMainExtra = "--";
    public string HltbMainExtra
    {
        get => _hltbMainExtra;
        set => SetProperty(ref _hltbMainExtra, value);
    }

    private string _hltbCompletionist = "--";
    public string HltbCompletionist
    {
        get => _hltbCompletionist;
        set => SetProperty(ref _hltbCompletionist, value);
    }

    private bool _hasHltbData;
    public bool HasHltbData
    {
        get => _hasHltbData;
        set => SetProperty(ref _hasHltbData, value);
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

    // ── Multi-Import State ──
    private bool _isMultiImporting;
    public bool IsMultiImporting
    {
        get => _isMultiImporting;
        set
        {
            SetProperty(ref _isMultiImporting, value);
            OnPropertyChanged(nameof(HasGameInfo));
        }
    }

    public ObservableCollection<ImportedGameInfo> ImportedGames { get; } = new();

    private string _multiImportSummary = "";
    public string MultiImportSummary
    {
        get => _multiImportSummary;
        set => SetProperty(ref _multiImportSummary, value);
    }

    private bool _multiImportComplete;
    public bool MultiImportComplete
    {
        get => _multiImportComplete;
        set => SetProperty(ref _multiImportComplete, value);
    }

    private bool _isDragOver;
    public bool IsDragOver
    {
        get => _isDragOver;
        set => SetProperty(ref _isDragOver, value);
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
        public ICommand ImportFilesCommand { get; }
        public ICommand DismissMultiImportCommand { get; }

    // --- Methods ---

    private void LoadRecentActivity()
    {
        var history = _settings.Settings.DownloadHistory
            .OrderByDescending(h => h.DownloadDate)
            .Take(5)
            .ToList();

        RecentActivity.Clear();
        foreach (var entry in history)
        {
            RecentActivity.Add(entry);
        }
    }

    /// <summary>
    /// Smart drag-and-drop handler. Detects file types and routes to the appropriate import method.
    /// Single .lua → full game card display (same UX as search+download).
    /// .zip → multi-game import with grid display.
    /// </summary>
    private async Task SmartImportFilesAsync(string[]? files)
    {
        if (files == null || files.Length == 0) return;

        // Determine what we're importing
        var luaFiles = files.Where(f => Path.GetExtension(f).Equals(".lua", StringComparison.OrdinalIgnoreCase)).ToList();
        var zipFiles = files.Where(f => Path.GetExtension(f).Equals(".zip", StringComparison.OrdinalIgnoreCase)).ToList();

        // Single lua file → show as single game card (like a normal download)
        if (luaFiles.Count == 1 && zipFiles.Count == 0)
        {
            await ImportSingleLuaAsync(luaFiles[0]);
            return;
        }

        // Multiple lua files or zip → multi-game import
        if (zipFiles.Count > 0 || luaFiles.Count > 1)
        {
            await ImportMultipleAsync(luaFiles, zipFiles);
            return;
        }

        // Fallback for unsupported files
        HasError = true;
        StatusMessage = "Only .lua and .zip files are supported for import.";
    }

    /// <summary>
    /// Import a single .lua file and display it as a full game card (same UX as downloading a game).
    /// </summary>
    private async Task ImportSingleLuaAsync(string luaPath)
    {
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

        DiscordService.Instance.SetPresence("Importing Lua config...", "Working");

        try
        {
            Progress = 20;
            StatusMessage = "Parsing Lua file...";

            var result = await _importService.SmartImportLuaAsync(
                luaPath,
                onStatus: msg => Application.Current?.Dispatcher.Invoke(() => StatusMessage = msg));

            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                Progress = 60;

                if (result.Success)
                {
                    // Set the app ID input to reflect the imported game
                    _appIdInput = result.AppId;
                    OnPropertyChanged(nameof(AppIdInput));

                    IsComplete = true;
                    GameName = result.GameName;
                    DiscordService.Instance.SetPresence("Exploring the Vault", "Idle");
                    GameImage = result.HeaderImageUrl;

                    // Try to get more details from Steam API
                    try
                    {
                        var gameInfo = await _steamApi.GetAppDetailsAsync(result.AppId);
                        if (gameInfo != null)
                        {
                            GameDescription = gameInfo.ShortDescription;
                            GameType = gameInfo.Type;
                            GameReleaseDate = gameInfo.ReleaseDate;
                            ShowMultiplayerWarning = gameInfo.HasMultiplayerCategories;

                            if (gameInfo.Compatibility != null)
                            {
                                CompatibilityRating = gameInfo.Compatibility.Rating;
                                CompatibilityBadgeColor = gameInfo.Compatibility.BadgeColor;
                                CompatibilityText = gameInfo.Compatibility.RecommendationText;
                                UserCpuName = gameInfo.Compatibility.UserCpuName;
                                UserGpuName = gameInfo.Compatibility.UserGpuName;
                                UserRamText = gameInfo.Compatibility.UserRamText;
                                GameReqRam = gameInfo.Compatibility.GameReqRam;
                                GameReqCpu = gameInfo.Compatibility.GameReqCpu;
                                GameReqGpu = gameInfo.Compatibility.GameReqGpu;
                                HasCompatibilityData = true;
                            }

                            if (gameInfo.HltbData != null && gameInfo.HltbData.HasData)
                            {
                                HltbMainStory = gameInfo.HltbData.MainStoryTime;
                                HltbMainExtra = gameInfo.HltbData.MainExtraTime;
                                HltbCompletionist = gameInfo.HltbData.CompletionistTime;
                                HasHltbData = true;
                            }

                            // Compute estimated size
                            var totalBytes = gameInfo.Depots?.Sum(d => d.SizeBytes) ?? 0;
                            if (totalBytes > 0)
                                EstimatedSize = FormatBytes(totalBytes);
                        }
                    }
                    catch { }

                    _lastLuaFilePath = Path.Combine(
                        _settings.Settings.LuaOutputPath ?? "",
                        $"{result.AppId}.lua");

                    var manifestText = result.ManifestCount > 0 ? $" · {result.ManifestCount} manifest(s)" : "";
                    ResultSummary = $"Imported via drag-and-drop{manifestText}";
                    StatusMessage = $"✓ {result.GameName} imported successfully!";
                    Progress = 100;

                    // Add to download history
                    _settings.Settings.DownloadHistory.Add(new DownloadHistoryEntry
                    {
                        AppId = result.AppId,
                        GameName = result.GameName,
                        HeaderImageUrl = result.HeaderImageUrl,
                        DownloadDate = DateTime.Now,
                        Status = "Imported"
                    });
                    _settings.Save();
                    LoadRecentActivity();
                }
                else
                {
                    HasError = true;
                    StatusMessage = $"✗ Import failed: {result.ErrorMessage}";
                    DiscordService.Instance.SetPresence("Exploring the Vault", "Idle");
                }

                CommandManager.InvalidateRequerySuggested();
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

    /// <summary>
    /// Import multiple .lua files and/or .zip files. Shows the multi-game import UI.
    /// </summary>
    private async Task ImportMultipleAsync(List<string> luaFiles, List<string> zipFiles)
    {
        IsMultiImporting = true;
        MultiImportComplete = false;
        ImportedGames.Clear();
        MultiImportSummary = "Preparing import...";

        DiscordService.Instance.SetPresence("Importing configs...", "Working");

        try
        {
            // Process zip files first
            foreach (var zipFile in zipFiles)
            {
                MultiImportSummary = $"Processing {Path.GetFileName(zipFile)}...";

                await _importService.SmartImportZipAsync(
                    zipFile,
                    onGameDiscovered: game =>
                    {
                        Application.Current?.Dispatcher.Invoke(() =>
                        {
                            ImportedGames.Add(game);
                            MultiImportSummary = $"Importing {ImportedGames.Count} game(s)...";
                        });
                    },
                    onStatus: msg => Application.Current?.Dispatcher.Invoke(() => MultiImportSummary = msg));
            }

            // Process individual lua files
            foreach (var luaFile in luaFiles)
            {
                var game = new ImportedGameInfo
                {
                    LuaSourcePath = luaFile,
                    AppId = Path.GetFileNameWithoutExtension(luaFile),
                    Status = "Queued"
                };

                Application.Current?.Dispatcher.Invoke(() =>
                {
                    ImportedGames.Add(game);
                    MultiImportSummary = $"Importing {ImportedGames.Count} game(s)...";
                });

                var result = await _importService.SmartImportLuaAsync(
                    luaFile,
                    onStatus: msg => Application.Current?.Dispatcher.Invoke(() =>
                    {
                        game.Status = "Processing";
                    }));

                Application.Current?.Dispatcher.Invoke(() =>
                {
                    game.GameName = result.GameName;
                    game.HeaderImageUrl = result.HeaderImageUrl;
                    game.AppId = result.AppId;
                    game.ManifestCount = result.ManifestCount;
                    game.Success = result.Success;
                    game.Status = result.Status;
                    game.ErrorMessage = result.ErrorMessage;
                });
            }

            // Final summary
            var successCount = ImportedGames.Count(g => g.Success);
            var failCount = ImportedGames.Count(g => !g.Success && g.Status == "Failed");
            var totalManifests = ImportedGames.Sum(g => g.ManifestCount);

            Application.Current?.Dispatcher.Invoke(() =>
            {
                var parts = new List<string>();
                parts.Add($"{successCount} game(s) imported");
                if (totalManifests > 0) parts.Add($"{totalManifests} manifest(s)");
                if (failCount > 0) parts.Add($"{failCount} failed");

                MultiImportSummary = string.Join(" · ", parts);
                MultiImportComplete = true;

                // Add all successful imports to download history
                foreach (var game in ImportedGames.Where(g => g.Success))
                {
                    _settings.Settings.DownloadHistory.Add(new DownloadHistoryEntry
                    {
                        AppId = game.AppId,
                        GameName = game.GameName,
                        HeaderImageUrl = game.HeaderImageUrl,
                        DownloadDate = DateTime.Now,
                        Status = "Imported"
                    });
                }
                _settings.Save();
                LoadRecentActivity();
            });

            DiscordService.Instance.SetPresence("Exploring the Vault", "Idle");
        }
        catch (Exception ex)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                MultiImportSummary = $"Import error: {ex.Message}";
                MultiImportComplete = true;
            });
        }
    }

    private void DismissMultiImport()
    {
        IsMultiImporting = false;
        ImportedGames.Clear();
        MultiImportSummary = "";
        MultiImportComplete = false;
        _ = LoadFeaturedGamesAsync();
    }

    // --- Search ---
    private void TriggerSearch(string query)
    {
        _debounceCts?.Cancel();

        if (string.IsNullOrWhiteSpace(query) || query.Length < 1)
        {
            ShowSearchResults = false;
            SearchResults.Clear();
            return;
        }

        var cts = new CancellationTokenSource();
        _debounceCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, cts.Token);
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    await PerformSearchAsync(query);
                });
            }
            catch (OperationCanceledException) { }
        });
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
        var dialog = new SteamVault.Views.ModernDialogWindow(
            "Do you want to include all available DLCs in this config?", 
            "Include DLCs?");
        var dlcChoice = dialog.ShowDialog();
        
        bool includeDlcs = dlcChoice == true;

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

        DiscordService.Instance.SetPresence($"Preparing to download...", "Working");

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
                    DiscordService.Instance.SetPresence($"Exploring the Vault", "Idle");
                    GameImage = result.Game?.HeaderImageUrl;
                    GameDescription = result.Game?.ShortDescription;
                    GameType = result.Game?.Type;
                    GameReleaseDate = result.Game?.ReleaseDate;
                    _lastLuaFilePath = result.LuaFilePath;
                    ShowMultiplayerWarning = result.Game?.HasMultiplayerCategories ?? false;

                    if (result.Game?.Compatibility != null)
                    {
                        CompatibilityRating = result.Game.Compatibility.Rating;
                        CompatibilityBadgeColor = result.Game.Compatibility.BadgeColor;
                        CompatibilityText = result.Game.Compatibility.RecommendationText;
                        UserCpuName = result.Game.Compatibility.UserCpuName;
                        UserGpuName = result.Game.Compatibility.UserGpuName;
                        UserRamText = result.Game.Compatibility.UserRamText;
                        GameReqRam = result.Game.Compatibility.GameReqRam;
                        GameReqCpu = result.Game.Compatibility.GameReqCpu;
                        GameReqGpu = result.Game.Compatibility.GameReqGpu;
                        HasCompatibilityData = true;
                    }

                    if (result.Game?.HltbData != null && result.Game.HltbData.HasData)
                    {
                        HltbMainStory = result.Game.HltbData.MainStoryTime;
                        HltbMainExtra = result.Game.HltbData.MainExtraTime;
                        HltbCompletionist = result.Game.HltbData.CompletionistTime;
                        HasHltbData = true;
                    }

                    // Add to search history
                    if (!string.IsNullOrWhiteSpace(GameName))
                    {
                        if (SearchHistoryList.Contains(GameName))
                            SearchHistoryList.Remove(GameName);
                        SearchHistoryList.Insert(0, GameName);
                        
                        while (SearchHistoryList.Count > 10)
                            SearchHistoryList.RemoveAt(SearchHistoryList.Count - 1);

                        _settings.Settings.SearchHistory = SearchHistoryList.ToList();
                        _settings.Save();
                    }

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
                    
                    // Update recent activity
                    LoadRecentActivity();
                }
                else
                {
                    HasError = true;
                    StatusMessage = $"✗ {result.Error}";
                    DiscordService.Instance.SetPresence($"Exploring the Vault", "Idle");
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

                        var reviewParts = new List<string>();
                        if (stats.MetacriticScore > 0) reviewParts.Add($"Metacritic: {stats.MetacriticScore}");
                        if (stats.PositiveReviewPercent > 0) reviewParts.Add($"{stats.PositiveReviewPercent}% positive");
                        ReviewScoreText = string.Join(" · ", reviewParts);

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

            // Filter to up to 4 newest
            var recentEntries = new List<LibraryEntry>();
            foreach (var e in entries.OrderByDescending(e => e.LastUpdated))
            {
                recentEntries.Add(e);
                if (recentEntries.Count >= 4)
                    break;
            }

            if (recentEntries.Count == 0) return;

            // Add entries to UI immediately with placeholder data
            foreach (var entry in recentEntries)
            {
                FeaturedGames.Add(new FeaturedGameEntry
                {
                    AppId = entry.AppId,
                    Name = entry.Name,
                    HeaderImageUrl = entry.HeaderImageUrl,
                    IsInstalled = _gameMgmt.IsGameInstalled(entry.AppId),
                });
            }
            HasFeaturedGames = FeaturedGames.Count > 0;

            // Fire API calls sequentially
            foreach (var entry in recentEntries)
            {
                var gameEntry = FeaturedGames.First(g => g.AppId == entry.AppId);
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
                catch { }
            }
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
        IsMultiImporting = false;
        ImportedGames.Clear();
        MultiImportComplete = false;
        CommandManager.InvalidateRequerySuggested();
        _ = LoadFeaturedGamesAsync();
    }

    private void ToggleAutoUpdate()
    {
        _settings.Settings.AutoUpdateEnabled = false;
        _settings.Save();
        OnPropertyChanged(nameof(AutoUpdateText));
    }

    public string AutoUpdateText => "Auto-update: Disabled (Maintenance)";

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