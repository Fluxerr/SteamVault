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
    /// <summary>
    /// OpenSteamTool v1.4.7+ AppTicket hex string (for SteamStub-only games).
    /// Extracted via the extract_tickets tool. When set, setAppTicket() is written to Lua.
    /// </summary>
    public string? AppTicket { get; set; }
    /// <summary>
    /// OpenSteamTool v1.4.7+ ETicket hex string.
    /// Extracted via the extract_tickets tool. When set, setETicket() is written to Lua.
    /// </summary>
    public string? ETicket { get; set; }
    public string ReleaseDate { get; set; } = "";
    /// <summary>
    /// Categories from Steam API (e.g. "Single-player", "Multi-player", "Co-op").
    /// </summary>
    public List<string> Categories { get; set; } = new();
    /// <summary>
    /// True if the game supports multiplayer categories (Multi-player, Co-op, Online PvP, etc.).
    /// Used to display a warning that cracked copies are singleplayer only.
    /// </summary>
    public bool HasMultiplayerCategories
    {
        get
        {
            var mpKeywords = new[] { "multi-player", "co-op", "online", "pvp", "multiplayer", "cross-platform" };
            return Categories.Any(c => mpKeywords.Any(kw => c.Contains(kw, StringComparison.OrdinalIgnoreCase)));
        }
    }
}

public class DepotInfo : INotifyPropertyChanged
{
    private string _depotId = "";
    private string? _decryptionKey;
    private string? _manifestId;
    private string _status = "Ready";
    private long _sizeBytes;

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

    /// <summary>
    /// Estimated depot size in bytes (from SteamCMD maxsize field).
    /// </summary>
    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (SetProperty(ref _sizeBytes, value))
            {
                OnPropertyChanged(nameof(SizeFormatted));
            }
        }
    }

    /// <summary>
    /// Human-readable depot size (e.g. "12.4 GB", "856 MB").
    /// </summary>
    public string SizeFormatted
    {
        get
        {
            if (_sizeBytes <= 0) return "Unknown";
            if (_sizeBytes >= 1_073_741_824)
                return $"{_sizeBytes / 1_073_741_824.0:F1} GB";
            if (_sizeBytes >= 1_048_576)
                return $"{_sizeBytes / 1_048_576.0:F0} MB";
            if (_sizeBytes >= 1_024)
                return $"{_sizeBytes / 1_024.0:F0} KB";
            return $"{_sizeBytes} B";
        }
    }

    /// <summary>
    /// If this depot belongs to a DLC, this is the DLC's App ID (from the dlcappid field in steamcmd).
    /// </summary>
    private string? _dlcAppId;
    public string? DlcAppId
    {
        get => _dlcAppId;
        set => SetProperty(ref _dlcAppId, value);
    }

    /// <summary>
    /// True if this depot belongs to a DLC (has a dlcappid).
    /// </summary>
    public bool IsDlcDepot => !string.IsNullOrWhiteSpace(DlcAppId);

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

/// <summary>
/// SteamDB-like stats: player count and review scores for a game.
/// </summary>
public class SteamDbStats
{
    public int CurrentPlayers { get; set; }
    public int MetacriticScore { get; set; }
    public int TotalReviews { get; set; }
    public double PositiveReviewPercent { get; set; }

    public bool HasPlayers => CurrentPlayers > 0;
    public bool HasScore => MetacriticScore > 0 || TotalReviews > 0;
}

/// <summary>
/// Detailed depot info from api.steamcmd.net per-depot endpoint.
/// </summary>
public class DepotDetail
{
    public string DepotId { get; set; } = "";
    public string? DecryptionKey { get; set; }
}
