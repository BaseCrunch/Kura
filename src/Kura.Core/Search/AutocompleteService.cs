using Kura.Core.Data;
using Kura.Core.Engines;
using Kura.Core.Models;

namespace Kura.Core.Search;

/// <summary>
/// Tag autocomplete: local search history and tag cache first, then the server's own native endpoint.
/// Never calls the third-party bisimplex service (SPEC 4.1 N7).
/// </summary>
public sealed class AutocompleteService(BooruClient client, SearchHistoryRepository history, TagCacheRepository tagCache)
{
    public const int MaxResults = 10;

    /// <summary>Returns the token being typed at <paramref name="caret"/> and its span in <paramref name="text"/>.</summary>
    public static (string Token, int Start, int Length) CurrentToken(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var start = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        var end = caret;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        return (text[start..end], start, end - start);
    }

    /// <summary>Strips search operators so <c>-cat</c> and <c>~dog</c> complete as tags.</summary>
    public static (string Prefix, string Term) SplitOperator(string token)
    {
        var i = 0;
        while (i < token.Length && token[i] is '-' or '~') i++;
        return (token[..i], token[i..]);
    }

    public IReadOnlyList<TagSuggestion> Local(string term)
    {
        if (term.Length == 0) return [];
        var results = new List<TagSuggestion>();
        foreach (var h in history.Find(term, 3))
            results.Add(new TagSuggestion(h.Query, TagCategory.Unknown, h.UseCount, FromHistory: true));
        results.AddRange(tagCache.Prefix(term, MaxResults));
        return Dedupe(results);
    }

    public async Task<IReadOnlyList<TagSuggestion>> SuggestAsync(ServerContext ctx, string term, CancellationToken ct)
    {
        var local = await Task.Run(() => Local(term), ct).ConfigureAwait(false);
        var remote = await client.AutocompleteAsync(ctx, term, ct).ConfigureAwait(false);
        if (remote.Count > 0) _ = Task.Run(() => tagCache.Upsert(remote), CancellationToken.None);
        // History first, then server results (authoritative counts), then anything else cached locally.
        return Dedupe(local.Where(s => s.FromHistory).Concat(remote).Concat(local.Where(s => !s.FromHistory)));
    }

    private static List<TagSuggestion> Dedupe(IEnumerable<TagSuggestion> items) =>
        items.DistinctBy(s => s.Name.ToLowerInvariant()).Take(MaxResults).ToList();
}
