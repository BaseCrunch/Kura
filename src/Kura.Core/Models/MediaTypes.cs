namespace Kura.Core.Models;

public static class MediaTypes
{
    public static readonly IReadOnlySet<string> Supported =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "jpg", "jpeg", "png", "gif", "webp", "avif", "mp4", "webm", "mkv", "mov", "avi", "zip" };

    /// <summary>Extension from an <c>ext=</c> query parameter, else from the last path segment (SPEC §1.7).</summary>
    public static string ExtensionOf(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        var q = url.IndexOf('?');
        if (q >= 0)
        {
            foreach (var part in url[(q + 1)..].Split('&'))
            {
                if (part.StartsWith("ext=", StringComparison.OrdinalIgnoreCase))
                    return part[4..].TrimStart('.').ToLowerInvariant();
            }
            url = url[..q];
        }
        var hash = url.IndexOf('#');
        if (hash >= 0) url = url[..hash];
        var slash = url.LastIndexOf('/');
        var name = slash >= 0 ? url[(slash + 1)..] : url;
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..].ToLowerInvariant() : "";
    }

    public static MediaKind KindOf(string ext) => ext.ToLowerInvariant() switch
    {
        "jpg" or "jpeg" or "png" or "webp" or "avif" or "bmp" => MediaKind.Image,
        "gif" => MediaKind.Gif,
        "mp4" or "webm" or "mkv" or "mov" or "avi" or "m4v" => MediaKind.Video,
        "zip" => MediaKind.Archive,
        _ => MediaKind.Unknown,
    };
}
