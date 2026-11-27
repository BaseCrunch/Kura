using System.Text.Json;
using Dapper;

namespace Kura.Core.Data;

/// <summary>A saved search shown on the home page (the original app's "home pins", SPEC 3.2).</summary>
public sealed record Pin(long Id, string Title, long ServerId, string Query, int SortOrder);

/// <summary>Home pins in the <c>pins</c> table (kind 3 = search). Groups (kind 4) come later.</summary>
public sealed class PinRepository(KuraDatabase db)
{
    private const int KindSearch = 3;

    private sealed record Row(long Id, string? Title, long ServerId, string? QueryJson, long SortOrder);
    private sealed record QueryPayload(string Query);

    public IReadOnlyList<Pin> GetAll()
    {
        using var c = db.Open();
        return c.Query<Row>(
                "SELECT id, title, server_id, query_json, sort_order FROM pins WHERE kind = @KindSearch AND server_id IS NOT NULL ORDER BY sort_order, id",
                new { KindSearch })
            .Select(ToPin)
            .ToList();
    }

    public Pin? Find(long serverId, string query)
    {
        var q = query.Trim();
        return GetAll().FirstOrDefault(p => p.ServerId == serverId && string.Equals(p.Query, q, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Pins a search (or returns the existing pin for the same server and query).</summary>
    public Pin Add(long serverId, string query, string? title = null)
    {
        var q = query.Trim();
        if (Find(serverId, q) is { } existing) return existing;
        using var c = db.Open();
        var order = c.ExecuteScalar<long>("SELECT IFNULL(MAX(sort_order), -1) + 1 FROM pins WHERE kind = @KindSearch", new { KindSearch });
        var id = c.ExecuteScalar<long>(
            "INSERT INTO pins (kind, title, server_id, query_json, sort_order) VALUES (@KindSearch, @title, @serverId, @json, @order) RETURNING id",
            new { KindSearch, title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(), serverId, json = JsonSerializer.Serialize(new QueryPayload(q)), order });
        return new Pin(id, string.IsNullOrWhiteSpace(title) ? DefaultTitle(q) : title.Trim(), serverId, q, (int)order);
    }

    public void Remove(long id)
    {
        using var c = db.Open();
        c.Execute("DELETE FROM pins WHERE id = @id", new { id });
    }

    public void Rename(long id, string? title)
    {
        using var c = db.Open();
        c.Execute("UPDATE pins SET title = @title WHERE id = @id", new { id, title = string.IsNullOrWhiteSpace(title) ? null : title.Trim() });
    }

    /// <summary>Moves a pin up (negative) or down (positive) one place, renumbering so orders stay dense.</summary>
    public void Move(long id, int delta)
    {
        var pins = GetAll().ToList();
        var i = pins.FindIndex(p => p.Id == id);
        var j = i + Math.Sign(delta);
        if (i < 0 || j < 0 || j >= pins.Count) return;
        (pins[i], pins[j]) = (pins[j], pins[i]);
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        for (var k = 0; k < pins.Count; k++)
            c.Execute("UPDATE pins SET sort_order = @k WHERE id = @id", new { k, id = pins[k].Id }, tx);
        tx.Commit();
    }

    private static Pin ToPin(Row r)
    {
        var query = "";
        if (!string.IsNullOrEmpty(r.QueryJson))
        {
            try { query = JsonSerializer.Deserialize<QueryPayload>(r.QueryJson)?.Query ?? ""; }
            catch (JsonException) { }
        }
        return new Pin(r.Id, string.IsNullOrWhiteSpace(r.Title) ? DefaultTitle(query) : r.Title!, r.ServerId, query, (int)r.SortOrder);
    }

    private static string DefaultTitle(string query) => query.Length == 0 ? "Latest posts" : query;
}
