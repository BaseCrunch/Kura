using System.Globalization;
using System.Text.Json;
using Kura.Core.Models;

namespace Kura.Core.Engines;

/// <summary>
/// Philomena (Derpibooru, Furbooru, …). Tags contain spaces, so search terms are comma-separated; ratings are
/// ordinary tags ("safe", "explicit"). SPEC 1.3, 1.7.
/// </summary>
public class PhilomenaEngine : BooruEngineBase
{
    private static readonly HashSet<string> RatingTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "safe", "suggestive", "questionable", "explicit", "semi-grimdark", "grimdark", "grotesque",
    };

    public override EngineType Type => EngineType.Philomena;
    public override EngineCapabilities Capabilities => EngineCapabilities.Autocomplete | EngineCapabilities.Pools | EngineCapabilities.RemoteFavorites;
    public override int PageSize => 50;
    public override string TagSeparator => ", ";
    public override string FullHdTerm => "width.gte:1920";
    protected override string DefaultRatingTerm => "safe";

    /// <summary>API root relative to the server URL.</summary>
    protected virtual string ApiBase => "/api/v1/json";
    protected virtual string SearchPath => "search/images";
    /// <summary>JSON property holding the result array.</summary>
    protected virtual string RootKey => "images";

    /// <summary>An empty Philomena query matches nothing, so it becomes <c>*</c> (SPEC 1.4 step 4).</summary>
    public override string NormalizeWildcards(string query)
    {
        var tokens = QueryPreprocessor.Tokenize(query, TagSeparator).Where(t => t != "*").ToList();
        return tokens.Count == 0 ? "*" : string.Join(TagSeparator, tokens);
    }

    private static string Key(ServerContext ctx) => ctx.ApiKey is { } k ? $"&key={Uri.EscapeDataString(k)}" : "";

    public override Uri BuildPostsUrl(ServerContext ctx, PreparedQuery query, int page) =>
        MakeUri($"{ctx.BaseUrl}{ApiBase}/{SearchPath}?per_page={PageSize}&page={page}&q={Enc(query.Text)}{Key(ctx)}");

    public override Uri BuildPingUrl(ServerContext ctx) =>
        MakeUri($"{ctx.BaseUrl}{ApiBase}/{SearchPath}?per_page=1&page=1&q=*{Key(ctx)}");

    public override Uri? BuildAutocompleteUrl(ServerContext ctx, string term) =>
        MakeUri($"{ctx.BaseUrl}{ApiBase}/search/tags?q={Enc(term)}*&per_page=10{Key(ctx)}");

    public override string PostPageUrl(ServerContext ctx, string remoteId) => $"{ctx.BaseUrl}/images/{remoteId}";
    public override string BrowserSearchUrl(ServerContext ctx, string query) => $"{ctx.BaseUrl}/search?q={Enc(query)}";

    public override async Task<PostPage> ParsePostsAsync(Stream body, ServerContext ctx, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(RootKey, out var items) || items.ValueKind != JsonValueKind.Array)
            return PostPage.Empty;

        var faved = new HashSet<string>();
        if (root.TryGetProperty("interactions", out var inter) && inter.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in inter.EnumerateArray())
                if (i.Str("interaction_type") == "faved" && (i.Str("image_id") ?? i.Str("post_id")) is { } id) faved.Add(id);
        }

        var posts = new List<Post>();
        var raw = 0;
        foreach (var e in items.EnumerateArray())
        {
            raw++;
            if (Map(e, ctx, faved) is { } p) posts.Add(p);
        }
        return new PostPage(posts, raw, raw - posts.Count);
    }

    private Post? Map(JsonElement e, ServerContext ctx, HashSet<string> faved)
    {
        var id = e.Str("id");
        if (id is null || e.Bool("hidden_from_users") || e.Str("duplicate_of") is not null || e.Str("deletion_reason") is not null)
            return null;
        if (!e.TryGetProperty("representations", out var reps) || reps.ValueKind != JsonValueKind.Object) return null;

        var fullUrl = Fix(reps.Str("full") ?? e.Str("view_url"), ctx);
        if (fullUrl is null) return null;
        var file = new PostImage(fullUrl, e.Int("width"), e.Int("height"));

        PostImage? sample = null;
        if (!file.IsAnimated && Fix(reps.Str("large"), ctx) is { } large && large != fullUrl) sample = new PostImage(large, 0, 0);

        var thumb = Fix(reps.Str("thumb") ?? reps.Str("medium"), ctx);
        // Video thumbnails are WebM too; the CDN serves a GIF beside them that the decoder can read (SPEC 1.7).
        if (thumb is not null && thumb.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)) thumb = thumb[..^5] + ".gif";
        var preview = thumb is null ? null : new PostImage(thumb, 0, 0);

        var tagNames = new List<string>();
        if (e.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            foreach (var t in tagsEl.EnumerateArray()) if (t.GetString() is { Length: > 0 } n) tagNames.Add(n);

        var sources = new List<string>();
        if (e.TryGetProperty("source_urls", out var su) && su.ValueKind == JsonValueKind.Array)
            foreach (var s in su.EnumerateArray()) if (s.GetString() is { Length: > 0 } u) sources.Add(u);
        if (sources.Count == 0 && e.Str("source_url") is { } single) sources.Add(single);

        return new Post
        {
            ServerId = ctx.Server.Id,
            RemoteId = id,
            PostUrl = PostPageUrl(ctx, id),
            Md5 = e.Str("orig_sha512_hash") ?? e.Str("sha512_hash"),
            Rating = tagNames.FirstOrDefault(RatingTags.Contains),
            Score = e.Int("score"),
            Source = sources.Count > 0 ? string.Join(" | ", sources) : null,
            Author = e.Str("uploader"),
            HasComments = e.Int("comment_count") > 0,
            CreatedAt = DateTimeOffset.TryParse(e.Str("created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null,
            File = file,
            Sample = sample,
            Preview = preview,
            Tags = tagNames.Select(n => new Tag(n, CategoryFromName(n))).ToList(),
            IsFavorite = faved.Contains(id),
        };
    }

    /// <summary>Namespaced tags carry their category; the rest are coloured from the tag cache.</summary>
    internal static TagCategory CategoryFromName(string name)
    {
        if (RatingTags.Contains(name)) return TagCategory.Meta;
        var colon = name.IndexOf(':');
        if (colon <= 0) return TagCategory.Unknown;
        return name[..colon].ToLowerInvariant() switch
        {
            "artist" or "editor" or "photographer" or "prompter" or "colorist" => TagCategory.Artist,
            "oc" => TagCategory.Character,
            "spoiler" or "comic" or "series" => TagCategory.Copyright,
            _ => TagCategory.Unknown,
        };
    }

    private static string? Fix(string? url, ServerContext ctx) => url switch
    {
        null or "" => null,
        _ when url.StartsWith("//", StringComparison.Ordinal) => "https:" + url,
        _ when url.StartsWith('/') => ctx.BaseUrl + url,
        _ => url,
    };

    public override async Task<IReadOnlyList<TagSuggestion>> ParseAutocompleteAsync(Stream body, ServerContext ctx, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array) return [];
        return tags.EnumerateArray()
            .Where(t => t.Str("name") is not null && t.Str("aliased_tag") is null)
            // Philomena counts under "images"; booru-on-rails under "posts".
            .Select(t => new TagSuggestion(t.Str("name")!, MapCategory(t.Str("category"), t.Str("name")!),
                t.Long("images") is > 0 and var images ? images : t.Long("posts")))
            .OrderByDescending(t => t.PostCount)
            .ToList();
    }

    internal static TagCategory MapCategory(string? category, string name) => category switch
    {
        "character" or "oc" => TagCategory.Character,
        "species" => TagCategory.Species,
        "content-fanmade" or "content-official" => TagCategory.Copyright,
        "origin" => TagCategory.Artist,
        "rating" or "spoiler" or "error" => TagCategory.Meta,
        _ => CategoryFromName(name) is var c && c != TagCategory.Unknown ? c : TagCategory.General,
    };
}

/// <summary>booru-on-rails (Twibooru): Philomena's predecessor with a v3 API and <c>posts</c> results.</summary>
public sealed class BooruOnRailsEngine : PhilomenaEngine
{
    public override EngineType Type => EngineType.BooruOnRails;
    public override EngineCapabilities Capabilities => EngineCapabilities.Autocomplete;
    protected override string ApiBase => "/api/v3";
    protected override string SearchPath => "search/posts";
    protected override string RootKey => "posts";
    public override string PostPageUrl(ServerContext ctx, string remoteId) => $"{ctx.BaseUrl}/{remoteId}";
}
