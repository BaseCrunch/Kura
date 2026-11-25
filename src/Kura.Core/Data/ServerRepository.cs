using Dapper;
using Kura.Core.Models;

namespace Kura.Core.Data;

public sealed class ServerRepository(KuraDatabase db)
{
    /// <summary>
    /// The original app defaulted to donmai.moe, which now sits behind a Cloudflare browser challenge; the main
    /// Danbooru host serves API clients that send an honest User-Agent.
    /// </summary>
    public const string DefaultServerUrl = "https://danbooru.donmai.us";
    private const string LegacyDefaultUrl = "https://donmai.moe";
    public const string DefaultServerName = "Danbooru";

    private const string Columns =
        "id, name, url, engine, username, rating_filter_enabled, use_native_autocomplete, is_default, sort_order, created_at";

    public IReadOnlyList<Server> GetAll()
    {
        using var c = db.Open();
        return c.Query<Server>($"SELECT {Columns} FROM servers ORDER BY is_default DESC, sort_order, name").AsList();
    }

    public Server? Get(long id)
    {
        using var c = db.Open();
        return c.QuerySingleOrDefault<Server>($"SELECT {Columns} FROM servers WHERE id = @id", new { id });
    }

    /// <summary>Creates the default Danbooru 2 server on first run (SPEC 1.2).</summary>
    public Server EnsureDefault()
    {
        using (var c = db.Open())
        {
            var existing = c.QuerySingleOrDefault<Server>($"SELECT {Columns} FROM servers WHERE is_default = 1 LIMIT 1");
            if (existing is not null)
            {
                if (string.Equals(existing.Url, LegacyDefaultUrl, StringComparison.OrdinalIgnoreCase))
                {
                    existing.Url = DefaultServerUrl;
                    existing.Name = DefaultServerName;
                    c.Execute("UPDATE servers SET url = @Url, name = @Name WHERE id = @Id", new { existing.Url, existing.Name, existing.Id });
                }
                return existing;
            }
        }
        var s = new Server
        {
            Name = DefaultServerName,
            Url = DefaultServerUrl,
            Engine = EngineType.Danbooru2,
            RatingFilterEnabled = true,
            UseNativeAutocomplete = true,
            IsDefault = true,
        };
        Save(s);
        return s;
    }

    public void Save(Server s)
    {
        s.Url = s.Url.Trim().TrimEnd('/');
        using var c = db.Open();
        var p = new
        {
            s.Id, s.Name, s.Url, Engine = (int)s.Engine, s.Username, s.RatingFilterEnabled, s.UseNativeAutocomplete,
            s.IsDefault, s.SortOrder, s.CreatedAt,
        };
        if (s.Id == 0)
        {
            s.Id = c.ExecuteScalar<long>(
                """
                INSERT INTO servers (name, url, engine, username, rating_filter_enabled, use_native_autocomplete, is_default, sort_order, created_at)
                VALUES (@Name, @Url, @Engine, @Username, @RatingFilterEnabled, @UseNativeAutocomplete, @IsDefault, @SortOrder, @CreatedAt)
                RETURNING id;
                """, p);
        }
        else
        {
            c.Execute(
                """
                UPDATE servers SET name = @Name, url = @Url, engine = @Engine, username = @Username,
                    rating_filter_enabled = @RatingFilterEnabled, use_native_autocomplete = @UseNativeAutocomplete,
                    sort_order = @SortOrder
                WHERE id = @Id;
                """, p);
        }
    }

    /// <summary>Deletes a server. The default server cannot be deleted (SPEC 2.1).</summary>
    public bool Delete(long id)
    {
        using var c = db.Open();
        return c.Execute("DELETE FROM servers WHERE id = @id AND is_default = 0", new { id }) > 0;
    }
}
