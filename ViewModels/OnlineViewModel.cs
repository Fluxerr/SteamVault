using SteamVault.Models;
using SteamVault.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Application = System.Windows.Application;

namespace SteamVault.ViewModels;

/// <summary>
/// Online tab — shows games with online-fix.me multiplayer fix integration.
/// Parses appmanifest files to find game directories.
/// User downloads RAR from online-fix.me via browser into the app folder;
/// the app auto-detects the RAR and extracts it into the game folder.
/// </summary>
public class OnlineViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly SteamApiService _steamApi;
    private readonly LuaParserService _luaParser;
    private readonly OnlineFixService _onlineFix;
    private readonly List<OnlineGameEntry> _allGames = new();

    public OnlineViewModel(
        SettingsService settings,
        SteamApiService steamApi,
        LuaParserService luaParser,
        OnlineFixService onlineFix)
    {
        _settings = settings;
        _steamApi = steamApi;
        _luaParser = luaParser;
        _onlineFix = onlineFix;

        RefreshCommand = new RelayCommand(async _ => await RefreshAsync(), _ => !IsScanning && !IsBusy);
        SelectGameCommand = new RelayCommand(param => SelectGame(param as OnlineGameEntry));
        BackToGridCommand = new RelayCommand(_ => SelectGame(null));
        OpenGameDirCommand = new RelayCommand(param => OpenGameDir(param as OnlineGameEntry));
        OpenOnlineFixCommand = new RelayCommand(param => OpenOnlineFixBrowser(param as OnlineGameEntry));
        ApplyFixCommand = new RelayCommand(param => StartFixProcess(param as OnlineGameEntry),
            param => param is OnlineGameEntry e && e.IsInstalled && !e.IsApplying && !IsBusy && !NeedsAdminRights);
        RestartAsAdminCommand = new RelayCommand(_ => RestartAsAdmin());
    }

    public ObservableCollection<OnlineGameEntry> Games { get; } = new();

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

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            SetProperty(ref _isBusy, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _statusMessage = "Click Refresh to scan installed games.";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    // --- Detail View Properties ---
    private bool _showGameDetail;
    public bool ShowGameDetail
    {
        get => _showGameDetail;
        set { SetProperty(ref _showGameDetail, value); OnPropertyChanged(nameof(ShowGameGrid)); }
    }
    public bool ShowGameGrid => !ShowGameDetail;

    private OnlineGameEntry? _selectedGame;
    public OnlineGameEntry? SelectedGame
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

    private bool _hasNoGames;
    public bool HasNoGames
    {
        get => _hasNoGames;
        set => SetProperty(ref _hasNoGames, value);
    }

    private double _downloadProgress;
    public double DownloadProgress
    {
        get => _downloadProgress;
        set => SetProperty(ref _downloadProgress, value);
    }

    private bool _needsAdminRights;
    public bool NeedsAdminRights
    {
        get => _needsAdminRights;
        set
        {
            SetProperty(ref _needsAdminRights, value);
            OnPropertyChanged(nameof(ShowAdminButton));
            OnPropertyChanged(nameof(ShowNormalControls));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool ShowAdminButton => NeedsAdminRights;
    public bool ShowNormalControls => !NeedsAdminRights;

    public ICommand RefreshCommand { get; }
    public ICommand SelectGameCommand { get; }
    public ICommand BackToGridCommand { get; }
    public ICommand OpenGameDirCommand { get; }
    public ICommand OpenOnlineFixCommand { get; }
    public ICommand ApplyFixCommand { get; }
    public ICommand RestartAsAdminCommand { get; }

    public async Task RefreshAsync()
    {
        IsScanning = true;
        StatusMessage = "Scanning Lua directory...";
        ShowGameDetail = false;
        _allGames.Clear();
        Games.Clear();
        HasNoGames = false;
        NeedsAdminRights = false;

        try
        {
            var luaPath = _settings.Settings.LuaOutputPath;
            if (string.IsNullOrWhiteSpace(luaPath) || !Directory.Exists(luaPath))
            {
                StatusMessage = "Lua folder not found. Download some games first!";
                HasNoGames = true;
                IsScanning = false;
                return;
            }

            var luaEntries = _luaParser.ScanLuaFolder(luaPath);
            if (luaEntries.Count == 0)
            {
                StatusMessage = "No games found. Download some Lua configs first!";
                HasNoGames = true;
                IsScanning = false;
                return;
            }

            foreach (var entry in luaEntries)
            {
                var gameEntry = new OnlineGameEntry
                {
                    AppId = entry.AppId,
                    Name = entry.Name,
                    Status = "Checking..."
                };
                _allGames.Add(gameEntry);
            }

            RefreshGamesList();
            StatusMessage = $"Found {luaEntries.Count} game(s) — checking categories & install status...";

            var semaphore = new SemaphoreSlim(5);
            var completed = 0;
            var installedCount = 0;
            var multiplayerCount = 0;
            var lockObj = new object();

            var tasks = _allGames.Select(async entry =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var gameInfo = await _steamApi.GetAppDetailsAsync(entry.AppId);
                    if (gameInfo != null)
                    {
                        var hasMultiplayer = gameInfo.HasMultiplayerCategories;

                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            entry.Name = gameInfo.Name;
                            entry.HeaderImageUrl = gameInfo.HeaderImageUrl;
                            entry.IsMultiplayer = hasMultiplayer;
                        });

                        if (!hasMultiplayer)
                        {
                            lock (lockObj) { completed++; }
                            return;
                        }
                    }
                    else
                    {
                        lock (lockObj) { completed++; }
                        return;
                    }

                    entry.IsInstalled = _onlineFix.IsGameInstalled(entry.AppId);
                    entry.InstallPath = _onlineFix.GetGameInstallPath(entry.AppId);
                    entry.FixApplied = _onlineFix.IsFixApplied(entry.AppId);

                    if (entry.IsInstalled)
                        entry.Status = entry.FixApplied ? "✅ Fix Applied" : "✅ Installed";
                    else
                        entry.Status = "⚠️ Not Installed";

                    lock (lockObj)
                    {
                        if (entry.IsInstalled) installedCount++;
                        multiplayerCount++;
                        completed++;
                        StatusMessage = $"{luaEntries.Count} games · {installedCount} installed · {completed}/{luaEntries.Count} checked";
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            _allGames.RemoveAll(g => !g.IsMultiplayer);

            RefreshGamesList();
            HasNoGames = _allGames.Count == 0;
            StatusMessage = HasNoGames
                ? "No online-capable games found in your library."
                : $"{_allGames.Count} online-capable game(s) · {installedCount} installed · {_allGames.Count - installedCount} not installed";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void RefreshGamesList()
    {
        Games.Clear();
        foreach (var g in _allGames.OrderBy(g => g.IsInstalled ? 0 : 1).ThenBy(g => g.Name))
            Games.Add(g);
    }

    private void SelectGame(OnlineGameEntry? entry)
    {
        _onlineFix.StopWatching();
        NeedsAdminRights = false;

        if (entry == null)
        {
            ShowGameDetail = false;
            SelectedGame = null;
            return;
        }

        SelectedGame = entry;
        ShowGameDetail = true;

        if (entry.IsInstalled)
        {
            DetailStatusMessage = entry.FixApplied
                ? "Fix has been applied. You can re-apply if needed."
                : "Press 'Apply Fix' to start — then download the .rar from online-fix.me and save it to the app folder.";
        }
        else
        {
            DetailStatusMessage = "Game is not installed. Install it via Steam first, then come back.";
        }
    }

    private void OpenGameDir(OnlineGameEntry? entry)
    {
        if (entry == null) return;
        _onlineFix.OpenGameDirectory(entry.AppId);
    }

    private void OpenOnlineFixBrowser(OnlineGameEntry? entry)
    {
        if (entry == null) return;
        _onlineFix.SearchOnlineFix(entry.Name);
    }

    /// <summary>
    /// Starts the fix workflow:
    /// 1. Opens online-fix.me search in browser
    /// 2. Opens app folder for the user
    /// 3. Starts watching for RAR files
    /// 4. Auto-extracts when a RAR is detected
    /// </summary>
    private void StartFixProcess(OnlineGameEntry? entry)
    {
        if (entry == null || !entry.IsInstalled || IsBusy) return;

        IsBusy = true;
        entry.IsApplying = true;
        DownloadProgress = 0;
        NeedsAdminRights = false;
        CommandManager.InvalidateRequerySuggested();

        var appDir = OnlineFixService.GetAppDirectory();
        DetailStatusMessage = $"1. Find your game on online-fix.me\n2. Download the .rar\n3. Save it to:\n{appDir}\n\nThe app will auto-detect and extract it.";

        // Open the search page in browser
        _onlineFix.SearchOnlineFix(entry.Name);

        // Start watching for RAR files
        _onlineFix.StartWatching(async rarPath =>
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                DetailStatusMessage = $"Found: {Path.GetFileName(rarPath)}\nExtracting...";
            });

            var gameDir = _onlineFix.GetGameInstallPath(entry.AppId);
            if (string.IsNullOrWhiteSpace(gameDir))
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    DetailStatusMessage = $"✗ Game directory not found for {entry.Name}.";
                    FinishFixProcess(entry, OnlineFixService.ExtractResult.Failed);
                });
                return;
            }

            var (result, error) = _onlineFix.ExtractFixToGameDir(rarPath, gameDir,
                onStatus: msg => Application.Current?.Dispatcher.Invoke(() => DetailStatusMessage = msg));

            Application.Current.Dispatcher.Invoke(() =>
            {
                if (result == OnlineFixService.ExtractResult.Success)
                {
                    entry.FixApplied = true;
                    entry.Status = "✅ Fix Applied";
                    DownloadProgress = 100;
                    DetailStatusMessage = $"✓ Multiplayer fix applied to {entry.Name}!";
                }
                else if (result == OnlineFixService.ExtractResult.PermissionDenied)
                {
                    DetailStatusMessage = error;
                    NeedsAdminRights = true;
                }
                else
                {
                    DetailStatusMessage = error ?? "✗ Failed to extract fix.";
                }
                FinishFixProcess(entry, result);
            });
        });
    }

    private void FinishFixProcess(OnlineGameEntry entry, OnlineFixService.ExtractResult result)
    {
        _onlineFix.StopWatching();
        entry.IsApplying = false;
        IsBusy = false;
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Restarts the application with administrator privileges.
    /// </summary>
    private void RestartAsAdmin()
    {
        try
        {
            var exePath = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
            var startInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas" // Triggers UAC prompt
            };
            Process.Start(startInfo);

            // Close the current instance
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            DetailStatusMessage = $"✗ Failed to restart as admin: {ex.Message}";
        }
    }
}

public class OnlineGameEntry : INotifyPropertyChanged
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

    private string _status = "Checking...";
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public bool IsInstalled { get; set; }
    public string? InstallPath { get; set; }
    public bool FixApplied { get; set; }
    public bool IsMultiplayer { get; set; }

    private bool _isApplying;
    public bool IsApplying
    {
        get => _isApplying;
        set { _isApplying = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanApplyFix)); }
    }

    public bool CanApplyFix => IsInstalled && !IsApplying && !FixApplied;

    private double _applyProgress;
    public double ApplyProgress
    {
        get => _applyProgress;
        set { _applyProgress = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}