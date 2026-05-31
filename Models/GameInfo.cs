using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SteamVault.Models;

public class GameInfo
{
    public string AppId { get; set; } = "";
    public string Name { get; set; } = "Unknown";
    public string HeaderImageUrl { get; set; } = "";
    public string ShortDescription { get; set; } = "";
    public string Type { get; set; } = "game";
    public bool IsFree { get; set; }
    public List<string> Developers { get; set; } = new();
    public List<string> Publishers { get; set; } = new();
    public List<string> Genres { get; set; } = new();
    public List<DepotInfo> Depots { get; set; } = new();
    public List<DlcInfo> Dlc { get; set; } = new();
    public string? AppAccessToken { get; set; }
    public string ReleaseDate { get; set; } = "";
}

public class DepotInfo : INotifyPropertyChanged
{
    private string _depotId = "";
    private string? _decryptionKey;
    private string? _manifestId;
    private string _status = "Ready";

    public string DepotId
    {
        get => _depotId;
        set => SetProperty(ref _depotId, value);
    }

    public string? DecryptionKey
    {
        get => _decryptionKey;
        set => SetProperty(ref _decryptionKey, value);
    }

    public string? ManifestId
    {
        get => _manifestId;
        set
        {
            if (SetProperty(ref _manifestId, value))
            {
                OnPropertyChanged(nameof(HasManifest));
            }
        }
    }

    public bool HasKey => !string.IsNullOrEmpty(DecryptionKey);
    public bool HasManifest => !string.IsNullOrEmpty(ManifestId);
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class DlcInfo
{
    public string AppId { get; set; } = "";
    public string Name { get; set; } = "";
    public string HeaderImageUrl { get; set; } = "";
}
