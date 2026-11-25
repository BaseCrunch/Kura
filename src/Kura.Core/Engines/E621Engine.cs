using System.Text.Json;
using System.Text.Json.Serialization;
using Kura.Core.Models;
using Kura.Core.Net;

namespace Kura.Core.Engines;

/// <summary>
/// e621 / e926. Requests the <c>v1</c> response shape, which carries tag categories; the newer flat array
/// shape is also accepted so a server-side switch degrades gracefully instead of breaking.
/// </summary>
public sealed class E621Engine : BooruEngineBase
{
    public override EngineType Type => EngineType.E621;
    public override EngineCapabilities Capabilities =>
        EngineCapabilities.Autocomplete | EngineCapabilities.CategorizedTags | EngineCapabilities.Notes |
        EngineCapabilities.Pools | EngineCapabilities.RemoteFavorites;
    public override int PageSize => 75;
    public override double DefaultRequestsPerSecond => 2; // e621 API policy
    protected override string DefaultRatingTerm => "rating:safe";

    // e621 handles a bare "*" fine, but drop it for consistency.
    public override Uri BuildPostsUrl(ServerContext ctx, PreparedQuery query, int page) =>
        MakeUri($"{ctx.BaseUrl}/posts.json?v1=true&limit={PageSize}&page={page}&tags={Enc(query.Text)}");

    public override Uri BuildPingUrl(ServerContext ctx) => MakeUri($"{ctx.BaseUrl}/posts.json?v1=true&limit=1");

    public override Uri? BuildAutocompleteUrl(ServerContext ctx, string term) =>
        MakeUri($"{ctx.BaseUrl}/tags.json?search[name_matches]={Enc(term)}*&search[order]=count&search[hide_empty]=true&limit=10");

    public override string PostPageUrl(ServerContext ctx, string remoteId) => $"{ctx.BaseUrl}/posts/{remoteId}";
    public override string BrowserSearchUrl(ServerContext ctx, string query) => $"{ctx.BaseUrl}/posts?tags={Enc(query)}";

    /// <summary>
    /// e621 requires a descriptive User-Agent naming the project, version and (when known) the user.
    /// Anonymous browsing gets a clean UA with no dangling "by" clause.
    /// </summary>
    public override string UserAgent(ServerContext ctx) => BuildUserAgent(ctx.Username);

    public static string BuildUserAgent(string? username) =>
        string.IsNullOrWhiteSpace(username)
            ? $"Kura/{KuraHttp.ProductVersion} (anonymous; desktop booru client)"
            : $"Kura/{KuraHttp.ProductVersion} (by {username.Trim()} on e621)";

    public override void PrepareRequest(HttpRequestMessage request, ServerContext ctx)
    {
        base.PrepareRequest(request, ctx);
        if (ctx.Username is { } u && ctx.ApiKey is { } k) KuraHttp.SetBasicAuth(request, u, k);
    }

    public override async Task<PostPage> ParsePostsAsync(Stream body, ServerContext ctx, CancellationToken ct)
    {
        var (first, stream) = await Lenient.PeekAsync(body, ct).ConfigureAwait(false);
        await using var _ = stream;
        if (first == (byte)'[')
        {
            var v2 = await JsonSerializer.DeserializeAsync(stream, E621Json.Default.ListE6V2Post, ct).ConfigureAwait(false) ?? [];
            return Collect(v2.Count, v2.Select(p => MapV2(p, ctx)));
        }
        var root = await JsonSerializer.DeserializeAsync(stream, E621Json.Default.E6V1Root, ct).ConfigureAwait(false);
        var posts = root?.Posts ?? [];
        return Collect(posts.Count, posts.Select(p => MapV1(p, ctx)));
    }

    private static PostPage Collect(int raw, IEnumerable<Post?> mapped)
    {
        var list = new List<Post>(raw);
        var hidden = 0;
        foreach (var p in mapped)
        {
            if (p is null) hidden++;
            else list.Add(p);
        }
        return new PostPage(list, raw, hidden);
    }

    internal Post? MapV1(E6V1Post p, ServerContext ctx)
    {
        if (p.Flags?.Deleted == true) return null;
        var f = p.File;
        // Without a login e621 withholds file URLs for some content; nothing to show.
        if (f?.Url is null || f.Ext == "swf") return null;

        var tags = new List<Tag>();
        if (p.Tags is { } t)
        {
            Add(tags, t.Artist, TagCategory.Artist);
            Add(tags, t.Contributor, TagCategory.Artist);
            Add(tags, t.Copyright, TagCategory.Copyright);
            Add(tags, t.Character, TagCategory.Character);
            Add(tags, t.Species, TagCategory.Species);
            Add(tags, t.General, TagCategory.General);
            Add(tags, t.Lore, TagCategory.Lore);
            Add(tags, t.Invalid, TagCategory.Invalid);
            Add(tags, t.Meta, TagCategory.Meta); // the tag strings, not the file metadata object (fixes Appendix A #2)
        }

        var id = p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new Post
        {
            ServerId = ctx.Server.Id,
            RemoteId = id,
            PostUrl = PostPageUrl(ctx, id),
            Md5 = f.Md5,
            Rating = p.Rating,
            Score = p.Score?.Total ?? 0,
            Source = p.Sources is { Count: > 0 } s ? string.Join(" | ", s) : null,
            Author = p.UploaderName ?? p.UploaderId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ParentId = p.Relationships?.ParentId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            HasChildren = p.Relationships?.HasChildren == true,
            HasNotes = p.HasNotes,
            HasComments = p.CommentCount > 0,
            CreatedAt = p.CreatedAt,
            File = new PostImage(f.Url, f.Width, f.Height),
            Sample = p.Sample is { Has: true, Url: { } su } ? new PostImage(su, p.Sample.Width, p.Sample.Height) : null,
            Preview = p.Preview?.Url is { } pu ? new PostImage(pu, p.Preview.Width, p.Preview.Height) : null,
            Tags = tags,
            IsFavorite = p.IsFavorited,
        };
    }

    internal Post? MapV2(E6V2Post p, ServerContext ctx)
    {
        if (p.Flags?.Deleted == true) return null;
        var meta = p.Files?.Meta;
        var orig = p.Files?.Original;
        if (orig?.Url is null || meta?.Ext == "swf") return null;
        var id = p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sample = p.Files?.Sample;
        var preview = p.Files?.Preview;
        return new Post
        {
            ServerId = ctx.Server.Id,
            RemoteId = id,
            PostUrl = PostPageUrl(ctx, id),
            Md5 = meta?.Md5,
            Rating = p.Rating,
            Score = p.Stats?.Score?.Total ?? 0,
            Source = p.Sources is { Count: > 0 } s ? string.Join(" | ", s) : null,
            Author = p.UploaderName ?? p.UploaderId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ParentId = p.Relationships?.ParentId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            HasChildren = p.Has?.Children == true,
            HasNotes = p.Has?.Notes == true,
            HasComments = p.Stats?.CommentCount > 0,
            CreatedAt = p.CreatedAt,
            File = new PostImage(orig.Url, orig.Width, orig.Height),
            Sample = meta?.HasSample == true && sample?.Jpg is { } sj ? new PostImage(sj, sample.Width, sample.Height) : null,
            Preview = preview?.Jpg is { } pj ? new PostImage(pj, preview.Width, preview.Height) : null,
            // The v2 shape has no categories; the UI colours these from the tag cache.
            Tags = (p.Tags ?? []).Select(t => new Tag(t, TagCategory.Unknown)).ToList(),
            IsFavorite = p.Stats?.IsFavorited == true,
        };
    }

    private static void Add(List<Tag> into, List<string>? names, TagCategory cat)
    {
        if (names is null) return;
        foreach (var n in names) into.Add(new Tag(n, cat));
    }

    public override async Task<IReadOnlyList<TagSuggestion>> ParseAutocompleteAsync(Stream body, ServerContext ctx, CancellationToken ct)
    {
        var (first, stream) = await Lenient.PeekAsync(body, ct).ConfigureAwait(false);
        await using var _ = stream;
        if (first != (byte)'[') return []; // e621 returns {"tags":[]} when nothing matches
        var tags = await JsonSerializer.DeserializeAsync(stream, E621Json.Default.ListE6Tag, ct).ConfigureAwait(false) ?? [];
        return tags.Where(t => !string.IsNullOrEmpty(t.Name))
            .Select(t => new TagSuggestion(t.Name!, MapCategory(t.Category), t.PostCount))
            .ToList();
    }

    internal static TagCategory MapCategory(int c) => c switch
    {
        0 => TagCategory.General,
        1 => TagCategory.Artist,
        2 => TagCategory.Artist, // contributor
        3 => TagCategory.Copyright,
        4 => TagCategory.Character,
        5 => TagCategory.Species,
        6 => TagCategory.Invalid,
        7 => TagCategory.Meta,
        8 => TagCategory.Lore,
        _ => TagCategory.General,
    };
}

internal sealed class E6V1Root { public List<E6V1Post>? Posts { get; set; } }

internal sealed class E6V1Post
{
    public long Id { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public E6File? File { get; set; }
    public E6Image? Preview { get; set; }
    public E6Sample? Sample { get; set; }
    public E6Score? Score { get; set; }
    public E6Tags? Tags { get; set; }
    public E6Flags? Flags { get; set; }
    public string? Rating { get; set; }
    public List<string>? Sources { get; set; }
    public E6Relationships? Relationships { get; set; }
    public long? UploaderId { get; set; }
    public string? UploaderName { get; set; }
    public int CommentCount { get; set; }
    public bool IsFavorited { get; set; }
    public bool HasNotes { get; set; }
}

internal sealed class E6File
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string? Ext { get; set; }
    public string? Md5 { get; set; }
    public string? Url { get; set; }
}

internal sealed class E6Image
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string? Url { get; set; }
}

internal sealed class E6Sample
{
    public bool Has { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? Url { get; set; }
}

internal sealed class E6Score { public int Total { get; set; } }

internal sealed class E6Flags { public bool Deleted { get; set; } }

internal sealed class E6Relationships
{
    public long? ParentId { get; set; }
    public bool HasChildren { get; set; }
}

internal sealed class E6Tags
{
    public List<string>? General { get; set; }
    public List<string>? Artist { get; set; }
    public List<string>? Contributor { get; set; }
    public List<string>? Copyright { get; set; }
    public List<string>? Character { get; set; }
    public List<string>? Species { get; set; }
    public List<string>? Invalid { get; set; }
    public List<string>? Meta { get; set; }
    public List<string>? Lore { get; set; }
}

internal sealed class E6V2Post
{
    public long Id { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public E6V2Files? Files { get; set; }
    public long? UploaderId { get; set; }
    public string? UploaderName { get; set; }
    public E6V2Stats? Stats { get; set; }
    public E6Flags? Flags { get; set; }
    public E6V2Has? Has { get; set; }
    public E6Relationships? Relationships { get; set; }
    public string? Rating { get; set; }
    public List<string>? Sources { get; set; }
    public List<string>? Tags { get; set; }
}

internal sealed class E6V2Files
{
    public E6V2Meta? Meta { get; set; }
    public E6Image? Original { get; set; }
    public E6V2Variant? Preview { get; set; }
    public E6V2Variant? Sample { get; set; }
}

internal sealed class E6V2Meta
{
    public string? Md5 { get; set; }
    public string? Ext { get; set; }
    public bool HasSample { get; set; }
}

internal sealed class E6V2Variant
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string? Jpg { get; set; }
    public string? Webp { get; set; }
}

internal sealed class E6V2Stats
{
    public E6Score? Score { get; set; }
    public bool IsFavorited { get; set; }
    public int CommentCount { get; set; }
}

internal sealed class E6V2Has
{
    public bool Children { get; set; }
    public bool Notes { get; set; }
}

internal sealed class E6Tag
{
    public string? Name { get; set; }
    public int Category { get; set; }
    public long PostCount { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(E6V1Root))]
[JsonSerializable(typeof(List<E6V2Post>))]
[JsonSerializable(typeof(List<E6Tag>))]
internal sealed partial class E621Json : JsonSerializerContext;
