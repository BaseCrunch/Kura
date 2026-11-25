using Dapper;
using Kura.Core.Data;
using Kura.Core.Models;
using Kura.Core.Security;

namespace Kura.Tests.Data;

public sealed class TempDatabase : IDisposable
{
    public TempDatabase()
    {
        Dir = Path.Combine(Path.GetTempPath(), "kura-tests", Guid.NewGuid().ToString("N"));
        Db = new KuraDatabase(Path.Combine(Dir, "kura.db"));
        Db.Migrate();
    }

    public string Dir { get; }
    public KuraDatabase Db { get; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}

public class DatabaseTests : IDisposable
{
    private readonly TempDatabase _t = new();
    public void Dispose() => _t.Dispose();

    [Fact]
    public void Migrate_creates_schema_in_wal_mode_and_is_idempotent()
    {
        Assert.Equal(1, _t.Db.SchemaVersion);
        Assert.Equal("wal", _t.Db.JournalMode);
        Assert.Equal(0, _t.Db.Migrate());

        using var c = _t.Db.Open();
        var tables = c.Query<string>("SELECT name FROM sqlite_master WHERE type IN ('table')").ToHashSet();
        foreach (var t in new[] { "servers", "posts", "tags", "post_tags", "posts_fts", "favorites", "view_history",
                     "search_history", "blacklist_rules", "pins", "pool_favorites", "downloads", "tag_cache", "settings" })
            Assert.Contains(t, tables);
    }

    [Fact]
    public void Fts5_is_available_and_matches_whole_tags()
    {
        using var c = _t.Db.Open();
        c.Execute("INSERT INTO posts_fts(rowid, tags, source) VALUES (1, 'cat_ears blue_sky', ''), (2, 'catgirl', '')");
        var hits = c.Query<long>("SELECT rowid FROM posts_fts WHERE posts_fts MATCH '\"cat_ears\"'").ToList();
        Assert.Equal([1L], hits);
    }

    [Fact]
    public void Default_server_is_created_once_and_cannot_be_deleted()
    {
        var repo = new ServerRepository(_t.Db);
        var a = repo.EnsureDefault();
        var b = repo.EnsureDefault();
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(EngineType.Danbooru2, b.Engine);
        Assert.True(b.RatingFilterEnabled);
        Assert.False(repo.Delete(a.Id));
        Assert.Single(repo.GetAll());
    }

    [Fact]
    public void Server_round_trips_and_trailing_slash_is_trimmed()
    {
        var repo = new ServerRepository(_t.Db);
        var s = new Server { Name = "e6", Url = "https://e621.net/", Engine = EngineType.E621, Username = "me" };
        repo.Save(s);
        var loaded = repo.Get(s.Id)!;
        Assert.Equal("https://e621.net", loaded.Url);
        Assert.Equal(EngineType.E621, loaded.Engine);
        Assert.Equal("me", loaded.Username);
        loaded.Name = "renamed";
        repo.Save(loaded);
        Assert.Equal("renamed", repo.Get(s.Id)!.Name);
        Assert.True(repo.Delete(s.Id));
    }

    [Fact]
    public void Search_history_records_once_per_query()
    {
        var repo = new SearchHistoryRepository(_t.Db);
        repo.Record("Blue_Sky", null);
        repo.Record("blue_sky", null);
        repo.Record("cat_ears", null);
        var hits = repo.Find("blue", 10);
        Assert.Single(hits);
        Assert.Equal(2, hits[0].UseCount);
        Assert.Empty(repo.Find("100%", 10));
    }

    [Fact]
    public void Settings_round_trip()
    {
        var repo = new SettingsRepository(_t.Db);
        Assert.Equal(220, repo.Get("thumb", 220));
        repo.Set("thumb", 300);
        Assert.Equal(300, repo.Get("thumb", 0));
    }

    [Fact]
    public void Tag_cache_lookup_and_prefix()
    {
        var repo = new TagCacheRepository(_t.Db);
        repo.Upsert([new Tag("hatsune_miku", TagCategory.Character), new Tag("general_tag", TagCategory.General)]);
        repo.Upsert([new TagSuggestion("hatsune_miku", TagCategory.Character, 100_000)]);
        var map = repo.Lookup(["Hatsune_Miku", "general_tag"]);
        Assert.Equal(TagCategory.Character, map["hatsune_miku"]);
        Assert.False(map.ContainsKey("general_tag")); // general is the default; not cached
        Assert.Single(repo.Prefix("hatsu", 5));
    }
}

public class CredentialStoreTests
{
    [Fact]
    public void Windows_credential_store_round_trips()
    {
        var store = new WindowsCredentialStore("Kura-test:" + Guid.NewGuid().ToString("N") + ":");
        Assert.True(store.Get(1).IsEmpty);
        store.Set(1, new ServerCredentials("key-123", null));
        Assert.Equal("key-123", store.Get(1).ApiKey);
        store.Delete(1);
        Assert.True(store.Get(1).IsEmpty);
        store.Delete(1); // deleting a missing credential is a no-op
    }
}

public class ServerPresetTests : IDisposable
{
    private readonly TempDatabase _t = new();
    public void Dispose() => _t.Dispose();

    [Fact]
    public void Seeds_presets_once_and_respects_deletions()
    {
        var servers = new ServerRepository(_t.Db);
        var settings = new SettingsRepository(_t.Db);
        servers.EnsureDefault();
        servers.Save(new Server { Name = "mine", Url = "https://e621.net", Engine = EngineType.E621 });

        var added = ServerPresets.Seed(servers, settings);
        Assert.Equal(ServerPresets.All.Count - 1, added); // e621 already configured
        var all = servers.GetAll();
        Assert.True(all[0].IsDefault);
        Assert.Contains(all, s => s.Url == "https://derpibooru.org" && s.Engine == EngineType.Philomena && s.RatingFilterEnabled);
        Assert.Equal("mine", all.Single(s => s.Url == "https://e621.net").Name);

        var kemono = all.Single(s => s.Url == "https://kemono.cr");
        servers.Delete(kemono.Id);
        Assert.Equal(0, ServerPresets.Seed(servers, settings));
        Assert.DoesNotContain(servers.GetAll(), s => s.Url == "https://kemono.cr");
    }

    [Fact]
    public void Every_preset_engine_is_supported() =>
        Assert.All(ServerPresets.All, s => Assert.True(Kura.Core.Engines.EngineRegistry.CreateDefault().IsSupported(s.Engine)));
}
