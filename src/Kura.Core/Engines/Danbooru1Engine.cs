using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kura.Core.Models;

namespace Kura.Core.Engines;

/// <summary>Danbooru 1.x and Moebooru (konachan, yande.re, sakugabooru…). SPEC 1.3, 1.5, 1.7.</summary>
public sealed class Danbooru1Engine : BooruEngineBase
{
    public override EngineType Type => EngineType.Danbooru1;
    public override EngineCapabilities Capabilities => EngineCapabilities.Autocomplete | EngineCapabilities.Notes | EngineCapabilities.Pools;
    protected override string DefaultRatingTerm => "rating:safe";

    public override Uri BuildPostsUrl(ServerContext ctx, PreparedQuery query, int page) =>
        MakeUri($"{ctx.BaseUrl}/post/index.json?{AuthQuery(ctx)}limit={PageSize}&page={page}&tags={Enc(query.Text)}");

    public override Uri BuildPingUrl(ServerContext ctx) => MakeUri($"{ctx.BaseUrl}/post/index.json?{AuthQuery(ctx)}limit=1");

    public override Uri? BuildAutocompleteUrl(ServerContext ctx, string term) =>
        MakeUri($"{ctx.BaseUrl}/tag.json?name={Enc(term)}*&order=count&limit=10");

    public override string PostPageUrl(ServerContext ctx, string remoteId) => $"{ctx.BaseUrl}/post/show/{remoteId}";
    public override string BrowserSearchUrl(ServerContext ctx, string query) => $"{ctx.BaseUrl}/post?tags={Enc(query)}";

    /// <summary><c>login</c> + <c>password_hash</c> query parameters, only when both are set.</summary>
    private static string AuthQuery(ServerContext ctx)
    {
        var password = ctx.Password ?? ctx.ApiKey;
        if (ctx.Username is not { } user || password is null) return "";
        return $"login={Uri.EscapeDataString(user)}&password_hash={PasswordHash(ctx.Host, password)}&";
    }

    /// <summary><c>sha1(salt with "your-password" replaced)</c>, salt chosen by host (SPEC 1.5).</summary>
    public static string PasswordHash(string host, string password)
    {
        var salt = host switch
        {
            _ when host.Contains("konachan", StringComparison.OrdinalIgnoreCase) => "So-I-Heard-You-Like-Mupkids-?--your-password--",
            _ when host.Contains("yukkuri.shii", StringComparison.OrdinalIgnoreCase) => "masaorz-r5quenm--your-password--",
            _ when host.Contains("sakugabooru", StringComparison.OrdinalIgnoreCase) => "er@!$rjiajd0$!dkaopc350!Y%)--your-password--",
            _ => "choujin-steiner--your-password--",
        };
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(salt.Replace("your-password", password, StringComparison.Ordinal)));
        return Convert.ToHexStringLower(bytes);
    }

    public override async Task<PostPage> ParsePostsAsync(Stream body, ServerContext ctx, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        // Moebooru answers /post/index.json with an array; some forks wrap it.
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("posts", out var inner)) root = inner;
        if (root.ValueKind != JsonValueKind.Array) return PostPage.Empty;
        var posts = new List<Post>();
        var raw = 0;
        foreach (var e in root.EnumerateArray())
        {
            raw++;
            if (Map(e, ctx) is { } p) posts.Add(p);
        }
        return new PostPage(posts, raw, raw - posts.Count);
    }

    private Post? Map(JsonElement e, ServerContext ctx)
    {
        var id = e.Str("id");
        var fileUrl = Absolute(e.Str("file_url"), ctx);
        if (id is null || fileUrl is null) return null;
        if (e.Str("status") == "deleted") return null;

        var file = new PostImage(fileUrl, e.Int("width"), e.Int("height"));
        PostImage? sample = null;
        if (Absolute(e.Str("sample_url"), ctx) is { } su && su != fileUrl && !file.IsAnimated)
            sample = new PostImage(su, e.Int("sample_width"), e.Int("sample_height"));

        PostImage? preview = null;
        if (Absolute(e.Str("preview_url"), ctx) is { } pu)
        {
            // Moebooru's preview file is larger than preview_width says; actual_preview_* gives its real size.
            var pw = e.Int("actual_preview_width") is > 0 and var aw ? aw : e.Int("preview_width");
            var ph = e.Int("actual_preview_height") is > 0 and var ah ? ah : e.Int("preview_height");
            preview = new PostImage(pu, pw, ph);
        }

        var tags = (Lenient.Html(e.Str("tags")) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new Tag(t, TagCategory.Unknown)).ToList();

        var parent = e.Str("parent_id");
        return new Post
        {
            ServerId = ctx.Server.Id,
            RemoteId = id,
            PostUrl = PostPageUrl(ctx, id),
            Md5 = e.Str("md5"),
            Rating = e.Str("rating"),
            Score = e.Int("score"),
            Source = e.Str("source"),
            Author = e.Str("author"),
            ParentId = parent is null or "0" ? null : parent,
            HasChildren = e.Bool("has_children"),
            HasNotes = e.Long("last_noted_at") > 0,
            HasComments = e.Long("last_commented_at") > 0,
            CreatedAt = ParseCreated(e),
            File = file,
            Sample = sample,
            Preview = preview,
            Tags = tags,
        };
    }

    /// <summary>Unix seconds (Moebooru), <c>{"s": seconds}</c> (Danbooru 1 Ruby Time), or a date string.</summary>
    private static DateTimeOffset? ParseCreated(JsonElement e)
    {
        if (!e.TryGetProperty("created_at", out var c)) return null;
        if (c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out var unix) && unix > 0) return DateTimeOffset.FromUnixTimeSeconds(unix);
        if (c.ValueKind == JsonValueKind.Object && c.Long("s") is > 0 and var s) return DateTimeOffset.FromUnixTimeSeconds(s);
        if (c.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(c.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)) return d;
        return null;
    }

    private static string? Absolute(string? url, ServerContext ctx) => url switch
    {
        null => null,
        _ when url.StartsWith("//", StringComparison.Ordinal) => "https:" + url,
        _ when url.StartsWith('/') => ctx.BaseUrl + url,
        _ => url,
    };

    public override async Task<IReadOnlyList<TagSuggestion>> ParseAutocompleteAsync(Stream body, ServerContext ctx, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        return doc.RootElement.EnumerateArray()
            .Where(t => t.Str("name") is not null)
            .Select(t => new TagSuggestion(t.Str("name")!, MapCategory(t.Int("type")), t.Long("count")))
            .ToList();
    }

    internal static TagCategory MapCategory(int type) => type switch
    {
        1 => TagCategory.Artist,
        3 => TagCategory.Copyright,
        4 => TagCategory.Character,
        5 => TagCategory.Artist, // Moebooru "circle" (artist group)
        6 => TagCategory.Meta,   // Moebooru "faults"
        _ => TagCategory.General,
    };
}
