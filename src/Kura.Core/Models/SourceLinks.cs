using System.Text.RegularExpressions;

namespace Kura.Core.Models;

public static partial class SourceLinks
{
    /// <summary>
    /// Splits a source field on <c>|</c> and rewrites Pixiv image/CDN URLs to the artwork page (SPEC 1.12).
    /// </summary>
    public static IReadOnlyList<string> Split(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return [];
        return source.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string Normalize(string url)
    {
        if ((url.Contains("pixiv.net/", StringComparison.OrdinalIgnoreCase) || url.Contains("pximg.net/", StringComparison.OrdinalIgnoreCase))
            && !url.Contains("illust_id=", StringComparison.OrdinalIgnoreCase) && !url.Contains("/artworks/", StringComparison.OrdinalIgnoreCase))
        {
            var m = PixivFile().Match(url);
            if (m.Success) return $"https://www.pixiv.net/en/artworks/{m.Groups[1].Value}";
        }
        return url;
    }

    public static bool IsWebLink(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    [GeneratedRegex(@"/(\d+)_[^/]*$")]
    private static partial Regex PixivFile();
}

public static class Ratings
{
    public static string Display(string? rating) => (rating ?? "").Trim().ToLowerInvariant() switch
    {
        "g" or "general" => "General",
        "s" or "safe" => "Safe",
        "sensitive" => "Sensitive",
        "q" or "questionable" => "Questionable",
        "e" or "explicit" => "Explicit",
        "" => "Unknown",
        var other => char.ToUpperInvariant(other[0]) + other[1..],
    };
}
