using Kura.Core.Models;

namespace Kura.Core.Data;

/// <summary>
/// Built-in servers, one or more per engine, added once on first run. Sites that host explicit content start with
/// the safe-rating filter on. Hydrus is left out: it's the user's own client and needs their access key.
/// </summary>
public static class ServerPresets
{
    /// <summary>Bump when presets are added, so existing installs get only the new ones.</summary>
    public const int Version = 1;

    public static IReadOnlyList<Server> All { get; } =
    [
        Make("Safebooru", "https://safebooru.org", EngineType.Gelbooru, ratingFilter: false),
        Make("Konachan (SFW)", "https://konachan.net", EngineType.Danbooru1, ratingFilter: false),
        Make("yande.re", "https://yande.re", EngineType.Danbooru1, ratingFilter: true),
        Make("e926 (safe e621)", "https://e926.net", EngineType.E621, ratingFilter: false),
        Make("e621", "https://e621.net", EngineType.E621, ratingFilter: true),
        Make("Derpibooru", "https://derpibooru.org", EngineType.Philomena, ratingFilter: true),
        Make("Twibooru", "https://twibooru.org", EngineType.BooruOnRails, ratingFilter: true),
        Make("Kemono", "https://kemono.cr", EngineType.Kemono, ratingFilter: false),
        Make("Shimmie testbed", "https://shimmie.shishnet.org", EngineType.Shimmie, ratingFilter: false),
    ];

    private static Server Make(string name, string url, EngineType engine, bool ratingFilter) => new()
    {
        Name = name,
        Url = url,
        Engine = engine,
        RatingFilterEnabled = ratingFilter,
        UseNativeAutocomplete = true,
    };

    /// <summary>
    /// Adds presets whose URL isn't configured yet, at most once per <see cref="Version"/>, so a preset the user
    /// deletes stays deleted. Returns how many were added.
    /// </summary>
    public static int Seed(ServerRepository servers, SettingsRepository settings)
    {
        const string key = "servers.presetsVersion";
        if (settings.Get(key, 0) >= Version) return 0;
        var existing = servers.GetAll().Select(s => s.Url.TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var order = servers.GetAll().Count;
        var added = 0;
        foreach (var preset in All)
        {
            if (existing.Contains(preset.Url)) continue;
            servers.Save(new Server
            {
                Name = preset.Name,
                Url = preset.Url,
                Engine = preset.Engine,
                RatingFilterEnabled = preset.RatingFilterEnabled,
                UseNativeAutocomplete = preset.UseNativeAutocomplete,
                SortOrder = order++,
            });
            added++;
        }
        settings.Set(key, Version);
        return added;
    }
}
