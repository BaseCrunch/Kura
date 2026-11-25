using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Kura.Core.Models;

namespace Kura.Core.Engines;

/// <summary>
/// Gelbooru 0.1.11 (and rule34.us's fork), scraped from list HTML. File URLs aren't on the list page, so they
/// are derived from the thumbnail URL as the original app did (SPEC 1.3, 1.7).
/// </summary>
public sealed partial class Gelbooru011Engine : HtmlEngineBase
{
    private const int ItemsPerPage = 20;

    public override EngineType Type => EngineType.Gelbooru011;
    public override EngineCapabilities Capabilities => EngineCapabilities.None;
    public override int FirstPage => 0;
    public override int PageSize => ItemsPerPage;
    public override double DefaultRequestsPerSecond => 3;
    protected override string DefaultRatingTerm => "rating:safe";

    private static bool IsRule34Us(ServerContext ctx) => ctx.Host.EndsWith("rule34.us", StringComparison.OrdinalIgnoreCase);

    public override Uri BuildPostsUrl(ServerContext ctx, PreparedQuery query, int page)
    {
        var q = query.Text.Trim();
        if (IsRule34Us(ctx))
            return MakeUri($"{ctx.BaseUrl}/index.php?r=posts/index{(q.Length > 0 ? "&q=" + Enc(q) : "")}&page={page}");
        return MakeUri($"{ctx.BaseUrl}/index.php?page=post&s=list&pid={page * ItemsPerPage}{(q.Length > 0 ? "&tags=" + Enc(q) : "")}");
    }

    public override Uri BuildPingUrl(ServerContext ctx) => BuildPostsUrl(ctx, new PreparedQuery("", "", false), 0);

    public override string PostPageUrl(ServerContext ctx, string remoteId) => IsRule34Us(ctx)
        ? $"{ctx.BaseUrl}/index.php?r=posts/view&id={remoteId}"
        : $"{ctx.BaseUrl}/index.php?page=post&s=view&id={remoteId}";

    public override string BrowserSearchUrl(ServerContext ctx, string query) =>
        $"{ctx.BaseUrl}/index.php?page=post&s=list&tags={Enc(query)}";

    public override async Task<PostPage> ParsePostsAsync(Stream body, ServerContext ctx, CancellationToken ct)
    {
        using var doc = await ParseHtmlAsync(body, ct).ConfigureAwait(false);
        IEnumerable<IElement> thumbs = doc.QuerySelectorAll("span.thumb");
        if (!thumbs.Any()) thumbs = doc.QuerySelectorAll(".thumbail-container a, .thumbnail-container a"); // sic: some themes misspell it
        var list = thumbs.ToList();
        var posts = new List<Post>();
        foreach (var t in list)
            if (Map(t, ctx) is { } p) posts.Add(p);
        return new PostPage(posts, list.Count, list.Count - posts.Count);
    }

    private Post? Map(IElement thumb, ServerContext ctx)
    {
        var a = thumb.LocalName == "a" ? thumb : thumb.QuerySelector("a");
        var id = IdOf(a, thumb);
        var img = thumb.QuerySelector("img");
        var thumbUrl = Absolute(img?.GetAttribute("data-src") ?? img?.GetAttribute("src"), ctx);
        if (id is null || thumbUrl is null) return null;

        // img@title carries the tags plus "score:N" and "rating:x".
        var title = WebUtility.HtmlDecode(img?.GetAttribute("title") ?? img?.GetAttribute("alt") ?? "");
        var score = ScoreIn().Match(title) is { Success: true } sm ? int.Parse(sm.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var rating = RatingIn().Match(title) is { Success: true } rm ? rm.Groups[1].Value : null;
        var tagNames = title.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.StartsWith("score:", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("rating:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var lazy = img?.HasAttribute("data-src") == true;
        var fileUrl = GuessFileUrl(thumbUrl, tagNames, lazy);
        return new Post
        {
            ServerId = ctx.Server.Id,
            RemoteId = id,
            PostUrl = PostPageUrl(ctx, id),
            Rating = rating,
            Score = score,
            File = new PostImage(fileUrl, 0, 0),
            Preview = new PostImage(thumbUrl, 0, 0),
            Tags = tagNames.Select(t => new Tag(t, TagCategory.Unknown)).ToList(),
        };
    }

    private static string? IdOf(IElement? a, IElement thumb)
    {
        foreach (var candidate in new[] { a?.Id, thumb.Id })
            if (candidate is { Length: > 1 } s && (s[0] == 'p' || s[0] == 's') && s[1..].All(char.IsAsciiDigit)) return s[1..];
        var href = a?.GetAttribute("href");
        return href is not null && IdInHref().Match(href) is { Success: true } m ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Thumbnail → file URL rewrites from the original app: thumbs→img, /thumbnails/→/images/, drop "thumbnail_",
    /// //aimg.→//img.; video posts become .webm (on a video. host for lazy-load themes), animated ones .gif.
    /// </summary>
    internal static string GuessFileUrl(string thumbUrl, IReadOnlyCollection<string> tags, bool lazyLayout)
    {
        var url = thumbUrl.Split('?')[0]
            .Replace("/thumbnails/", "/images/", StringComparison.Ordinal)
            .Replace("thumbnail_", "", StringComparison.Ordinal)
            .Replace("//aimg.", "//img.", StringComparison.Ordinal)
            .Replace("//thumbs.", "//img.", StringComparison.Ordinal)
            .Replace("/thumbs/", "/img/", StringComparison.Ordinal);

        var isVideo = tags.Any(t => t.Contains("video", StringComparison.OrdinalIgnoreCase)
                                    && !t.Equals("video_game", StringComparison.OrdinalIgnoreCase)
                                    && !t.Contains("video game", StringComparison.OrdinalIgnoreCase));
        var isAnimated = tags.Any(t => t.Equals("animated", StringComparison.OrdinalIgnoreCase));
        if (isVideo)
        {
            url = ChangeExt(url, "webm");
            if (lazyLayout && Uri.TryCreate(url, UriKind.Absolute, out var u))
            {
                var parts = u.Host.Split('.');
                var domain = parts.Length > 2 ? string.Join('.', parts[^2..]) : u.Host;
                url = new UriBuilder(u) { Host = "video." + domain }.Uri.ToString();
            }
        }
        else if (isAnimated) url = ChangeExt(url, "gif");
        return url;
    }

    private static string ChangeExt(string url, string ext)
    {
        var dot = url.LastIndexOf('.');
        var slash = url.LastIndexOf('/');
        return dot > slash ? url[..(dot + 1)] + ext : url + "." + ext;
    }

    [GeneratedRegex(@"score:(-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ScoreIn();

    [GeneratedRegex(@"rating:(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex RatingIn();

    [GeneratedRegex(@"[?&]id=(\d+)")]
    private static partial Regex IdInHref();
}
