using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kura.Core.Util;
using SkiaSharp;

namespace Kura.Media;

/// <summary>
/// Frame-by-frame animated GIF/WebP decoding with a memory budget (SPEC 4.4 V5). Each frame is composited onto
/// the frame it depends on (Skia's <c>RequiredFrame</c>), and decoded frames are kept in an LRU sized by bytes,
/// so a huge GIF re-decodes as it loops instead of holding every frame in RAM.
/// </summary>
public sealed class AnimatedImage : IDisposable
{
    public const long DefaultFrameBudget = 256L * 1024 * 1024;
    private static readonly SKColorSpace Srgb = SKColorSpace.CreateSrgb();

    private readonly SKData _data;
    private readonly SKCodec _codec;
    private readonly SKCodecFrameInfo[] _frames;
    private readonly LruCache<int, byte[]> _cache;
    private readonly object _gate = new();
    private readonly SKImageInfo _info;
    private bool _disposed;

    internal AnimatedImage(SKData data, SKCodec codec, long frameBudget = DefaultFrameBudget)
    {
        _data = data;
        _codec = codec;
        _frames = codec.FrameInfo;
        Width = codec.Info.Width;
        Height = codec.Info.Height;
        _info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul, Srgb);
        var frameBytes = (long)_info.BytesSize;
        // Keep at least a few frames so the dependency chain never re-decodes from scratch every tick.
        _cache = new LruCache<int, byte[]>(Math.Max(frameBudget, frameBytes * 3), b => b.LongLength);
        Durations = _frames.Select(f => TimeSpan.FromMilliseconds(f.Duration <= 10 ? 100 : f.Duration)).ToArray();
        LoopCount = codec.RepetitionCount; // -1 = infinite
    }

    public int Width { get; }
    public int Height { get; }
    public int FrameCount => _frames.Length;
    public IReadOnlyList<TimeSpan> Durations { get; }
    public int LoopCount { get; }
    public TimeSpan TotalDuration => TimeSpan.FromTicks(Durations.Sum(d => d.Ticks));

    /// <summary>Returns frame <paramref name="index"/> as a frozen bitmap. Safe to call from any thread.</summary>
    public BitmapSource GetFrame(int index)
    {
        byte[] pixels;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            pixels = DecodeLocked(index);
        }
        var bs = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Pbgra32, null, pixels, _info.RowBytes);
        bs.Freeze();
        return bs;
    }

    private byte[] DecodeLocked(int index)
    {
        if (_cache.TryGet(index, out var cached)) return cached;

        var required = _frames[index].RequiredFrame;
        var buffer = new byte[_info.BytesSize];
        if (required >= 0)
        {
            var prior = DecodeLocked(required);
            Buffer.BlockCopy(prior, 0, buffer, 0, buffer.Length);
        }

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var opts = new SKCodecOptions(index, required);
            _codec.GetPixels(_info, handle.AddrOfPinnedObject(), opts);
        }
        finally
        {
            handle.Free();
        }
        _cache.Set(index, buffer);
        return buffer;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cache.Clear();
            _codec.Dispose();
            _data.Dispose();
        }
    }
}
