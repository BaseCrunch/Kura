using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kura.App.Services;
using Kura.Core.Data;
using Kura.Core.Models;
using Kura.Core.Net;

namespace Kura.App.ViewModels;

public sealed record PinThumb(PostItemViewModel Item, double Width, double Height);

/// <summary>One saved search on the home page: title, server and a live strip of its newest thumbnails.</summary>
public sealed partial class PinViewModel : ObservableObject
{
    public const double StripHeight = 150;
    private const int MaxThumbs = 14;
    private readonly AppServices _services;
    private readonly HomeViewModel _home;
    private CancellationTokenSource? _cts;

    public PinViewModel(AppServices services, HomeViewModel home, Pin pin, Server? server)
    {
        _services = services;
        _home = home;
        Pin = pin;
        Server = server;
    }

    public Pin Pin { get; private set; }
    public Server? Server { get; }
    public string Title => Pin.Title;
    public string Query => Pin.Query;
    public string ServerLabel => Server?.Url ?? "Server removed";
    public ObservableCollection<PinThumb> Thumbs { get; } = [];
    public bool HasLoaded { get; private set; }

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _message;

    public void Update(Pin pin)
    {
        Pin = pin;
        OnPropertyChanged(nameof(Title));
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (Server is null) { Message = "This pin's server was deleted."; return; }
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsLoading = true;
        Message = null;
        try
        {
            var client = _services.Client;
            var ctx = client.CreateContext(Server, _services.Credentials.Get(Server.Id));
            var engine = client.Engines.Get(Server.Engine);
            var page = await client.FetchPostsAsync(ctx, client.Prepare(ctx, Query), engine.FirstPage, cts.Token);
            if (cts.IsCancellationRequested) return;
            Thumbs.Clear();
            var index = 0;
            foreach (var post in page.Posts)
            {
                var item = new PostItemViewModel(post, index++, 1, Server.Name, post.Tags) { IsFavorite = post.IsFavorite };
                if (item.ThumbUrl is null) continue;
                Thumbs.Add(new PinThumb(item, Math.Clamp(item.Aspect * StripHeight, 70, 320), StripHeight));
                if (Thumbs.Count >= MaxThumbs) break;
            }
            Message = Thumbs.Count == 0 ? page.ServerMessage ?? "No posts." : null;
            HasLoaded = true;
        }
        catch (OperationCanceledException) { }
        catch (BooruRequestException ex) { Message = ex.Message; }
        catch (NotSupportedException ex) { Message = ex.Message; }
        finally
        {
            if (!cts.IsCancellationRequested) IsLoading = false;
        }
    }

    [RelayCommand]
    private Task OpenAsync() => Server is null ? Task.CompletedTask : _home.Main.OpenSearchAsync(Server, Query, null);

    public Task OpenAtAsync(PinThumb thumb) =>
        Server is null ? Task.CompletedTask : _home.Main.OpenSearchAsync(Server, Query, thumb.Item.Index);

    [RelayCommand] private void MoveUp() => _home.Move(this, -1);
    [RelayCommand] private void MoveDown() => _home.Move(this, +1);
    [RelayCommand] private void Remove() => _home.Remove(this);
}

/// <summary>The home page: saved searches ("pins", SPEC 3.2) plus a small form to add one.</summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly AppServices _services;

    public HomeViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        Main = main;
        Reload();
    }

    public MainViewModel Main { get; }
    public ObservableCollection<PinViewModel> Pins { get; } = [];
    public bool IsEmpty => Pins.Count == 0;

    [ObservableProperty] private bool _isAddOpen;
    [ObservableProperty] private string _newQuery = "";
    [ObservableProperty] private Server? _newServer;

    /// <summary>Rebuilds the card list from the database, keeping already-loaded strips.</summary>
    public void Reload()
    {
        var existing = Pins.ToDictionary(p => p.Pin.Id);
        Pins.Clear();
        foreach (var pin in _services.Pins.GetAll())
        {
            var server = Main.Servers.FirstOrDefault(s => s.Id == pin.ServerId);
            if (existing.TryGetValue(pin.Id, out var vm) && vm.Server?.Id == server?.Id)
            {
                vm.Update(pin);
                Pins.Add(vm);
            }
            else Pins.Add(new PinViewModel(_services, this, pin, server));
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Loads strips that haven't loaded yet (the original's "auto load pins").</summary>
    public void EnsureLoaded()
    {
        foreach (var p in Pins.Where(p => !p.HasLoaded && !p.IsLoading)) _ = p.RefreshAsync();
    }

    [RelayCommand]
    private void RefreshAll()
    {
        foreach (var p in Pins) _ = p.RefreshAsync();
    }

    [RelayCommand]
    private void ToggleAdd()
    {
        IsAddOpen = !IsAddOpen;
        if (IsAddOpen)
        {
            NewQuery = "";
            NewServer = Main.SelectedTab?.SelectedServer ?? Main.LastUsedServer ?? Main.Servers.FirstOrDefault();
        }
    }

    [RelayCommand]
    private void AddPin()
    {
        if (NewServer is null) return;
        Main.Pin(NewServer, NewQuery);
        IsAddOpen = false;
    }

    public void Move(PinViewModel pin, int delta)
    {
        _services.Pins.Move(pin.Pin.Id, delta);
        Reload();
    }

    public void Remove(PinViewModel pin)
    {
        _services.Pins.Remove(pin.Pin.Id);
        Reload();
        Main.RefreshPinStates();
    }
}
