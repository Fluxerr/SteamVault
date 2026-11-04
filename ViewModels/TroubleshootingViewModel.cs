using SteamVault.Services;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using MessageBox = System.Windows.MessageBox;
using WpfApplication = System.Windows.Application;

namespace SteamVault.ViewModels;

public class TroubleshootingViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly OpenSteamToolInstaller _installer = new();

    public TroubleshootingViewModel(SettingsService settingsService, string? initialStatus = null)
    {
        _settingsService = settingsService;
        if (!string.IsNullOrWhiteSpace(initialStatus))
            _statusMessage = initialStatus;
        RepairOpenSteamToolCommand = new RelayCommand(
            async _ => await RepairOpenSteamToolAsync(),
            _ => !IsBusy && IsSteamDirectoryValid);
        RecreateFoldersCommand = new RelayCommand(
            async _ => await RecreateFoldersAsync(),
            _ => !IsBusy && IsSteamDirectoryValid);
        OpenSteamFolderCommand = new RelayCommand(_ => OpenSteamFolder(), _ => Directory.Exists(SteamDirectory));
        RestartAsAdminCommand = new RelayCommand(_ => RestartAsAdmin());
    }

    public string SteamDirectory => _settingsService.Settings.SteamDirectory;
    public bool IsSteamDirectoryValid =>
        !string.IsNullOrWhiteSpace(SteamDirectory)
        && Directory.Exists(SteamDirectory)
        && File.Exists(Path.Combine(SteamDirectory, "steam.exe"));

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsNotBusy => !IsBusy;

    private string _statusMessage = "Choose a repair to get started.";
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public ICommand RepairOpenSteamToolCommand { get; }
    public ICommand RecreateFoldersCommand { get; }
    public ICommand OpenSteamFolderCommand { get; }
    public ICommand RestartAsAdminCommand { get; }

    private async Task RepairOpenSteamToolAsync()
    {
        if (IsBusy || !IsSteamDirectoryValid)
        {
            StatusMessage = "The configured Steam folder is unavailable. Update it in Settings and try again.";
            return;
        }

        var confirm = MessageBox.Show(
            "SteamVault will close Steam, replace the OpenSteamTool DLLs, and recreate its required folders. Steam will stay closed until you open it again. Save your work and stop downloads before continuing.",
            "Repair OpenSteamTool files",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        IsBusy = true;
        StatusMessage = "Closing Steam before replacing its DLLs...";

        try
        {
            var result = await Task.Run(async () =>
            {
                await CloseSteamAsync();
                var installResult = _installer.Install(SteamDirectory, out bool needsAdmin);
                return (installResult, needsAdmin);
            });

            if (!result.installResult.Success)
            {
                var adminHint = result.needsAdmin ? " Run SteamVault as administrator and try again." : string.Empty;
                StatusMessage = $"Repair failed: {result.installResult.ErrorMessage}{adminHint}";
                return;
            }

            StatusMessage = $"Replaced {result.installResult.DllsCopied} OpenSteamTool DLLs and recreated {result.installResult.DirsCreated} missing folders. Steam is closed; open it again to load the repaired files.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Repair failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RecreateFoldersAsync()
    {
        if (IsBusy || !IsSteamDirectoryValid)
        {
            StatusMessage = "The configured Steam folder is unavailable. Update it in Settings and try again.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Checking Steam's Lua and depot cache folders...";

        try
        {
            int created = await Task.Run(() => OpenSteamToolInstaller.EnsureRequiredDirectories(SteamDirectory));
            StatusMessage = created == 0
                ? "All required Lua and depot cache folders already exist."
                : $"Recreated {created} missing Steam folder(s).";
        }
        catch (UnauthorizedAccessException)
        {
            StatusMessage = "Windows denied access to the Steam folder. Run SteamVault as administrator and try again.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Folder repair failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CloseSteamAsync()
    {
        var steamProcesses = Process.GetProcessesByName("steam");
        if (steamProcesses.Length == 0)
            return;
        foreach (var process in steamProcesses)
            process.Dispose();

        try
        {
            using var shutdown = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(SteamDirectory, "steam.exe"),
                Arguments = "-shutdown",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Steam could not be asked to shut down: {ex.Message}", ex);
        }

        var timeout = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < timeout)
        {
            await Task.Delay(500);
            var currentProcesses = Process.GetProcessesByName("steam");
            foreach (var process in currentProcesses)
                process.Dispose();

            if (currentProcesses.Length == 0)
                return;
        }

        throw new TimeoutException("Steam did not close. Close Steam manually, then run the repair again. SteamVault did not force-quit it.");
    }

    private void OpenSteamFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = SteamDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open the Steam folder: {ex.Message}";
        }
    }

    private void RestartAsAdmin()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
                throw new InvalidOperationException("Could not find the SteamVault executable path.");

            Process.Start(new ProcessStartInfo(executablePath)
            {
                UseShellExecute = true,
                Verb = "runas"
            });
            WpfApplication.Current.Shutdown();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not restart SteamVault as administrator: {ex.Message}";
        }
    }
}
