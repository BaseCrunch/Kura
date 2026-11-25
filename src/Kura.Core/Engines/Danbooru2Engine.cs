using System.Text.Json;
using System.Text.Json.Serialization;
using Kura.Core.Models;
using Kura.Core.Net;

namespace Kura.Core.Engines;

/// <summary>Danbooru 2 (danbooru.donmai.us and forks). SPEC 1.3, 1.5, 1.7, 1.8.</summary>
public sealed class Danbooru2Engine : BooruEngineBase
{
    public override EngineType Type => EngineType.Danbooru2;
    public override EngineCapabilities Capabilities =>
        EngineCapabilities.Autocomplete | EngineCapabilities.CategorizedTags | EngineCapabilities.Notes |
        EngineCapabilities.Pools | EngineCapabilities.RemoteFavorites;
    public override double DefaultRequestsPerSecond => 10;
    protected override string DefaultRatingTerm => "rating:safe";

    public override Uri BuildPostsUrl(ServerContext ctx, PreparedQuery query, int page) =>
        MakeUri($"{ctx.BaseUrl}/posts.json?limit={PageSize}&page={page}&tags={Enc(query.Text)}");

    public override Uri BuildPingUrl(ServerContext ctx) => MakeUri($"{ctx.BaseUrl}/posts.json?limit=1");

    public override Uri? BuildAutocompleteUrl(ServerContext ctx, string term) =>
        MakeUri($"{ctx.BaseUrl}/tags.json?search[name_matches]={Enc(term)}*&search[order]=count&search[hide_empty]=true" +
                "&limit=10&only=id,name,category,post_count,is_deprecated");

    public override string PostPageUrl(ServerContext ctx, string remoteId) => $"{ctx.BaseUrl}/posts/{remoteId}";
    public override string BrowserSearchUrl(ServerContext ctx, string query) => $"{ctx.BaseUrl}/posts?tags={Enc(query)}";

    /// <summary>Uses HTTP Basic (login:api_key) so secrets stay out of URLs and logs.</summary>
    public override void PrepareRequest(HttpRequestMessage request, ServerContext ctx)
    {
        base.PrepareRequest(request, ctx);
        if (ctx.Username is { } u && ctx.ApiKey is { } k) KuraHttp.SetBasicAuth(request, u, k);
    }

    public override async Task<PostPage> ParsePostsAsync(Stream body, ServerContext ctx, CancellationToken ct)
    {
        var dtos = await JsonSerializer.DeserializeAsync(body, Danbooru2Json.Default.ListD2Post, ct).ConfigureAwait(false) ?? [];
        var posts = new List<Post>(dtos.Count);
        var hidden = 0;
        foreach (var d in dtos)
        {
            var p = Map(d, ctx);
            if (p is null) hidden++;
            else posts.Add(p);
        }
        return new PostPage(posts, dtos.Count, hidden);
    }

    internal Post? Map(D2Post d, ServerContext ctx)
    {
        // Posts without md5 are restricted for this user (SPEC 1.7).
        if (string.IsNullOrEmpty(d.Md5)) return null;
        var ext = d.FileExt ?? MediaTypes.ExtensionOf(d.FileUrl ?? "");
        var variants = d.MediaAsset?.Variants ?? [];
        D2Variant? V(string type) => variants.FirstOrDefault(v => v.Type == type && !string.IsNullOrEmpty(v.Url));

        var fileUrl = d.FileUrl ?? $"{ctx.BaseUrl}/data/{d.Md5}.{ext}";
        var file = new PostImage(fileUrl, d.ImageWidth, d.ImageHeight);

        PostImage? sample = null;
        if (V("sample") is { } sv) sample = new PostImage(sv.Url!, sv.Width, sv.Height);
        else if (!string.IsNullOrEmpty(d.LargeFileUrl) && d.LargeFileUrl != fileUrl)
            sample = new PostImage(d.LargeFileUrl, 0, 0);

        // Ugoira: play the video sample instead of the zip (SPEC 1.7, 4.4 V6).
        if (ext == "zip" && sample is not null && sample.Kind == MediaKind.Video)
        {
            file = sample with { Width = d.ImageWidth, Height = d.ImageHeight };
            sample = null;
        }

        var previewVariant = V("360x360") ?? V("180x180");
        PostImage? preview = previewVariant is not null
            ? new PostImage(previewVariant.Url!, previewVariant.Width, previewVariant.Height)
            : d.PreviewFileUrl is { Length: > 0 } pu ? new PostImage(pu, 0, 0) : null;

        var tags = new List<Tag>();
        AddTags(tags, d.TagStringArtist, TagCategory.Artist);
        AddTags(tags, d.TagStringCopyright, TagCategory.Copyright);
        AddTags(tags, d.TagStringCharacter, TagCategory.Character);
        AddTags(tags, d.TagStringGeneral, TagCategory.General);
        AddTags(tags, d.TagStringMeta, TagCategory.Meta);

        return new Post
        {
            ServerId = ctx.Server.Id,
            RemoteId = d.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PostUrl = PostPageUrl(ctx, d.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Md5 = d.Md5,
            Rating = d.Rating,
            Score = d.Score,
            Source = d.Source,
            Author = d.UploaderId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ParentId = d.ParentId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            HasChildren = d.HasChildren,
            HasNotes = d.LastNotedAt is not null,
            HasComments = d.LastCommentedAt is not null,
            CreatedAt = d.CreatedAt,
            File = file,
            Sample = sample,
            Preview = preview,
            Tags = tags,
            IsFavorite = d.IsFavorited == true,
        };
    }

    private static void AddTags(List<Tag> into, string? tagString, TagCategory cat)
    {
        if (string.IsNullOrWhiteSpace(tagString)) return;
        foreach (var t in tagString.Split(' ', StringSplitOptions.RemoveEmptyEntries)) into.Add(new Tag(t, cat));
    }

    public override async Task<IReadOnlyList<TagSuggestion>> ParseAutocompleteAsync(Stream body, ServerContext ctx, CancellationToken ct)
    {
        var tags = await JsonSerializer.DeserializeAsync(body, Danbooru2Json.Default.ListD2Tag, ct).ConfigureAwait(false) ?? [];
        return tags.Where(t => !t.IsDeprecated && !string.IsNullOrEmpty(t.Name))
            .Select(t => new TagSuggestion(t.Name!, MapCategory(t.Category), t.PostCount))
            .ToList();
    }

    internal static TagCategory MapCategory(int c) => c switch
    {
        0 => TagCategory.General,
        1 => TagCategory.Artist,
        3 => TagCategory.Copyright,
        4 => TagCategory.Character,
        5 => TagCategory.Meta,
        _ => TagCategory.General,
    };
}

internal sealed class D2Post
{
    public long Id { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public long? UploaderId { get; set; }
    public int Score { get; set; }
    public string? Source { get; set; }
    public string? Md5 { get; set; }
    public string? Rating { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public string? FileExt { get; set; }
    public string? LastNotedAt { get; set; }
    public string? LastCommentedAt { get; set; }
    public long? ParentId { get; set; }
    public bool HasChildren { get; set; }
    public bool HasLarge { get; set; }
    public string? TagStringGeneral { get; set; }
    public string? TagStringArtist { get; set; }
    public string? TagStringCharacter { get; set; }
    public string? TagStringCopyright { get; set; }
    public string? TagStringMeta { get; set; }
    public string? FileUrl { get; set; }
    public string? LargeFileUrl { get; set; }
    public string? PreviewFileUrl { get; set; }
    public bool? IsFavorited { get; set; }
    public D2MediaAsset? MediaAsset { get; set; }
}

internal sealed class D2MediaAsset
{
    public List<D2Variant>? Variants { get; set; }
}

internal sealed class D2Variant
{
    public string? Type { get; set; }
    public string? Url { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

internal sealed class D2Tag
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public int Category { get; set; }
    public long PostCount { get; set; }
    public bool IsDeprecated { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(List<D2Post>))]
[JsonSerializable(typeof(List<D2Tag>))]
internal sealed partial class Danbooru2Json : JsonSerializerContext;
