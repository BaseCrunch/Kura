using System.Globalization;
using System.Text.Json;
using Kura.Core.Models;

namespace Kura.Core.Engines;

/// <summary>
/// Kemono (creator-post archive). Each post fans out into one item per unique supported file (SPEC 1.7).
/// The API gives no image dimensions, so cells start square.
/// </summary>
public sealed class KemonoEngine : BooruEngineBase
{
    public override EngineType Type => EngineType.Kemono;
    public override EngineCapabilities Capabilities => EngineCapabilities.None;
    public override int PageSize => 50;
    public override int FirstPage => 0;
    public override double DefaultRequestsPerSecond => 2;
    protected override string DefaultRatingTerm => ""; // no ratings on Kemono

    public override Uri BuildPostsUrl(ServerContext ctx, PreparedQuery query, int page)
    {
        var q = query.Text.Trim();
        return MakeUri($"{ctx.BaseUrl}/api/v1/posts?o={page * PageSize}{(q.Length > 0 ? "&q=" + Enc(q) : "")}");
    }

    public override Uri BuildPingUrl(ServerContext ctx) => MakeUri($"{ctx.BaseUrl}/api/v1/posts?o=0");
    public override string PostPageUrl(ServerContext ctx, string remoteId) => ctx.BaseUrl;
    public override string BrowserSearchUrl(ServerContext ctx, string query) => $"{ctx.BaseUrl}/posts?q={Enc(query)}";

    public override async Task<PostPage> ParsePostsAsync(Stream body, ServerContext ctx, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        // /api/v1/posts wraps results; creator feeds return a bare array.
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("posts", out var inner)) root = inner;
        if (root.ValueKind != JsonValueKind.Array) return PostPage.Empty;

        var items = new List<Post>();
        var raw = 0;
        foreach (var p in root.EnumerateArray())
        {
            raw++;
            items.AddRange(FanOut(p, ctx));
        }
        // RawCount counts API posts so paging ends on an empty page, not on a page of text-only posts.
        return new PostPage(items, raw);
    }

    private static IEnumerable<Post> FanOut(JsonElement p, ServerContext ctx)
    {
        var id = p.Str("id");
        var user = p.Str("user");
        var service = p.Str("service");
        if (id is null || user is null || service is null) yield break;

        var paths = new List<string>();
        if (p.TryGetProperty("file", out var f) && f.Str("path") is { } fp) paths.Add(fp);
        if (p.TryGetProperty("attachments", out var att) && att.ValueKind == JsonValueKind.Array)
            foreach (var a in att.EnumerateArray()) if (a.Str("path") is { } ap) paths.Add(ap);

        var postUrl = $"{ctx.BaseUrl}/{service}/user/{user}/post/{id}";
        var created = DateTimeOffset.TryParse(p.Str("published"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d : (DateTimeOffset?)null;
        var tags = new List<Tag> { new(user, TagCategory.Artist), new(service, TagCategory.Meta) };
        if (p.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
            foreach (var tag in t.EnumerateArray()) if (tag.GetString() is { Length: > 0 } tagName) tags.Add(new Tag(tagName.Replace(' ', '_'), TagCategory.General));

        var n = 0;
        foreach (var path in paths.Distinct())
        {
            var ext = MediaTypes.ExtensionOf(path);
            if (!MediaTypes.Supported.Contains(ext) || ext == "zip") continue;
            var file = new PostImage(FileUrl(ctx, path), 0, 0);
            var name = Path.GetFileNameWithoutExtension(path);
            yield return new Post
            {
                ServerId = ctx.Server.Id,
                RemoteId = n == 0 ? id : $"{id}-{n}",
                PostUrl = postUrl,
                Title = p.Str("title"),
                Md5 = name,
                Author = user,
                CreatedAt = created,
                File = file,
                Preview = file.Kind == MediaKind.Image || file.Kind == MediaKind.Gif ? new PostImage(ThumbUrl(ctx, path), 0, 0) : null,
                Tags = tags,
            };
            n++;
        }
    }

    /// <summary>Files are served from <c>/data{path}</c> (redirecting to a storage node).</summary>
    internal static string FileUrl(ServerContext ctx, string path) => $"{ctx.BaseUrl}/data{path}";

    /// <summary>Thumbnails come from the <c>img.</c> subdomain: <c>https://img.host/thumbnail/data{path}</c>.</summary>
    internal static string ThumbUrl(ServerContext ctx, string path)
    {
        var uri = new Uri(ctx.BaseUrl);
        var host = uri.Host.StartsWith("img.", StringComparison.OrdinalIgnoreCase) ? uri.Host : "img." + uri.Host;
        return $"{uri.Scheme}://{host}/thumbnail/data{path}";
    }
}
