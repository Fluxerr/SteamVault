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
    private CancellationTokenSource? _searchCts;
    private System.Timers.Timer? _searchDebounce;

    public DashboardViewModel(DownloadService downloadService, GameSearchService searchService)
    {
        _downloadService = downloadService;
        _searchService = searchService;

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

    private string? _resultSummary;
    public string? ResultSummary
    {
        get => _resultSummary;
        set => SetProperty(ref _resultSummary, value);
    }

    public bool HasGameInfo => !string.IsNullOrWhiteSpace(_gameName);

    private string? _lastLuaFilePath;

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

        var appId = AppIdInput.Trim();

        try
        {
            var result = await _downloadService.DownloadGameAsync(
                appId,
                onStatus: msg => Application.Current?.Dispatcher.Invoke(() => StatusMessage = msg),
                onProgress: pct => Application.Current?.Dispatcher.Invoke(() => Progress = pct));

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
}
