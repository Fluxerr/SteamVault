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
using System.Linq;

namespace SteamVault.ViewModels;

public class OnlineViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly SteamApiService _steamApi;
    private readonly LuaParserService _luaParser;
    private readonly OnlineFixService _onlineFix;
    private readonly LuaToolsApiClient _apiClient;
    private readonly AuthService _authService;
    
    private readonly List<OnlineGameEntry> _allGames = new();
    private List<DenuvoFix> _gameFixes = new();

    public OnlineViewModel(
        SettingsService settings,
        SteamApiService steamApi,
        LuaParserService luaParser,
        OnlineFixService onlineFix,
        LuaToolsApiClient apiClient,
        AuthService authService)
    {
        _settings = settings;
        _steamApi = steamApi;
        _luaParser = luaParser;
        _onlineFix = onlineFix;
        _apiClient = apiClient;
        _authService = authService;

        _authService.AuthStateChanged += () =>
        {
            OnPropertyChanged(nameof(IsSignedIn));
            OnPropertyChanged(nameof(UserDisplayName));
        };

        RefreshCommand = new RelayCommand(async _ => await RefreshAsync(), _ => !IsScanning && !IsBusy);
        SelectGameCommand = new RelayCommand(param => SelectGame(param as OnlineGameEntry));
        BackToGridCommand = new RelayCommand(_ => SelectGame(null));
        OpenGameDirCommand = new RelayCommand(param => OpenGameDir(param as OnlineGameEntry));
        RestartAsAdminCommand = new RelayCommand(_ => RestartAsAdmin());
        
        FilterCommand = new RelayCommand(param => SetFilter(param as string));
        SetViewModeCommand = new RelayCommand(param => { if (param is string v) ViewMode = v; });
        
        DownloadBypassCommand = new RelayCommand(param => DownloadFix(param as DenuvoFix, "manifest"), _ => !IsBusy);
        DownloadOnlineFixCommand = new RelayCommand(param => DownloadFix(param as DenuvoFix, "fix"), _ => !IsBusy);

        OpenSignInModalCommand = new RelayCommand(async _ => await BrowserSignInAsync());
        CloseSignInModalCommand = new RelayCommand(_ => IsSignInModalVisible = false);
        RedeemBotCodeCommand = new RelayCommand(async _ => await RedeemBotCodeAsync(), _ => !IsRedeemingCode);
        BrowserSignInCommand = new RelayCommand(async _ => await BrowserSignInAsync());
        SignOutCommand = new RelayCommand(_ => _authService.SignOut());
    }

    private string _viewMode = "Grid";
    public string ViewMode
    {
        get => _viewMode;
        set => SetProperty(ref _viewMode, value);
    }
    public ICommand SetViewModeCommand { get; }

    public bool IsSignedIn => _authService.IsSignedIn;
    public string UserDisplayName => _authService.DisplayName ?? _authService.Email ?? "Signed In";

    private bool _isSignInModalVisible;
    public bool IsSignInModalVisible
    {
        get => _isSignInModalVisible;
        set => SetProperty(ref _isSignInModalVisible, value);
    }

    private string _botCodeInput = "";
    public string BotCodeInput
    {
        get => _botCodeInput;
        set => SetProperty(ref _botCodeInput, value);
    }

    private bool _isRedeemingCode;
    public bool IsRedeemingCode
    {
        get => _isRedeemingCode;
        set => SetProperty(ref _isRedeemingCode, value);
    }

    private string? _codeErrorMessage;
    public string? CodeErrorMessage
    {
        get => _codeErrorMessage;
        set => SetProperty(ref _codeErrorMessage, value);
    }

    public ICommand OpenSignInModalCommand { get; }
    public ICommand CloseSignInModalCommand { get; }
    public ICommand RedeemBotCodeCommand { get; }
    public ICommand BrowserSignInCommand { get; }
    public ICommand SignOutCommand { get; }

    public ObservableCollection<OnlineGameEntry> Games { get; } = new();
    
    private string _searchQuery = "";
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                ApplyFilters();
            }
        }
    }
    
    private bool _isAdminRequired;
    public bool IsAdminRequired
    {
        get => _isAdminRequired;
        set => SetProperty(ref _isAdminRequired, value);
    }
    
    private string? _pendingZipPath;
    private string? _pendingGameDir;
    private string? _pendingAppId;
    private string? _pendingFixName;

    private string _currentFilter = "All";
    public string CurrentFilter
    {
        get => _currentFilter;
        set
        {
            if (SetProperty(ref _currentFilter, value))
                ApplyFilters();
        }
    }
    
    private void SetFilter(string? filter)
    {
        if (filter != null) CurrentFilter = filter;
    }

    private void ApplyFilters()
    {
        Games.Clear();
        var query = _searchQuery.ToLowerInvariant();
        
        var filtered = _allGames.Where(g => 
            (string.IsNullOrWhiteSpace(query) || g.Name.ToLowerInvariant().Contains(query)) &&
            (CurrentFilter == "All" || 
             (CurrentFilter == "Bypasses" && g.HasBypass) || 
             (CurrentFilter == "Online Fixes" && g.HasOnlineFix))
        ).OrderBy(g => g.IsInstalled ? 0 : 1).ThenBy(g => g.Name);

        foreach (var g in filtered)
            Games.Add(g);
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
    
    public ObservableCollection<DenuvoFix> SelectedGameFixes { get; } = new();

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
    public ICommand RestartAsAdminCommand { get; }
    public ICommand FilterCommand { get; }
    public ICommand DownloadBypassCommand { get; }
    public ICommand DownloadOnlineFixCommand { get; }

    public async Task RefreshAsync()
    {
        IsScanning = true;
        StatusMessage = "Loading fixes from API...";
        ShowGameDetail = false;
        _allGames.Clear();
        Games.Clear();
        HasNoGames = false;
        NeedsAdminRights = false;

        try
        {
            var apiListings = await _apiClient.GetDenuvoListingsAsync();
            if (apiListings == null || apiListings.Games.Count == 0)
            {
                StatusMessage = "Failed to load fixes from API or no fixes available.";
                IsScanning = false;
                return;
            }

            StatusMessage = "Scanning installed games...";
            
            var allSteamGames = new List<OnlineGameEntry>();
            
            var luaPath = _settings.Settings.LuaOutputPath;
            if (!string.IsNullOrWhiteSpace(luaPath) && Directory.Exists(luaPath))
            {
                var luaEntries = _luaParser.ScanLuaFolder(luaPath);
                foreach (var entry in luaEntries)
                {
                    allSteamGames.Add(new OnlineGameEntry { AppId = entry.AppId, Name = entry.Name });
                }
            }

            if (apiListings?.Games != null)
            {
                foreach (var apiGame in apiListings.Games)
                {
                    if (!allSteamGames.Any(g => g.AppId == apiGame.AppId))
                    {
                        allSteamGames.Add(new OnlineGameEntry { AppId = apiGame.AppId, Name = apiGame.Name });
                    }
                }
            }

            var semaphore = new SemaphoreSlim(5);
            var completed = 0;
            var installedCount = 0;
            var lockObj = new object();

            var tasks = allSteamGames.Select(async entry =>
            {
                await semaphore.WaitAsync();
                try
                {
                    entry.IsInstalled = _onlineFix.IsGameInstalled(entry.AppId);
                    if (!entry.IsInstalled) return; // We only care about installed games per requirements

                    var apiGame = apiListings?.Games.FirstOrDefault(g => g.AppId == entry.AppId);
                    if (apiGame != null && apiGame.FixCount > 0)
                    {
                        bool hasOnlineTag = apiGame.Tags.Any(t => t.Name.Contains("Online Fix", StringComparison.OrdinalIgnoreCase) || t.Slug.Contains("online-fix", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("Steamworks", StringComparison.OrdinalIgnoreCase));
                        bool hasBypassTag = apiGame.Tags.Any(t => t.Name.Contains("Bypass", StringComparison.OrdinalIgnoreCase) || t.Slug.Contains("bypass", StringComparison.OrdinalIgnoreCase) || t.Slug.Contains("denuvo", StringComparison.OrdinalIgnoreCase));
                        
                        if (!hasOnlineTag && !hasBypassTag) 
                        {
                            hasBypassTag = true; // Default if tags are missing but fixes exist
                        }

                        entry.HasOnlineFix = hasOnlineTag;
                        entry.HasBypass = hasBypassTag;
                        entry.HeaderImageUrl = apiGame.HeaderImage ?? "";
                        entry.Name = apiGame.Name;

                        entry.InstallPath = _onlineFix.GetGameInstallPath(entry.AppId);
                        entry.FixApplied = _onlineFix.IsFixApplied(entry.AppId);
                        entry.Status = entry.FixApplied ? "✅ Fix Applied" : "✅ Installed";

                        lock (lockObj)
                        {
                            installedCount++;
                            _allGames.Add(entry);
                        }
                    }
                    
                    lock (lockObj)
                    {
                        completed++;
                        StatusMessage = $"Scanning... {completed}/{allSteamGames.Count} checked";
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            ApplyFilters();
            HasNoGames = _allGames.Count == 0;
            StatusMessage = HasNoGames
                ? "No supported installed games found."
                : $"Found {_allGames.Count} installed game(s) with fixes.";
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

    private async void SelectGame(OnlineGameEntry? entry)
    {
        NeedsAdminRights = false;

        if (entry == null)
        {
            ShowGameDetail = false;
            SelectedGame = null;
            return;
        }

        SelectedGame = entry;
        ShowGameDetail = true;
        DetailStatusMessage = "Loading fixes...";
        SelectedGameFixes.Clear();

        if (entry.IsInstalled)
        {
            DetailStatusMessage = entry.FixApplied
                ? "Fix has been applied. You can re-apply if needed."
                : "Select a fix or bypass to download.";
                
            // Fetch fixes from API
            try
            {
                var fixesData = await _apiClient.GetDenuvoFixesAsync(entry.AppId);
                if (fixesData != null && fixesData.Fixes != null)
                {
                    foreach (var fix in fixesData.Fixes)
                    {
                        SelectedGameFixes.Add(fix);
                    }
                }
            }
            catch (Exception)
            {
                // ignore if not found
            }
        }
        else
        {
            DetailStatusMessage = "Game is not installed. Install it via Steam first.";
        }
    }
    
    private async void DownloadFix(DenuvoFix? fix, string slot)
    {
        if (fix == null || SelectedGame == null) return;
        
        IsBusy = true;
        DetailStatusMessage = "Downloading...";
        
        try
        {
            var progress = new Progress<double?>(p => {
                if (p.HasValue) DownloadProgress = p.Value * 100;
            });
            
            var result = await _apiClient.DownloadDenuvoAsync(fix.Id, slot, $"{SelectedGame.AppId}_fix.zip", progress);
            
            DetailStatusMessage = $"Downloaded to: {result.FilePath}";
            
            // Extract it
            var gameDir = _onlineFix.GetGameInstallPath(SelectedGame.AppId);
            if (!string.IsNullOrWhiteSpace(gameDir))
            {
                DetailStatusMessage = "Extracting...";
                var (extractResult, error) = _onlineFix.ExtractZipToGameDir(result.FilePath, gameDir, msg => DetailStatusMessage = msg);
                if (extractResult == OnlineFixService.ExtractResult.Success)
                {
                    SelectedGame.FixApplied = true;
                    SelectedGame.Status = "✅ Fix Applied";
                    DetailStatusMessage = "✓ Fix applied successfully!";
                    IsAdminRequired = false;
                }
                else if (extractResult == OnlineFixService.ExtractResult.PermissionDenied)
                {
                    DetailStatusMessage = error ?? "Permission denied.";
                    _pendingZipPath = result.FilePath;
                    _pendingGameDir = gameDir;
                    _pendingAppId = SelectedGame.AppId;
                    _pendingFixName = slot == "manifest" ? "Bypass" : "Online Fix";
                    IsAdminRequired = true;
                }
                else
                {
                    DetailStatusMessage = error ?? "✗ Failed to extract fix.";
                    IsAdminRequired = false;
                }
            }
        }
        catch (AuthException)
        {
            DetailStatusMessage = "Sign in required. Opening browser to login...";
            await BrowserSignInAsync();
        }
        catch (Exception ex)
        {
            DetailStatusMessage = $"Download failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RedeemBotCodeAsync()
    {
        var code = BotCodeInput.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            CodeErrorMessage = "Please enter a 6-character code from Discord.";
            return;
        }

        IsRedeemingCode = true;
        CodeErrorMessage = null;
        try
        {
            await _authService.SignInWithCodeAsync(code);
            IsSignInModalVisible = false;
            BotCodeInput = "";
            DetailStatusMessage = "✓ Signed in with Discord successfully! Please click download again.";
        }
        catch (Exception ex)
        {
            CodeErrorMessage = ex.Message;
        }
        finally
        {
            IsRedeemingCode = false;
        }
    }

    private async Task BrowserSignInAsync()
    {
        CodeErrorMessage = null;
        DetailStatusMessage = "Opening browser for Discord login...";
        try
        {
            await _authService.SignInAsync();
            IsSignInModalVisible = false;
            DetailStatusMessage = "✓ Signed in with Discord! Please click download again.";
        }
        catch (Exception ex)
        {
            CodeErrorMessage = $"Browser sign-in failed: {ex.Message}";
        }
    }

    private void OpenGameDir(OnlineGameEntry? entry)
    {
        if (entry == null) return;
        _onlineFix.OpenGameDirectory(entry.AppId);
    }

    private void RestartAsAdmin()
    {
        try
        {
            var exePath = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
            string args = "";
            if (!string.IsNullOrWhiteSpace(_pendingZipPath) && !string.IsNullOrWhiteSpace(_pendingGameDir) && !string.IsNullOrWhiteSpace(_pendingAppId))
            {
                args = $"--extract-fix \"{_pendingZipPath}\" \"{_pendingGameDir}\" \"{_pendingAppId}\" \"{_pendingFixName ?? "Fix"}\"";
            }
            
            var startInfo = new ProcessStartInfo(exePath)
            {
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas" 
            };
            
            SteamVault.App.ReleaseSingleInstanceMutex();
            Process.Start(startInfo);

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            DetailStatusMessage = $"✗ Failed to restart as admin: {ex.Message}";
        }
    }

    public async Task ProcessPendingExtractionAsync()
    {
        var zipPath = App.PendingFixZipPath;
        var gameDir = App.PendingFixGameDir;
        var appId = App.PendingFixAppId;
        var fixName = App.PendingFixName ?? "Fix";

        if (string.IsNullOrWhiteSpace(zipPath) || string.IsNullOrWhiteSpace(gameDir) || string.IsNullOrWhiteSpace(appId))
            return;

        IsBusy = true;
        DetailStatusMessage = $"Applying {fixName} with admin permissions...";

        // Wait a little bit for the UI to fully load and display
        await Task.Delay(1500);

        // Load the games list if it hasn't been loaded yet so we can select the right game
        if (!Games.Any())
        {
            await RefreshAsync();
        }
        
        var gameToSelect = Games.FirstOrDefault(g => g.AppId == appId);
        if (gameToSelect != null)
        {
            SelectGame(gameToSelect);
        }

        await Task.Run(() =>
        {
            var (result, error) = _onlineFix.ExtractZipToGameDir(zipPath, gameDir, msg => 
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => DetailStatusMessage = msg);
            });

            OnlineFixService.CleanupTempFile(zipPath);

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (result == OnlineFixService.ExtractResult.Success)
                {
                    DetailStatusMessage = $"✓ {fixName} applied successfully!";
                    // Try to update the game in the list if it exists
                    var game = Games.FirstOrDefault(g => g.AppId == appId);
                    if (game != null)
                    {
                        game.FixApplied = true;
                        game.Status = $"✅ {fixName} Applied";
                        if (SelectedGame == game)
                        {
                            IsAdminRequired = false;
                        }
                    }
                }
                else
                {
                    DetailStatusMessage = error ?? $"✗ Failed to extract {fixName.ToLower()}.";
                }
                IsBusy = false;
            });
        });
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
    
    // New properties for Fix/Bypass tags
    private bool _hasOnlineFix;
    public bool HasOnlineFix
    {
        get => _hasOnlineFix;
        set { _hasOnlineFix = value; OnPropertyChanged(); OnPropertyChanged(nameof(TagText)); }
    }
    
    private bool _hasBypass;
    public bool HasBypass
    {
        get => _hasBypass;
        set { _hasBypass = value; OnPropertyChanged(); OnPropertyChanged(nameof(TagText)); }
    }
    
    public string TagText
    {
        get
        {
            if (HasOnlineFix && HasBypass) return "Fix & Bypass";
            if (HasBypass) return "Bypass";
            if (HasOnlineFix) return "Online Fix";
            return "";
        }
    }

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
