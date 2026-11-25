using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kura.App.Services;
using Kura.Core.Models;
using Kura.Core.Net;

namespace Kura.App.ViewModels;

public sealed record EngineOption(EngineType Type, string Name, bool IsSupported)
{
    public string Label => IsSupported ? Name : $"{Name}  ·  coming soon";
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
        Engines = EngineOrder
            .Select(e => new EngineOption(e, e.DisplayName(), services.Client.Engines.IsSupported(e)))
            .ToList();
        Reload();
        Selected = Servers.FirstOrDefault();
    }

    /// <summary>Same engine list and order as the original app's server form.</summary>
    private static readonly EngineType[] EngineOrder =
    [
        EngineType.Gelbooru, EngineType.Danbooru2, EngineType.Danbooru1, EngineType.Gelbooru011, EngineType.Philomena,
        EngineType.E621, EngineType.Hydrus, EngineType.Shimmie, EngineType.Kemono, EngineType.BooruOnRails,
    ];

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

    public string UsernameLabel => Engine?.Type switch
    {
        EngineType.Gelbooru => "User ID",
        EngineType.Danbooru2 or EngineType.E621 or EngineType.Danbooru1 or null => "Username",
        _ => "Username (not used)",
    };

    /// <summary>Danbooru 1 signs in with a password (hashed per host); Hydrus calls its key an access key.</summary>
    public string SecretLabel => Engine?.Type switch
    {
        EngineType.Danbooru1 => "Password",
        EngineType.Hydrus => "Access key",
        EngineType.Kemono or EngineType.Shimmie or EngineType.Gelbooru011 => "API key (not used)",
        _ => "API key",
    };

    public string CredentialHint => Engine?.Type switch
    {
        EngineType.Gelbooru => "Find your API key and user ID under My Account → Options on the site. You can also paste the whole " +
                               "\"&api_key=…&user_id=…\" string into the API key field; Kura splits it on save. Leave both empty to browse anonymously.",
        EngineType.E621 => "Generate an API key under Account → Manage API Access. Your username is also sent in the User-Agent, as e621 requires.",
        EngineType.Danbooru2 => "Create an API key on your profile page (My Account → API Key). Optional for browsing.",
        EngineType.Danbooru1 => "Moebooru sites (konachan, yande.re…) sign in with your username and password. Only a salted hash of " +
                                "the password is ever sent.",
        EngineType.Philomena => "Optional: your API key (Account → Settings) applies your filters and shows your faves. " +
                                "Separate search terms with commas, e.g. \"twilight sparkle, safe\".",
        EngineType.BooruOnRails => "Optional API key from your account settings. Separate search terms with commas.",
        EngineType.Hydrus => "Use your Hydrus client's API address, usually http://127.0.0.1:45869, and an access key with " +
                             "search and file permissions (services → review services → client api). Separate tags with commas.",
        EngineType.Kemono => "No account needed. Search matches post titles and text; each attached file becomes its own item.",
        EngineType.Shimmie => "No account needed. Kura reads the site's post list pages.",
        EngineType.Gelbooru011 => "No account needed. File links are derived from thumbnails, so an occasional image may fail to open.",
        _ => "",
    };

    partial void OnEngineChanged(EngineOption? value)
    {
        OnPropertyChanged(nameof(UsernameLabel));
        OnPropertyChanged(nameof(SecretLabel));
        OnPropertyChanged(nameof(CredentialHint));
    }

    /// <summary>Only saved, non-default servers can be deleted.</summary>
    public bool CanDelete => Selected is { IsDefault: false };

    partial void OnSelectedChanged(Server? value)
    {
        OnPropertyChanged(nameof(CanDelete));
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
        var creds = _services.Credentials.Get(value.Id);
        ApiKey = creds.ApiKey ?? creds.Password ?? "";
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

    /// <summary>Splits a pasted Gelbooru "&amp;api_key=…&amp;user_id=…" string into the two fields.</summary>
    private void NormalizeGelbooruCredentials()
    {
        if (Engine?.Type != EngineType.Gelbooru || !ApiKey.Contains("api_key=", StringComparison.OrdinalIgnoreCase)) return;
        var (key, user) = Kura.Core.Engines.GelbooruEngine.ParseCredentials(ApiKey, string.IsNullOrWhiteSpace(Username) ? null : Username);
        if (key is null) return;
        ApiKey = key;
        ApiKeyLoaded?.Invoke(key);
        if (user is not null) Username = user;
    }

    private Server? BuildFromForm(out string? error)
    {
        error = null;
        NormalizeGelbooruCredentials();
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
        if (!Engine.IsSupported)
        {
            error = $"{Engine.Name} support isn't available yet.";
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
        _services.Credentials.Set(server.Id, CredentialsFromForm(server.Engine));
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
            var ctx = _services.Client.CreateContext(server, CredentialsFromForm(server.Engine));
            var msg = await _services.Client.PingAsync(ctx, cts.Token);
            SetStatus(msg, false);
        }
        catch (BooruRequestException ex) { SetStatus(ex.Message, true); }
        catch (OperationCanceledException) { }
        finally { IsTesting = false; }
    }

    /// <summary>Danbooru 1 stores a password (hashed at request time); every other engine stores an API key.</summary>
    private ServerCredentials CredentialsFromForm(EngineType engine)
    {
        var secret = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim();
        return engine == EngineType.Danbooru1 ? new ServerCredentials(null, secret) : new ServerCredentials(secret, null);
    }

    private void SetStatus(string text, bool isError)
    {
        Status = text;
        StatusIsError = isError;
    }
}
