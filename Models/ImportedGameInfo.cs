using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SteamVault.Models;

/// <summary>
/// Represents a single game that was imported via drag-and-drop.
/// Used for both single-lua imports and multi-game zip imports.
/// Implements INotifyPropertyChanged for live UI updates during import.
/// </summary>
public class ImportedGameInfo : INotifyPropertyChanged
{
    public string AppId { get; set; } = "";

    private string _gameName = "Unknown";
    public string GameName
    {
        get => _gameName;
        set { _gameName = value; OnPropertyChanged(); }
    }

    private string _headerImageUrl = "";
    public string HeaderImageUrl
    {
        get => _headerImageUrl;
        set { _headerImageUrl = value; OnPropertyChanged(); }
    }

    private int _manifestCount;
    public int ManifestCount
    {
        get => _manifestCount;
        set { _manifestCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(ManifestText)); }
    }

    public string ManifestText => ManifestCount > 0 ? $"{ManifestCount} manifest(s)" : "No manifests";

    private bool _success;
    public bool Success
    {
        get => _success;
        set { _success = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusIcon)); OnPropertyChanged(nameof(StatusColor)); }
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; OnPropertyChanged(); }
    }

    private string _status = "Queued";
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsProcessing)); OnPropertyChanged(nameof(StatusIcon)); OnPropertyChanged(nameof(StatusColor)); }
    }

    /// <summary>
    /// True while this game is actively being processed (resolving, downloading manifests, etc.)
    /// </summary>
    public bool IsProcessing => Status == "Processing" || Status == "Resolving" || Status == "Downloading Manifests";

    /// <summary>
    /// Segoe Fluent Icons glyph for current status.
    /// </summary>
    public string StatusIcon => Status switch
    {
        "Queued" => "\uE823",        // Clock
        "Processing" => "\uE896",    // Download
        "Resolving" => "\uE721",     // Search
        "Downloading Manifests" => "\uE896", // Download
        "Complete" => "\uE73E",      // Checkmark
        "Failed" => "\uE783",        // Error
        _ => "\uE823"
    };

    /// <summary>
    /// Status-dependent color key for theming.
    /// </summary>
    public string StatusColor => Status switch
    {
        "Complete" => "Success",
        "Failed" => "Error",
        "Queued" => "Dim",
        _ => "Accent"
    };

    public string LuaSourcePath { get; set; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
