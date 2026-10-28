using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SteamVault.Models;
using SteamVault.Services;

namespace SteamVault.ViewModels;

public class WhatsNewViewModel : INotifyPropertyChanged
{
    private string _version = "2.6.1";
    public string Version
    {
        get => _version;
        set
        {
            _version = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VersionTitle));
        }
    }

    public string VersionTitle => $"SteamVault v{Version}";

    private string _releaseSubtitle = "Here's what changed in this version";
    public string ReleaseSubtitle
    {
        get => _releaseSubtitle;
        set { _releaseSubtitle = value; OnPropertyChanged(); }
    }

    private ObservableCollection<WhatsNewItem> _items = new();
    public ObservableCollection<WhatsNewItem> Items
    {
        get => _items;
        set { _items = value; OnPropertyChanged(); }
    }

    public void LoadReleaseNotes(string version, string? rawReleaseBody = null)
    {
        Version = version;
        Items = ReleaseNotesService.GetReleaseItems(version, rawReleaseBody);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
