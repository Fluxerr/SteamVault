using SteamVault.Services;
using System.Windows.Input;

namespace SteamVault.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;

    public DashboardViewModel DashboardVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public MyGamesViewModel MyGamesVM { get; }
    public DiscoverViewModel DiscoverVM { get; }
    public OnlineViewModel OnlineVM { get; }
    public StatsViewModel StatsVM { get; }
    public FamilyVaultViewModel FamilyVaultVM { get; }

    private ViewModelBase _currentView = null!;
    public ViewModelBase CurrentView
    {
        get => _currentView;
        set
        {
            if (SetProperty(ref _currentView, value))
            {
                OnPropertyChanged(nameof(IsDashboardActive));
                OnPropertyChanged(nameof(IsMyGamesActive));
                OnPropertyChanged(nameof(IsSettingsActive));
                OnPropertyChanged(nameof(IsDiscoverActive));
                OnPropertyChanged(nameof(IsOnlineActive));
                OnPropertyChanged(nameof(IsStatsActive));
                OnPropertyChanged(nameof(IsFamilyActive));
            }
        }
    }

    public bool IsDashboardActive => CurrentView == DashboardVM;
    public bool IsMyGamesActive => CurrentView == MyGamesVM;
    public bool IsSettingsActive => CurrentView == SettingsVM;
    public bool IsDiscoverActive => CurrentView == DiscoverVM;
    public bool IsOnlineActive => CurrentView == OnlineVM;
    public bool IsStatsActive => CurrentView == StatsVM;
    public bool IsFamilyActive => CurrentView == FamilyVaultVM;

    public ICommand NavigateDashboardCommand { get; }
    public ICommand NavigateSettingsCommand { get; }
    public ICommand NavigateMyGamesCommand { get; }
    public ICommand NavigateDiscoverCommand { get; }
    public ICommand NavigateOnlineCommand { get; }
    public ICommand NavigateStatsCommand { get; }
    public ICommand NavigateFamilyCommand { get; }

    public string GameCount => CountLuaFiles();

    public string AutoUpdateStatus => "Auto-update: Disabled";

    private string CountLuaFiles()
    {
        var luaPath = _settingsService.Settings.LuaOutputPath;
        if (string.IsNullOrWhiteSpace(luaPath) || !System.IO.Directory.Exists(luaPath))
            return "0";

        try
        {
            var count = System.IO.Directory.GetFiles(luaPath, "*.lua", System.IO.SearchOption.AllDirectories).Length;
            return count.ToString();
        }
        catch
        {
            return "0";
        }
    }

    public MainViewModel(
        SettingsService settingsService,
        DepotKeyService depotKeyService,
        SteamApiService steamApi,
        DownloadService downloadService,
        LuaParserService luaParser,
        GameSearchService searchService,
        ExportService exportService,
        OnlineFixService onlineFixService,
        GameManagementService gameMgmtService,
        SteamVault.Services.LuaToolsApiClient apiClient,
        AuthService authService,
        ImportService importService,
        FamilyVaultService familyService)
    {
        _settingsService = settingsService;

        MyGamesVM = new MyGamesViewModel(settingsService, steamApi, depotKeyService, luaParser, downloadService, gameMgmtService);
        DashboardVM = new DashboardViewModel(downloadService, searchService, steamApi, luaParser, settingsService, gameMgmtService, depotKeyService, importService);
        DashboardVM.NavigateToLibrary = () => CurrentView = MyGamesVM;
        DashboardVM.NavigateToLibraryAndUpdate = () =>
        {
            CurrentView = MyGamesVM;
            _ = MyGamesVM.RefreshAsync();
        };
        SettingsVM = new SettingsViewModel(settingsService, depotKeyService, exportService);
        DiscoverVM = new DiscoverViewModel(steamApi, downloadService, searchService);
        OnlineVM = new OnlineViewModel(settingsService, steamApi, luaParser, onlineFixService, apiClient, authService);
        StatsVM = new StatsViewModel(settingsService, luaParser, steamApi, gameMgmtService);
        FamilyVaultVM = new FamilyVaultViewModel(familyService, settingsService, steamApi, searchService);

        if (!string.IsNullOrWhiteSpace(App.PendingFixZipPath))
        {
            CurrentView = OnlineVM;
            _ = OnlineVM.ProcessPendingExtractionAsync();
        }
        else
        {
            CurrentView = DashboardVM; // Default view
        }

        NavigateDashboardCommand = new RelayCommand(_ => CurrentView = DashboardVM);
        NavigateSettingsCommand = new RelayCommand(_ =>
        {
            SettingsVM.RefreshAutoUpdateStatus();
            CurrentView = SettingsVM;
        });
        NavigateMyGamesCommand = new RelayCommand(_ => CurrentView = MyGamesVM);
        NavigateDiscoverCommand = new RelayCommand(_ =>
        {
            if (CurrentView == DiscoverVM)
                _ = DiscoverVM.LoadAllAsync(); // Refresh on re-click
            else
                CurrentView = DiscoverVM;
        });
        NavigateOnlineCommand = new RelayCommand(_ => CurrentView = OnlineVM);
        NavigateStatsCommand = new RelayCommand(_ => 
        {
            CurrentView = StatsVM;
            _ = StatsVM.RefreshStatsAsync();
        });
        NavigateFamilyCommand = new RelayCommand(_ => CurrentView = FamilyVaultVM);

        // Listen for settings changes to update sidebar
        SettingsVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.AutoUpdateEnabled))
                RefreshSidebarStats();
        };

        // Load data in background
        Task.Run(async () => await depotKeyService.LoadAsync());
    }

    public void RefreshSidebarStats()
    {
        OnPropertyChanged(nameof(GameCount));
        OnPropertyChanged(nameof(AutoUpdateStatus));
    }

    public void HandleDeepLink(string url)
    {
        // Example URL: steamvault://download/4001890
        // Or: steamvault://join/ff8081819543e33f01954b868e4c7717
        try
        {
            var uri = new System.Uri(url);
            if (uri.Scheme.Equals("steamvault", System.StringComparison.OrdinalIgnoreCase))
            {
                if (uri.Host.Equals("download", System.StringComparison.OrdinalIgnoreCase))
                {
                    var appId = uri.Segments.LastOrDefault()?.Trim('/');
                    if (uint.TryParse(appId, out uint parsedAppId))
                    {
                        // Navigate to dashboard and trigger download
                        CurrentView = DashboardVM;
                        DashboardVM.AppIdInput = appId;
                        if (DashboardVM.DownloadCommand.CanExecute(null))
                        {
                            DashboardVM.DownloadCommand.Execute(null);
                        }
                    }
                }
                else if (uri.Host.Equals("join", System.StringComparison.OrdinalIgnoreCase))
                {
                    CurrentView = FamilyVaultVM;
                    FamilyVaultVM.ImportTokenInput = url;
                    if (FamilyVaultVM.ImportTokenCommand.CanExecute(null))
                    {
                        FamilyVaultVM.ImportTokenCommand.Execute(null);
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to handle deep link: {ex.Message}");
        }
    }
}

