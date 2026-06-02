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
    private readonly GameManagementService _gameMgmt;
    private readonly List<LibraryEntry> _allGames = new();

    public MyGamesViewModel(
        SettingsService settings,
        SteamApiService steamApi,
        DepotKeyService depotKeyService,
        LuaParserService luaParser,
        DownloadService downloadService,
        GameManagementService gameMgmt)
    {
        _settings = settings;
        _steamApi = steamApi;
        _depotKeyService = depotKeyService;
        _luaParser = luaParser;
        _downloadService = downloadService;
        _gameMgmt = gameMgmt;

        RefreshCommand = new RelayCommand(async _ => await RefreshAsync(), _ => !IsScanning && !IsUpdatingAll);
        UpdateGameCommand = new RelayCommand(async param => await UpdateGameAsync(param as LibraryEntry),
            param => param is LibraryEntry entry && entry.CanUpdate && !IsUpdatingAll);
        UpdateAllCommand = new RelayCommand(async _ => await UpdateAllGamesAsync(), _ => CanUpdateAll && !IsUpdatingAll && !IsScanning);
        SelectGameCommand = new RelayCommand(param => SelectGame(param as LibraryEntry));
        BackToGridCommand = new RelayCommand(_ => SelectGame(null));
        DeleteGameCommand = new RelayCommand(async param => await DeleteGameAsync(param as LibraryEntry), _ => ShowGameDetail && !IsDeleting);
        LaunchGameCommand = new RelayCommand(param => LaunchGame(param as LibraryEntry), _ => ShowGameDetail && _selectedGame?.IsInstalled == true);
        OpenInstallFolderCommand = new RelayCommand(param => OpenInstallFolder(param as LibraryEntry), _ => ShowGameDetail && _selectedGame?.IsInstalled == true);
        OpenLuaFolderCommand = new RelayCommand(param => OpenLuaFolder(param as LibraryEntry));
    }

    public ObservableCollection<LibraryEntry> Games { get; } = new();

    private string _searchQuery = "";
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
                ApplyFilter();
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

    // --- Detail View Properties ---
    private bool _showGameDetail;
    public bool ShowGameDetail
    {
        get => _showGameDetail;
        set { SetProperty(ref _showGameDetail, value); OnPropertyChanged(nameof(ShowGameGrid)); }
    }
    public bool ShowGameGrid => !ShowGameDetail;

    private LibraryEntry? _selectedGame;
    public LibraryEntry? SelectedGame
    {
        get => _selectedGame;
        set
        {
            SetProperty(ref _selectedGame, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _detailStatusMessage = "";
    public string DetailStatusMessage
    {
        get => _detailStatusMessage;
        set => SetProperty(ref _detailStatusMessage, value);
    }

    private bool _isDeleting;
    public bool IsDeleting
    {
        get => _isDeleting;
        set
        {
            SetProperty(ref _isDeleting, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public ICommand RefreshCommand { get; }
    public ICommand UpdateGameCommand { get; }
    public ICommand UpdateAllCommand { get; }
    public ICommand SelectGameCommand { get; }
    public ICommand BackToGridCommand { get; }
    public ICommand DeleteGameCommand { get; }
    public ICommand LaunchGameCommand { get; }
    public ICommand OpenInstallFolderCommand { get; }
    public ICommand OpenLuaFolderCommand { get; }

    public async Task RefreshAsync()
    {
        IsScanning = true;
        StatusMessage = "Scanning Lua folder...";
        ShowGameDetail = false;
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

            if (!_depotKeyService.IsLoaded)
                await _depotKeyService.LoadAsync();

            var localEntries = _luaParser.ScanLuaFolder(luaPath);

            if (localEntries.Count == 0)
            {
                StatusMessage = "No .lua files found in the Lua folder. Download some games first!";
                HasNoGames = true;
                IsScanning = false;
                return;
            }

            foreach (var entry in localEntries)
            {
                entry.Status = "Checking...";
                _allGames.Add(entry);
            }

            ApplyFilter();
            StatusMessage = $"Found {localEntries.Count} game(s). Fetching details...";
            HasNoGames = _allGames.Count == 0;

            var semaphore = new SemaphoreSlim(5);
            var completed = 0;
            var needsUpdateCount = 0;
            var upToDateCount = 0;
            var installedCount = 0;
            var lockObj = new object();

            var tasks = _allGames.Select(async entry =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var gameInfo = await _steamApi.GetAppDetailsAsync(entry.AppId);
                    if (gameInfo != null)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            entry.Name = gameInfo.Name;
                            entry.HeaderImageUrl = gameInfo.HeaderImageUrl;
                            entry.Description = gameInfo.ShortDescription;
                            entry.Type = gameInfo.Type;
                            entry.ReleaseDate = gameInfo.ReleaseDate;
                        });
                    }

                    entry.IsInstalled = _gameMgmt.IsGameInstalled(entry.AppId);
                    await CheckForUpdatesAsync(entry);

                    lock (lockObj)
                    {
                        if (entry.Status == "Up to Date") upToDateCount++;
                        else if (entry.Status == "Update Available") needsUpdateCount++;
                        if (entry.IsInstalled) installedCount++;
                        completed++;
                        StatusMessage = $"{localEntries.Count} games · {installedCount} installed · {upToDateCount} up to date" +
                                        (completed < localEntries.Count ? $" ({completed}/{localEntries.Count})" : "");
                        CanUpdateAll = needsUpdateCount > 0;
                    }
                }
                finally { semaphore.Release(); }
            });

            await Task.WhenAll(tasks);
            StatusMessage = $"{localEntries.Count} games · {installedCount} installed · {upToDateCount} up to date";
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
            foreach (var g in _allGames) Games.Add(g);
            return;
        }

        var scoredList = new List<(LibraryEntry Entry, double Score)>();
        foreach (var entry in _allGames)
        {
            double score = entry.AppId.Contains(currentQuery) ? 100
                : GameSearchService.CalculateFuzzyScore(currentQuery, entry.Name);
            if (score > 15) scoredList.Add((entry, score));
        }

        Games.Clear();
        foreach (var item in scoredList.OrderByDescending(x => x.Score))
            Games.Add(item.Entry);
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
                if (string.IsNullOrWhiteSpace(localDepot.ManifestId)) continue;
                var latest = latestDepots.FirstOrDefault(d => d.DepotId == localDepot.DepotId);
                if (latest == null) continue;
                if (!string.IsNullOrWhiteSpace(latest.ManifestId) &&
                    !string.Equals(localDepot.ManifestId, latest.ManifestId, StringComparison.OrdinalIgnoreCase))
                { anyOutdated = true; }
            }
            foreach (var latest in latestDepots)
            {
                if (string.IsNullOrWhiteSpace(latest.ManifestId)) continue;
                if (!entry.Depots.Any(d => d.DepotId == latest.DepotId)) { anyOutdated = true; }
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

        // Ask user if they want to include DLCs
        bool includeDlcs = false;
        var dlcChoice = System.Windows.MessageBox.Show(
            "Do you want to include all available DLCs for this game?",
            "Include DLCs?",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        includeDlcs = dlcChoice == System.Windows.MessageBoxResult.Yes;

        entry.IsUpdating = true; entry.UpdateProgress = 0;
        CommandManager.InvalidateRequerySuggested();

        try
        {
            StatusMessage = $"Updating {entry.Name}...";
            var result = await _downloadService.DownloadGameAsync(entry.AppId,
                onStatus: msg => Application.Current?.Dispatcher.Invoke(() => StatusMessage = msg),
                onProgress: pct => Application.Current?.Dispatcher.Invoke(() => entry.UpdateProgress = pct),
                includeDlcs: includeDlcs);

            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (result.Success)
                {
                    entry.Status = "Up to Date";
                    entry.LastUpdated = DateTime.Now;
                    StatusMessage = $"✓ {entry.Name} updated successfully!";
                }
                else StatusMessage = $"✗ Failed to update {entry.Name}: {result.Error}";
            });
        }
        catch (Exception ex) { StatusMessage = $"Update error: {ex.Message}"; }
        finally
        {
            entry.IsUpdating = false; entry.UpdateProgress = 100;
            CanUpdateAll = _allGames.Any(g => g.Status == "Update Available");
            Application.Current?.Dispatcher.Invoke(() => CommandManager.InvalidateRequerySuggested());
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
                StatusMessage = $"[Batch {i + 1}/{gamesToUpdate.Count}] Updating {gamesToUpdate[i].Name}...";
                await UpdateGameAsync(gamesToUpdate[i]);
            }
            StatusMessage = "✓ All outdated games updated successfully!";
        }
        catch (Exception ex) { StatusMessage = $"Batch update error: {ex.Message}"; }
        finally
        {
            IsUpdatingAll = false;
            CanUpdateAll = _allGames.Any(g => g.Status == "Update Available");
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void SelectGame(LibraryEntry? entry)
    {
        if (entry == null)
        {
            ShowGameDetail = false;
            SelectedGame = null;
            return;
        }
        SelectedGame = entry;
        ShowGameDetail = true;
        DetailStatusMessage = entry.IsInstalled
            ? $"Installed at: {_gameMgmt.GetGameInstallPath(entry.AppId)}"
            : "Not installed — use Dashboard to install via Steam first.";
    }

    private async Task DeleteGameAsync(LibraryEntry? entry)
    {
        if (entry == null || IsDeleting) return;
        IsDeleting = true;
        DetailStatusMessage = $"Deleting {entry.Name}...";

        try
        {
            if (entry.IsInstalled)
            {
                DetailStatusMessage = $"Removing game files for {entry.Name}...";
                _gameMgmt.DeleteGameInstallation(entry.AppId);
            }

            DetailStatusMessage = $"Removing Lua config for {entry.Name}...";
            _gameMgmt.DeleteLuaFile(entry.AppId);

            // Remove from lists
            _allGames.Remove(entry);
            Games.Remove(entry);
            ShowGameDetail = false;

            HasNoGames = _allGames.Count == 0;
            StatusMessage = $"✓ {entry.Name} deleted successfully.";
        }
        catch (Exception ex) { DetailStatusMessage = $"✗ Delete failed: {ex.Message}"; }
        finally { IsDeleting = false; CommandManager.InvalidateRequerySuggested(); }
    }

    private void LaunchGame(LibraryEntry? entry)
    {
        if (entry == null) return;
        var success = _gameMgmt.LaunchGame(entry.Name, entry.AppId);
        if (!success)
            DetailStatusMessage = $"✗ Could not find executable for {entry.Name}.";
    }

    private void OpenInstallFolder(LibraryEntry? entry)
    {
        if (entry == null) return;
        var path = _gameMgmt.GetGameInstallPath(entry.AppId);
        if (path != null && Directory.Exists(path))
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void OpenLuaFolder(LibraryEntry? entry)
    {
        if (entry == null) return;
        var dir = Path.GetDirectoryName(entry.LuaFilePath);
        if (dir != null && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }
}