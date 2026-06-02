using SteamVault.Models;
using SteamVault.Services;
using System.Windows;
using System.Windows.Input;
using WpfApplication = System.Windows.Application;

namespace SteamVault.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly DepotKeyService _depotKeyService;
    private readonly ExportService _exportService;

    public SettingsViewModel(SettingsService settingsService, DepotKeyService depotKeyService, ExportService exportService)
    {
        _settingsService = settingsService;
        _depotKeyService = depotKeyService;
        _exportService = exportService;

        SaveCommand = new RelayCommand(_ => SaveSettings());
        SyncDatabaseCommand = new RelayCommand(async _ => await SyncDatabaseAsync(), _ => !IsSyncing);
        ExportLuaCommand = new RelayCommand(async _ => await ExportLuaAsync(), _ => !IsExporting);
        ChangeThemeCommand = new RelayCommand(param => ChangeTheme(param as string));
    }

    public AppSettings Settings => _settingsService.Settings;

    public ICommand SaveCommand { get; }
    public ICommand SyncDatabaseCommand { get; }
    public ICommand ExportLuaCommand { get; }
    public ICommand ChangeThemeCommand { get; }

    // Auto-update toggle
    public bool AutoUpdateEnabled
    {
        get => _settingsService.Settings.AutoUpdateEnabled;
        set
        {
            if (_settingsService.Settings.AutoUpdateEnabled == value) return;
            _settingsService.Settings.AutoUpdateEnabled = value;
            OnPropertyChanged();

            // Register/unregister from Windows startup
            AutoUpdateService.SetStartupEnabled(value);
            _settingsService.Save();

            StatusMessage = value
                ? "Auto-update enabled — SteamVault will start with Windows."
                : "Auto-update disabled — SteamVault won't start automatically.";

            ClearStatusAfterDelay();
        }
    }

    public void RefreshAutoUpdateStatus()
    {
        OnPropertyChanged(nameof(AutoUpdateEnabled));
    }

    private string _statusMessage = "";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    private string _syncStatusMessage = "";
    public string SyncStatusMessage
    {
        get => _syncStatusMessage;
        set => SetProperty(ref _syncStatusMessage, value);
    }

    private string _exportStatus = "";
    public string ExportStatus
    {
        get => _exportStatus;
        set => SetProperty(ref _exportStatus, value);
    }

    private bool _isSyncing;
    public bool IsSyncing
    {
        get => _isSyncing;
        set
        {
            SetProperty(ref _isSyncing, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private bool _isExporting;
    public bool IsExporting
    {
        get => _isExporting;
        set
        {
            SetProperty(ref _isExporting, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _selectedTheme = "";
    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (SetProperty(ref _selectedTheme, value))
            {
                ChangeTheme(value);
            }
        }
    }

    public List<string> AvailableThemes { get; } = new()
    {
        "Dark", "AmoledBlack", "MidnightBlue", "SlateGray", "EmeraldNight"
    };

    public string DatabaseStats => _depotKeyService.IsLoaded
        ? $"{_depotKeyService.DepotKeyCount:N0} depot keys, {_depotKeyService.AppTokenCount:N0} app tokens loaded."
        : "Database not loaded yet.";

    private void SaveSettings()
    {
        _settingsService.Save();
        StatusMessage = "Settings saved successfully.";
        ClearStatusAfterDelay();
    }

    private void ClearStatusAfterDelay()
    {
        Task.Delay(3000).ContinueWith(_ =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                StatusMessage = "");
        });
    }

    private async Task SyncDatabaseAsync()
    {
        IsSyncing = true;
        SyncStatusMessage = "Initializing database synchronization...";

        try
        {
            var success = await _depotKeyService.SyncDatabaseAsync(msg =>
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    SyncStatusMessage = msg;
                });
            });

            if (success)
            {
                OnPropertyChanged(nameof(DatabaseStats));
            }
        }
        catch (Exception ex)
        {
            SyncStatusMessage = $"Sync failed: {ex.Message}";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private async Task ExportLuaAsync()
    {
        IsExporting = true;
        ExportStatus = "Select a destination folder...";

        try
        {
            var initialDir = string.IsNullOrWhiteSpace(_settingsService.Settings.LastExportDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                : _settingsService.Settings.LastExportDirectory;

            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select folder for Lua backup ZIP",
                InitialDirectory = initialDir,
                ShowNewFolderButton = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _settingsService.Settings.LastExportDirectory = dialog.SelectedPath;
                _settingsService.Save();

                ExportStatus = "Exporting Lua files...";
                var result = await _exportService.ExportAllLuaFilesAsync(dialog.SelectedPath, msg =>
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() => ExportStatus = msg);
                });

                if (result.Success)
                {
                    ExportStatus = $"✓ Exported {result.FileCount} lua + {result.ManifestCount} manifest files ({result.SizeFormatted}) → {result.FilePath}";
                }
                else
                {
                    ExportStatus = $"✗ {result.Error}";
                }
            }
            else
            {
                ExportStatus = "";
            }
        }
        catch (Exception ex)
        {
            ExportStatus = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
        }
    }

    private void ChangeTheme(string? themeName)
    {
        if (string.IsNullOrWhiteSpace(themeName)) return;

        _settingsService.Settings.Theme = themeName;
        _settingsService.Save();

        WpfApplication.Current?.Dispatcher.Invoke(() =>
        {
            ThemeManager.ApplyTheme(themeName);
        });
    }
}
