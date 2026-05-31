using SteamVault.Services;
using System.Windows.Input;

namespace SteamVault.ViewModels;

public class MainViewModel : ViewModelBase
{
    public DashboardViewModel DashboardVM { get; }
    public SettingsViewModel SettingsVM { get; }

    private ViewModelBase _currentView;
    public ViewModelBase CurrentView
    {
        get => _currentView;
        set => SetProperty(ref _currentView, value);
    }

    public ICommand NavigateDashboardCommand { get; }
    public ICommand NavigateSettingsCommand { get; }

    public MainViewModel(
        SettingsService settingsService,
        DepotKeyService depotKeyService,
        SteamApiService steamApi,
        DownloadService downloadService)
    {
        DashboardVM = new DashboardViewModel(downloadService);
        SettingsVM = new SettingsViewModel(settingsService, depotKeyService);

        _currentView = DashboardVM; // Default view

        NavigateDashboardCommand = new RelayCommand(_ => CurrentView = DashboardVM);
        NavigateSettingsCommand = new RelayCommand(_ => CurrentView = SettingsVM);

        // Load data in background
        Task.Run(async () => await depotKeyService.LoadAsync());
    }
}
