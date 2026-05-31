using SteamVault.Services;
using SteamVault.ViewModels;
using System.Windows;
using System.Drawing;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace SteamVault;

public partial class App : Application
{
    private static System.Threading.Mutex? _instanceMutex;
    private static System.Threading.EventWaitHandle? _showEvent;

    private NotifyIcon? _notifyIcon;
    private SettingsService? _settingsService;
    private AutoUpdateService? _autoUpdateService;
    private MainWindow? _mainWindow;
    private bool _isExiting;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        // 1. Single Instance mutext check
        _instanceMutex = new System.Threading.Mutex(true, "SteamVault_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            try
            {
                if (System.Threading.EventWaitHandle.TryOpenExisting("SteamVault_ShowInstance_Event", out var existingEvent))
                {
                    existingEvent.Set();
                }
            }
            catch { }
            
            Shutdown();
            return;
        }

        _showEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, "SteamVault_ShowInstance_Event");
        
        Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    if (_showEvent.WaitOne())
                    {
                        ShowMainWindow();
                    }
                }
                catch
                {
                    break;
                }
            }
        });

        // 2. Standard initialization
        _settingsService = new SettingsService();
        _settingsService.Load();

        // Auto-detect Steam paths if not configured
        _settingsService.Settings.AutoDetectPaths();

        // Check if OpenSteamTool installation is needed (first run)
        if (!_settingsService.Settings.IsInstalled)
        {
            _settingsService.Save();

            // Show installation wizard — user verifies directory, then we check for existing DLLs
            var installVM = new InstallationViewModel(_settingsService);
            
            installVM.OnInstallationComplete += () =>
            {
                Current.Dispatcher.Invoke(() =>
                {
                    InitializeMainApp(startSilent: false);
                });
            };

            // Create window showing installation view
            _mainWindow = new MainWindow
            {
                DataContext = installVM
            };
            _mainWindow.Closing += MainWindow_Closing;
            _mainWindow.Show();
        }
        else
        {
            _settingsService.Save();
            InitializeMainApp(e.Args.Contains("--silent"));
        }
    }

    /// <summary>
    /// Checks whether OpenSteamTool DLLs already exist in the given Steam directory.
    /// </summary>
    private static bool IsOpenSteamToolAlreadyInstalled(string steamDirectory)
    {
        if (string.IsNullOrWhiteSpace(steamDirectory) || !System.IO.Directory.Exists(steamDirectory))
            return false;

        // Check for the signature OpenSteamTool DLLs
        var dllNames = new[] { "OpenSteamTool.dll", "dwmapi.dll", "xinput1_4.dll" };
        return dllNames.All(dll => System.IO.File.Exists(
            System.IO.Path.Combine(steamDirectory, dll)));
    }

    private void InitializeMainApp(bool startSilent)
    {
        // Core services
        var depotKeyService = new DepotKeyService();
        var steamApi = new SteamApiService();
        var luaGenerator = new LuaGeneratorService();
        var luaParser = new LuaParserService();
        var searchService = new GameSearchService();
        var downloadService = new DownloadService(steamApi, depotKeyService, luaGenerator, _settingsService!);

        _autoUpdateService = new AutoUpdateService(
            _settingsService!,
            steamApi,
            depotKeyService,
            luaParser,
            downloadService);

        var mainVM = new MainViewModel(
            _settingsService!,
            depotKeyService,
            steamApi,
            downloadService,
            luaParser,
            searchService);

        // Initialize System Tray Icon (only once)
        if (_notifyIcon == null)
            CreateTrayIcon();

        // Reuse the existing window if already created (from installation wizard)
        if (_mainWindow != null)
        {
            _mainWindow.DataContext = mainVM;
        }
        else
        {
            _mainWindow = new MainWindow
            {
                DataContext = mainVM
            };
            _mainWindow.Closing += MainWindow_Closing;

            if (startSilent)
            {
                _notifyIcon?.ShowBalloonTip(3000, "SteamVault", "SteamVault running in background tray.", ToolTipIcon.Info);
            }
            else
            {
                _mainWindow.Show();
            }
        }

        // Start background updater timer
        StartBackgroundUpdateTimer();
    }

    private void StartBackgroundUpdateTimer()
    {
        Task.Run(async () =>
        {
            await Task.Delay(2000);

            while (true)
            {
                if (_settingsService?.Settings.AutoUpdateEnabled == true)
                {
                    await RunBackgroundUpdateCheckAsync();
                }

                await Task.Delay(TimeSpan.FromHours(4));
            }
        });
    }

    private void CreateTrayIcon()
    {
        _notifyIcon = new NotifyIcon
        {
            Text = "SteamVault Automated Vault Downloader",
            Visible = true
        };

        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath))
            {
                _notifyIcon.Icon = Icon.ExtractAssociatedIcon(exePath) ?? SystemIcons.Application;
            }
            else
            {
                _notifyIcon.Icon = SystemIcons.Shield;
            }
        }
        catch
        {
            _notifyIcon.Icon = SystemIcons.Application;
        }

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Open SteamVault Dashboard", null, (s, e) => ShowMainWindow());
        contextMenu.Items.Add("Scan & Update Now", null, async (s, e) => await RunBackgroundUpdateCheckAsync());
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, (s, e) => ExitApplication());

        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += (s, e) => ShowMainWindow();
    }

    private async Task RunBackgroundUpdateCheckAsync()
    {
        if (_autoUpdateService == null || _autoUpdateService.IsRunning) return;

        Action<string, bool> onGameUpdated = (gameName, success) =>
        {
            Current.Dispatcher.Invoke(() =>
            {
                if (success)
                {
                    _notifyIcon?.ShowBalloonTip(4000, "SteamVault Update", $"✓ Updated config for {gameName}", ToolTipIcon.Info);
                }
            });
        };

        _autoUpdateService.OnGameUpdated += onGameUpdated;
        
        var result = await _autoUpdateService.RunAutoUpdateAsync();

        Current.Dispatcher.Invoke(() =>
        {
            if (_mainWindow?.DataContext is MainViewModel mainVM)
            {
                mainVM.RefreshSidebarStats();
                mainVM.MyGamesVM.RefreshCommand.Execute(null);
            }
        });

        _autoUpdateService.OnGameUpdated -= onGameUpdated;

        Current.Dispatcher.Invoke(() =>
        {
            if (result.FolderMissing)
            {
                _notifyIcon?.ShowBalloonTip(5000, "SteamVault Scan Failed", "✗ Lua output folder not found. Please verify your directories in Settings.", ToolTipIcon.Error);
            }
            else if (result.TotalGames == 0)
            {
                _notifyIcon?.ShowBalloonTip(5000, "SteamVault Update", "📂 No games found in local vault. Download game configs first!", ToolTipIcon.Warning);
            }
            else if (result.UpdatedCount > 0)
            {
                var summaryText = $"✓ Updated: {result.UpdatedCount} game(s)\n• Up to date: {result.UpToDateCount} game(s)";
                if (result.FailedCount > 0) summaryText += $"\n✗ Failed: {result.FailedCount} game(s)";
                
                _notifyIcon?.ShowBalloonTip(6000, "SteamVault Update Summary", summaryText, ToolTipIcon.Info);
            }
            else
            {
                _notifyIcon?.ShowBalloonTip(5000, "SteamVault Update", $"✓ All {result.UpToDateCount} game(s) are fully up to date!", ToolTipIcon.Info);
            }
        });
    }

    private void ShowMainWindow()
    {
        Current.Dispatcher.Invoke(() =>
        {
            if (_mainWindow != null)
            {
                _mainWindow.Show();
                _mainWindow.WindowState = WindowState.Normal;
                _mainWindow.Activate();
            }
        });
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExiting)
        {
            if (_settingsService?.Settings.AutoUpdateEnabled == true)
            {
                e.Cancel = true;
                _mainWindow?.Hide();
                _notifyIcon?.ShowBalloonTip(2500, "SteamVault minimized", "Running in background in your system tray.", ToolTipIcon.Info);
            }
        }
    }

    private void ExitApplication()
    {
        _isExiting = true;
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        
        _autoUpdateService?.Cancel();
        
        Current.Dispatcher.Invoke(() =>
        {
            _mainWindow?.Close();
            Shutdown();
        });
    }
}