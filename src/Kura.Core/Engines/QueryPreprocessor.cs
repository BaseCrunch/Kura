namespace Kura.Core.Engines;

/// <summary>Query preprocessing shared by all engines (SPEC 1.4).</summary>
public static class QueryPreprocessor
{
    /// <param name="extraTags">Sort/filter terms from pickers. A typed argument rather than a magic map key (fixes Appendix A #4).</param>
    public static PreparedQuery Prepare(IBooruEngine engine, ServerContext ctx, string raw, string? extraTags = null)
    {
        var tokens = Tokenize(raw).ToList();
        if (!string.IsNullOrWhiteSpace(extraTags)) tokens.AddRange(Tokenize(extraTags));

        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Equals("#fullhd", StringComparison.OrdinalIgnoreCase)) tokens[i] = "width:>=1920";
        }

        if (ctx.Server.RatingFilterEnabled && !tokens.Any(IsRatingToken))
        {
            var term = engine.RatingTerm(ctx);
            if (!string.IsNullOrEmpty(term)) tokens.Add(term);
        }

        var text = engine.NormalizeWildcards(string.Join(' ', tokens));
        var forceOriginal = tokens.Any(t =>
            t.Contains("width:", StringComparison.OrdinalIgnoreCase) || t.Contains("height:", StringComparison.OrdinalIgnoreCase));
        return new PreparedQuery(raw.Trim(), text, forceOriginal);
    }

    public static IEnumerable<string> Tokenize(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static bool IsRatingToken(string t)
    {
        var s = t.TrimStart('-', '~');
        return s.StartsWith("rating:", StringComparison.OrdinalIgnoreCase) || s.StartsWith("rating=", StringComparison.OrdinalIgnoreCase);
    }
}
