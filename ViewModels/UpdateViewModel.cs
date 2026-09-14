using SteamVault.Models;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SteamVault.ViewModels;

public class UpdateViewModel : INotifyPropertyChanged
{
    private string _statusText = "Checking for updates...";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        set { _progressValue = value; OnPropertyChanged(); }
    }

    private bool _isIndeterminate = true;
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set { _isIndeterminate = value; OnPropertyChanged(); }
    }

    private string _versionInfo = "";
    public string VersionInfo
    {
        get => _versionInfo;
        set { _versionInfo = value; OnPropertyChanged(); }
    }

    public void UpdateProgress(double percentage)
    {
        IsIndeterminate = false;
        ProgressValue = percentage;
        StatusText = $"Downloading update... {percentage:0.0}%";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
