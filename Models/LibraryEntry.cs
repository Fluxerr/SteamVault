using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SteamVault.Models;

public class LibraryEntry : INotifyPropertyChanged
{
    public string AppId { get; set; } = "";

    private string _name = "Unknown";
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    private string _headerImageUrl = "";
    public string HeaderImageUrl
    {
        get => _headerImageUrl;
        set { _headerImageUrl = value; OnPropertyChanged(); }
    }
    public DateTime LastUpdated { get; set; }
    public ObservableCollection<LibraryDepotInfo> Depots { get; set; } = new();
    public string LuaFilePath { get; set; } = "";

    private string _status = "Checking...";
    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(CanUpdate));
        }
    }

    public string StatusColor => Status switch
    {
        "Up to Date" => "#22C55E",
        "Update Available" => "#F59E0B",
        "Checking..." => "#64748B",
        _ => "#EF4444"
    };

    public System.Windows.Media.Brush StatusBrush => new System.Windows.Media.SolidColorBrush(
        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(StatusColor));

    private bool _isUpdating;
    public bool IsUpdating
    {
        get => _isUpdating;
        set { _isUpdating = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanUpdate)); }
    }

    public bool CanUpdate => !IsUpdating;

    private double _updateProgress;
    public double UpdateProgress
    {
        get => _updateProgress;
        set { _updateProgress = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class LibraryDepotInfo
{
    public string DepotId { get; set; } = "";
    public string? ManifestId { get; set; }
    public string? DecryptionKey { get; set; }
}