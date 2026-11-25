namespace Kura.Core.Engines;

/// <summary>Query preprocessing shared by all engines (SPEC 1.4).</summary>
public static class QueryPreprocessor
{
    private static readonly HashSet<string> RatingTagNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "safe", "suggestive", "questionable", "explicit", "semi-grimdark", "grimdark",
    };

    /// <param name="extraTags">Sort/filter terms from pickers. A typed argument rather than a magic map key (fixes Appendix A #4).</param>
    public static PreparedQuery Prepare(IBooruEngine engine, ServerContext ctx, string raw, string? extraTags = null)
    {
        var sep = engine.TagSeparator;
        var tokens = Tokenize(raw, sep).ToList();
        if (!string.IsNullOrWhiteSpace(extraTags)) tokens.AddRange(Tokenize(extraTags, sep));

        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Equals("#fullhd", StringComparison.OrdinalIgnoreCase)) tokens[i] = engine.FullHdTerm;
        }

        if (ctx.Server.RatingFilterEnabled && !tokens.Any(t => IsRatingToken(t, sep)))
        {
            var term = engine.RatingTerm(ctx);
            if (!string.IsNullOrEmpty(term)) tokens.Add(term);
        }

        var text = engine.NormalizeWildcards(string.Join(sep, tokens));
        var forceOriginal = tokens.Any(t =>
            t.Contains("width", StringComparison.OrdinalIgnoreCase) || t.Contains("height", StringComparison.OrdinalIgnoreCase));
        return new PreparedQuery(raw.Trim(), text, forceOriginal);
    }

    /// <summary>Whitespace-separated terms.</summary>
    public static IEnumerable<string> Tokenize(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Splits by the engine's separator: commas for engines whose tag names contain spaces, whitespace otherwise.</summary>
    public static IEnumerable<string> Tokenize(string s, string separator) =>
        IsCommaSeparated(separator)
            ? s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Tokenize(s);

    public static bool IsCommaSeparated(string separator) => separator.Trim() == ",";

    private static bool IsRatingToken(string t, string separator)
    {
        var s = t.TrimStart('-', '~', '!');
        if (s.StartsWith("rating:", StringComparison.OrdinalIgnoreCase) || s.StartsWith("rating=", StringComparison.OrdinalIgnoreCase))
            return true;
        // Philomena-style boorus express ratings as plain tags.
        return IsCommaSeparated(separator) && RatingTagNames.Contains(s);
    }
}
