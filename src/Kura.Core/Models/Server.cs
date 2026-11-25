namespace Kura.Core.Models;

/// <summary>A configured booru server. Secrets (API key, password) never live here — see ICredentialStore.</summary>
public sealed class Server
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public EngineType Engine { get; set; }
    /// <summary>Login name (Danbooru/e621) or numeric user id (Gelbooru).</summary>
    public string? Username { get; set; }
    public bool RatingFilterEnabled { get; set; }
    public bool UseNativeAutocomplete { get; set; } = true;
    public bool IsDefault { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Base URL with no trailing slash.</summary>
    public string BaseUrl => Url.TrimEnd('/');

    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host : Url;

    public override string ToString() => Name;
}

/// <summary>Secrets for a server, stored in Windows Credential Manager.</summary>
public sealed record ServerCredentials(string? ApiKey, string? Password)
{
    public static readonly ServerCredentials Empty = new(null, null);
    public bool IsEmpty => string.IsNullOrEmpty(ApiKey) && string.IsNullOrEmpty(Password);
}
