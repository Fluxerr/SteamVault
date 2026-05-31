using Microsoft.Win32;
using SteamVault.Models;
using SteamVault.Services;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows.Input;
using WpfApp = System.Windows.Application;

namespace SteamVault.ViewModels;

public class InstallationViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly OpenSteamToolInstaller _installer;

    public InstallationViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _installer = new OpenSteamToolInstaller();

        DetectSteamDirectory();
        
        BrowseCommand = new RelayCommand(_ => BrowseSteamDirectory());
        ConfirmCommand = new RelayCommand(_ => ConfirmAndInstall());
        RetryAsAdminCommand = new RelayCommand(_ => RetryAsAdmin());
    }

    private string _steamDirectory = "";
    public string SteamDirectory
    {
        get => _steamDirectory;
        set
        {
            if (SetProperty(ref _steamDirectory, value))
            {
                OnPropertyChanged(nameof(IsValidPath));
                OnPropertyChanged(nameof(IsSteamExeFound));
            }
        }
    }

    private string _statusMessage = "";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    private string _statusIcon = "";
    public string StatusIcon
    {
        get => _statusIcon;
        set => SetProperty(ref _statusIcon, value);
    }

    public bool IsValidPath =>
        !string.IsNullOrWhiteSpace(SteamDirectory) && File.Exists(Path.Combine(SteamDirectory, "steam.exe"));

    public bool IsSteamExeFound => IsValidPath;

    private bool _isInstalling;
    public bool IsInstalling
    {
        get => _isInstalling;
        set
        {
            if (SetProperty(ref _isInstalling, value))
            {
                OnPropertyChanged(nameof(CanBrowse));
                OnPropertyChanged(nameof(CanConfirm));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool CanBrowse => !IsInstalling;
    public bool CanConfirm => !IsInstalling && IsValidPath;

    private bool _isComplete;
    public bool IsComplete
    {
        get => _isComplete;
        set
        {
            if (SetProperty(ref _isComplete, value))
                OnPropertyChanged(nameof(ShowInstallContent));
        }
    }

    public bool ShowInstallContent => !IsComplete;

    private bool _requestsAdmin;
    public bool RequestsAdmin
    {
        get => _requestsAdmin;
        set
        {
            if (SetProperty(ref _requestsAdmin, value))
                OnPropertyChanged(nameof(ShowAdminPrompt));
        }
    }

    public bool ShowAdminPrompt => RequestsAdmin;

    private double _progressPercent;
    public double ProgressPercent
    {
        get => _progressPercent;
        set => SetProperty(ref _progressPercent, value);
    }

    private string _progressText = "";
    public string ProgressText
    {
        get => _progressText;
        set => SetProperty(ref _progressText, value);
    }

    public ICommand BrowseCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand RetryAsAdminCommand { get; }

    private void DetectSteamDirectory()
    {
        var detectedPath = GetSteamPathFromRegistry();
        
        if (string.IsNullOrEmpty(detectedPath) || !Directory.Exists(detectedPath))
            detectedPath = GetSteamPathFromCommonLocations();

        if (!string.IsNullOrEmpty(detectedPath))
        {
            SteamDirectory = detectedPath;
            StatusMessage = File.Exists(Path.Combine(detectedPath, "steam.exe"))
                ? "Steam installation detected via registry."
                : "Steam directory found, but steam.exe not detected. Please verify.";
            StatusIcon = File.Exists(Path.Combine(detectedPath, "steam.exe")) ? "\u2705" : "\u26A0\uFE0F";
        }
        else
        {
            SteamDirectory = @"C:\Program Files (x86)\Steam";
            StatusMessage = "Could not auto-detect Steam. Please browse to your Steam directory.";
            StatusIcon = "\U0001F50D";
        }
    }

    private static string? GetSteamPathFromRegistry()
    {
        using var key64 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
        if (key64 != null)
        {
            var installPath = key64.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
                return installPath;
        }

        using var key32 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
        if (key32 != null)
        {
            var installPath = key32.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
                return installPath;
        }

        using var hkcu = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
        if (hkcu != null)
        {
            var installPath = hkcu.GetValue("SteamPath") as string;
            if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
                return installPath;
        }

        return null;
    }

    private static string? GetSteamPathFromCommonLocations()
    {
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam",
            @"D:\Steam", @"D:\STEAM", @"E:\Steam", @"E:\STEAM",
            @"C:\Steam", @"F:\Steam", @"G:\Steam",
        };

        foreach (var path in candidates)
        {
            if (Directory.Exists(path) && File.Exists(Path.Combine(path, "steam.exe")))
                return path;
        }
        return null;
    }

    private void BrowseSteamDirectory()
    {
        if (IsInstalling) return;

        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select your Steam installation directory (the folder containing steam.exe)",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(SteamDirectory) ? SteamDirectory : @"C:\",
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            SteamDirectory = dialog.SelectedPath;
            StatusMessage = File.Exists(Path.Combine(SteamDirectory, "steam.exe"))
                ? "Valid Steam directory selected."
                : "steam.exe not found. Make sure this is your Steam installation folder.";
            StatusIcon = File.Exists(Path.Combine(SteamDirectory, "steam.exe")) ? "\u2705" : "\u26A0\uFE0F";
        }
    }

    private void ConfirmAndInstall()
    {
        if (IsInstalling || !IsValidPath) return;

        IsInstalling = true;

        // Check if OpenSteamTool DLLs already exist in the target directory
        if (CheckAlreadyInstalled(SteamDirectory))
        {
            // Already installed — skip file copy, just save settings
            WpfApp.Current.Dispatcher.Invoke(() =>
            {
                SaveSettingsAndComplete(dllsCopied: 0, dirsCreated: 0, alreadyHad: true);
            });
            return;
        }

        ProgressPercent = 0;
        ProgressText = "Preparing to install...";

        Task.Run(async () =>
        {
            try
            {
                // Simulate progress steps for visual feedback
                await Task.Delay(300);
                WpfApp.Current.Dispatcher.Invoke(() => { ProgressPercent = 25; ProgressText = "Copying DLL files..."; });
                await Task.Delay(400);
                WpfApp.Current.Dispatcher.Invoke(() => { ProgressPercent = 50; ProgressText = "Creating config directories..."; });

                var result = _installer.Install(SteamDirectory, out bool needsAdmin);
                await Task.Delay(300);

                WpfApp.Current.Dispatcher.Invoke(() => { ProgressPercent = 75; ProgressText = "Finalizing setup..."; });
                await Task.Delay(400);
                WpfApp.Current.Dispatcher.Invoke(() => { ProgressPercent = 100; ProgressText = "Complete!"; });
                await Task.Delay(200);

                WpfApp.Current.Dispatcher.Invoke(() =>
                {
                    if (needsAdmin && !IsRunningAsAdmin())
                    {
                        RequestsAdmin = true;
                        StatusMessage = "Administrator permissions required to create directories.";
                        StatusIcon = "\U0001F512";
                        IsInstalling = false;
                    }
                    else if (result.Success)
                    {
                        SaveSettingsAndComplete(result.DllsCopied, result.DirsCreated, alreadyHad: false);
                    }
                    else
                    {
                        StatusMessage = $"Installation failed: {result.ErrorMessage}";
                        StatusIcon = "\u274C";
                        IsInstalling = false;
                    }
                });
            }
            catch (Exception ex)
            {
                WpfApp.Current.Dispatcher.Invoke(() =>
                {
                    StatusMessage = $"Error during installation: {ex.Message}";
                    StatusIcon = "\u274C";
                    IsInstalling = false;
                });
            }
        });
    }

    private void SaveSettingsAndComplete(int dllsCopied, int dirsCreated, bool alreadyHad)
    {
        _settingsService.Settings.SteamDirectory = SteamDirectory;
        _settingsService.Settings.LuaOutputPath = Path.Combine(SteamDirectory, "config", "lua");
        _settingsService.Settings.ManifestCachePath = Path.Combine(SteamDirectory, "config", "depotcache");
        _settingsService.Settings.IsInstalled = true;
        _settingsService.Save();

        if (alreadyHad)
            StatusMessage = "OpenSteamTool is already installed — you're all set!";
        else
            StatusMessage = $"OpenSteamTool installed successfully! ({dllsCopied} DLLs, {dirsCreated} directories)";

        StatusIcon = "\u2705";
        IsComplete = true;
        
        Task.Delay(1500).ContinueWith(_ =>
        {
            WpfApp.Current.Dispatcher.Invoke(() =>
            {
                OnInstallationComplete?.Invoke();
            });
        });
    }

    public void RetryAsAdmin()
    {
        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath)) return;

        try
        {
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(psi);
            WpfApp.Current.Shutdown();
        }
        catch
        {
            StatusMessage = "Admin elevation was declined. Please run the app as administrator manually.";
            StatusIcon = "\u26A0\uFE0F";
            RequestsAdmin = false;
            IsInstalling = false;
        }
    }

    private static bool CheckAlreadyInstalled(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return false;
        // Only need to check for the main OpenSteamTool DLL — the proxy DLLs are optional
        return File.Exists(Path.Combine(directory, "OpenSteamTool.dll"));
    }

    private static bool IsRunningAsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public event Action? OnInstallationComplete;
}

public class OpenSteamToolInstaller
{
    private static readonly string OpenSteamToolSource = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "OpenSteamTool");

    public InstallResult Install(string steamDirectory, out bool needsAdmin)
    {
        needsAdmin = false;
        var result = new InstallResult();

        if (!Directory.Exists(OpenSteamToolSource))
        {
            result.ErrorMessage = $"OpenSteamTool folder not found at: {OpenSteamToolSource}";
            return result;
        }

        var dllFiles = Directory.GetFiles(OpenSteamToolSource, "*.dll");
        if (dllFiles.Length == 0)
        {
            result.ErrorMessage = "No DLL files found in OpenSteamTool folder.";
            return result;
        }

        foreach (var dllPath in dllFiles)
        {
            try
            {
                var destPath = Path.Combine(steamDirectory, Path.GetFileName(dllPath));
                File.Copy(dllPath, destPath, overwrite: true);
                result.DllsCopied++;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Failed to copy {Path.GetFileName(dllPath)}: {ex.Message}";
                return result;
            }
        }

        var luaPath = Path.Combine(steamDirectory, "config", "lua");
        var manifestPath = Path.Combine(steamDirectory, "config", "depotcache");

        try
        {
            if (!Directory.Exists(luaPath))
                Directory.CreateDirectory(luaPath);
            result.DirsCreated++;
        }
        catch (UnauthorizedAccessException)
        {
            needsAdmin = true;
            result.ErrorMessage = "Need admin permissions to create the Lua config directory.";
            return result;
        }

        try
        {
            if (!Directory.Exists(manifestPath))
                Directory.CreateDirectory(manifestPath);
            result.DirsCreated++;
        }
        catch (UnauthorizedAccessException)
        {
            needsAdmin = true;
            result.ErrorMessage = "Need admin permissions to create the depotcache directory.";
            return result;
        }

        result.Success = true;
        return result;
    }
}

public class InstallResult
{
    public bool Success { get; set; }
    public int DllsCopied { get; set; }
    public int DirsCreated { get; set; }
    public string ErrorMessage { get; set; } = "";
}