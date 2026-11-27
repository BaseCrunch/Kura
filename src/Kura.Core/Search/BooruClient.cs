using System.Text.Json;
using System.Xml;
using Kura.Core.Engines;
using Kura.Core.Models;
using Kura.Core.Net;

namespace Kura.Core.Search;

/// <summary>Runs engine requests through the shared HTTP stack and maps failures to <see cref="BooruRequestException"/>.</summary>
/// <summary>Engines whose connection test isn't "fetch one post" (e.g. Hydrus verifies its access key).</summary>
public interface IPingable
{
    Task<string> PingAsync(EngineTransport transport, ServerContext ctx, CancellationToken ct);
}

public sealed class BooruClient(KuraHttp http, EngineRegistry engines, QuirkProfiles quirks)
{
    public EngineRegistry Engines => engines;
    public KuraHttp Http => http;

    public ServerContext CreateContext(Server server, ServerCredentials credentials)
    {
        var q = quirks.Resolve(server.Host);
        var ctx = new ServerContext(server, credentials, q);
        if (engines.IsSupported(server.Engine))
        {
            var engine = engines.Get(server.Engine);
            http.Limiter.Configure(server.Host, q.RequestsPerSecond ?? engine.DefaultRequestsPerSecond);
            engine.ConfigureCookies(http.Cookies, ctx);
        }
        return ctx;
    }

    public HostQuirks QuirksFor(string host) => quirks.Resolve(host);

    public PreparedQuery Prepare(ServerContext ctx, string rawQuery, string? extraTags = null) =>
        QueryPreprocessor.Prepare(engines.Get(ctx.Server.Engine), ctx, rawQuery, extraTags);

    public async Task<PostPage> FetchPostsAsync(ServerContext ctx, PreparedQuery query, int page, CancellationToken ct)
    {
        var engine = engines.Get(ctx.Server.Engine);
        try
        {
            return await engine.FetchPageAsync(new EngineTransport(http, engine), ctx, query, page, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Finds which engine a site runs: the known-site hint first, then each engine's first page. A site that
    /// answers with posts, or with a server message such as "missing authentication", counts as a match.
    /// Hydrus is skipped (a local client, never auto-detected).
    /// </summary>
    public async Task<EngineType?> DetectEngineAsync(Server server, ServerCredentials credentials, CancellationToken ct)
    {
        var hint = quirks.Resolve(server.Host).Engine;
        EngineType[] order =
        [
            EngineType.Danbooru2, EngineType.Gelbooru, EngineType.E621, EngineType.Danbooru1, EngineType.Philomena,
            EngineType.BooruOnRails, EngineType.Kemono, EngineType.Shimmie, EngineType.Gelbooru011,
        ];
        var candidates = (hint is { } h ? order.Prepend(h) : order).Distinct().Where(engines.IsSupported);
        foreach (var engine in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var probe = new Server { Id = server.Id, Name = server.Name, Url = server.Url, Engine = engine, Username = server.Username };
            var ctx = CreateContext(probe, credentials);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(12));
                var e = engines.Get(engine);
                var page = await FetchPostsAsync(ctx, Prepare(ctx, ""), e.FirstPage, cts.Token).ConfigureAwait(false);
                if (page.Posts.Count > 0 || page.ServerMessage is not null) return engine;
            }
            catch (BooruRequestException ex) when (ex.Kind == FailureKind.InvalidCredentials)
            {
                return engine; // the right API, just needs a key
            }
            catch (Exception ex) when (ex is BooruRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
            }
        }
        return null;
    }

    /// <summary>The engine a known site runs, if Kura knows it.</summary>
    public EngineType? KnownEngine(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? quirks.Resolve(u.Host).Engine : null;

    /// <summary>Validates the URL, engine type and credentials with a one-post request.</summary>
    public async Task<string> PingAsync(ServerContext ctx, CancellationToken ct)
    {
        var engine = engines.Get(ctx.Server.Engine);
        if (engine is IPingable pingable) return await pingable.PingAsync(new EngineTransport(http, engine), ctx, ct).ConfigureAwait(false);
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
