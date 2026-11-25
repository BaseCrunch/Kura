using System.Net;
using Kura.Core.Models;
using Kura.Core.Net;

namespace Kura.Core.Engines;

[Flags]
public enum EngineCapabilities
{
    None = 0,
    Autocomplete = 1,
    Notes = 2,
    Pools = 4,
    RemoteFavorites = 8,
    CategorizedTags = 16,
}

/// <summary>Everything an engine needs to talk to one server.</summary>
public sealed record ServerContext(Server Server, ServerCredentials Credentials, HostQuirks Quirks)
{
    public string BaseUrl => Server.BaseUrl;
    public string Host => Server.Host;
    public string? Username => string.IsNullOrWhiteSpace(Server.Username) ? null : Server.Username.Trim();
    public string? ApiKey => string.IsNullOrWhiteSpace(Credentials.ApiKey) ? null : Credentials.ApiKey.Trim();
    public string? Password => string.IsNullOrEmpty(Credentials.Password) ? null : Credentials.Password;
}

/// <summary>GET helper handed to engines whose pages need more than one request (Hydrus).</summary>
public sealed class EngineTransport(KuraHttp http, IBooruEngine engine)
{
    /// <summary>Sends a GET with the engine's headers; non-2xx throws <see cref="BooruRequestException"/>.</summary>
    public async Task<HttpResponseMessage> GetAsync(Uri url, ServerContext ctx, CancellationToken ct)
    {
        var req = KuraHttp.Get(url);
        engine.PrepareRequest(req, ctx);
        try { return await http.SendAsync(req, ct).ConfigureAwait(false); }
        finally { req.Dispose(); }
    }
}

/// <summary>A query after rating filter, macros, extra tags and wildcard normalisation (SPEC 1.4).</summary>
public sealed record PreparedQuery(string Raw, string Text, bool ForceOriginal);

/// <summary>One parsed page of results.</summary>
public sealed record PostPage(IReadOnlyList<Post> Posts, int RawCount, int HiddenCount = 0, string? ServerMessage = null)
{
    public static readonly PostPage Empty = new([], 0);
}

/// <summary>
/// One booru engine family (SPEC 4.1 N6). Engines are stateless; per-server data travels in <see cref="ServerContext"/>.
/// </summary>
public interface IBooruEngine
{
    EngineType Type { get; }
    EngineCapabilities Capabilities { get; }
    int PageSize { get; }
    int FirstPage { get; }
    double DefaultRequestsPerSecond { get; }

    /// <summary>Separator between search terms: " " for most boorus, ", " where tag names contain spaces.</summary>
    string TagSeparator { get; }

    /// <summary>The engine's spelling of the <c>#fullhd</c> macro (SPEC 1.4 step 2).</summary>
    string FullHdTerm { get; }

    /// <summary>The safe-rating term appended when the server's rating filter is on.</summary>
    string RatingTerm(ServerContext ctx);

    /// <summary>Removes or substitutes wildcard-only tokens as the engine expects.</summary>
    string NormalizeWildcards(string query);

    Uri BuildPostsUrl(ServerContext ctx, PreparedQuery query, int page);
    Uri BuildPingUrl(ServerContext ctx);
    Uri? BuildAutocompleteUrl(ServerContext ctx, string term);
    string PostPageUrl(ServerContext ctx, string remoteId);
    string BrowserSearchUrl(ServerContext ctx, string query);

    /// <summary>Applies User-Agent and authentication headers.</summary>
    void PrepareRequest(HttpRequestMessage request, ServerContext ctx);

    Task<PostPage> ParsePostsAsync(Stream body, ServerContext ctx, CancellationToken ct);
    Task<IReadOnlyList<TagSuggestion>> ParseAutocompleteAsync(Stream body, ServerContext ctx, CancellationToken ct);

    /// <summary>Fetches and parses one page. The default is one GET of <see cref="BuildPostsUrl"/>.</summary>
    Task<PostPage> FetchPageAsync(EngineTransport transport, ServerContext ctx, PreparedQuery query, int page, CancellationToken ct);

    /// <summary>Seeds cookies the site needs (e.g. Shimmie's age-gate cookie).</summary>
    void ConfigureCookies(CookieContainer cookies, ServerContext ctx);
}

public abstract class BooruEngineBase : IBooruEngine
{
    public abstract EngineType Type { get; }
    public abstract EngineCapabilities Capabilities { get; }
    public virtual int PageSize => 100;
    public virtual int FirstPage => 1;
    public virtual double DefaultRequestsPerSecond => 5;
    public virtual string TagSeparator => " ";
    public virtual string FullHdTerm => "width:>=1920";

    protected abstract string DefaultRatingTerm { get; }
    public string RatingTerm(ServerContext ctx) => ctx.Quirks.RatingTerm ?? DefaultRatingTerm;

    public virtual string NormalizeWildcards(string query) =>
        string.Join(TagSeparator, QueryPreprocessor.Tokenize(query, TagSeparator).Where(t => t != "*"));

    public abstract Uri BuildPostsUrl(ServerContext ctx, PreparedQuery query, int page);
    public abstract Uri BuildPingUrl(ServerContext ctx);
    public virtual Uri? BuildAutocompleteUrl(ServerContext ctx, string term) => null;
    public abstract string PostPageUrl(ServerContext ctx, string remoteId);
    public abstract string BrowserSearchUrl(ServerContext ctx, string query);

    public virtual void PrepareRequest(HttpRequestMessage request, ServerContext ctx)
    {
        request.Headers.Remove("User-Agent");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent(ctx));
    }

    public virtual string UserAgent(ServerContext ctx) => ctx.Quirks.UserAgent ?? Net.KuraHttp.DefaultUserAgent;

    public abstract Task<PostPage> ParsePostsAsync(Stream body, ServerContext ctx, CancellationToken ct);

    public virtual Task<IReadOnlyList<TagSuggestion>> ParseAutocompleteAsync(Stream body, ServerContext ctx, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TagSuggestion>>([]);

    public virtual async Task<PostPage> FetchPageAsync(EngineTransport transport, ServerContext ctx, PreparedQuery query, int page,
        CancellationToken ct)
    {
        using var resp = await transport.GetAsync(BuildPostsUrl(ctx, query, page), ctx, ct).ConfigureAwait(false);
        await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await ParsePostsAsync(body, ctx, ct).ConfigureAwait(false);
    }

    public virtual void ConfigureCookies(CookieContainer cookies, ServerContext ctx) { }

    protected static string Enc(string s) => Uri.EscapeDataString(s);

    protected static Uri MakeUri(string s) => new(s, UriKind.Absolute);
}
