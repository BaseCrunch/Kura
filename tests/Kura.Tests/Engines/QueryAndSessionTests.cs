using Kura.Core.Engines;
using Kura.Core.Models;
using Kura.Core.Search;

namespace Kura.Tests.Engines;

public class QueryPreprocessorTests
{
    [Fact]
    public void Appends_rating_term_only_when_enabled_and_absent()
    {
        var e = new Danbooru2Engine();
        var on = Fx.Ctx(EngineType.Danbooru2, "https://my.booru", rating: true);
        Assert.Equal("cat rating:safe", QueryPreprocessor.Prepare(e, on, "cat").Text);
        Assert.Equal("cat -rating:e", QueryPreprocessor.Prepare(e, on, "cat -rating:e").Text);
        Assert.Equal("cat", QueryPreprocessor.Prepare(e, Fx.Ctx(EngineType.Danbooru2, "https://my.booru"), "cat").Text);
        Assert.Equal("rating:general",
            QueryPreprocessor.Prepare(new GelbooruEngine(), Fx.Ctx(EngineType.Gelbooru, "https://gelbooru.com", rating: true), "").Text);
    }

    [Fact]
    public void Macro_extra_tags_wildcards_and_force_original()
    {
        var e = new GelbooruEngine();
        var ctx = Fx.Ctx(EngineType.Gelbooru, "https://booru.example");
        var q = QueryPreprocessor.Prepare(e, ctx, "  *  #fullhd  ", extraTags: "sort:score");
        Assert.Equal("width:>=1920 sort:score", q.Text);
        Assert.True(q.ForceOriginal);
        Assert.False(QueryPreprocessor.Prepare(e, ctx, "cat").ForceOriginal);
        // Clearing the extra tags really clears them (Appendix A #4).
        Assert.Equal("cat", QueryPreprocessor.Prepare(e, ctx, "cat", extraTags: "").Text);
    }
}

public class QuirkTests
{
    [Fact]
    public void Suffix_match_and_most_specific_wins()
    {
        Assert.Equal("rating:g", Fx.Quirks.Resolve("safebooru.donmai.us").RatingTerm);
        Assert.Null(Fx.Quirks.Resolve("notdonmai.us").RatingTerm);
        Assert.True(Fx.Quirks.Resolve("api.rule34.xxx").CacheBuster);
    }

    [Fact]
    public void Thumbnail_fallbacks()
    {
        var q = Fx.Quirks.Resolve("gelbooru.com");
        Assert.Equal(["https://img3.gelbooru.com/thumbnails/aa/bb/x.jpg"],
            q.FallbackUrls("https://img2.gelbooru.com/thumbnails/aa/bb/x.jpg").ToList());
        Assert.Equal(["https://img3.gelbooru.com/thumbnails/aa/x.jpg"],
            q.FallbackUrls("https://thumbs.gelbooru.com/aa/x.jpg").ToList());
    }
}

public class SearchSessionTests
{
    private static PostPage Page(int count, int start = 0) => new(
        Enumerable.Range(start, count).Select(i => new Post
        {
            ServerId = 1, RemoteId = i.ToString(), File = new PostImage($"https://x/{i}.jpg", 10, 10),
        }).ToList(), count);

    [Fact]
    public async Task Pages_until_empty_and_tracks_display_pages()
    {
        var calls = new List<int>();
        var s = new SearchSession((p, _) => { calls.Add(p); return Task.FromResult(p < 2 ? Page(3, p * 3) : Page(0)); }, startPage: 0);
        Assert.Equal(3, (await s.LoadNextAsync(default))!.Posts.Count);
        Assert.Equal(3, (await s.LoadNextAsync(default))!.Posts.Count);
        Assert.Empty((await s.LoadNextAsync(default))!.Posts);
        Assert.True(s.IsLastPage);
        Assert.Null(await s.LoadNextAsync(default));
        Assert.Equal([0, 1, 2], calls);
        Assert.Equal(1, s.DisplayPageOf(0, firstPage: 0));
        Assert.Equal(2, s.DisplayPageOf(4, firstPage: 0));
    }

    [Fact]
    public async Task Prefetch_is_consumed_instead_of_refetching()
    {
        var calls = 0;
        var s = new SearchSession((p, _) => { calls++; return Task.FromResult(Page(2)); }, startPage: 1);
        await s.LoadNextAsync(default);
        s.Prefetch();
        s.Prefetch(); // idempotent
        await s.LoadNextAsync(default);
        Assert.Equal(2, calls);
        Assert.Equal(3, s.NextPage);
    }

    [Fact]
    public async Task Failed_prefetch_falls_back_to_a_fresh_fetch()
    {
        var calls = 0;
        var s = new SearchSession((p, _) =>
        {
            calls++;
            return calls == 2 ? Task.FromException<PostPage>(new IOException("boom")) : Task.FromResult(Page(1));
        }, startPage: 1);
        await s.LoadNextAsync(default);
        s.Prefetch();
        var page = await s.LoadNextAsync(default);
        Assert.Single(page!.Posts);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Jump_to_page_starts_there()
    {
        var calls = new List<int>();
        var s = new SearchSession((p, _) => { calls.Add(p); return Task.FromResult(Page(1)); }, startPage: 5);
        await s.LoadNextAsync(default);
        Assert.Equal([5], calls);
        Assert.Equal(5, s.DisplayPageOf(0, firstPage: 1));
    }
}

public class SmallHelpersTests
{
    [Theory]
    [InlineData("blue sky", 2, "blue", 0)]
    [InlineData("blue sky", 6, "sky", 5)]
    [InlineData("blue -sk", 8, "-sk", 5)]
    [InlineData("", 0, "", 0)]
    public void Current_token(string text, int caret, string token, int start)
    {
        var (t, s, _) = AutocompleteService.CurrentToken(text, caret);
        Assert.Equal(token, t);
        Assert.Equal(start, s);
    }

    [Fact]
    public void Operator_split() => Assert.Equal(("-", "cat"), AutocompleteService.SplitOperator("-cat"));

    [Theory]
    [InlineData("https://x/a.JPG", "jpg")]
    [InlineData("https://x/a.php?id=1&ext=webm", "webm")]
    [InlineData("https://x/a.png?t=123", "png")]
    [InlineData("https://x/noext", "")]
    public void Extension_detection(string url, string ext) => Assert.Equal(ext, MediaTypes.ExtensionOf(url));

    [Fact]
    public void Pixiv_sources_are_rewritten()
    {
        var links = SourceLinks.Split("https://i.pximg.net/img-original/img/2026/01/01/00/00/00/12345678_p0.png | https://x.com/a");
        Assert.Equal(["https://www.pixiv.net/en/artworks/12345678", "https://x.com/a"], links);
    }

    [Fact]
    public void Visible_image_prefers_sample_except_animated_or_forced()
    {
        var file = new PostImage("https://x/a.png", 4000, 3000);
        var sample = new PostImage("https://x/s.jpg", 850, 637);
        var p = new Post { ServerId = 1, RemoteId = "1", File = file, Sample = sample };
        Assert.Same(sample, p.VisibleImage(false));
        Assert.Same(file, p.VisibleImage(true));
        var v = new Post { ServerId = 1, RemoteId = "2", File = new PostImage("https://x/v.webm", 1, 1), Sample = sample };
        Assert.Equal("webm", v.VisibleImage(false).Extension);
    }
}
