using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Kura.Core.Models;

namespace Kura.Core.Engines;

/// <summary>Shimmie 2, scraped from its post list HTML (SPEC 1.3, 1.5, 1.7).</summary>
public sealed partial class ShimmieEngine : HtmlEngineBase
{
    public override EngineType Type => EngineType.Shimmie;
    public override EngineCapabilities Capabilities => EngineCapabilities.Autocomplete;
    public override double DefaultRequestsPerSecond => 3;
    protected override string DefaultRatingTerm => "rating=s";

    /// <summary>An empty query lists <c>/post/list/{page}</c>, not <c>/post/list//{page}</c> (fixes Appendix A #10).</summary>
    public override Uri BuildPostsUrl(ServerContext ctx, PreparedQuery query, int page)
    {
        var q = query.Text.Trim();
        return MakeUri(q.Length == 0 ? $"{ctx.BaseUrl}/post/list/{page}" : $"{ctx.BaseUrl}/post/list/{Enc(q)}/{page}");
    }

    public override Uri BuildPingUrl(ServerContext ctx) => MakeUri($"{ctx.BaseUrl}/post/list/1");

    public override Uri? BuildAutocompleteUrl(ServerContext ctx, string term) =>
        MakeUri($"{ctx.BaseUrl}/api/internal/autocomplete?s={Enc(term)}");

    public override string PostPageUrl(ServerContext ctx, string remoteId) => $"{ctx.BaseUrl}/post/view/{remoteId}";
    public override string BrowserSearchUrl(ServerContext ctx, string query) => $"{ctx.BaseUrl}/post/list/{Enc(query)}/1";

    /// <summary>Shimmie's age gate checks an <c>agreed=true</c> cookie (SPEC 1.5).</summary>
    public override void ConfigureCookies(CookieContainer cookies, ServerContext ctx)
    {
        var uri = new Uri(ctx.BaseUrl);
        cookies.Add(uri, new Cookie("agreed", "true", "/"));
    }

    public override async Task<PostPage> ParsePostsAsync(Stream body, ServerContext ctx, CancellationToken ct)
    {
        using var doc = await ParseHtmlAsync(body, ct).ConfigureAwait(false);
        var thumbs = doc.QuerySelectorAll(".thumb");
        var posts = new List<Post>();
        foreach (var el in thumbs)
            if (MapThumb(el, ctx) is { } p) posts.Add(p);

        // A single-post page (search that matched exactly one id) has a main image instead of thumbnails.
        if (thumbs.Length == 0 && doc.QuerySelector("#main_image") is { } main && MapMain(main, doc, ctx) is { } single)
            posts.Add(single);

        return new PostPage(posts, Math.Max(thumbs.Length, posts.Count), Math.Max(0, thumbs.Length - posts.Count));
    }

    private Post? MapThumb(IElement el, ServerContext ctx)
    {
        var link = el.LocalName == "a" ? el : el.QuerySelector("a.shm-thumb-link, a[href*='/post/view/']");
        var id = el.GetAttribute("data-post-id")
                 ?? (link?.GetAttribute("href") is { } href && PostIdInHref().Match(href) is { Success: true } m ? m.Groups[1].Value : null);
        if (id is null) return null;

        var img = el.QuerySelector("img");
        var thumbUrl = Absolute(img?.GetAttribute("data-src") ?? img?.GetAttribute("src"), ctx);
        var ext = ExtFromMime(el.GetAttribute("data-mime")) ?? el.GetAttribute("data-ext") ?? el.GetAttribute("data-fileext");

        // A thumb may carry a direct file link besides the post link; otherwise derive the file URL from the thumbnail.
        var direct = el.QuerySelectorAll("a").Concat(el.LocalName == "a" ? [el] : [])
            .Select(a => a.GetAttribute("href"))
            .FirstOrDefault(h => h is not null && !h.Contains("/post/view/", StringComparison.Ordinal) && MediaTypes.ExtensionOf(h).Length > 0);
        var fileUrl = Absolute(direct, ctx) ?? GuessFileUrl(thumbUrl, id, ext);
        if (fileUrl is null) return null;
        if (ext is not null && MediaTypes.ExtensionOf(fileUrl) != ext) fileUrl += (fileUrl.Contains('?') ? "&" : "?") + "ext=" + ext;

        int.TryParse(el.GetAttribute("data-width"), out var w);
        int.TryParse(el.GetAttribute("data-height"), out var h);
        int.TryParse(img?.GetAttribute("width"), out var tw);
        int.TryParse(img?.GetAttribute("height"), out var th);

        return new Post
        {
            ServerId = ctx.Server.Id,
            RemoteId = id,
            PostUrl = PostPageUrl(ctx, id),
            Md5 = Md5FromThumb(thumbUrl),
            File = new PostImage(fileUrl, w, h),
            Preview = thumbUrl is null ? null : new PostImage(thumbUrl, tw, th),
            Tags = SplitTags(el.GetAttribute("data-tags")),
        };
    }

    private Post? MapMain(IElement main, IParentNode doc, ServerContext ctx)
    {
        var src = Absolute(main.GetAttribute("src"), ctx);
        var id = doc.QuerySelector("link[rel='canonical']")?.GetAttribute("href") is { } canon && PostIdInHref().Match(canon) is { Success: true } m
            ? m.Groups[1].Value : null;
        if (src is null || id is null) return null;
        int.TryParse(main.GetAttribute("data-width"), out var w);
        int.TryParse(main.GetAttribute("data-height"), out var h);
        return new Post
        {
            ServerId = ctx.Server.Id,
            RemoteId = id,
            PostUrl = PostPageUrl(ctx, id),
            File = new PostImage(src, w, h),
            Tags = SplitTags(main.GetAttribute("data-tags")),
        };
    }

    /// <summary><c>/_thumbs/{hash}/thumb.jpg</c> → <c>/_images/{hash}/{id}.{ext}</c> (SPEC 1.7).</summary>
    internal static string? GuessFileUrl(string? thumbUrl, string id, string? ext)
    {
        if (thumbUrl is null) return null;
        var url = thumbUrl.Replace("/_thumbs/", "/_images/", StringComparison.Ordinal).Replace("/thumbs/", "/images/", StringComparison.Ordinal);
        var slash = url.LastIndexOf('/');
        var name = url[(slash + 1)..];
        if (name.StartsWith("thumb", StringComparison.OrdinalIgnoreCase))
            url = url[..(slash + 1)] + id + "." + (ext ?? MediaTypes.ExtensionOf(name));
        return url;
    }

    private static string? Md5FromThumb(string? thumbUrl)
    {
        if (thumbUrl is null || !Uri.TryCreate(thumbUrl, UriKind.Absolute, out var u)) return null;
        var segs = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segs.Length >= 2 ? segs[^2] : null;
    }

    /// <summary>Maps a MIME type to an extension, ignoring parameters such as Shimmie's <c>image/gif; animated=true</c>.</summary>
    private static string? ExtFromMime(string? mime) => mime?.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => "jpg",
        "image/png" => "png",
        "image/gif" => "gif",
        "image/webp" => "webp",
        "image/avif" => "avif",
        "video/webm" => "webm",
        "video/mp4" => "mp4",
        "video/quicktime" => "mov",
        _ => null,
    };

    private static List<Tag> SplitTags(string? tags) =>
        (WebUtility.HtmlDecode(tags) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => new Tag(t, TagCategory.Unknown)).ToList();

    /// <summary>Accepts both <c>{"tag": count}</c> and newer <c>{"tag": {"count": n}}</c> replies.</summary>
    public override async Task<IReadOnlyList<TagSuggestion>> ParseAutocompleteAsync(Stream body, ServerContext ctx, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
        return doc.RootElement.EnumerateObject()
            .Select(p => new TagSuggestion(p.Name, TagCategory.Unknown,
                p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt64() : p.Value.Long("count")))
            .OrderByDescending(s => s.PostCount)
            .ToList();
    }

    [GeneratedRegex(@"/post/view/(\d+)")]
    private static partial Regex PostIdInHref();
}
