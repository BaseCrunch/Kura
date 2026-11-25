using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace Kura.Core.Net;

/// <summary>
/// One token bucket per host (SPEC 4.1 N4). API hosts get their engine/quirk rate; unknown hosts (image CDNs)
/// get a generous default so thumbnails are not starved.
/// </summary>
public sealed class HostRateLimiter : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, double> _rates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TokenBucketRateLimiter> _buckets = new(StringComparer.OrdinalIgnoreCase);

    public HostRateLimiter(double defaultRequestsPerSecond = 20) => DefaultRequestsPerSecond = defaultRequestsPerSecond;

    public double DefaultRequestsPerSecond { get; }

    /// <summary>Sets the rate for a host. Takes effect for the next bucket created for that host.</summary>
    public void Configure(string host, double requestsPerSecond)
    {
        if (requestsPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(requestsPerSecond));
        _rates[host] = requestsPerSecond;
        if (_buckets.TryRemove(host, out var old)) old.Dispose();
    }

    public double RateFor(string host) => _rates.TryGetValue(host, out var r) ? r : DefaultRequestsPerSecond;

    public async ValueTask AcquireAsync(string host, CancellationToken ct)
    {
        var bucket = _buckets.GetOrAdd(host, h => Create(RateFor(h)));
        using var lease = await bucket.AcquireAsync(1, ct).ConfigureAwait(false);
        if (!lease.IsAcquired) throw new HttpRequestException($"Rate limiter queue for {host} is full.");
    }

    private static TokenBucketRateLimiter Create(double rps)
    {
        // Sub-1 rps (e.g. 0.5) becomes one token every 1/rps seconds.
        var period = rps >= 1 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(1 / rps);
        var tokens = rps >= 1 ? (int)Math.Round(rps) : 1;
        return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = tokens,
            TokensPerPeriod = tokens,
            ReplenishmentPeriod = period,
            QueueLimit = 10_000,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var b in _buckets.Values) await b.DisposeAsync().ConfigureAwait(false);
        _buckets.Clear();
    }
}

/// <summary>Waits for a token from the request host's bucket before sending.</summary>
public sealed class RateLimitHandler(HostRateLimiter limiter) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri is { } uri) await limiter.AcquireAsync(uri.Host, ct).ConfigureAwait(false);
        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }
}
