using SteamVault.Models;
using SteamVault.Services;
using System.Windows.Input;

namespace SteamVault.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly DepotKeyService _depotKeyService;

    public SettingsViewModel(SettingsService settingsService, DepotKeyService depotKeyService)
    {
        _settingsService = settingsService;
        _depotKeyService = depotKeyService;

        SaveCommand = new RelayCommand(_ => SaveSettings());
        SyncDatabaseCommand = new RelayCommand(async _ => await SyncDatabaseAsync(), _ => !IsSyncing);
    }

    public AppSettings Settings => _settingsService.Settings;

    public ICommand SaveCommand { get; }
    public ICommand SyncDatabaseCommand { get; }

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
}
