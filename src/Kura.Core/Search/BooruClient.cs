using System.Text.Json;
using System.Xml;
using Kura.Core.Engines;
using Kura.Core.Models;
using Kura.Core.Net;

namespace Kura.Core.Search;

/// <summary>Runs engine requests through the shared HTTP stack and maps failures to <see cref="BooruRequestException"/>.</summary>
public sealed class BooruClient(KuraHttp http, EngineRegistry engines, QuirkProfiles quirks)
{
    public EngineRegistry Engines => engines;
    public KuraHttp Http => http;

    public ServerContext CreateContext(Server server, ServerCredentials credentials)
    {
        var q = quirks.Resolve(server.Host);
        var ctx = new ServerContext(server, credentials, q);
        if (engines.IsSupported(server.Engine))
            http.Limiter.Configure(server.Host, q.RequestsPerSecond ?? engines.Get(server.Engine).DefaultRequestsPerSecond);
        return ctx;
    }

    public HostQuirks QuirksFor(string host) => quirks.Resolve(host);

    public PreparedQuery Prepare(ServerContext ctx, string rawQuery, string? extraTags = null) =>
        QueryPreprocessor.Prepare(engines.Get(ctx.Server.Engine), ctx, rawQuery, extraTags);

    public async Task<PostPage> FetchPostsAsync(ServerContext ctx, PreparedQuery query, int page, CancellationToken ct)
    {
        var engine = engines.Get(ctx.Server.Engine);
        using var req = KuraHttp.Get(engine.BuildPostsUrl(ctx, query, page));
        engine.PrepareRequest(req, ctx);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            return await engine.ParsePostsAsync(body, ctx, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or XmlException or FormatException or InvalidOperationException)
        {
            throw new BooruRequestException(FailureKind.CannotParse, BooruRequestException.Describe(FailureKind.CannotParse), inner: ex);
        }
    }

    /// <summary>Native per-server autocomplete. Returns an empty list when the server has none or fails.</summary>
    public async Task<IReadOnlyList<TagSuggestion>> AutocompleteAsync(ServerContext ctx, string term, CancellationToken ct)
    {
        if (!ctx.Server.UseNativeAutocomplete || term.Length == 0) return [];
        var engine = engines.Get(ctx.Server.Engine);
        if (engine.BuildAutocompleteUrl(ctx, term) is not { } url) return [];
        try
        {
            using var req = KuraHttp.Get(url, accept: "application/json");
            engine.PrepareRequest(req, ctx);
            using var resp = await http.SendAsync(req, ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await engine.ParseAutocompleteAsync(body, ctx, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is BooruRequestException or JsonException or XmlException)
        {
            return []; // autocomplete is best-effort; local suggestions still show
        }
    }

    /// <summary>Validates the URL, engine type and credentials with a one-post request.</summary>
    public async Task<string> PingAsync(ServerContext ctx, CancellationToken ct)
    {
        var engine = engines.Get(ctx.Server.Engine);
        using var req = KuraHttp.Get(engine.BuildPingUrl(ctx));
        engine.PrepareRequest(req, ctx);
        using var resp = await http.SendAsync(req, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        PostPage page;
        try { page = await engine.ParsePostsAsync(body, ctx, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is JsonException or XmlException or InvalidOperationException)
        {
            throw new BooruRequestException(FailureKind.CannotParse,
                "Connected, but the response doesn't look like this engine type.", inner: ex);
        }
        if (page.ServerMessage is { } msg) return $"Connected. Server says: {msg}";
        return page.RawCount > 0 ? "Connected successfully." : "Connected, but the server returned no posts.";
    }
}
