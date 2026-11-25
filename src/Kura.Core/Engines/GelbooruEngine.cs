using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Kura.Core.Models;

namespace Kura.Core.Engines;

/// <summary>
/// Gelbooru 0.2.x DAPI (gelbooru.com, safebooru.org, rule34.xxx and forks). JSON on hosts whose quirk profile
/// says so, XML otherwise (SPEC 1.2, 1.3, 1.7).
/// </summary>
public sealed partial class GelbooruEngine : BooruEngineBase
{
    public override EngineType Type => EngineType.Gelbooru;
    public override EngineCapabilities Capabilities => EngineCapabilities.Autocomplete | EngineCapabilities.Notes;
    public override int FirstPage => 0;
    public override double DefaultRequestsPerSecond => 5;
    protected override string DefaultRatingTerm => "rating:safe";

    public static bool UsesJson(ServerContext ctx) => ctx.Quirks.GelbooruJson == true;

    public override Uri BuildPostsUrl(ServerContext ctx, PreparedQuery query, int page) =>
        MakeUri($"{ctx.BaseUrl}/index.php?page=dapi&s=post&q=index{(UsesJson(ctx) ? "&json=1" : "")}" +
                $"&limit={PageSize}&pid={page}&tags={Enc(query.Text)}{AuthSuffix(ctx)}");

    public override Uri BuildPingUrl(ServerContext ctx) =>
        MakeUri($"{ctx.BaseUrl}/index.php?page=dapi&s=post&q=index{(UsesJson(ctx) ? "&json=1" : "")}&limit=1{AuthSuffix(ctx)}");

    public override Uri? BuildAutocompleteUrl(ServerContext ctx, string term) => ctx.Quirks.NativeAutocomplete
        ? MakeUri($"{ctx.BaseUrl}/index.php?page=autocomplete2&term={Enc(term)}&type=tag_query&limit=10{AuthSuffix(ctx)}")
        : MakeUri($"{ctx.BaseUrl}/autocomplete.php?q={Enc(term)}");

    public override string PostPageUrl(ServerContext ctx, string remoteId) => $"{ctx.BaseUrl}/index.php?page=post&s=view&id={remoteId}";
    public override string BrowserSearchUrl(ServerContext ctx, string query) => $"{ctx.BaseUrl}/index.php?page=post&s=list&tags={Enc(query)}";

    /// <summary>
    /// Gelbooru credentials are query parameters. The API key and user id are separate fields (SPEC 4.1 N6);
    /// a legacy raw suffix pasted into the key field (<c>&amp;api_key=…&amp;user_id=…</c>) is still understood.
    /// </summary>
    public static string AuthSuffix(ServerContext ctx)
    {
        var (key, user) = ParseCredentials(ctx.ApiKey, ctx.Username);
        if (key is null || user is null) return "";
        return $"&api_key={Uri.EscapeDataString(key)}&user_id={Uri.EscapeDataString(user)}";
    }

    public static (string? Key, string? UserId) ParseCredentials(string? apiKey, string? username)
    {
        if (apiKey is not null && apiKey.Contains("api_key=", StringComparison.OrdinalIgnoreCase))
        {
            string? key = null, user = username;
            foreach (var part in apiKey.TrimStart('&', '?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2) continue;
                if (kv[0].Equals("api_key", StringComparison.OrdinalIgnoreCase)) key = Uri.UnescapeDataString(kv[1]);
                else if (kv[0].Equals("user_id", StringComparison.OrdinalIgnoreCase)) user = Uri.UnescapeDataString(kv[1]);
            }
            return (key, user);
        }
        return (apiKey, username);
    }

    public override async Task<PostPage> ParsePostsAsync(Stream body, ServerContext ctx, CancellationToken ct)
    {
        var (first, stream) = await Lenient.PeekAsync(body, ct).ConfigureAwait(false);
        await using var _ = stream;
        if (first == (byte)'<') return await ParseXmlAsync(stream, ctx, ct).ConfigureAwait(false);
        if (first == 0) return PostPage.Empty;
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return ParseJson(doc.RootElement, ctx);
    }

    internal PostPage ParseJson(JsonElement root, ServerContext ctx)
    {
        // A bare string is a server message, e.g. "Too deep! Pull it back some." (SPEC 1.6).
        if (root.ValueKind == JsonValueKind.String) return new PostPage([], 0, ServerMessage: root.GetString());

        JsonElement array;
        if (root.ValueKind == JsonValueKind.Array) array = root;
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("post", out var p))
            array = p.ValueKind == JsonValueKind.Array ? p : default;
        else if (root.ValueKind == JsonValueKind.Object && (root.Str("message") ?? root.Str("reason")) is { } msg)
            return new PostPage([], 0, ServerMessage: msg);
        else return PostPage.Empty;

        if (array.ValueKind != JsonValueKind.Array) return PostPage.Empty;
        var posts = new List<Post>();
        var raw = 0;
        foreach (var e in array.EnumerateArray())
        {
            raw++;
            var post = Map(new JsonFields(e), ctx);
            if (post is not null) posts.Add(post);
        }
        return new PostPage(posts, raw, raw - posts.Count);
    }

    private async Task<PostPage> ParseXmlAsync(Stream stream, ServerContext ctx, CancellationToken ct)
    {
        XDocument doc;
        try { doc = await XDocument.LoadAsync(stream, LoadOptions.None, ct).ConfigureAwait(false); }
        catch (XmlException) { return PostPage.Empty; } // HTML or garbage; treated as end of results
        var rootEl = doc.Root;
        if (rootEl is null) return PostPage.Empty;
        if (rootEl.Name.LocalName == "response" && rootEl.Attribute("success")?.Value == "false")
            return new PostPage([], 0, ServerMessage: rootEl.Attribute("reason")?.Value ?? rootEl.Value);

        var posts = new List<Post>();
        var raw = 0;
        foreach (var el in rootEl.Elements("post"))
        {
            raw++;
            var post = Map(new XmlFields(el), ctx);
            if (post is not null) posts.Add(post);
        }
        return new PostPage(posts, raw, raw - posts.Count);
    }

    private Post? Map(IFields f, ServerContext ctx)
    {
        var id = f.Str("id");
        if (id is null) return null;

        var fileUrl = f.Str("file_url");
        if (fileUrl is null && f.Str("directory") is { } dir && f.Str("image") is { } img)
            fileUrl = $"{ctx.BaseUrl}/images/{dir}/{img}";
        if (fileUrl is null) return null;
        fileUrl = Absolute(fileUrl, ctx);

        var width = f.Int("width");
        var height = f.Int("height");
        PostImage? sample = null;
        var sampleUrl = f.Str("sample_url");
        var hasSample = f.Str("sample") is not { } flag || flag is "1" or "true";
        if (sampleUrl is not null && hasSample && !SameUrl(sampleUrl, fileUrl))
            sample = new PostImage(Absolute(sampleUrl, ctx), f.Int("sample_width"), f.Int("sample_height"));

        var previewUrl = f.Str("preview_url");
        var preview = previewUrl is null ? null : new PostImage(Absolute(previewUrl, ctx), f.Int("preview_width"), f.Int("preview_height"));

        if (ctx.Quirks.CacheBuster)
        {
            // rule34.xxx's CDN "polish" serves stale/garbled files without a cache-buster (SPEC 1.7).
            var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            fileUrl = AddQuery(fileUrl, $"t={t}");
            if (sample is not null) sample = sample with { Url = AddQuery(sample.Url, $"t={t}") };
        }

        var tags = (Lenient.Html(f.Str("tags")) ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new Tag(t, TagCategory.Unknown))
            .ToList();

        var parent = f.Str("parent_id");
        return new Post
        {
            ServerId = ctx.Server.Id,
            RemoteId = id,
            PostUrl = PostPageUrl(ctx, id),
            Md5 = f.Str("md5") ?? f.Str("hash"),
            Rating = f.Str("rating"),
            Score = f.Int("score"),
            Source = Lenient.Html(f.Str("source")),
            Author = f.Str("owner") ?? f.Str("creator_id"),
            ParentId = parent is null or "0" ? null : parent,
            HasChildren = f.Bool("has_children"),
            HasNotes = f.Bool("has_notes"),
            HasComments = f.Bool("has_comments") || f.Int("comment_count") > 0,
            CreatedAt = ParseDate(f.Str("created_at")),
            File = new PostImage(fileUrl, width, height),
            Sample = sample,
            Preview = preview,
            Tags = tags,
        };
    }

    public override async Task<IReadOnlyList<TagSuggestion>> ParseAutocompleteAsync(Stream body, ServerContext ctx, CancellationToken ct)
    {
        var (first, stream) = await Lenient.PeekAsync(body, ct).ConfigureAwait(false);
        await using var _ = stream;
        if (first != (byte)'[') return [];
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var result = new List<TagSuggestion>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.Str("type") is { } type && type != "tag") continue; // autocomplete2 also returns users/pools
            var name = e.Str("value");
            if (name is null) continue;
            long count = e.Long("post_count");
            if (count == 0 && e.Str("label") is { } label && CountInLabel().Match(label) is { Success: true } m)
                count = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            result.Add(new TagSuggestion(Lenient.Html(name)!, MapCategory(e.Str("category")), count));
        }
        return result;
    }

    internal static TagCategory MapCategory(string? c) => c?.ToLowerInvariant() switch
    {
        "artist" or "1" => TagCategory.Artist,
        "copyright" or "3" => TagCategory.Copyright,
        "character" or "4" => TagCategory.Character,
        "metadata" or "meta" or "5" => TagCategory.Meta,
        "tag" or "general" or "0" => TagCategory.General,
        _ => TagCategory.Unknown,
    };

    private static bool SameUrl(string a, string b) => string.Equals(a.Split('?')[0], b.Split('?')[0], StringComparison.OrdinalIgnoreCase);

    private static string AddQuery(string url, string kv) => url + (url.Contains('?') ? "&" : "?") + kv;

    private static string Absolute(string url, ServerContext ctx)
    {
        if (url.StartsWith("//", StringComparison.Ordinal)) return "https:" + url;
        if (url.StartsWith('/')) return ctx.BaseUrl + url;
        return url;
    }

    /// <summary>Gelbooru dates look like <c>Wed Nov 25 11:00:00 -0500 2026</c>.</summary>
    internal static DateTimeOffset? ParseDate(string? s)
    {
        if (s is null) return null;
        var m = GelbooruDate().Match(s);
        if (m.Success)
        {
            var normalized = $"{m.Groups["rest"].Value} {m.Groups["year"].Value} {m.Groups["sign"].Value}{m.Groups["hh"].Value}:{m.Groups["mm"].Value}";
            if (DateTimeOffset.TryParseExact(normalized, "ddd MMM d HH:mm:ss yyyy zzz", CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var d)) return d;
        }
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix) && unix > 0)
            return DateTimeOffset.FromUnixTimeSeconds(unix);
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var any) ? any : null;
    }

    [GeneratedRegex(@"^(?<rest>\w{3} \w{3} +\d{1,2} \d{2}:\d{2}:\d{2}) (?<sign>[+-])(?<hh>\d{2})(?<mm>\d{2}) (?<year>\d{4})$")]
    private static partial Regex GelbooruDate();

    [GeneratedRegex(@"\((\d+)\)\s*$")]
    private static partial Regex CountInLabel();

    private interface IFields
    {
        string? Str(string name);
        int Int(string name) => int.TryParse(Str(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        bool Bool(string name) => Str(name) is { } s && (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    private readonly struct JsonFields(JsonElement e) : IFields
    {
        public string? Str(string name) => e.Str(name);
    }

    private readonly struct XmlFields(XElement el) : IFields
    {
        public string? Str(string name) => el.Attribute(name)?.Value is { Length: > 0 } v ? v.Trim() : null;
    }
}
