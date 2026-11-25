using System.Net;

namespace Kura.Core.Net;

/// <summary>
/// Retries idempotent requests: honours <c>Retry-After</c> on 429/503 and backs off exponentially with jitter
/// on other 5xx and transient connection failures (SPEC 4.1 N4).
/// </summary>
public sealed class RetryHandler : DelegatingHandler
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public RetryHandler(int maxRetries = 3, TimeSpan? baseDelay = null, TimeSpan? maxRetryAfter = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        MaxRetries = maxRetries;
        BaseDelay = baseDelay ?? TimeSpan.FromMilliseconds(500);
        MaxRetryAfter = maxRetryAfter ?? TimeSpan.FromSeconds(60);
        _delay = delay ?? Task.Delay;
    }

    public int MaxRetries { get; }
    public TimeSpan BaseDelay { get; }
    public TimeSpan MaxRetryAfter { get; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var idempotent = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (idempotent && attempt < MaxRetries && !ct.IsCancellationRequested)
            {
                await _delay(Backoff(attempt), ct).ConfigureAwait(false);
                continue;
            }

            if (!idempotent || attempt >= MaxRetries || !IsRetryable(response.StatusCode)) return response;

            var wait = RetryAfter(response) ?? Backoff(attempt);
            if (wait > MaxRetryAfter) return response; // server asked for too long; surface it instead of hanging
            response.Dispose();
            await _delay(wait, ct).ConfigureAwait(false);
        }
    }

    internal static bool IsRetryable(HttpStatusCode code) =>
        code is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout || (int)code == 421 /* Danbooru UserThrottled */;

    private TimeSpan Backoff(int attempt)
    {
        var exp = BaseDelay.TotalMilliseconds * Math.Pow(2, attempt);
        var jitter = Random.Shared.NextDouble() * BaseDelay.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(exp + jitter);
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage r)
    {
        var ra = r.Headers.RetryAfter;
        if (ra is null) return null;
        if (ra.Delta is { } d) return d;
        if (ra.Date is { } date)
        {
            var delta = date - DateTimeOffset.UtcNow;
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }
        return null;
    }
}
