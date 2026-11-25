using System.Net;
using System.Text;
using Kura.Core.Engines;
using Kura.Core.Models;
using Kura.Core.Net;
using Kura.Core.Search;

namespace Kura.Tests.Engines;

public class Danbooru1Tests
{
    private readonly Danbooru1Engine _e = new();

    [Fact]
    public async Task Parses_moebooru_response()
    {
        var ctx = Fx.Ctx(EngineType.Danbooru1, "https://konachan.net");
        await using var s = Fx.Open("moebooru_posts.json");
        var page = await _e.ParsePostsAsync(s, ctx, default);
        Assert.Equal(3, page.RawCount);
        var p = page.Posts[0];
        Assert.Contains("konachan.net/image/", p.File.Url);
        Assert.True(p.File.Width > 0);
        Assert.NotNull(p.Sample);
        Assert.True(p.Preview!.Width >= 150); // actual_preview_* preferred over the nominal preview size
        Assert.Equal("https://konachan.net/post/show/" + p.RemoteId, p.PostUrl);
        Assert.NotNull(p.CreatedAt);
        Assert.All(p.Tags, t => Assert.Equal(TagCategory.Unknown, t.Category));
    }

    [Fact]
    public void Password_hash_uses_host_salt()
    {
        // Reference values computed independently: sha1(salt with the password substituted).
        Assert.Equal("80a114d2973833fb8d6c18ef9274b4b9597b4a2a", Danbooru1Engine.PasswordHash("konachan.net", "hunter2"));
        Assert.Equal("1fc0adf8544b5cb927ac1895f8e67c042e6e8dba", Danbooru1Engine.PasswordHash("yande.re", "hunter2"));
    }

    [Fact]
    public void Credentials_go_in_query_only_when_complete()
    {
        var anon = _e.BuildPostsUrl(Fx.Ctx(EngineType.Danbooru1, "https://yande.re"), new PreparedQuery("x", "x", false), 1).AbsoluteUri;
        Assert.DoesNotContain("login=", anon);
        var ctx = new ServerContext(new Server { Id = 1, Url = "https://yande.re", Engine = EngineType.Danbooru1, Username = "me" },
            new ServerCredentials(null, "hunter2"), HostQuirks.None);
        var authed = _e.BuildPostsUrl(ctx, new PreparedQuery("x", "x", false), 1).AbsoluteUri;
        Assert.Contains("login=me&password_hash=1fc0adf8544b5cb927ac1895f8e67c042e6e8dba", authed);
        Assert.DoesNotContain("hunter2", authed);
    }

    [Fact]
    public async Task Parses_tag_autocomplete()
    {
        await using var s = Fx.Open("moebooru_tags.json");
        var tags = await _e.ParseAutocompleteAsync(s, Fx.Ctx(EngineType.Danbooru1, "https://konachan.net"), default);
        Assert.Equal("hatsune_miku", tags[0].Name);
        Assert.Equal(TagCategory.Character, tags[0].Category);
    }
}

public class PhilomenaTests
{
    private readonly PhilomenaEngine _e = new();

    [Fact]
    public async Task Parses_derpibooru_images()
    {
        var ctx = Fx.Ctx(EngineType.Philomena, "https://derpibooru.org");
        await using var s = Fx.Open("philomena_images.json");
        var page = await _e.ParsePostsAsync(s, ctx, default);
        Assert.Equal(3, page.RawCount);
        var p = page.Posts[0];
        Assert.StartsWith("https://derpicdn.net/img/view/", p.File.Url);
        Assert.Contains("/thumb.", p.Preview!.Url);
        Assert.Equal(128, p.Md5!.Length); // sha512
        Assert.Equal("safe", p.Rating);
        Assert.Contains(p.Tags, t => t.Name.Contains(' ') || t.Name.Contains(':'));
        Assert.Contains(p.Tags, t => t.Name.StartsWith("artist:") && t.Category == TagCategory.Artist);
        Assert.Equal($"https://derpibooru.org/images/{p.RemoteId}", p.PostUrl);
    }

    [Fact]
    public async Task Webm_gets_gif_thumbnail_and_no_sample()
    {
        await using var s = Fx.Open("philomena_webm.json");
        var p = (await _e.ParsePostsAsync(s, Fx.Ctx(EngineType.Philomena, "https://derpibooru.org"), default)).Posts.Single();
        Assert.Equal(MediaKind.Video, p.Kind);
        Assert.EndsWith(".gif", p.Preview!.Url);
        Assert.Null(p.Sample);
    }

    [Fact]
    public void Comma_separated_query_rules()
    {
        var on = Fx.Ctx(EngineType.Philomena, "https://derpibooru.org", rating: true);
        Assert.Equal("twilight sparkle, safe", QueryPreprocessor.Prepare(_e, on, "twilight sparkle, *").Text);
        Assert.Equal("twilight sparkle, explicit", QueryPreprocessor.Prepare(_e, on, "twilight sparkle, explicit").Text);
        Assert.Equal("*", QueryPreprocessor.Prepare(_e, Fx.Ctx(EngineType.Philomena, "https://derpibooru.org"), "  ").Text);
        Assert.Equal("width.gte:1920", QueryPreprocessor.Prepare(_e, Fx.Ctx(EngineType.Philomena, "https://derpibooru.org"), "#fullhd").Text);
        Assert.Contains("q=twilight%20sparkle%2C%20safe", _e.BuildPostsUrl(on, QueryPreprocessor.Prepare(_e, on, "twilight sparkle"), 1).AbsoluteUri);
    }

    [Fact]
    public async Task Parses_tag_search()
    {
        await using var s = Fx.Open("philomena_tags.json");
        var tags = await _e.ParseAutocompleteAsync(s, Fx.Ctx(EngineType.Philomena, "https://derpibooru.org"), default);
        Assert.Equal("twilight sparkle", tags[0].Name);
        Assert.Equal(TagCategory.Character, tags[0].Category);
        Assert.Contains(tags, t => t.Category == TagCategory.Copyright);
    }

    [Fact]
    public async Task Booru_on_rails_uses_v3_posts()
    {
        var bor = new BooruOnRailsEngine();
        var ctx = Fx.Ctx(EngineType.BooruOnRails, "https://twibooru.org");
        Assert.Contains("/api/v3/search/posts?", bor.BuildPostsUrl(ctx, new PreparedQuery("", "*", false), 1).AbsoluteUri);
        await using var s = Fx.Open("booruonrails_posts.json");
        var page = await bor.ParsePostsAsync(s, ctx, default);
        Assert.Equal(3, page.RawCount);
        Assert.All(page.Posts, p => Assert.StartsWith("https://cdn.twibooru.org/", p.File.Url));
        Assert.Equal($"https://twibooru.org/{page.Posts[0].RemoteId}", page.Posts[0].PostUrl);
    }
}

public class KemonoTests
{
    [Fact]
    public async Task Fans_out_files_and_builds_cdn_urls()
    {
        var e = new KemonoEngine();
        var ctx = Fx.Ctx(EngineType.Kemono, "https://kemono.cr");
        await using var s = Fx.Open("kemono_posts.json");
        var page = await e.ParsePostsAsync(s, ctx, default);
        Assert.Equal(3, page.RawCount);
        var first = page.Posts.Where(p => p.PostUrl == "https://kemono.cr/patreon/user/90822862/post/147648418").ToList();
        Assert.Equal(2, first.Count); // mp4 file + png attachment
        Assert.Equal(["147648418", "147648418-1"], first.Select(p => p.RemoteId));
        var video = first[0];
        Assert.Equal(MediaKind.Video, video.Kind);
        Assert.Null(video.Preview);
        Assert.StartsWith("https://kemono.cr/data/", video.File.Url);
        Assert.StartsWith("https://img.kemono.cr/thumbnail/data/", first[1].Preview!.Url);
        Assert.Equal("Sample post 1", video.Title);
        Assert.Contains(video.Tags, t => t.Category == TagCategory.Artist && t.Name == "90822862");
        Assert.Equal("https://kemono.cr/api/v1/posts?o=100&q=cats",
            e.BuildPostsUrl(ctx, new PreparedQuery("cats", "cats", false), 2).AbsoluteUri);
    }
}

public class HydrusTests
{
    private sealed class FakeHydrus : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;
            var body = path switch
            {
                "/get_files/search_files" => "{\"file_ids\":[3,2,1],\"version\":80}",
                "/get_files/file_metadata" => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hydrus_metadata.json")),
                "/verify_access_key" => "{\"human_description\":\"API Permissions (Kura): search and fetch files\"}",
                _ => "{}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static (HydrusEngine, EngineTransport, FakeHydrus, ServerContext) Setup()
    {
        var e = new HydrusEngine();
        var fake = new FakeHydrus();
        var ctx = Fx.Ctx(EngineType.Hydrus, "http://127.0.0.1:45869", key: "abc123");
        return (e, new EngineTransport(new KuraHttp(fake), e), fake, ctx);
    }

    [Fact]
    public async Task Two_stage_fetch_keeps_search_order_and_maps_metadata()
    {
        var (e, transport, fake, ctx) = Setup();
        var q = QueryPreprocessor.Prepare(e, ctx, "creator:someone, blue sky");
        var page = await e.FetchPageAsync(transport, ctx, q, 0, default);

        Assert.Equal(3, page.RawCount);
        Assert.Equal(["2", "1"], page.Posts.Select(p => p.RemoteId)); // stage-1 order; the .psd is dropped
        var search = Uri.UnescapeDataString(fake.Requests[0].RequestUri!.Query);
        Assert.Contains("[\"creator:someone\",\"blue sky\"]", search);
        Assert.Equal("abc123", fake.Requests[0].Headers.GetValues("Hydrus-Client-API-Access-Key").Single());

        var img = page.Posts[1];
        Assert.Contains("file_id=1", img.File.Url);
        Assert.Equal("png", img.File.Extension);
        Assert.Equal("https://danbooru.donmai.us/posts/123", img.PostUrl);
        Assert.StartsWith("https://gelbooru.com/", img.Source);
        Assert.True(img.IsFavorite);
        Assert.Contains(img.Tags, t => t.Name == "creator:someone" && t.Category == TagCategory.Artist);
        Assert.Contains(img.Tags, t => t.Name == "series:wonderland" && t.Category == TagCategory.Copyright);
        Assert.DoesNotContain(img.Tags, t => t.Name == "blue sky" && t.Category != TagCategory.General);
        Assert.Equal(MediaKind.Video, page.Posts[0].Kind);
        Assert.False(page.Posts[0].IsFavorite);

        // Page 2 reuses the cached id list and is past the end: no new search request.
        var before = fake.Requests.Count;
        var next = await e.FetchPageAsync(transport, ctx, q, 1, default);
        Assert.Equal(0, next.RawCount);
        Assert.Equal(before, fake.Requests.Count);
    }

    [Fact]
    public async Task Empty_query_is_system_everything_and_ping_reports_permissions()
    {
        var (e, transport, _, ctx) = Setup();
        Assert.Equal("system:everything", QueryPreprocessor.Prepare(e, ctx, "").Text);
        Assert.Contains("search and fetch files", await e.PingAsync(transport, ctx, default));
    }
}

public class ShimmieTests
{
    private readonly ShimmieEngine _e = new();

    [Fact]
    public async Task Parses_list_page_and_derives_file_urls()
    {
        var ctx = Fx.Ctx(EngineType.Shimmie, "https://shimmie.shishnet.org");
        await using var s = Fx.Open("shimmie_list.html");
        var page = await _e.ParsePostsAsync(s, ctx, default);
        Assert.True(page.Posts.Count >= 20);
        var p = page.Posts.Single(x => x.RemoteId == "6605");
        Assert.Equal("https://shimmie.shishnet.org/_images/2edd5890d4916ee60579667032df86b0/6605.jpg", p.File.Url);
        Assert.Equal((350, 230), (p.File.Width, p.File.Height));
        Assert.Equal("https://shimmie.shishnet.org/_thumbs/2edd5890d4916ee60579667032df86b0/thumb.jpg", p.Preview!.Url);
        Assert.Equal("2edd5890d4916ee60579667032df86b0", p.Md5);
        Assert.Equal("https://shimmie.shishnet.org/post/view/6605", p.PostUrl);
        Assert.Contains(page.Posts, x => x.Kind == MediaKind.Gif);
    }

    [Fact]
    public void Empty_query_has_no_double_slash()
    {
        var ctx = Fx.Ctx(EngineType.Shimmie, "https://shimmie.shishnet.org");
        Assert.Equal("https://shimmie.shishnet.org/post/list/3", _e.BuildPostsUrl(ctx, new PreparedQuery("", "", false), 3).AbsoluteUri);
        Assert.Equal("https://shimmie.shishnet.org/post/list/brown%20hair/1", _e.BuildPostsUrl(ctx, new PreparedQuery("", "brown hair", false), 1).AbsoluteUri);
    }

    [Fact]
    public async Task Autocomplete_accepts_both_shapes()
    {
        var ctx = Fx.Ctx(EngineType.Shimmie, "https://shimmie.shishnet.org");
        var newer = await _e.ParseAutocompleteAsync(Fx.Text("{\"tagme\":{\"newtag\":null,\"count\":229},\"tama\":{\"count\":14}}"), ctx, default);
        Assert.Equal(("tagme", 229L), (newer[0].Name, newer[0].PostCount));
        var older = await _e.ParseAutocompleteAsync(Fx.Text("{\"tama\":14,\"tagme\":229}"), ctx, default);
        Assert.Equal("tagme", older[0].Name);
    }

    [Fact]
    public void Age_gate_cookie_is_set()
    {
        var cookies = new CookieContainer();
        _e.ConfigureCookies(cookies, Fx.Ctx(EngineType.Shimmie, "https://shimmie.shishnet.org"));
        Assert.Equal("true", cookies.GetCookies(new Uri("https://shimmie.shishnet.org/post/list/1"))["agreed"]?.Value);
    }
}

public class Gelbooru011Tests
{
    private readonly Gelbooru011Engine _e = new();

    [Fact]
    public async Task Parses_thumbs_and_guesses_file_urls()
    {
        var ctx = Fx.Ctx(EngineType.Gelbooru011, "https://booru.example.org");
        await using var s = Fx.Open("gelbooru011_list.html");
        var page = await _e.ParsePostsAsync(s, ctx, default);
        Assert.Equal(3, page.Posts.Count);

        var a = page.Posts[0];
        Assert.Equal("101", a.RemoteId);
        Assert.Equal("https://img.example.org/images/12/abc123.jpg", a.File.Url);
        Assert.Equal(5, a.Score);
        Assert.Equal("safe", a.Rating);
        Assert.Equal(["blue_sky", "cloud"], a.Tags.Select(t => t.Name));

        var video = page.Posts[1];
        Assert.Equal(MediaKind.Video, video.Kind);
        Assert.Equal("https://video.example.org/images/13/def456.webm", video.File.Url);
        Assert.Equal(-1, video.Score);

        var gif = page.Posts[2];
        Assert.Equal("103", gif.RemoteId);           // id from href when the anchor has no id
        Assert.Equal(MediaKind.Gif, gif.Kind);       // "video_game" is not a video
        Assert.StartsWith("https://booru.example.org/images/14/", gif.File.Url);
    }

    [Fact]
    public void Paging_and_rule34us_layout()
    {
        var q = new PreparedQuery("cat", "cat", false);
        Assert.Equal("https://booru.example.org/index.php?page=post&s=list&pid=40&tags=cat",
            _e.BuildPostsUrl(Fx.Ctx(EngineType.Gelbooru011, "https://booru.example.org"), q, 2).AbsoluteUri);
        Assert.Equal("https://rule34.us/index.php?r=posts/index&q=cat&page=2",
            _e.BuildPostsUrl(Fx.Ctx(EngineType.Gelbooru011, "https://rule34.us"), q, 2).AbsoluteUri);
    }
}

public class SeparatorTests
{
    [Fact]
    public void Comma_tokens_keep_spaces()
    {
        var (token, start, len) = AutocompleteService.CurrentToken("safe, twilight spa", 18, ", ");
        Assert.Equal("twilight spa", token);
        Assert.Equal(6, start);
        Assert.Equal(12, len);
        Assert.Equal(["a b", "c"], QueryPreprocessor.Tokenize(" a b , c ,", ", "));
    }

    [Fact]
    public void Registry_has_all_original_engines()
    {
        var r = EngineRegistry.CreateDefault();
        foreach (var t in new[] { EngineType.Gelbooru, EngineType.Danbooru2, EngineType.Danbooru1, EngineType.Gelbooru011, EngineType.Philomena,
                     EngineType.E621, EngineType.Hydrus, EngineType.Shimmie, EngineType.Kemono, EngineType.BooruOnRails })
            Assert.True(r.IsSupported(t), t.ToString());
    }
}
