using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using SteamVault.Models;
using SteamVault.Services;

namespace SteamVault.ViewModels;

public class FamilyVaultViewModel : ViewModelBase
{
    private readonly FamilyVaultService? _familyService;
    private readonly SettingsService? _settingsService;
    private readonly SteamApiService? _steamApiService;
    private readonly GameSearchService? _searchService;

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _debounceCts;

    public FamilyVaultViewModel(
        FamilyVaultService familyService,
        SettingsService settingsService,
        SteamApiService steamApiService,
        GameSearchService searchService)
    {
        _familyService = familyService;
        _settingsService = settingsService;
        _steamApiService = steamApiService;
        _searchService = searchService;

        var membersList = _familyService?.Manifest?.Members ?? new List<FamilyMember>();
        var gamesList = _familyService?.Manifest?.Games ?? new List<FamilyGameItem>();

        Members = new ObservableCollection<FamilyMember>(membersList.Where(m => m != null));
        FamilyGames = new ObservableCollection<FamilyGameItem>(gamesList.Where(g => g != null));

        RemoveMemberCommand = new RelayCommand(param => RemoveMember(param as string));
        RemoveGameCommand = new RelayCommand(param => RemoveGame(param));
        CloseFamilyCommand = new RelayCommand(_ => CloseFamily());
        LeaveFamilyCommand = new RelayCommand(_ => LeaveFamily());
        ContributeGameCommand = new RelayCommand(async _ => await ContributeGameAsync());
        SelectSearchResultCommand = new RelayCommand(async param => await SelectSearchResultAsync(param as SearchResult));
        SyncAllCommand = new RelayCommand(async _ => await SyncAllGamesAsync());
        ExportTokenCommand = new RelayCommand(async _ => await ExportTokenAsync());
        ImportTokenCommand = new RelayCommand(async _ => await ImportTokenAsync());
        RefreshCommand = new RelayCommand(async _ => await ManualRefreshOnlineAsync());

        RefreshStats();

        _ = Task.Run(async () =>
        {
            if (_familyService != null)
            {
                await _familyService.EnsureCloudRoomAsync();
            }
            StartLiveSyncLoop();
        });
    }

    public ObservableCollection<FamilyMember> Members { get; }
    public ObservableCollection<FamilyGameItem> FamilyGames { get; }
    public ObservableCollection<SearchResult> SearchResults { get; } = new();

    public ICommand RemoveMemberCommand { get; }
    public ICommand RemoveGameCommand { get; }
    public ICommand CloseFamilyCommand { get; }
    public ICommand LeaveFamilyCommand { get; }
    public ICommand ContributeGameCommand { get; }
    public ICommand SelectSearchResultCommand { get; }
    public ICommand SyncAllCommand { get; }
    public ICommand ExportTokenCommand { get; }
    public ICommand ImportTokenCommand { get; }
    public ICommand RefreshCommand { get; }

    public string FamilyName => _familyService?.Manifest?.FamilyName ?? "Family Vault";
    public string FamilyId => _familyService?.Manifest?.FamilyId ?? "";
    public string UserUsername => _settingsService?.Settings?.Username ?? "VaultUser";

    public bool IsHost
    {
        get
        {
            var hostName = _familyService?.Manifest?.HostUsername;
            var myName = UserUsername;
            if (string.IsNullOrWhiteSpace(hostName)) return true;
            return string.Equals(hostName, myName, StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool IsMember => !IsHost;

    private string _newMemberNameInput = "";
    public string NewMemberNameInput
    {
        get => _newMemberNameInput;
        set => SetProperty(ref _newMemberNameInput, value);
    }

    private string _importTokenInput = "";
    public string ImportTokenInput
    {
        get => _importTokenInput;
        set => SetProperty(ref _importTokenInput, value);
    }

    private string _searchQuery = "";
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                TriggerSearch(value);
            }
        }
    }

    private bool _showSearchResults;
    public bool ShowSearchResults
    {
        get => _showSearchResults;
        set => SetProperty(ref _showSearchResults, value);
    }

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        set => SetProperty(ref _isSearching, value);
    }

    private bool _isSyncing;
    public bool IsSyncing
    {
        get => _isSyncing;
        set => SetProperty(ref _isSyncing, value);
    }

    private double _syncProgress;
    public double SyncProgress
    {
        get => _syncProgress;
        set => SetProperty(ref _syncProgress, value);
    }

    private string _statusMessage = "Search for a game to add to your Family Pool or sync Lua configs.";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public int TotalGamesCount => FamilyGames.Count;
    public int TotalMembersCount => Members.Count;

    private void RefreshAll()
    {
        if (_familyService == null) return;
        _familyService.LoadManifest();

        Members.Clear();
        foreach (var m in _familyService.Manifest.Members.Where(m => m != null))
            Members.Add(m);

        FamilyGames.Clear();
        foreach (var g in _familyService.Manifest.Games.Where(g => g != null))
            FamilyGames.Add(g);

        StatusMessage = "Family Vault refreshed.";
        RefreshStats();
    }

    private void RefreshStats()
    {
        OnPropertyChanged(nameof(TotalGamesCount));
        OnPropertyChanged(nameof(TotalMembersCount));
        OnPropertyChanged(nameof(UserUsername));
        OnPropertyChanged(nameof(FamilyName));
        OnPropertyChanged(nameof(FamilyId));
        OnPropertyChanged(nameof(IsHost));
        OnPropertyChanged(nameof(IsMember));
    }

    private void CloseFamily()
    {
        if (_familyService == null) return;
        _familyService.CloseFamily();

        Members.Clear();
        foreach (var m in _familyService.Manifest.Members) Members.Add(m);

        FamilyGames.Clear();
        StatusMessage = "Family Vault closed. All shared members removed and new lobby code generated.";
        RefreshStats();
    }

    private void LeaveFamily()
    {
        if (_familyService == null) return;
        _familyService.LeaveFamily();

        Members.Clear();
        foreach (var m in _familyService.Manifest.Members) Members.Add(m);

        FamilyGames.Clear();
        StatusMessage = "Left Family Vault. Reset to empty solo lobby.";
        RefreshStats();
    }

    private void TriggerSearch(string query)
    {
        _debounceCts?.Cancel();

        if (string.IsNullOrWhiteSpace(query) || query.Length < 1 || _searchService == null)
        {
            ShowSearchResults = false;
            SearchResults.Clear();
            return;
        }

        var cts = new CancellationTokenSource();
        _debounceCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, cts.Token);
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    await PerformSearchAsync(query);
                });
            }
            catch (OperationCanceledException) { }
        });
    }

    private async Task PerformSearchAsync(string query)
    {
        if (_searchService == null) return;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();

        IsSearching = true;

        try
        {
            var results = await _searchService.SearchAsync(query, _searchCts.Token);

            SearchResults.Clear();
            foreach (var r in results)
                SearchResults.Add(r);

            ShowSearchResults = SearchResults.Count > 0;
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsSearching = false;
        }
    }

    private async Task SelectSearchResultAsync(SearchResult? result)
    {
        if (result == null) return;

        ShowSearchResults = false;
        SearchResults.Clear();
        SearchQuery = "";

        if (uint.TryParse(result.AppId, out uint appId))
        {
            await AddGameByIdAsync(appId, result.Name, result.ImageUrl);
        }
    }

    private async Task ContributeGameAsync()
    {
        var input = SearchQuery.Trim();
        if (string.IsNullOrWhiteSpace(input)) return;

        ShowSearchResults = false;
        SearchResults.Clear();

        if (uint.TryParse(input, out uint appId))
        {
            SearchQuery = "";
            await AddGameByIdAsync(appId, null, null);
        }
        else if (SearchResults.Count > 0)
        {
            var topResult = SearchResults[0];
            SearchQuery = "";
            await SelectSearchResultAsync(topResult);
        }
        else
        {
            StatusMessage = "Enter a valid App ID or select a game from search results.";
        }
    }

    private async Task AddGameByIdAsync(uint appId, string? fallbackTitle, string? fallbackBanner)
    {
        if (_familyService == null) return;

        if (FamilyGames.Any(g => g.AppId == appId))
        {
            StatusMessage = $"AppID {appId} is already in the Family Pool!";
            return;
        }

        IsBusy = true;
        StatusMessage = $"Fetching AppID {appId} details & generating Lua configs...";

        try
        {
            var appIdStr = appId.ToString();
            string title = fallbackTitle ?? $"AppID {appIdStr}";
            string banner = fallbackBanner ?? $"https://cdn.akamai.steamstatic.com/steam/apps/{appIdStr}/header.jpg";

            if (_steamApiService != null)
            {
                try
                {
                    var gameInfo = await _steamApiService.GetAppDetailsAsync(appIdStr);
                    if (gameInfo != null)
                    {
                        if (!string.IsNullOrWhiteSpace(gameInfo.Name)) title = gameInfo.Name;
                        if (!string.IsNullOrWhiteSpace(gameInfo.HeaderImageUrl)) banner = gameInfo.HeaderImageUrl;
                    }
                }
                catch { }
            }

            if (_familyService.AddGameToFamily(appId, title, banner, UserUsername))
            {
                FamilyGames.Clear();
                foreach (var g in _familyService.Manifest.Games) FamilyGames.Add(g);

                // Download / sync lua configs for all family games with progress
                await SyncAllGamesAsync();

                StatusMessage = $"✓ Added '{title}' ({appId}) to Family Pool & synced Lua configs!";
                RefreshStats();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error adding game: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RemoveMember(string? memberId)
    {
        if (string.IsNullOrWhiteSpace(memberId) || _familyService == null) return;

        if (_familyService.RemoveMember(memberId))
        {
            Members.Clear();
            foreach (var m in _familyService.Manifest.Members) Members.Add(m);
            StatusMessage = "Member removed.";
            RefreshStats();
        }
    }

    private void RemoveGame(object? param)
    {
        if (_familyService == null || param == null) return;

        uint appId = 0;
        string? gameName = null;

        if (param is FamilyGameItem item)
        {
            appId = item.AppId;
            gameName = item.Name;
        }
        else if (param is uint id)
        {
            appId = id;
        }
        else if (param is int i)
        {
            appId = (uint)i;
        }
        else if (param is long l)
        {
            appId = (uint)l;
        }
        else if (param is string str && uint.TryParse(str, out uint parsedId))
        {
            appId = parsedId;
        }

        if (appId == 0) return;

        var target = FamilyGames.FirstOrDefault(g => g.AppId == appId);
        if (target != null)
        {
            gameName ??= target.Name;
        }

        if (_familyService.RemoveGameFromFamily(appId))
        {
            if (target != null)
            {
                FamilyGames.Remove(target);
            }
            else
            {
                FamilyGames.Clear();
                foreach (var g in _familyService.Manifest.Games) FamilyGames.Add(g);
            }

            StatusMessage = $"✓ Removed '{gameName ?? appId.ToString()}' ({appId}) from Family Pool.";
            RefreshStats();
        }
    }

    private async Task SyncAllGamesAsync()
    {
        if (_familyService == null) return;

        IsSyncing = true;
        SyncProgress = 0;
        StatusMessage = "Preparing to sync all family games to Steam...";

        try
        {
            await _familyService.SyncAllFamilyGamesToLuaAsync(
                onStatus: msg => System.Windows.Application.Current.Dispatcher.Invoke(() => StatusMessage = msg),
                onProgress: pct => System.Windows.Application.Current.Dispatcher.Invoke(() => SyncProgress = pct)
            );
        }
        catch (Exception ex)
        {
            StatusMessage = $"Sync failed: {ex.Message}";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private async Task ManualRefreshOnlineAsync()
    {
        if (_familyService == null) return;
        IsBusy = true;
        StatusMessage = "Checking online Family Vault room for updates...";

        try
        {
            await _familyService.FetchCloudRoomUpdateAsync();
            RefreshAll();
            StatusMessage = "✓ Family Vault updated from online cloud room.";
        }
        catch { }
        finally
        {
            IsBusy = false;
        }
    }

    private void StartLiveSyncLoop()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await Task.Delay(8000);
                    if (_familyService != null)
                    {
                        bool updated = await _familyService.FetchCloudRoomUpdateAsync();
                        if (updated)
                        {
                            await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                            {
                                RefreshAll();
                                await SyncAllGamesAsync();
                            });
                        }
                    }
                }
                catch { }
            }
        });
    }

    private async Task ExportTokenAsync()
    {
        if (_familyService == null) return;
        IsBusy = true;
        StatusMessage = "Generating Family Lobby Code...";

        try
        {
            var token = await _familyService.ExportFamilyTokenAsync();
            System.Windows.Clipboard.SetText(token);
            StatusMessage = "✓ Family Lobby Code copied to clipboard! Send this code to your friends to join your room.";
        }
        catch
        {
            StatusMessage = "Failed to copy invite code.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportTokenAsync()
    {
        if (string.IsNullOrWhiteSpace(ImportTokenInput) || _familyService == null) return;

        IsBusy = true;
        StatusMessage = "Connecting to online Family Vault room...";

        try
        {
            var token = ImportTokenInput.Trim();
            if (await _familyService.ImportFamilyTokenAsync(token))
            {
                ImportTokenInput = "";
                RefreshAll();
                StatusMessage = "✓ Successfully joined Family Vault online! Syncing games...";
                await SyncAllGamesAsync();
            }
            else
            {
                StatusMessage = "Invalid or expired Invite Code. Make sure the code is correct.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to join lobby: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}

