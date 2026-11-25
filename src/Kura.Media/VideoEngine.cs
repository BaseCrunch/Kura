using LibVLCSharp.Shared;
using VlcMedia = LibVLCSharp.Shared.Media;

namespace Kura.Media;

/// <summary>Owns the process-wide LibVLC instance (created lazily: loading libvlc costs ~100 ms).</summary>
public sealed class VideoEngine : IDisposable
{
    private readonly Lazy<LibVLC> _vlc = new(() =>
    {
        LibVLCSharp.Shared.Core.Initialize();
        return new LibVLC("--no-video-title-show", "--no-snapshot-preview", "--no-osd", "--quiet");
    });

    public LibVLC LibVLC => _vlc.Value;

    public MediaPlayer CreatePlayer() => new(LibVLC)
    {
        EnableHardwareDecoding = true,
        EnableKeyInput = false,   // keyboard stays with the WPF viewer
        EnableMouseInput = false,
    };

    /// <summary>Creates media for a remote URL with the Referer/User-Agent the booru CDN expects, looping forever.</summary>
    public VlcMedia CreateMedia(string url, string? referer, string? userAgent)
    {
        var media = new VlcMedia(LibVLC, new Uri(url));
        if (referer is not null) media.AddOption($":http-referrer={referer}");
        if (userAgent is not null) media.AddOption($":http-user-agent={userAgent}");
        media.AddOption(":input-repeat=65535");
        media.AddOption(":network-caching=1000");
        return media;
    }

    public void Dispose()
    {
        if (_vlc.IsValueCreated) _vlc.Value.Dispose();
    }
}
