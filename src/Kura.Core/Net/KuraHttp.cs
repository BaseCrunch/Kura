using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;

namespace Kura.Core.Net;

/// <summary>
/// The single HTTP stack for API, image and download traffic (SPEC 4.1 N8): one pooled
/// <see cref="SocketsHttpHandler"/>, one RFC 6265 <see cref="CookieContainer"/> (fixes Appendix A #11),
/// then per-host rate limiting and retries.
/// </summary>
public sealed class KuraHttp : IDisposable
{
    public static readonly string ProductVersion = typeof(KuraHttp).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    public static readonly string DefaultUserAgent = $"Kura/{ProductVersion} (Windows desktop booru client)";

    public KuraHttp(HostRateLimiter? limiter = null, IWebProxy? proxy = null)
    {
        Limiter = limiter ?? new HostRateLimiter();
        Cookies = new CookieContainer();
        var sockets = new SocketsHttpHandler
        {
            CookieContainer = Cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            MaxConnectionsPerServer = 8,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            EnableMultipleHttp2Connections = true,
            Proxy = proxy,
            UseProxy = proxy is not null,
        };
        var pipeline = new RetryHandler { InnerHandler = new RateLimitHandler(Limiter) { InnerHandler = sockets } };
        Client = new HttpClient(pipeline, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan, // per-call timeouts via CancellationToken; big downloads must not be cut off
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    /// <summary>Test hook: sends through <paramref name="handler"/> with no rate limiting or retries.</summary>
    internal KuraHttp(HttpMessageHandler handler)
    {
        Limiter = new HostRateLimiter();
        Cookies = new CookieContainer();
        Client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public HttpClient Client { get; }
    public CookieContainer Cookies { get; }
    public HostRateLimiter Limiter { get; }

    public static HttpRequestMessage Get(Uri uri, string? userAgent = null, string? referer = null, string? accept = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.TryAddWithoutValidation("User-Agent", userAgent ?? DefaultUserAgent);
        if (referer is not null && Uri.TryCreate(referer, UriKind.Absolute, out var r)) req.Headers.Referrer = r;
        if (accept is not null) req.Headers.Accept.ParseAdd(accept);
        return req;
    }

    public static void SetBasicAuth(HttpRequestMessage req, string user, string secret) =>
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{secret}")));

    /// <summary>Sends and maps transport/HTTP failures to <see cref="BooruRequestException"/>.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        HttpResponseMessage resp;
        try
        {
            resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new BooruRequestException(FailureKind.Timeout, BooruRequestException.Describe(FailureKind.Timeout));
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            throw new BooruRequestException(FailureKind.InvalidCertificate, BooruRequestException.Describe(FailureKind.InvalidCertificate), inner: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new BooruRequestException(FailureKind.Connection, $"{BooruRequestException.Describe(FailureKind.Connection)} {ex.Message}", inner: ex);
        }

        if (resp.IsSuccessStatusCode) return resp;
        var code = resp.StatusCode;
        var kind = BooruRequestException.Classify(code);
        string detail;
        try { detail = await ReadSnippetAsync(resp, ct).ConfigureAwait(false); }
        finally { resp.Dispose(); }
        throw new BooruRequestException(kind, $"{BooruRequestException.Describe(kind)} (HTTP {(int)code}){detail}", code);
    }

    private static async Task<string> ReadSnippetAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            text = text.Trim();
            if (text.Contains("Just a moment", StringComparison.Ordinal) || text.Contains("cf-chl", StringComparison.Ordinal))
                return " The site is behind a Cloudflare browser check, which Kura can't pass yet.";
            if (text.Length == 0 || text.StartsWith('<')) return ""; // skip HTML error pages
            return " " + (text.Length > 200 ? text[..200] + "…" : text);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return "";
        }
    }

    public void Dispose() => Client.Dispose();
}
