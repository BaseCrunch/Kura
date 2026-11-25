using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Kura.Core.Models;

namespace Kura.Core.Engines;

/// <summary>
/// Hydrus Client API. A search is two-stage: <c>search_files</c> returns every matching file id, then each page
/// asks <c>file_metadata</c> for 100 of them (SPEC 1.3, 1.6, 1.7). Tags are comma-separated and namespaced.
/// </summary>
public sealed class HydrusEngine : BooruEngineBase, Search.IPingable
{
    private const string KeyHeader = "Hydrus-Client-API-Access-Key";
    private const int ServiceTypeAllKnownTags = 10;
    private const int ServiceTypeLikeDislike = 7;

    // Stage-1 results per (server, query), so later pages don't re-run the search. Small and bounded.
    private readonly ConcurrentDictionary<string, long[]> _ids = new();

    public override EngineType Type => EngineType.Hydrus;
    public override EngineCapabilities Capabilities => EngineCapabilities.Autocomplete | EngineCapabilities.CategorizedTags | EngineCapabilities.RemoteFavorites;
    public override int PageSize => 100;
    public override int FirstPage => 0;
    public override double DefaultRequestsPerSecond => 20; // a local client
    public override string TagSeparator => ", ";
    public override string FullHdTerm => "system:width > 1919";
    protected override string DefaultRatingTerm => "";

    public override string NormalizeWildcards(string query)
    {
        var tokens = QueryPreprocessor.Tokenize(query, TagSeparator).Where(t => t != "*").ToList();
        return tokens.Count == 0 ? "system:everything" : string.Join(TagSeparator, tokens);
    }

    private static string TagsJson(PreparedQuery q) =>
        JsonSerializer.Serialize(QueryPreprocessor.Tokenize(q.Text, ", ").ToArray(), HydrusJson.Default.StringArray);

    public override Uri BuildPostsUrl(ServerContext ctx, PreparedQuery query, int page) =>
        MakeUri($"{ctx.BaseUrl}/get_files/search_files?tags={Enc(TagsJson(query))}");

    public override Uri BuildPingUrl(ServerContext ctx) => MakeUri($"{ctx.BaseUrl}/verify_access_key");

    public override Uri? BuildAutocompleteUrl(ServerContext ctx, string term) =>
        MakeUri($"{ctx.BaseUrl}/add_tags/search_tags?search={Enc(term)}&tag_display_type=display");

    public override string PostPageUrl(ServerContext ctx, string remoteId) => ctx.BaseUrl;
    public override string BrowserSearchUrl(ServerContext ctx, string query) => ctx.BaseUrl;

    public override void PrepareRequest(HttpRequestMessage request, ServerContext ctx)
    {
        base.PrepareRequest(request, ctx);
        if (ctx.ApiKey is { } k) request.Headers.TryAddWithoutValidation(KeyHeader, k);
    }

    public override async Task<PostPage> FetchPageAsync(EngineTransport transport, ServerContext ctx, PreparedQuery query, int page,
        CancellationToken ct)
    {
        var cacheKey = $"{ctx.Server.Id}|{query.Text}";
        if (page == FirstPage || !_ids.TryGetValue(cacheKey, out var ids))
        {
            using var resp = await transport.GetAsync(BuildPostsUrl(ctx, query, page), ctx, ct).ConfigureAwait(false);
            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            ids = await ParseFileIdsAsync(body, ct).ConfigureAwait(false);
            if (_ids.Count > 32) _ids.Clear();
            _ids[cacheKey] = ids;
        }

        var slice = ids.Skip((page - FirstPage) * PageSize).Take(PageSize).ToArray();
        if (slice.Length == 0) return PostPage.Empty;

        var idsJson = JsonSerializer.Serialize(slice, HydrusJson.Default.Int64Array);
        var metaUrl = MakeUri($"{ctx.BaseUrl}/get_files/file_metadata?file_ids={Enc(idsJson)}&detailed_url_information=true&include_services_object=true");
        using var metaResp = await transport.GetAsync(metaUrl, ctx, ct).ConfigureAwait(false);
        await using var metaBody = await metaResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var posts = await ParseMetadataAsync(metaBody, ctx, ct).ConfigureAwait(false);

        // Keep stage-1 order (the user's sort), which file_metadata doesn't promise.
        var order = slice.Select((id, i) => (id, i)).ToDictionary(x => x.id.ToString(CultureInfo.InvariantCulture), x => x.i);
        var sorted = posts.OrderBy(p => order.GetValueOrDefault(p.RemoteId, int.MaxValue)).ToList();
        return new PostPage(sorted, slice.Length, slice.Length - sorted.Count);
    }

    internal static async Task<long[]> ParseFileIdsAsync(Stream body, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("file_ids", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        return arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).ToArray();
    }

    /// <summary>Not used directly (pages go through <see cref="FetchPageAsync"/>); parses a <c>file_metadata</c> reply.</summary>
    public override async Task<PostPage> ParsePostsAsync(Stream body, ServerContext ctx, CancellationToken ct)
    {
        var posts = await ParseMetadataAsync(body, ctx, ct).ConfigureAwait(false);
        return new PostPage(posts, posts.Count);
    }

    internal static async Task<List<Post>> ParseMetadataAsync(Stream body, ServerContext ctx, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var posts = new List<Post>();
        if (!root.TryGetProperty("metadata", out var meta) || meta.ValueKind != JsonValueKind.Array) return posts;

        // Service key → type, from the top-level services object (include_services_object=true).
        var serviceTypes = new Dictionary<string, int>();
        if (root.TryGetProperty("services", out var services) && services.ValueKind == JsonValueKind.Object)
            foreach (var s in services.EnumerateObject()) serviceTypes[s.Name] = s.Value.Int("type");
        var likeService = serviceTypes.FirstOrDefault(kv => kv.Value == ServiceTypeLikeDislike).Key;

        foreach (var m in meta.EnumerateArray())
        {
            var id = m.Str("file_id");
            var ext = (m.Str("ext") ?? "").TrimStart('.').ToLowerInvariant();
            if (id is null || !MediaTypes.Supported.Contains(ext) || ext == "zip") continue;

            var key = ctx.ApiKey is { } k ? $"&{KeyHeader}={Uri.EscapeDataString(k)}" : "";
            var file = new PostImage($"{ctx.BaseUrl}/get_files/file?file_id={id}{key}&ext={ext}", m.Int("width"), m.Int("height"));
            var thumb = new PostImage($"{ctx.BaseUrl}/get_files/thumbnail?file_id={id}{key}&ext=jpg", 0, 0);

            var (postUrl, source) = Urls(m);
            var tags = DisplayTags(m, serviceTypes).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(t => new Tag(t, CategoryOf(t))).ToList();

            var favorite = likeService is not null && m.TryGetProperty("ratings", out var ratings) &&
                           ratings.ValueKind == JsonValueKind.Object && ratings.TryGetProperty(likeService, out var r) &&
                           r.ValueKind == JsonValueKind.True;

            var modified = m.Long("time_modified");
            posts.Add(new Post
            {
                ServerId = ctx.Server.Id,
                RemoteId = id,
                PostUrl = postUrl,
                Md5 = m.Str("hash"),
                Source = source,
                CreatedAt = modified > 0 ? DateTimeOffset.FromUnixTimeSeconds(modified) : null,
                File = file,
                Preview = thumb,
                Tags = tags,
                IsFavorite = favorite,
            });
        }
        return posts;
    }

    /// <summary>First post-type URL is the post page, the next one the source; else the first known URL is the source.</summary>
    private static (string? PostUrl, string? Source) Urls(JsonElement m)
    {
        var postUrls = new List<string>();
        if (m.TryGetProperty("detailed_known_urls", out var det) && det.ValueKind == JsonValueKind.Array)
            foreach (var u in det.EnumerateArray())
                if (u.Int("url_type") == 0 && (u.Str("normalised_url") ?? u.Str("url")) is { } url) postUrls.Add(url);
        if (postUrls.Count > 0) return (postUrls[0], postUrls.Count > 1 ? postUrls[1] : null);
        if (m.TryGetProperty("known_urls", out var known) && known.ValueKind == JsonValueKind.Array)
            foreach (var u in known.EnumerateArray()) if (u.GetString() is { } s) return (null, s);
        return (null, null);
    }

    /// <summary>Current display tags from the "all known tags" service, across Hydrus API versions.</summary>
    private static IEnumerable<string> DisplayTags(JsonElement m, Dictionary<string, int> serviceTypes)
    {
        if (m.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
        {
            JsonElement? chosen = null;
            foreach (var svc in tags.EnumerateObject())
            {
                var type = svc.Value.Int("type") is > 0 and var t ? t : serviceTypes.GetValueOrDefault(svc.Name);
                if (type == ServiceTypeAllKnownTags) { chosen = svc.Value; break; }
            }
            var sources = chosen is { } c ? [c] : tags.EnumerateObject().Select(s => s.Value).ToList();
            foreach (var svc in sources)
                if (svc.TryGetProperty("display_tags", out var dt) && dt.TryGetProperty("0", out var current) && current.ValueKind == JsonValueKind.Array)
                    foreach (var t in current.EnumerateArray()) if (t.GetString() is { Length: > 0 } s) yield return s;
            yield break;
        }
        // Older API: service_names_to_statuses_to_display_tags["all known tags"]["0"].
        if (m.TryGetProperty("service_names_to_statuses_to_display_tags", out var old) && old.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "all known tags", "my tags", "my tag repository" })
                if (old.TryGetProperty(name, out var st) && st.TryGetProperty("0", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in arr.EnumerateArray()) if (t.GetString() is { Length: > 0 } s) yield return s;
                    yield break;
                }
        }
    }

    /// <summary>Hydrus namespaces (SPEC 1.7): creator → artist, character/person → character, series/title → copyright.</summary>
    internal static TagCategory CategoryOf(string tag)
    {
        var colon = tag.IndexOf(':');
        if (colon <= 0 || tag.StartsWith("system:", StringComparison.OrdinalIgnoreCase)) return TagCategory.General;
        return tag[..colon].ToLowerInvariant() switch
        {
            "creator" or "artist" => TagCategory.Artist,
            "character" or "person" => TagCategory.Character,
            "series" or "title" or "copyright" => TagCategory.Copyright,
            "species" => TagCategory.Species,
            "meta" or "rating" or "medium" => TagCategory.Meta,
            _ => TagCategory.General,
        };
    }

    public override async Task<IReadOnlyList<TagSuggestion>> ParseAutocompleteAsync(Stream body, ServerContext ctx, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array) return [];
        return tags.EnumerateArray()
            .Where(t => t.Str("value") is not null)
            .Select(t => new TagSuggestion(t.Str("value")!, CategoryOf(t.Str("value")!), t.Long("count")))
            .OrderByDescending(t => t.PostCount)
            .ToList();
    }

    public async Task<string> PingAsync(EngineTransport transport, ServerContext ctx, CancellationToken ct)
    {
        using var resp = await transport.GetAsync(BuildPingUrl(ctx), ctx, ct).ConfigureAwait(false);
        await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
        var who = doc.RootElement.Str("human_description");
        return who is null ? "Connected to Hydrus." : $"Connected to Hydrus. {who}";
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
[System.Text.Json.Serialization.JsonSerializable(typeof(long[]))]
internal sealed partial class HydrusJson : System.Text.Json.Serialization.JsonSerializerContext;
