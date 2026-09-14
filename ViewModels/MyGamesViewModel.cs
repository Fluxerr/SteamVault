using SteamVault.Models;
using SteamVault.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
        DeleteGameCommand = new RelayCommand(param => DeleteGame(param as LibraryEntry), _ => ShowGameDetail && !IsDeleting);
        LaunchGameCommand = new RelayCommand(param => LaunchGame(param as LibraryEntry), _ => ShowGameDetail && _selectedGame?.IsInstalled == true);
        OpenInstallFolderCommand = new RelayCommand(param => OpenInstallFolder(param as LibraryEntry), _ => ShowGameDetail && _selectedGame?.IsInstalled == true);
        OpenLuaFolderCommand = new RelayCommand(param => OpenLuaFolder(param as LibraryEntry));

        ToggleExportModeCommand = new RelayCommand(_ => IsExportMode = !IsExportMode);
        SetViewModeCommand = new RelayCommand(param => { if (param is string v) ViewMode = v; });
        SetSortModeCommand = new RelayCommand(param => { if (param is string s) SortMode = s; });
        ExportSelectedCommand = new RelayCommand(async _ => await ExportSelectedGamesAsync(), _ => IsExportMode && _allGames.Any(g => g.IsSelectedForExport));
        ExportSingleGameCommand = new RelayCommand(async param => 
        {
            if (param is LibraryEntry entry)
            {
                entry.IsSelectedForExport = true;
                await ExportSelectedGamesAsync();
            }
        });

        RestoreManifestsCommand = new RelayCommand(param => RestoreManifests(param as LibraryEntry ?? SelectedGame));
        RestoreAllManifestsCommand = new RelayCommand(_ => RestoreAllManifests());
    }

    public ICommand RestoreManifestsCommand { get; }
    public ICommand RestoreAllManifestsCommand { get; }

    public ObservableCollection<LibraryEntry> Games { get; } = new();

    private string _searchQuery = "";
    public string SearchQuery
    {
        get => _searchQuery;
        set { if (SetProperty(ref _searchQuery, value)) ApplyFilter(); }
    }

    private string _sortMode = "Name";
    public string SortMode
    {
        get => _sortMode;
        set { if (SetProperty(ref _sortMode, value)) ApplyFilter(); }
    }

    private string _viewMode = "Grid";
    public string ViewMode
    {
        get => _viewMode;
        set => SetProperty(ref _viewMode, value);
    }

    private bool _isExportMode;
    public bool IsExportMode
    {
        get => _isExportMode;
        set
        {
            SetProperty(ref _isExportMode, value);
            if (!value) foreach (var g in _allGames) g.IsSelectedForExport = false;
            UpdateExportCount();
        }
    }

    private int _exportSelectedCount;
    public int ExportSelectedCount
    {
        get => _exportSelectedCount;
        set => SetProperty(ref _exportSelectedCount, value);
    }

    public void UpdateExportCount()
    {
        ExportSelectedCount = _allGames.Count(g => g.IsSelectedForExport);
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnLibraryEntryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryEntry.IsSelectedForExport))
        {
            UpdateExportCount();
        }
    }

    public ICommand ToggleExportModeCommand { get; }
    public ICommand ExportSelectedCommand { get; }
    public ICommand ExportSingleGameCommand { get; }
    public ICommand SetViewModeCommand { get; }
    public ICommand SetSortModeCommand { get; }

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
            foreach (var g in _allGames)
            {
                g.PropertyChanged -= OnLibraryEntryPropertyChanged;
            }
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
                    entry.PropertyChanged += OnLibraryEntryPropertyChanged;
                    _allGames.Add(entry);
                }

                ApplyFilter();
                UpdateExportCount();
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
        Games.Clear();
        IEnumerable<LibraryEntry> query = _allGames;

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var sq = SearchQuery.ToLowerInvariant();
            query = query.Where(g => g.Name.ToLowerInvariant().Contains(sq) || g.AppId.Contains(sq));
        }

        query = SortMode switch
        {
            "LastUpdated" => query.OrderByDescending(g => g.LastUpdated),
            "Size" => query.OrderByDescending(g => g.Depots.Sum(d => d.FileSize)),
            _ => query.OrderBy(g => g.Name)
        };

        foreach (var game in query)
        {
            Games.Add(game);
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
                if (string.IsNullOrWhiteSpace(localDepot.ManifestId)) continue;
                var latest = latestDepots.FirstOrDefault(d => d.DepotId == localDepot.DepotId);
                if (latest == null) continue;
                if (!string.IsNullOrWhiteSpace(latest.ManifestId) &&
                    !string.Equals(localDepot.ManifestId, latest.ManifestId, StringComparison.OrdinalIgnoreCase))
                { anyOutdated = true; }
            }
            foreach (var latest in latestDepots)
            {
                if (string.IsNullOrWhiteSpace(latest.ManifestId) || string.IsNullOrWhiteSpace(latest.DecryptionKey)) continue;
                if (!entry.Depots.Any(d => d.DepotId == latest.DepotId)) { anyOutdated = true; }
            }

            Application.Current.Dispatcher.Invoke(() =>
                entry.Status = anyOutdated ? "Update Available" : "Up to Date");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Update check failed for {entry.AppId}: {ex.Message}");
            Application.Current.Dispatcher.Invoke(() => entry.Status = "Check Failed");
        }
    }

    private async Task UpdateGameAsync(LibraryEntry? entry, bool? includeDlcsParam = null)
    {
        if (entry == null || entry.IsUpdating) return;

        // Ask user if they want to include DLCs
        bool includeDlcs = false;
        if (includeDlcsParam.HasValue)
        {
            includeDlcs = includeDlcsParam.Value;
        }
        else
        {
            var dialog = new SteamVault.Views.ModernDialogWindow(
                "Do you want to include all available DLCs in this config?", 
                "Include DLCs?");
            dialog.Owner = System.Windows.Application.Current.MainWindow;
            
            var blur = new System.Windows.Media.Effects.BlurEffect { Radius = 8 };
            System.Windows.Application.Current.MainWindow.Effect = blur;
            
            var dlcChoice = dialog.ShowDialog();
            
            System.Windows.Application.Current.MainWindow.Effect = null;
            
            includeDlcs = dlcChoice == true;
        }

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
        // Prompt once for all games
        var dialog = new SteamVault.Views.ModernDialogWindow(
            "Do you want to include all available DLCs?", 
            "Include DLCs?");
        dialog.Owner = System.Windows.Application.Current.MainWindow;

        var blur = new System.Windows.Media.Effects.BlurEffect { Radius = 8 };
        System.Windows.Application.Current.MainWindow.Effect = blur;

        var dlcChoice = dialog.ShowDialog();
        
        System.Windows.Application.Current.MainWindow.Effect = null;

        bool includeDlcs = dlcChoice == true;

        try
        {
            var semaphore = new SemaphoreSlim(4); // Run 4 updates concurrently
            var completed = 0;
            var lockObj = new object();

            var tasks = gamesToUpdate.Select(async game =>
            {
                await semaphore.WaitAsync();
                try
                {
                    // UpdateGameAsync will update the individual game's status
                    await UpdateGameAsync(game, includeDlcs);
                    
                    lock (lockObj)
                    {
                        completed++;
                        Application.Current?.Dispatcher.InvokeAsync(() => 
                            StatusMessage = $"[Batch {completed}/{gamesToUpdate.Count}] Updating in parallel...");
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
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

    private void DeleteGame(LibraryEntry? entry)
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
            entry.PropertyChanged -= OnLibraryEntryPropertyChanged;
            _allGames.Remove(entry);
            Games.Remove(entry);
            ShowGameDetail = false;
            UpdateExportCount();

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

    private Task ExportSelectedGamesAsync()
    {
        var toExport = _allGames.Where(g => g.IsSelectedForExport).ToList();
        if (toExport.Count == 0) return Task.CompletedTask;

        StatusMessage = $"Exporting {toExport.Count} configurations...";

        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export Configurations",
                Filter = "ZIP Archive (*.zip)|*.zip",
                FileName = $"SteamVault_Export_{DateTime.Now:yyyyMMdd_HHmmss}.zip",
                DefaultExt = ".zip"
            };

            if (dialog.ShowDialog() != true)
            {
                StatusMessage = "Export cancelled.";
                return Task.CompletedTask;
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "SteamVault_Export_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            int count = 0;
            foreach (var g in toExport)
            {
                if (File.Exists(g.LuaFilePath))
                {
                    string destPath = Path.Combine(tempDir, Path.GetFileName(g.LuaFilePath));
                    File.Copy(g.LuaFilePath, destPath, true);
                    count++;
                }
            }

            if (count > 0)
            {
                if (File.Exists(dialog.FileName)) File.Delete(dialog.FileName);
                ZipFile.CreateFromDirectory(tempDir, dialog.FileName);
                StatusMessage = $"Successfully exported {count} configs to {Path.GetFileName(dialog.FileName)}";
            }
            else
            {
                StatusMessage = "No Lua files found for the selected games.";
            }

            Directory.Delete(tempDir, true);

            foreach (var g in toExport) g.IsSelectedForExport = false;
            IsExportMode = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
        
        return Task.CompletedTask;
    }

    private void RestoreManifests(LibraryEntry? entry)
    {
        if (entry == null) return;
        var count = _downloadService.RestoreManifestsForApp(entry.AppId);
        if (count > 0)
        {
            StatusMessage = $"Restored {count} manifest(s) for '{entry.Name}' into Steam depotcache.";
            System.Windows.MessageBox.Show(
                $"Successfully restored {count} manifest file(s) for '{entry.Name}' to Steam's depotcache directories.\n\nAll files have been set to Read-Only to prevent Steam from deleting them on restart.",
                "Manifests Re-applied", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            StatusMessage = $"No cached manifests found for '{entry.Name}'.";
            System.Windows.MessageBox.Show(
                $"No cached manifests found in local vault for '{entry.Name}'. You can re-download them using the 'Update' button.",
                "No Manifests Cached", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RestoreAllManifests()
    {
        var total = _downloadService.RestoreAllVaultManifests();
        if (total > 0)
        {
            StatusMessage = $"Restored {total} manifest(s) across all games into Steam depotcache.";
            System.Windows.MessageBox.Show(
                $"Successfully restored {total} manifest file(s) across all games into Steam's depotcache directories.\n\nAll files have been locked (Read-Only) to prevent Steam from wiping them on restart.",
                "All Manifests Re-applied", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            StatusMessage = "No cached manifests found in local vault.";
            System.Windows.MessageBox.Show(
                "No cached manifests found in local vault to restore.",
                "No Manifests Cached", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}