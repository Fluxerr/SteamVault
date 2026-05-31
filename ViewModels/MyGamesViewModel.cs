using SteamVault.Models;
using SteamVault.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Application = System.Windows.Application;

namespace SteamVault.ViewModels;

public class MyGamesViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly SteamApiService _steamApi;
    private readonly DepotKeyService _depotKeyService;
    private readonly LuaParserService _luaParser;
    private readonly DownloadService _downloadService;
    private readonly List<LibraryEntry> _allGames = new();

    public MyGamesViewModel(
        SettingsService settings,
        SteamApiService steamApi,
        DepotKeyService depotKeyService,
        LuaParserService luaParser,
        DownloadService downloadService)
    {
        _settings = settings;
        _steamApi = steamApi;
        _depotKeyService = depotKeyService;
        _luaParser = luaParser;
        _downloadService = downloadService;

        RefreshCommand = new RelayCommand(async _ => await RefreshAsync(), _ => !IsScanning && !IsUpdatingAll);
        UpdateGameCommand = new RelayCommand(async param => await UpdateGameAsync(param as LibraryEntry),
            param => param is LibraryEntry entry && entry.CanUpdate && !IsUpdatingAll);
        UpdateAllCommand = new RelayCommand(async _ => await UpdateAllGamesAsync(), _ => CanUpdateAll && !IsUpdatingAll && !IsScanning);
        OpenGameFolderCommand = new RelayCommand(param => OpenGameFolder(param as LibraryEntry));
    }

    public ObservableCollection<LibraryEntry> Games { get; } = new();

    private string _searchQuery = "";
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                ApplyFilter();
            }
        }
    }

    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        set
        {
            SetProperty(ref _isScanning, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private bool _isUpdatingAll;
    public bool IsUpdatingAll
    {
        get => _isUpdatingAll;
        set
        {
            SetProperty(ref _isUpdatingAll, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _statusMessage = "Click Refresh to scan your Lua folder.";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    private bool _hasNoGames;
    public bool HasNoGames
    {
        get => _hasNoGames;
        set
        {
            SetProperty(ref _hasNoGames, value);
            OnPropertyChanged(nameof(HasGames));
        }
    }

    public bool HasGames => !HasNoGames;

    private bool _canUpdateAll;
    public bool CanUpdateAll
    {
        get => _canUpdateAll;
        set => SetProperty(ref _canUpdateAll, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand UpdateGameCommand { get; }
    public ICommand UpdateAllCommand { get; }
    public ICommand OpenGameFolderCommand { get; }

    public async Task RefreshAsync()
    {
        IsScanning = true;
        StatusMessage = "Scanning Lua folder...";
        _allGames.Clear();
        Games.Clear();
        HasNoGames = false;
        CanUpdateAll = false;

        try
        {
            var luaPath = _settings.Settings.LuaOutputPath;
            if (string.IsNullOrWhiteSpace(luaPath) || !Directory.Exists(luaPath))
            {
                StatusMessage = "Lua output folder not found. Check Settings.";
                HasNoGames = true;
                IsScanning = false;
                return;
            }

            // Ensure depot key database is loaded
            if (!_depotKeyService.IsLoaded)
                await _depotKeyService.LoadAsync();

            // Step 1: Scan for .lua files
            var localEntries = _luaParser.ScanLuaFolder(luaPath);

            if (localEntries.Count == 0)
            {
                StatusMessage = "No .lua files found in the Lua folder. Download some games first!";
                HasNoGames = true;
                IsScanning = false;
                return;
            }

            // Show cards IMMEDIATELY with "Checking..." status
            foreach (var entry in localEntries)
            {
                entry.Status = "Checking...";
                _allGames.Add(entry);
            }

            ApplyFilter();
            StatusMessage = $"Found {localEntries.Count} game(s). Fetching details...";
            HasNoGames = _allGames.Count == 0;

            // Parallelize API calls with throttling
            var semaphore = new SemaphoreSlim(5); // max 5 concurrent
            var completed = 0;
            var needsUpdateCount = 0;
            var upToDateCount = 0;
            var lockObj = new object();

            var tasks = _allGames.Select(async entry =>
            {
                await semaphore.WaitAsync();
                try
                {
                    // Fetch game name/image from Steam Store API
                    var gameInfo = await _steamApi.GetAppDetailsAsync(entry.AppId);
                    if (gameInfo != null)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            entry.Name = gameInfo.Name;
                            entry.HeaderImageUrl = gameInfo.HeaderImageUrl;
                        });
                    }

                    // Check for updates by comparing manifest IDs
                    await CheckForUpdatesAsync(entry);

                    lock (lockObj)
                    {
                        if (entry.Status == "Up to Date") upToDateCount++;
                        else if (entry.Status == "Update Available") needsUpdateCount++;

                        completed++;
                        StatusMessage = $"{localEntries.Count} games · {upToDateCount} up to date · {needsUpdateCount} need update" +
                                        (completed < localEntries.Count ? $" (fetching {completed}/{localEntries.Count})" : "");
                        
                        CanUpdateAll = needsUpdateCount > 0;
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ApplyFilter()
    {
        var currentQuery = SearchQuery.Trim();
        if (string.IsNullOrEmpty(currentQuery))
        {
            Games.Clear();
            foreach (var g in _allGames)
                Games.Add(g);
            return;
        }

        // Apply fuzzy matching based on AppID or Name
        var scoredList = new List<(LibraryEntry Entry, double Score)>();
        foreach (var entry in _allGames)
        {
            double score = 0;
            if (entry.AppId.Contains(currentQuery))
            {
                score = 100;
            }
            else
            {
                score = GameSearchService.CalculateFuzzyScore(currentQuery, entry.Name);
            }

            if (score > 15) // Keep matches with reasonable score
            {
                scoredList.Add((entry, score));
            }
        }

        Games.Clear();
        foreach (var item in scoredList.OrderByDescending(x => x.Score))
        {
            Games.Add(item.Entry);
        }
    }

    private async Task CheckForUpdatesAsync(LibraryEntry entry)
    {
        if (entry.Depots.Count == 0)
        {
            Application.Current.Dispatcher.Invoke(() => entry.Status = "No depots found");
            return;
        }

        try
        {
            var latestDepots = await _steamApi.GetDepotsFromSteamCmdAsync(entry.AppId);

            if (latestDepots.Count == 0)
            {
                Application.Current.Dispatcher.Invoke(() => entry.Status = "Up to Date");
                return;
            }

            bool anyOutdated = false;

            foreach (var localDepot in entry.Depots)
            {
                if (string.IsNullOrWhiteSpace(localDepot.ManifestId))
                    continue;

                var latest = latestDepots.FirstOrDefault(d => d.DepotId == localDepot.DepotId);
                if (latest == null)
                    continue;

                if (!string.IsNullOrWhiteSpace(latest.ManifestId) &&
                    !string.Equals(localDepot.ManifestId, latest.ManifestId, StringComparison.OrdinalIgnoreCase))
                {
                    anyOutdated = true;
                }
            }

            foreach (var latest in latestDepots)
            {
                if (string.IsNullOrWhiteSpace(latest.ManifestId))
                    continue;

                if (!entry.Depots.Any(d => d.DepotId == latest.DepotId))
                {
                    anyOutdated = true;
                }
            }

            Application.Current.Dispatcher.Invoke(() =>
                entry.Status = anyOutdated ? "Update Available" : "Up to Date");
        }
        catch
        {
            Application.Current.Dispatcher.Invoke(() => entry.Status = "Up to Date");
        }
    }

    private async Task UpdateGameAsync(LibraryEntry? entry)
    {
        if (entry == null || entry.IsUpdating) return;

        entry.IsUpdating = true;
        entry.UpdateProgress = 0;
        CommandManager.InvalidateRequerySuggested();

        try
        {
            StatusMessage = $"Updating {entry.Name}...";

            var result = await _downloadService.DownloadGameAsync(
                entry.AppId,
                onStatus: msg => Application.Current?.Dispatcher.Invoke(() =>
                {
                    StatusMessage = msg;
                }),
                onProgress: pct => Application.Current?.Dispatcher.Invoke(() =>
                {
                    entry.UpdateProgress = pct;
                }));

            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (result.Success)
                {
                    entry.Status = "Up to Date";
                    entry.LastUpdated = DateTime.Now;
                    StatusMessage = $"✓ {entry.Name} updated successfully!";
                }
                else
                {
                    StatusMessage = $"✗ Failed to update {entry.Name}: {result.Error}";
                }
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Update error: {ex.Message}";
        }
        finally
        {
            entry.IsUpdating = false;
            entry.UpdateProgress = 100;
            
            // Check if there are still games to update
            CanUpdateAll = _allGames.Any(g => g.Status == "Update Available");

            Application.Current?.Dispatcher.Invoke(() =>
            {
                CommandManager.InvalidateRequerySuggested();
            });
        }
    }

    private async Task UpdateAllGamesAsync()
    {
        var gamesToUpdate = _allGames.Where(g => g.Status == "Update Available" && !g.IsUpdating).ToList();
        if (gamesToUpdate.Count == 0) return;

        IsUpdatingAll = true;
        StatusMessage = $"Starting batch update for {gamesToUpdate.Count} game(s)...";

        try
        {
            for (int i = 0; i < gamesToUpdate.Count; i++)
            {
                var game = gamesToUpdate[i];
                StatusMessage = $"[Batch {i + 1}/{gamesToUpdate.Count}] Updating {game.Name}...";
                await UpdateGameAsync(game);
            }
            StatusMessage = "✓ All outdated games updated successfully!";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Batch update error: {ex.Message}";
        }
        finally
        {
            IsUpdatingAll = false;
            CanUpdateAll = _allGames.Any(g => g.Status == "Update Available");
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void OpenGameFolder(LibraryEntry? entry)
    {
        if (entry == null) return;
        var dir = Path.GetDirectoryName(entry.LuaFilePath);
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