using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Kura.Core.Net;

namespace Kura.Core.Engines;

/// <summary>Shared bits for engines that scrape HTML list pages (Shimmie 2, Gelbooru 0.1.11).</summary>
public abstract class HtmlEngineBase : BooruEngineBase
{
    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// HTML boors answer an out-of-range page with 404 ("No posts found"), which means "no more results" here
    /// (SPEC 1.6). Other failures still surface as errors.
    /// </summary>
    public override async Task<PostPage> FetchPageAsync(EngineTransport transport, ServerContext ctx, PreparedQuery query, int page,
        CancellationToken ct)
    {
        try
        {
            return await base.FetchPageAsync(transport, ctx, query, page, ct).ConfigureAwait(false);
        }
        catch (BooruRequestException ex) when (ex.Kind == FailureKind.NotFound)
        {
            return PostPage.Empty;
        }
    }

    protected static Task<IHtmlDocument> ParseHtmlAsync(Stream body, CancellationToken ct) => Parser.ParseDocumentAsync(body, ct);

    /// <summary>
    /// Resolves a link from the page. Root-relative paths resolve against the site origin, not the configured URL,
    /// because a subpath install already includes it (SPEC 1.7 Shimmie).
    /// </summary>
    protected static string? Absolute(string? url, ServerContext ctx)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        if (url.StartsWith("//", StringComparison.Ordinal)) return "https:" + url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs) && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps)) return url;
        var origin = new Uri(ctx.BaseUrl);
        return new Uri(new Uri(origin.GetLeftPart(UriPartial.Authority) + "/"), url).ToString();
    }
}
