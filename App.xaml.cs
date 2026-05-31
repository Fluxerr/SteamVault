using SteamVault.Services;
using SteamVault.ViewModels;
using System.Windows;

namespace SteamVault;

public partial class App : Application
{
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        // Load settings
        var settingsService = new SettingsService();
        settingsService.Load();

        // Auto-detect Steam paths if not configured
        settingsService.Settings.AutoDetectPaths();
        settingsService.Save();

        // Core services
        var depotKeyService = new DepotKeyService();
        var steamApi = new SteamApiService();
        var luaGenerator = new LuaGeneratorService();
        var downloadService = new DownloadService(steamApi, depotKeyService, luaGenerator, settingsService);

        var mainVM = new MainViewModel(
            settingsService,
            depotKeyService,
            steamApi,
            downloadService);

        var mainWindow = new MainWindow
        {
            DataContext = mainVM
        };

        mainWindow.Show();
    }
}
