using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kura.App.Services;
using Kura.Core.Models;
using Kura.Core.Net;

namespace Kura.App.ViewModels;

public sealed record EngineOption(EngineType Type, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Minimal server manager: list, add/edit/delete, test connection. Secrets go to Credential Manager.</summary>
public sealed partial class ServerEditorViewModel : ObservableObject
{
    private readonly AppServices _services;
    private CancellationTokenSource? _testCts;

    public ServerEditorViewModel(AppServices services)
    {
        _services = services;
        Engines = services.Client.Engines.Supported.OrderBy(e => e.DisplayName()).Select(e => new EngineOption(e, e.DisplayName())).ToList();
        Reload();
        Selected = Servers.FirstOrDefault();
    }

    public ObservableCollection<Server> Servers { get; } = [];
    public IReadOnlyList<EngineOption> Engines { get; }
    public bool Changed { get; private set; }

    [ObservableProperty] private Server? _selected;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private EngineOption? _engine;
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private bool _ratingFilter;
    [ObservableProperty] private bool _nativeAutocomplete = true;
    [ObservableProperty] private bool _isDefault;
    [ObservableProperty] private bool _isNew;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private bool _isTesting;

    /// <summary>Raised when the API key field should be refreshed (PasswordBox can't be bound).</summary>
    public event Action<string>? ApiKeyLoaded;

    public string UsernameLabel => Engine?.Type == EngineType.Gelbooru ? "User ID" : "Username";

    public string CredentialHint => Engine?.Type switch
    {
        EngineType.Gelbooru => "Find your API key and user ID under My Account → Options on the site. Leave both empty to browse anonymously.",
        EngineType.E621 => "Generate an API key under Account → Manage API Access. Your username is also sent in the User-Agent, as e621 requires.",
        EngineType.Danbooru2 => "Create an API key on your profile page (My Account → API Key). Optional for browsing.",
        _ => "",
    };

    partial void OnEngineChanged(EngineOption? value)
    {
        OnPropertyChanged(nameof(UsernameLabel));
        OnPropertyChanged(nameof(CredentialHint));
    }

    partial void OnSelectedChanged(Server? value)
    {
        Status = null;
        if (value is null) return;
        IsNew = false;
        Name = value.Name;
        Url = value.Url;
        Engine = Engines.FirstOrDefault(e => e.Type == value.Engine);
        Username = value.Username ?? "";
        RatingFilter = value.RatingFilterEnabled;
        NativeAutocomplete = value.UseNativeAutocomplete;
        IsDefault = value.IsDefault;
        ApiKey = _services.Credentials.Get(value.Id).ApiKey ?? "";
        ApiKeyLoaded?.Invoke(ApiKey);
    }

    private void Reload(long? selectId = null)
    {
        Servers.Clear();
        foreach (var s in _services.Servers.GetAll()) Servers.Add(s);
        if (selectId is { } id) Selected = Servers.FirstOrDefault(s => s.Id == id);
    }

    [RelayCommand]
    private void New()
    {
        Selected = null;
        IsNew = true;
        IsDefault = false;
        Name = "";
        Url = "https://";
        Engine = Engines.FirstOrDefault(e => e.Type == EngineType.Danbooru2);
        Username = "";
        ApiKey = "";
        ApiKeyLoaded?.Invoke("");
        RatingFilter = false;
        NativeAutocomplete = true;
        Status = null;
    }

    private Server? BuildFromForm(out string? error)
    {
        error = null;
        var url = Url.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            error = "Enter a full URL starting with https://";
            return null;
        }
        if (Engine is null)
        {
            error = "Choose an engine type.";
            return null;
        }
        return new Server
        {
            Id = Selected?.Id ?? 0,
            Name = string.IsNullOrWhiteSpace(Name) ? uri.Host : Name.Trim(),
            Url = url,
            Engine = Engine.Type,
            Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
            RatingFilterEnabled = RatingFilter,
            UseNativeAutocomplete = NativeAutocomplete,
            IsDefault = Selected?.IsDefault ?? false,
            SortOrder = Selected?.SortOrder ?? Servers.Count,
            CreatedAt = Selected?.CreatedAt ?? DateTimeOffset.UtcNow,
        };
    }

    [RelayCommand]
    private void Save()
    {
        var server = BuildFromForm(out var error);
        if (server is null) { SetStatus(error!, true); return; }
        try
        {
            _services.Servers.Save(server);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19) // UNIQUE constraint
        {
            SetStatus("A server with this URL already exists.", true);
            return;
        }
        _services.Credentials.Set(server.Id, new ServerCredentials(string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim(), null));
        Changed = true;
        Reload(server.Id);
        SetStatus("Saved.", false);
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is not { } s || s.IsDefault) return;
        if (!Shell.Confirm($"Delete \"{s.Name}\"? Its saved API key is removed too.")) return;
        _services.Servers.Delete(s.Id);
        _services.Credentials.Delete(s.Id);
        Changed = true;
        Reload();
        Selected = Servers.FirstOrDefault();
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        var server = BuildFromForm(out var error);
        if (server is null) { SetStatus(error!, true); return; }
        _testCts?.Cancel();
        var cts = _testCts = new CancellationTokenSource();
        IsTesting = true;
        SetStatus("Testing…", false);
        try
        {
            var ctx = _services.Client.CreateContext(server, new ServerCredentials(string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim(), null));
            var msg = await _services.Client.PingAsync(ctx, cts.Token);
            SetStatus(msg, false);
        }
        catch (BooruRequestException ex) { SetStatus(ex.Message, true); }
        catch (OperationCanceledException) { }
        finally { IsTesting = false; }
    }

    private void SetStatus(string text, bool isError)
    {
        Status = text;
        StatusIsError = isError;
    }
}
