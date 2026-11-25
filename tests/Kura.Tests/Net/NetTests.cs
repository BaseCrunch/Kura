using System.Diagnostics;
using System.Net;
using Kura.Core.Net;

namespace Kura.Tests.Net;

internal sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps) : HttpMessageHandler
{
    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var step = steps[Math.Min(Calls, steps.Length - 1)];
        Calls++;
        return Task.FromResult(step(request));
    }
}

public class RetryHandlerTests
{
    private static readonly List<TimeSpan> NoDelays = [];

    private static (HttpClient, ScriptedHandler, List<TimeSpan>) Build(params Func<HttpRequestMessage, HttpResponseMessage>[] steps)
    {
        var delays = new List<TimeSpan>();
        var inner = new ScriptedHandler(steps);
        var retry = new RetryHandler(maxRetries: 3, baseDelay: TimeSpan.FromMilliseconds(10),
            delay: (d, _) => { delays.Add(d); return Task.CompletedTask; }) { InnerHandler = inner };
        return (new HttpClient(retry), inner, delays);
    }

    [Fact]
    public async Task Honours_retry_after_on_429()
    {
        var (client, inner, delays) = Build(
            _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromSeconds(2)) } },
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        var resp = await client.GetAsync("https://example.test/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, inner.Calls);
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(delays));
    }

    [Fact]
    public async Task Backs_off_on_5xx_then_gives_up()
    {
        var (client, inner, delays) = Build(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var resp = await client.GetAsync("https://example.test/");
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.Equal(4, inner.Calls);
        Assert.Equal(3, delays.Count);
        Assert.True(delays[2] > delays[0]);
    }

    [Fact]
    public async Task Does_not_retry_404_or_post()
    {
        var (client, inner, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        await client.GetAsync("https://example.test/");
        Assert.Equal(1, inner.Calls);

        var (client2, inner2, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await client2.PostAsync("https://example.test/", new StringContent("{}"));
        Assert.Equal(1, inner2.Calls);
        _ = NoDelays;
    }

    [Fact]
    public async Task Surfaces_excessive_retry_after_instead_of_waiting()
    {
        var (client, inner, delays) = Build(
            _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromMinutes(10)) } });
        var resp = await client.GetAsync("https://example.test/");
        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
        Assert.Equal(1, inner.Calls);
        Assert.Empty(delays);
    }
}

public class HostRateLimiterTests
{
    [Fact]
    public async Task Limits_per_host_independently()
    {
        await using var limiter = new HostRateLimiter(defaultRequestsPerSecond: 1000);
        limiter.Configure("slow.test", 2);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 4; i++) await limiter.AcquireAsync("slow.test", CancellationToken.None);
        var slow = sw.Elapsed;
        sw.Restart();
        for (var i = 0; i < 50; i++) await limiter.AcquireAsync("fast.test", CancellationToken.None);
        var fast = sw.Elapsed;

        // 2 tokens immediately, the next 2 after one replenishment period (~1s).
        Assert.True(slow >= TimeSpan.FromMilliseconds(800), $"slow host took {slow}");
        Assert.True(fast < TimeSpan.FromMilliseconds(500), $"fast host took {fast}");
    }

    [Fact]
    public void Sub_one_rate_is_supported()
    {
        var limiter = new HostRateLimiter();
        limiter.Configure("e621.test", 0.5);
        Assert.Equal(0.5, limiter.RateFor("e621.test"));
        Assert.Equal(20, limiter.RateFor("other.test"));
    }
}

public class FailureMappingTests
{
    [Theory]
    [InlineData(403, FailureKind.Forbidden)]
    [InlineData(421, FailureKind.Throttled)]
    [InlineData(429, FailureKind.Throttled)]
    [InlineData(503, FailureKind.ServiceUnavailable)]
    [InlineData(502, FailureKind.ServerError)]
    [InlineData(401, FailureKind.InvalidCredentials)]
    public void Classifies_status_codes(int code, FailureKind expected) =>
        Assert.Equal(expected, BooruRequestException.Classify((HttpStatusCode)code));
}
