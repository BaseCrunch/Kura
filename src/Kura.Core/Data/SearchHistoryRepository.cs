using Dapper;

namespace Kura.Core.Data;

public sealed record SearchHistoryEntry(long Id, string Query, string SearchedAt, long Starred, long UseCount);

/// <summary>Search history. A query is recorded once per new search, not per page (fixes SPEC 4.1 N10).</summary>
public sealed class SearchHistoryRepository(KuraDatabase db)
{
    public void Record(string query, long? serverId)
    {
        query = query.Trim();
        if (query.Length == 0) return;
        using var c = db.Open();
        c.Execute(
            """
            INSERT INTO search_history (server_id, query, query_lower, searched_at)
            VALUES (@serverId, @query, @lower, @now)
            ON CONFLICT(query_lower) DO UPDATE SET searched_at = excluded.searched_at,
                use_count = use_count + 1, server_id = excluded.server_id;
            """, new { serverId, query, lower = query.ToLowerInvariant(), now = DateTimeOffset.UtcNow });
    }

    /// <summary>Previous searches containing <paramref name="term"/>, starred first then most recent.</summary>
    public IReadOnlyList<SearchHistoryEntry> Find(string term, int limit)
    {
        using var c = db.Open();
        var like = "%" + Sql.EscapeLike(term.ToLowerInvariant()) + "%";
        return c.Query<SearchHistoryEntry>(
            """
            SELECT id, query, searched_at, starred, use_count FROM search_history
            WHERE query_lower LIKE @like ESCAPE '\'
            ORDER BY starred DESC, searched_at DESC LIMIT @limit
            """, new { like, limit }).AsList();
    }
}

internal static class Sql
{
    public static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
