using System.Text.Json;
using Dapper;

namespace Kura.Core.Data;

/// <summary>Typed key/value settings stored as JSON in the <c>settings</c> table.</summary>
public sealed class SettingsRepository(KuraDatabase db)
{
    public T Get<T>(string key, T fallback)
    {
        using var c = db.Open();
        var json = c.ExecuteScalar<string?>("SELECT value FROM settings WHERE key = @key", new { key });
        if (json is null) return fallback;
        try { return JsonSerializer.Deserialize<T>(json) ?? fallback; }
        catch (JsonException) { return fallback; }
    }

    public void Set<T>(string key, T value)
    {
        using var c = db.Open();
        c.Execute("INSERT INTO settings(key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            new { key, value = JsonSerializer.Serialize(value) });
    }
}
