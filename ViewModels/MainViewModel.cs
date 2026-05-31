using SteamVault.Services;
using System.Windows.Input;

namespace SteamVault.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;

    public DashboardViewModel DashboardVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public MyGamesViewModel MyGamesVM { get; }

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
            }
        }
    }

    public bool IsDashboardActive => CurrentView == DashboardVM;
    public bool IsMyGamesActive => CurrentView == MyGamesVM;
    public bool IsSettingsActive => CurrentView == SettingsVM;

    public ICommand NavigateDashboardCommand { get; }
    public ICommand NavigateSettingsCommand { get; }
    public ICommand NavigateMyGamesCommand { get; }

    public string GameCount => CountLuaFiles();

    public string AutoUpdateStatus => _settingsService.Settings.AutoUpdateEnabled
        ? "Auto-update: ON"
        : "Auto-update: OFF";

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
        GameSearchService searchService)
    {
        _settingsService = settingsService;

        DashboardVM = new DashboardViewModel(downloadService, searchService);
        SettingsVM = new SettingsViewModel(settingsService, depotKeyService);
        MyGamesVM = new MyGamesViewModel(settingsService, steamApi, depotKeyService, luaParser, downloadService);

        CurrentView = DashboardVM; // Default view

        NavigateDashboardCommand = new RelayCommand(_ => CurrentView = DashboardVM);
        NavigateSettingsCommand = new RelayCommand(_ =>
        {
            SettingsVM.RefreshAutoUpdateStatus();
            CurrentView = SettingsVM;
        });
        NavigateMyGamesCommand = new RelayCommand(_ => CurrentView = MyGamesVM);

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
