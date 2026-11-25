using System.Text;
using Kura.Core.Engines;
using Kura.Core.Models;

namespace Kura.Tests.Engines;

internal static class Fx
{
    public static readonly QuirkProfiles Quirks = QuirkProfiles.LoadEmbedded();

    public static Stream Open(string name) => File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static Stream Text(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    public static ServerContext Ctx(EngineType engine, string url, string? user = null, string? key = null, bool rating = false) =>
        new(new Server { Id = 7, Name = "t", Url = url, Engine = engine, Username = user, RatingFilterEnabled = rating },
            new ServerCredentials(key, null), Quirks.Resolve(new Uri(url).Host));
}

public class Danbooru2Tests
{
    private readonly Danbooru2Engine _e = new();

    [Fact]
    public async Task Parses_real_response()
    {
        var ctx = Fx.Ctx(EngineType.Danbooru2, "https://danbooru.donmai.us");
        await using var s = Fx.Open("danbooru2_posts.json");
        var page = await _e.ParsePostsAsync(s, ctx, default);

        Assert.Equal(6, page.RawCount);
        Assert.NotEmpty(page.Posts);
        foreach (var p in page.Posts)
        {
            Assert.Equal(7, p.ServerId);
            Assert.StartsWith("https://", p.File.Url);
            Assert.NotNull(p.Md5);
            Assert.Contains("/360x360/", p.Preview!.Url);
            Assert.True(p.File.Width > 0 && p.File.Height > 0);
            Assert.Equal($"https://danbooru.donmai.us/posts/{p.RemoteId}", p.PostUrl);
            Assert.Contains(p.Tags, t => t.Category == TagCategory.General);
        }
        Assert.Contains(page.Posts, p => p.Kind == MediaKind.Video);
        Assert.Contains(page.Posts, p => p.Tags.Any(t => t.Category == TagCategory.Artist));
    }

    [Fact]
    public void Skips_posts_without_md5_and_uses_ugoira_video_sample()
    {
        var ctx = Fx.Ctx(EngineType.Danbooru2, "https://danbooru.donmai.us");
        Assert.Null(_e.Map(new D2Post { Id = 1, FileUrl = "https://x/a.jpg" }, ctx));

        var ugoira = _e.Map(new D2Post
        {
            Id = 2, Md5 = "abc", FileExt = "zip", ImageWidth = 600, ImageHeight = 400,
            FileUrl = "https://cdn/original/abc.zip", LargeFileUrl = "https://cdn/sample/abc.webm",
        }, ctx)!;
        Assert.Equal(MediaKind.Video, ugoira.Kind);
        Assert.Equal(600, ugoira.File.Width);
        Assert.Null(ugoira.Sample);
    }

    [Fact]
    public void Builds_urls_with_rating_filter_and_keeps_secrets_out_of_url()
    {
        var ctx = Fx.Ctx(EngineType.Danbooru2, "https://danbooru.donmai.us/", user: "alice", key: "s3cret", rating: true);
        var q = QueryPreprocessor.Prepare(_e, ctx, "blue_sky *");
        Assert.Equal("blue_sky rating:g", q.Text);
        var url = _e.BuildPostsUrl(ctx, q, 2).AbsoluteUri;
        Assert.Equal("https://danbooru.donmai.us/posts.json?limit=100&page=2&tags=blue_sky%20rating%3Ag", url);
        Assert.DoesNotContain("s3cret", url);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        _e.PrepareRequest(req, ctx);
        Assert.Equal("Basic", req.Headers.Authorization!.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:s3cret")), req.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task Parses_autocomplete()
    {
        var ctx = Fx.Ctx(EngineType.Danbooru2, "https://danbooru.donmai.us");
        await using var s = Fx.Open("danbooru2_tags.json");
        var tags = await _e.ParseAutocompleteAsync(s, ctx, default);
        Assert.Equal("hatsune_miku", tags[0].Name);
        Assert.Equal(TagCategory.Character, tags[0].Category);
        Assert.True(tags[0].PostCount > 1000);
    }
}

public class E621Tests
{
    private readonly E621Engine _e = new();

    [Fact]
    public async Task Parses_v1_with_categories_and_meta_tag_strings()
    {
        var ctx = Fx.Ctx(EngineType.E621, "https://e621.net");
        await using var s = Fx.Open("e621_posts_v1.json");
        var page = await _e.ParsePostsAsync(s, ctx, default);

        Assert.Equal(4, page.RawCount);
        Assert.NotEmpty(page.Posts);
        var p = page.Posts[0];
        Assert.NotNull(p.Sample);
        Assert.NotNull(p.Preview);
        Assert.Contains(p.Tags, t => t.Category == TagCategory.Species);
        // Appendix A #2: meta tags must be tag names, never a serialized JSON object.
        Assert.All(p.Tags.Where(t => t.Category == TagCategory.Meta), t => Assert.DoesNotContain("{", t.Name));
        Assert.Contains(page.Posts, x => x.Kind == MediaKind.Video);
    }

    [Fact]
    public async Task Parses_v2_array_shape_as_fallback()
    {
        var ctx = Fx.Ctx(EngineType.E621, "https://e621.net");
        await using var s = Fx.Open("e621_posts_v2.json");
        var page = await _e.ParsePostsAsync(s, ctx, default);
        Assert.Equal(2, page.RawCount);
        Assert.All(page.Posts, p => Assert.All(p.Tags, t => Assert.Equal(TagCategory.Unknown, t.Category)));
        Assert.All(page.Posts, p => Assert.StartsWith("https://static1.e621.net/", p.File.Url));
    }

    [Fact]
    public void User_agent_handles_anonymous_and_named_users()
    {
        var anon = _e.UserAgent(Fx.Ctx(EngineType.E621, "https://e621.net"));
        Assert.StartsWith("Kura/", anon);
        Assert.Contains("anonymous", anon);
        Assert.DoesNotContain("by ", anon);
        Assert.DoesNotContain("()", anon);

        var named = _e.UserAgent(Fx.Ctx(EngineType.E621, "https://e621.net", user: "  wolfy "));
        Assert.EndsWith("(by wolfy on e621)", named);

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://e621.net/posts.json");
        _e.PrepareRequest(req, Fx.Ctx(EngineType.E621, "https://e621.net", user: "wolfy"));
        Assert.Null(req.Headers.Authorization); // username without key: UA only, no auth
        Assert.Contains("wolfy", req.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Parses_autocomplete_and_empty_object_reply()
    {
        var ctx = Fx.Ctx(EngineType.E621, "https://e621.net");
        await using var s = Fx.Open("e621_tags.json");
        var tags = await _e.ParseAutocompleteAsync(s, ctx, default);
        Assert.Equal("wolf", tags[0].Name);
        Assert.Equal(TagCategory.Species, tags[0].Category);
        Assert.Empty(await _e.ParseAutocompleteAsync(Fx.Text("{\"tags\":[]}"), ctx, default));
    }

    [Fact]
    public void Rate_limit_quirk_is_two_per_second() =>
        Assert.Equal(2, Fx.Quirks.Resolve("e621.net").RequestsPerSecond);
}

public class GelbooruTests
{
    private readonly GelbooruEngine _e = new();

    [Fact]
    public async Task Parses_safebooru_json_with_html_entities_and_nulls()
    {
        var ctx = Fx.Ctx(EngineType.Gelbooru, "https://safebooru.org");
        await using var s = Fx.Open("gelbooru_safebooru.json");
        var page = await _e.ParsePostsAsync(s, ctx, default);
        Assert.Equal(3, page.Posts.Count);
        var p = page.Posts[0];
        Assert.Equal("7347211", p.RemoteId);
        Assert.Equal(0, p.Score);
        Assert.Null(p.ParentId);
        Assert.Contains(p.Tags, t => t.Name == "bang_dream!_it's_mygo!!!!!");
        Assert.NotNull(p.Sample);
        Assert.Equal("https://safebooru.org/index.php?page=post&s=view&id=7347211", p.PostUrl);
    }

    [Fact]
    public async Task Json_and_xml_agree()
    {
        var ctx = Fx.Ctx(EngineType.Gelbooru, "https://safebooru.org");
        await using var j = Fx.Open("gelbooru_safebooru.json");
        await using var x = Fx.Open("gelbooru_safebooru.xml");
        var pj = await _e.ParsePostsAsync(j, ctx, default);
        var px = await _e.ParsePostsAsync(x, ctx, default);
        Assert.Equal(pj.Posts.Select(p => p.RemoteId), px.Posts.Select(p => p.RemoteId));
        Assert.Equal(pj.Posts.Select(p => p.File.Url), px.Posts.Select(p => p.File.Url));
    }

    [Fact]
    public async Task Parses_gelbooru_com_shape()
    {
        var ctx = Fx.Ctx(EngineType.Gelbooru, "https://gelbooru.com");
        await using var s = Fx.Open("gelbooru_com.json");
        var page = await _e.ParsePostsAsync(s, ctx, default);
        Assert.Equal(2, page.Posts.Count);
        var a = page.Posts[0];
        Assert.True(a.HasNotes);
        Assert.Equal(12, a.Score);
        Assert.Contains(a.Tags, t => t.Name == "tom_&_jerry");
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 11, 0, 0, TimeSpan.FromHours(-5)), a.CreatedAt);
        var b = page.Posts[1];
        Assert.Equal(MediaKind.Video, b.Kind);
        Assert.Null(b.Sample);
        Assert.True(b.HasChildren);
        Assert.Equal("11000001", b.ParentId);
    }

    [Fact]
    public async Task Server_message_and_empty_results()
    {
        var ctx = Fx.Ctx(EngineType.Gelbooru, "https://gelbooru.com");
        var msg = await _e.ParsePostsAsync(Fx.Text("\"Too deep! Pull it back some.\""), ctx, default);
        Assert.Equal("Too deep! Pull it back some.", msg.ServerMessage);
        Assert.Equal(0, msg.RawCount);
        var empty = await _e.ParsePostsAsync(Fx.Text("{\"@attributes\":{\"count\":0}}"), ctx, default);
        Assert.Equal(0, empty.RawCount);
        var xmlErr = await _e.ParsePostsAsync(Fx.Text("<response success=\"false\" reason=\"Search error\"/>"),
            Fx.Ctx(EngineType.Gelbooru, "https://example.org"), default);
        Assert.Equal("Search error", xmlErr.ServerMessage);
    }

    [Fact]
    public void Credentials_as_separate_fields_or_legacy_suffix()
    {
        var ctx = Fx.Ctx(EngineType.Gelbooru, "https://gelbooru.com", user: "123", key: "abc");
        Assert.Equal("&api_key=abc&user_id=123", GelbooruEngine.AuthSuffix(ctx));
        var legacy = Fx.Ctx(EngineType.Gelbooru, "https://gelbooru.com", key: "&api_key=xyz&user_id=456");
        Assert.Equal("&api_key=xyz&user_id=456", GelbooruEngine.AuthSuffix(legacy));
        Assert.Equal("", GelbooruEngine.AuthSuffix(Fx.Ctx(EngineType.Gelbooru, "https://gelbooru.com", key: "only-key")));
    }

    [Fact]
    public void Json_vs_xml_and_pid_paging()
    {
        var gel = Fx.Ctx(EngineType.Gelbooru, "https://gelbooru.com");
        var other = Fx.Ctx(EngineType.Gelbooru, "https://booru.example");
        var q = new PreparedQuery("x", "x", false);
        Assert.Contains("&json=1", _e.BuildPostsUrl(gel, q, 0).ToString());
        Assert.DoesNotContain("json=1", _e.BuildPostsUrl(other, q, 3).ToString());
        Assert.Contains("&pid=3", _e.BuildPostsUrl(other, q, 3).ToString());
        Assert.Equal(0, _e.FirstPage);
    }

    [Fact]
    public async Task Rule34_cache_buster()
    {
        var ctx = Fx.Ctx(EngineType.Gelbooru, "https://api.rule34.xxx");
        await using var s = Fx.Open("gelbooru_safebooru.json");
        var page = await _e.ParsePostsAsync(s, ctx, default);
        Assert.All(page.Posts, p => Assert.Contains("?t=", p.File.Url));
    }

    [Fact]
    public async Task Parses_both_autocomplete_shapes()
    {
        var ctx = Fx.Ctx(EngineType.Gelbooru, "https://safebooru.org");
        var classic = await _e.ParseAutocompleteAsync(
            Fx.Text("[{\"label\":\"hatsune_miku (114237)\",\"value\":\"hatsune_miku\"}]"), ctx, default);
        Assert.Equal(114237, classic[0].PostCount);
        Assert.Equal(TagCategory.Unknown, classic[0].Category);

        var v2 = await _e.ParseAutocompleteAsync(Fx.Text(
            "[{\"type\":\"tag\",\"label\":\"hatsune_miku\",\"value\":\"hatsune_miku\",\"post_count\":\"98000\",\"category\":\"character\"}," +
            "{\"type\":\"user\",\"label\":\"x\",\"value\":\"x\"}]"), ctx, default);
        Assert.Single(v2);
        Assert.Equal(TagCategory.Character, v2[0].Category);
        Assert.Equal(98000, v2[0].PostCount);
    }

    [Fact]
    public void Autocomplete_endpoint_depends_on_host()
    {
        Assert.Contains("page=autocomplete2", _e.BuildAutocompleteUrl(Fx.Ctx(EngineType.Gelbooru, "https://gelbooru.com"), "ha")!.ToString());
        Assert.EndsWith("/autocomplete.php?q=ha", _e.BuildAutocompleteUrl(Fx.Ctx(EngineType.Gelbooru, "https://safebooru.org"), "ha")!.ToString());
    }
}
