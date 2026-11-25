using System.Collections.Concurrent;
using System.Windows.Media.Imaging;
using Kura.Core.Engines;
using Kura.Core.Net;
using Kura.Core.Util;

namespace Kura.Media;

/// <summary>
/// The image pipeline: RAM LRU of decoded bitmaps, then the disk cache, then the network through the shared
/// <see cref="KuraHttp"/> stack (SPEC 4.1 N8, 4.3 G3). Concurrent requests for one URL share a single download.
/// </summary>
public sealed class ImageLoader
{
    private readonly KuraHttp _http;
    private readonly Func<string, HostQuirks> _quirks;
    private readonly LruCache<string, BitmapSource> _ram;
    private readonly ConcurrentDictionary<string, Task<string>> _inflight = new();
    private readonly ConcurrentDictionary<string, string> _thumbKeyByUrl = new();
    private readonly ConcurrentDictionary<string, UrlRewrite> _workingMirror = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _decodeGate = new(Math.Max(2, Environment.ProcessorCount - 1));

    public ImageLoader(KuraHttp http, DiskCache disk, long ramBudgetBytes, Func<string, HostQuirks> quirks)
    {
        _http = http;
        Disk = disk;
        _quirks = quirks;
        _ram = new LruCache<string, BitmapSource>(ramBudgetBytes, b => (long)b.PixelWidth * b.PixelHeight * 4);
    }

    public DiskCache Disk { get; }
    public long RamBytes => _ram.TotalCost;

    /// <summary>Rounds decode heights up to 64px buckets so nearby thumbnail sizes share cache entries.</summary>
    public static int Bucket(int px) => Math.Max(64, (px + 63) / 64 * 64);

    public BitmapSource? PeekThumbnail(string url) =>
        _thumbKeyByUrl.TryGetValue(url, out var key) && _ram.TryGet(key, out var b) ? b : null;

    public async Task<BitmapSource?> LoadThumbnailAsync(string url, string? referer, int decodeHeight, CancellationToken ct)
    {
        var h = Bucket(decodeHeight);
        var key = $"t|{h}|{url}";
        if (_ram.TryGet(key, out var hit)) return hit;

        var path = await GetFileAsync(url, referer, null, ct).ConfigureAwait(false);
        await _decodeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            var bmp = await Task.Run(() => SkiaDecoder.DecodeThumbnail(path, h), ct).ConfigureAwait(false);
            _ram.Set(key, bmp);
            _thumbKeyByUrl[url] = key;
            return bmp;
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    /// <summary>Full decode for the viewer. Still images are cached in RAM; animations are not (they hold a codec).</summary>
    public async Task<DecodedImage> LoadFullAsync(string url, string? referer, IProgress<double>? progress, CancellationToken ct)
    {
        var key = $"f|{url}";
        if (_ram.TryGet(key, out var hit)) return new DecodedImage(hit, null);
        var path = await GetFileAsync(url, referer, progress, ct).ConfigureAwait(false);
        var decoded = await Task.Run(() => SkiaDecoder.DecodeFull(path, ct), ct).ConfigureAwait(false);
        if (decoded.Still is not null) _ram.Set(key, decoded.Still);
        return decoded;
    }

    /// <summary>Downloads into the disk cache without decoding (viewer preloading, SPEC 4.4 V2).</summary>
    public Task PrefetchAsync(string url, string? referer, CancellationToken ct) => GetFileAsync(url, referer, null, ct);

    /// <summary>Returns a local path for <paramref name="url"/>, downloading it (once) if needed.</summary>
    public Task<string> GetFileAsync(string url, string? referer, IProgress<double>? progress, CancellationToken ct)
    {
        if (Disk.TryGet(url, out var path))
        {
            progress?.Report(1);
            return Task.FromResult(path);
        }
        var task = _inflight.GetOrAdd(url, u => DownloadWithFallbackAsync(u, referer, progress));
        return task.WaitAsync(ct);
    }

    private async Task<string> DownloadWithFallbackAsync(string url, string? referer, IProgress<double>? progress)
    {
        try
        {
            var host = new Uri(url).Host;
            var candidates = new List<(string Url, UrlRewrite? Via)>();
            // Try the mirror that worked last time for this host first (SPEC 4.3 G6).
            if (_workingMirror.TryGetValue(host, out var remembered))
                candidates.AddRange(Rewrite(url, remembered).Select(u => (u, (UrlRewrite?)remembered)));
            candidates.Add((url, null));
            foreach (var rw in _quirks(host).ThumbnailFallbacks)
                candidates.AddRange(Rewrite(url, rw).Select(u => (u, (UrlRewrite?)rw)));

            BooruRequestException? last = null;
            foreach (var (candidate, via) in candidates.DistinctBy(c => c.Url))
            {
                try
                {
                    // Downloads are not tied to one caller's cancellation: other waiters may still want the file.
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                    var path = await DownloadAsync(url, candidate, referer, progress, cts.Token).ConfigureAwait(false);
                    if (via is not null) _workingMirror[host] = via;
                    return path;
                }
                catch (BooruRequestException ex) when (ex.Kind is FailureKind.NotFound or FailureKind.Forbidden or FailureKind.Connection)
                {
                    last = ex;
                }
            }
            throw last ?? new BooruRequestException(FailureKind.Other, "Download failed.");
        }
        finally
        {
            _inflight.TryRemove(url, out _);
        }
    }

    private static IEnumerable<string> Rewrite(string url, UrlRewrite rw) =>
        new HostQuirks { ThumbnailFallbacks = [rw] }.FallbackUrls(url);

    private async Task<string> DownloadAsync(string cacheKey, string url, string? referer, IProgress<double>? progress, CancellationToken ct)
    {
        using var req = KuraHttp.Get(new Uri(url), referer: referer);
        using var resp = await _http.SendAsync(req, ct, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        var total = resp.Content.Headers.ContentLength;
        return await Disk.PutAsync(cacheKey, async (dest, token) =>
        {
            await using var src = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var buffer = new byte[81920];
            long done = 0;
            int n;
            var lastReport = 0.0;
            while ((n = await src.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false);
                done += n;
                if (progress is not null && total is > 0)
                {
                    var p = (double)done / total.Value;
                    if (p - lastReport >= 0.02) { progress.Report(p); lastReport = p; }
                }
            }
            progress?.Report(1);
        }, ct).ConfigureAwait(false);
    }
}
