using Dapper;
using Kura.Core.Models;

namespace Kura.Core.Data;

/// <summary>Tag-to-category cache used to colour tags from engines that don't categorise them (e.g. Gelbooru).</summary>
public sealed class TagCacheRepository(KuraDatabase db)
{
    public void Upsert(IEnumerable<Tag> tags)
    {
        var rows = tags.Where(t => t.Category is not TagCategory.Unknown and not TagCategory.General)
            .DistinctBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new { name = t.Name, category = (int)t.Category, now = DateTimeOffset.UtcNow })
            .ToList();
        if (rows.Count == 0) return;
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        c.Execute(
            """
            INSERT INTO tag_cache (name, category, updated_at) VALUES (@name, @category, @now)
            ON CONFLICT(name) DO UPDATE SET category = excluded.category, updated_at = excluded.updated_at
            """, rows, tx);
        tx.Commit();
    }

    public void Upsert(IEnumerable<TagSuggestion> suggestions)
    {
        var rows = suggestions.Where(s => s.Category != TagCategory.Unknown && !s.FromHistory)
            .DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new { name = s.Name, category = (int)s.Category, count = s.PostCount, now = DateTimeOffset.UtcNow })
            .ToList();
        if (rows.Count == 0) return;
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        c.Execute(
            """
            INSERT INTO tag_cache (name, category, post_count, updated_at) VALUES (@name, @category, @count, @now)
            ON CONFLICT(name) DO UPDATE SET category = excluded.category, post_count = excluded.post_count, updated_at = excluded.updated_at
            """, rows, tx);
        tx.Commit();
    }

    public IReadOnlyDictionary<string, TagCategory> Lookup(IEnumerable<string> names)
    {
        var list = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, TagCategory>(StringComparer.OrdinalIgnoreCase);
        if (list.Count == 0) return result;
        using var c = db.Open();
        foreach (var chunk in list.Chunk(500))
        {
            foreach (var row in c.Query<(string Name, long Category)>("SELECT name, category FROM tag_cache WHERE name IN @chunk", new { chunk }))
                result[row.Name] = (TagCategory)row.Category;
        }
        return result;
    }

    public IReadOnlyList<TagSuggestion> Prefix(string prefix, int limit)
    {
        using var c = db.Open();
        var like = Sql.EscapeLike(prefix) + "%";
        return c.Query<(string Name, long Category, long PostCount)>(
                "SELECT name, category, post_count FROM tag_cache WHERE name LIKE @like ESCAPE '\\' ORDER BY post_count DESC LIMIT @limit",
                new { like, limit })
            .Select(r => new TagSuggestion(r.Name, (TagCategory)r.Category, r.PostCount))
            .ToList();
    }
}
