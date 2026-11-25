using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace Kura.Media;

/// <summary>
/// Decodes images with SkiaSharp into full 32-bit premultiplied BGRA, converting embedded ICC profiles to sRGB
/// (SPEC 4.3 G1). Falls back to WIC for formats Skia can't open, such as HEIF/AVIF with OS codecs installed.
/// </summary>
public static class SkiaDecoder
{
    /// <summary>Largest edge decoded for the viewer; beyond this the image is downscaled to keep GPU textures sane.</summary>
    public const int MaxEdge = 16384;

    private static readonly SKColorSpace Srgb = SKColorSpace.CreateSrgb();

    /// <summary>Decodes a thumbnail whose height is at most <paramref name="targetHeight"/> pixels.</summary>
    public static BitmapSource DecodeThumbnail(string path, int targetHeight)
    {
        using var codec = SKCodec.Create(path);
        if (codec is null) return WicDecode(path, targetHeight);

        var info = codec.Info;
        var scale = Math.Min(1f, targetHeight / (float)Math.Max(1, info.Height));
        // JPEG/WebP can decode directly at a reduced size, which is much cheaper than a full decode + resize.
        var scaled = codec.GetScaledDimensions(scale);
        var decodeInfo = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Bgra8888, SKAlphaType.Premul, Srgb);
        using var bmp = new SKBitmap(decodeInfo);
        var result = codec.GetPixels(decodeInfo, bmp.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            // Codec can't scale this format: decode full size, then resize.
            decodeInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul, Srgb);
            using var full = new SKBitmap(decodeInfo);
            result = codec.GetPixels(decodeInfo, full.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return WicDecode(path, targetHeight);
            return ResizeToHeight(full, targetHeight);
        }
        return bmp.Height > targetHeight * 1.15 ? ResizeToHeight(bmp, targetHeight) : ToBitmapSource(bmp);
    }

    /// <summary>Decodes the full image. Animated GIF/WebP returns an <see cref="AnimatedImage"/>.</summary>
    public static DecodedImage DecodeFull(string path, CancellationToken ct)
    {
        var data = SKData.Create(path);
        if (data is null) return new DecodedImage(WicDecode(path, null), null);
        var codec = SKCodec.Create(data);
        if (codec is null)
        {
            data.Dispose();
            return new DecodedImage(WicDecode(path, null), null);
        }

        if (codec.FrameCount > 1)
            return new DecodedImage(null, new AnimatedImage(data, codec));

        try
        {
            ct.ThrowIfCancellationRequested();
            var info = codec.Info;
            var w = info.Width;
            var h = info.Height;
            var longest = Math.Max(w, h);
            if (longest > MaxEdge)
            {
                var s = codec.GetScaledDimensions(MaxEdge / (float)longest);
                w = s.Width;
                h = s.Height;
            }
            var decodeInfo = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul, Srgb);
            using var bmp = new SKBitmap(decodeInfo);
            var result = codec.GetPixels(decodeInfo, bmp.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                return new DecodedImage(WicDecode(path, null), null);
            return new DecodedImage(ToBitmapSource(bmp), null);
        }
        finally
        {
            codec.Dispose();
            data.Dispose();
        }
    }

    private static BitmapSource ResizeToHeight(SKBitmap src, int targetHeight)
    {
        var h = Math.Max(1, targetHeight);
        var w = Math.Max(1, (int)Math.Round(src.Width * (h / (double)src.Height)));
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul, Srgb);
        using var resized = src.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell));
        return ToBitmapSource(resized ?? src);
    }

    internal static BitmapSource ToBitmapSource(SKBitmap bmp)
    {
        var bs = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Pbgra32, null,
            bmp.GetPixels(), bmp.ByteCount, bmp.RowBytes);
        bs.Freeze();
        return bs;
    }

    private static BitmapSource WicDecode(string path, int? targetHeight)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bi.UriSource = new Uri(path);
        if (targetHeight is { } th) bi.DecodePixelHeight = th;
        bi.EndInit();
        BitmapSource result = bi;
        if (bi.Format != PixelFormats.Pbgra32) result = new FormatConvertedBitmap(bi, PixelFormats.Pbgra32, null, 0);
        result.Freeze();
        return result;
    }
}

/// <summary>Result of a full decode: a still bitmap or an animation.</summary>
public sealed record DecodedImage(BitmapSource? Still, AnimatedImage? Animation)
{
    public Size PixelSize => Still is not null
        ? new Size(Still.PixelWidth, Still.PixelHeight)
        : new Size(Animation!.Width, Animation.Height);

    public long EstimatedBytes => Still is not null ? (long)Still.PixelWidth * Still.PixelHeight * 4 : 0;
}
