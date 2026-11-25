using Kura.Core.Layout;
using Kura.Core.Util;
using Kura.Media;

namespace Kura.Tests.Media;

public class JustifiedLayoutTests
{
    [Fact]
    public void Complete_rows_fill_the_width_exactly()
    {
        var aspects = Enumerable.Range(0, 50).Select(i => 0.6 + (i % 7) * 0.25).ToList();
        var rows = JustifiedLayout.Pack(aspects, 1000, 200, 6);
        Assert.Equal(50, rows.Sum(r => r.Count));
        foreach (var r in rows.Where(r => r.IsComplete))
        {
            Assert.Equal(1000, r.Widths.Sum() + 6 * (r.Count - 1), precision: 3);
            Assert.InRange(r.Height, 100, 320);
        }
        Assert.All(rows.Take(rows.Count - 1), r => Assert.True(r.IsComplete));
    }

    [Fact]
    public void Trailing_row_keeps_target_height()
    {
        var rows = JustifiedLayout.Pack([1.0, 1.0], 2000, 200, 6);
        var r = Assert.Single(rows);
        Assert.False(r.IsComplete);
        Assert.Equal(200, r.Height);
    }

    [Fact]
    public void Repacking_from_a_row_start_is_deterministic()
    {
        var aspects = Enumerable.Range(0, 40).Select(i => 0.5 + (i * 37 % 11) / 5.0).ToList();
        var all = JustifiedLayout.Pack(aspects, 1200, 220, 4);
        var from = all[3].Start;
        var tail = JustifiedLayout.Pack(aspects, 1200, 220, 4, startIndex: from);
        Assert.Equal(all.Skip(3).Select(r => (r.Start, r.Count)), tail.Select(r => (r.Start, r.Count)));
    }

    [Fact]
    public void Handles_degenerate_input()
    {
        Assert.Empty(JustifiedLayout.Pack([], 1000, 200, 4));
        Assert.Empty(JustifiedLayout.Pack([1], 0, 200, 4));
        var rows = JustifiedLayout.Pack([double.NaN, 0, 50], 600, 200, 4);
        Assert.Equal(3, rows.Sum(r => r.Count));
    }
}

public class LruCacheTests
{
    [Fact]
    public void Evicts_least_recent_by_cost()
    {
        var evicted = new List<string>();
        var c = new LruCache<string, string>(10, v => v.Length, evicted.Add);
        c.Set("a", "aaaa");
        c.Set("b", "bbbb");
        Assert.True(c.TryGet("a", out _)); // a is now most recent
        c.Set("c", "cccc");                // over budget: evict b
        Assert.Equal(["bbbb"], evicted);
        Assert.False(c.TryGet("b", out _));
        Assert.Equal(8, c.TotalCost);
    }

    [Fact]
    public void Keeps_single_oversized_item()
    {
        var c = new LruCache<int, byte[]>(10, b => b.Length);
        c.Set(1, new byte[50]);
        Assert.True(c.TryGet(1, out _));
        c.Set(2, new byte[50]);
        Assert.False(c.TryGet(1, out _));
        Assert.Equal(1, c.Count);
    }
}

public class DiskCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kura-tests", "disk-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public async Task Put_get_and_evict_oldest()
    {
        var cache = new DiskCache(_dir, quotaBytes: 2500);
        for (var i = 0; i < 3; i++)
        {
            await cache.PutAsync($"k{i}", (s, ct) => s.WriteAsync(new byte[1000], ct).AsTask(), default);
            File.SetLastWriteTimeUtc(cache.PathFor($"k{i}"), DateTime.UtcNow.AddMinutes(-30 + i));
        }
        await cache.EvictAsync();
        Assert.False(cache.TryGet("k0", out _));
        Assert.True(cache.TryGet("k2", out var p));
        Assert.Equal(1000, new FileInfo(p).Length);
        Assert.True(cache.TotalBytes <= 2250);
    }

    [Fact]
    public async Task Failed_write_leaves_no_entry()
    {
        var cache = new DiskCache(_dir, 1_000_000);
        await Assert.ThrowsAsync<IOException>(() =>
            cache.PutAsync("bad", (_, _) => throw new IOException("network dropped"), default));
        Assert.False(cache.TryGet("bad", out _));
        Assert.Empty(Directory.EnumerateFiles(cache.TempDir));
    }
}

public class SkiaDecoderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kura-tests", "skia-" + Guid.NewGuid().ToString("N"));
    public SkiaDecoderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string WritePng(int w, int h)
    {
        using var bmp = new SkiaSharp.SKBitmap(w, h);
        using (var canvas = new SkiaSharp.SKCanvas(bmp)) canvas.Clear(new SkiaSharp.SKColor(200, 100, 50));
        var path = Path.Combine(_dir, $"{w}x{h}.png");
        using var data = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public void Thumbnail_is_scaled_to_height_in_32bit()
    {
        var bmp = SkiaDecoder.DecodeThumbnail(WritePng(1200, 800), 200);
        Assert.Equal(200, bmp.PixelHeight);
        Assert.Equal(300, bmp.PixelWidth);
        Assert.Equal(System.Windows.Media.PixelFormats.Pbgra32, bmp.Format);
        Assert.True(bmp.IsFrozen);
    }

    [Fact]
    public void Full_decode_keeps_resolution()
    {
        var d = SkiaDecoder.DecodeFull(WritePng(640, 480), default);
        Assert.NotNull(d.Still);
        Assert.Null(d.Animation);
        Assert.Equal(640, d.Still!.PixelWidth);
    }
}
