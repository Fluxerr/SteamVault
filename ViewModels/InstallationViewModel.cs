using Microsoft.Win32;
using SteamVault.Models;
using SteamVault.Services;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Windows.Input;
using WpfApp = System.Windows.Application;

namespace SteamVault.ViewModels;

public class InstallationViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly OpenSteamToolInstaller _installer;

    public InstallationViewModel(SettingsService settingsService, string? initialStatus = null)
    {
        _settingsService = settingsService;
        _installer = new OpenSteamToolInstaller();

        if (!string.IsNullOrWhiteSpace(_settingsService.Settings.Username))
        {
            _username = _settingsService.Settings.Username;
        }
        else
        {
            _username = string.IsNullOrWhiteSpace(Environment.UserName) ? "VaultUser" : Environment.UserName;
        }

        DetectSteamDirectory();
        if (!string.IsNullOrWhiteSpace(initialStatus))
        {
            StatusMessage = initialStatus;
            StatusIcon = "\u26A0\uFE0F";
        }
        
        BrowseCommand = new RelayCommand(_ => BrowseSteamDirectory());
        ConfirmCommand = new RelayCommand(_ => ConfirmAndInstall());
        RetryAsAdminCommand = new RelayCommand(_ => RetryAsAdmin());
        FinishSetupCommand = new RelayCommand(_ => FinishSetup());
    }

    private string _username = "VaultUser";
    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
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
    public ICommand FinishSetupCommand { get; }

    private void DetectSteamDirectory()
    {
        var configuredPath = _settingsService.Settings.SteamDirectory;
        if (!string.IsNullOrWhiteSpace(configuredPath)
            && Directory.Exists(configuredPath)
            && File.Exists(Path.Combine(configuredPath, "steam.exe")))
        {
            SteamDirectory = configuredPath;
            StatusMessage = "Using the configured Steam installation.";
            StatusIcon = "\u2705";
            return;
        }

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
                        StatusMessage = "Administrator permissions are required to refresh files in this Steam folder.";
                        StatusIcon = "\U0001F512";
                        IsInstalling = false;
                    }
                    else if (result.Success)
                    {
                        SaveSettingsAndComplete(result.DllsCopied, result.DirsCreated);
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

    private void SaveSettingsAndComplete(int dllsCopied, int dirsCreated)
    {
        _settingsService.Settings.SteamDirectory = SteamDirectory;
        _settingsService.Settings.LuaOutputPath = Path.Combine(SteamDirectory, "config", "lua");
        if (!string.IsNullOrWhiteSpace(Username))
        {
            _settingsService.Settings.Username = Username.Trim();
        }

        _settingsService.Settings.IsInstalled = true;
        _settingsService.Save();

        StatusMessage = $"OpenSteamTool files refreshed successfully! ({dllsCopied} DLLs, {dirsCreated} directories created)";

        StatusIcon = "\u2705";
        IsComplete = true;
    }

    private void FinishSetup()
    {
        if (string.IsNullOrWhiteSpace(Username))
        {
            Username = "VaultUser";
        }

        _settingsService.Settings.Username = Username.Trim();
        _settingsService.Save();

        OnInstallationComplete?.Invoke();
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
    private const string OpenSteamToolResourcePrefix = "SteamVault.OpenSteamTool.";

    public InstallResult Install(string steamDirectory, out bool needsAdmin)
    {
        needsAdmin = false;
        var result = new InstallResult();

        if (string.IsNullOrWhiteSpace(steamDirectory)
            || !Directory.Exists(steamDirectory)
            || !File.Exists(Path.Combine(steamDirectory, "steam.exe")))
        {
            result.ErrorMessage = "The configured Steam folder is invalid or does not contain steam.exe.";
            return result;
        }

        var assembly = typeof(OpenSteamToolInstaller).Assembly;
        var dllResources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(OpenSteamToolResourcePrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (dllResources.Length == 0)
        {
            result.ErrorMessage = "No bundled OpenSteamTool DLL resources were found.";
            return result;
        }

        var filesNeedRefresh = dllResources.Any(resourceName => !IsResourceCurrent(assembly, resourceName, steamDirectory));
        if (filesNeedRefresh && IsSteamRunning())
        {
            result.ErrorMessage = "Steam is running and may have these files loaded. Close Steam, then retry the refresh from Troubleshoot.";
            return result;
        }

        foreach (var resourceName in dllResources)
        {
            var dllName = resourceName[OpenSteamToolResourcePrefix.Length..];
            try
            {
                if (IsResourceCurrent(assembly, resourceName, steamDirectory))
                    continue;

                using var source = assembly.GetManifestResourceStream(resourceName);
                if (source is null)
                    throw new IOException($"Could not open bundled resource {resourceName}.");

                var destPath = Path.Combine(steamDirectory, dllName);
                using var destination = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                source.CopyTo(destination);
                result.DllsCopied++;
            }
            catch (Exception ex)
            {
                needsAdmin = ex is UnauthorizedAccessException;
                result.ErrorMessage = $"Failed to refresh {dllName}: {ex.Message}";
                return result;
            }
        }

        try
        {
            result.DirsCreated = EnsureRequiredDirectories(steamDirectory);
        }
        catch (Exception ex)
        {
            needsAdmin = ex is UnauthorizedAccessException;
            result.ErrorMessage = $"Could not create the required Steam folders: {ex.Message}";
            return result;
        }

        result.Success = true;
        return result;
    }

    public static bool IsSteamRunning()
    {
        var processes = Process.GetProcessesByName("steam");
        foreach (var process in processes)
            process.Dispose();
        return processes.Length > 0;
    }

    private static bool IsResourceCurrent(System.Reflection.Assembly assembly, string resourceName, string steamDirectory)
    {
        try
        {
            var destinationPath = Path.Combine(steamDirectory, resourceName[OpenSteamToolResourcePrefix.Length..]);
            if (!File.Exists(destinationPath))
                return false;

            using var bundledStream = assembly.GetManifestResourceStream(resourceName);
            if (bundledStream is null)
                return false;

            using var installedStream = File.OpenRead(destinationPath);
            return CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(bundledStream),
                SHA256.HashData(installedStream));
        }
        catch
        {
            return false;
        }
    }

    public static int EnsureRequiredDirectories(string steamDirectory)
    {
        if (string.IsNullOrWhiteSpace(steamDirectory)
            || !Directory.Exists(steamDirectory)
            || !File.Exists(Path.Combine(steamDirectory, "steam.exe")))
        {
            throw new DirectoryNotFoundException("The configured Steam folder is invalid or does not contain steam.exe.");
        }

        var directories = new[]
        {
            Path.Combine(steamDirectory, "config", "lua"),
            Path.Combine(steamDirectory, "config", "depotcache"),
            Path.Combine(steamDirectory, "depotcache")
        };

        int created = 0;
        foreach (var directory in directories)
        {
            if (Directory.Exists(directory))
                continue;

            Directory.CreateDirectory(directory);
            created++;
        }

        return created;
    }
}

public class InstallResult
{
    public bool Success { get; set; }
    public int DllsCopied { get; set; }
    public int DirsCreated { get; set; }
    public string ErrorMessage { get; set; } = "";
}
