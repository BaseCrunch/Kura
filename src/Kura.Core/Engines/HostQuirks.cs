using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kura.Core.Engines;

public sealed record UrlRewrite(string From, string To);

/// <summary>
/// Declarative per-host behaviour that the original app hard-coded (SPEC 4.1 N6, 4.3 G6): rating term,
/// Gelbooru JSON support, CDN cache-buster, thumbnail mirror fallbacks, rate and User-Agent overrides.
/// </summary>
public sealed record HostQuirks
{
    public static readonly HostQuirks None = new();

    public string? Match { get; init; }
    public string? RatingTerm { get; init; }
    public bool? GelbooruJson { get; init; }
    public bool CacheBuster { get; init; }
    public double? RequestsPerSecond { get; init; }
    public string? UserAgent { get; init; }
    public bool NativeAutocomplete { get; init; }
    /// <summary>Where the API lives when it isn't the site itself (rule34.xxx serves its API from api.rule34.xxx).</summary>
    public string? ApiBase { get; init; }
    /// <summary>The engine this site runs, used to pre-select the server type.</summary>
    public Models.EngineType? Engine { get; init; }
    public IReadOnlyList<UrlRewrite> ThumbnailFallbacks { get; init; } = [];

    /// <summary>Overlays <paramref name="more"/> (more specific) on top of this profile.</summary>
    public HostQuirks Merge(HostQuirks more) => new()
    {
        Match = more.Match ?? Match,
        RatingTerm = more.RatingTerm ?? RatingTerm,
        GelbooruJson = more.GelbooruJson ?? GelbooruJson,
        CacheBuster = more.CacheBuster || CacheBuster,
        RequestsPerSecond = more.RequestsPerSecond ?? RequestsPerSecond,
        UserAgent = more.UserAgent ?? UserAgent,
        NativeAutocomplete = more.NativeAutocomplete || NativeAutocomplete,
        ApiBase = more.ApiBase ?? ApiBase,
        Engine = more.Engine ?? Engine,
        ThumbnailFallbacks = more.ThumbnailFallbacks.Count > 0 ? more.ThumbnailFallbacks : ThumbnailFallbacks,
    };

    /// <summary>Candidate fallback URLs for a failed thumbnail, in order.</summary>
    public IEnumerable<string> FallbackUrls(string url)
    {
        foreach (var rw in ThumbnailFallbacks)
        {
            var from = "//" + rw.From + "/";
            var i = url.IndexOf(from, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) yield return url[..i] + "//" + rw.To + "/" + url[(i + from.Length)..];
        }
    }
}

/// <summary>Loads <c>quirks.json</c> and resolves the merged profile for a host (suffix match, most specific last).</summary>
public sealed class QuirkProfiles
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IReadOnlyList<HostQuirks> _profiles;

    public QuirkProfiles(IEnumerable<HostQuirks> profiles) =>
        _profiles = profiles.Where(p => !string.IsNullOrEmpty(p.Match)).OrderBy(p => p.Match!.Length).ToList();

    public static QuirkProfiles LoadEmbedded()
    {
        using var s = typeof(QuirkProfiles).Assembly.GetManifestResourceStream("Kura.Core.Engines.quirks.json")
                      ?? throw new InvalidOperationException("quirks.json resource missing");
        return Load(s);
    }

    public static QuirkProfiles Load(Stream json)
    {
        var doc = JsonSerializer.Deserialize<QuirkFile>(json, Options) ?? new QuirkFile();
        return new QuirkProfiles(doc.Hosts);
    }

    public HostQuirks Resolve(string host)
    {
        var result = HostQuirks.None;
        foreach (var p in _profiles)
        {
            if (host.Equals(p.Match, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + p.Match, StringComparison.OrdinalIgnoreCase))
                result = result.Merge(p);
        }
        return result;
    }

    private sealed class QuirkFile
    {
        [JsonPropertyName("hosts")] public List<HostQuirks> Hosts { get; set; } = [];
    }
}
