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
            }
        }
    }

    public bool IsDashboardActive => CurrentView == DashboardVM;
    public bool IsMyGamesActive => CurrentView == MyGamesVM;
    public bool IsSettingsActive => CurrentView == SettingsVM;
    public bool IsDiscoverActive => CurrentView == DiscoverVM;
    public bool IsOnlineActive => CurrentView == OnlineVM;
    public bool IsStatsActive => CurrentView == StatsVM;

    public ICommand NavigateDashboardCommand { get; }
    public ICommand NavigateSettingsCommand { get; }
    public ICommand NavigateMyGamesCommand { get; }
    public ICommand NavigateDiscoverCommand { get; }
    public ICommand NavigateOnlineCommand { get; }
    public ICommand NavigateStatsCommand { get; }

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
        ImportService importService)
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
}

