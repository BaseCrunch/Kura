namespace Kura.Core.Models;

/// <summary>
/// Booru engine families. Values match the original app's database codes (SPEC §1.2) so
/// imports and diagnostics line up; backups use a different mapping (see BackupServerItemType in Phase 2).
/// </summary>
public enum EngineType
{
    Danbooru1 = 0,
    Gelbooru = 1,
    Danbooru2 = 2,
    Gelbooru011 = 3,
    Shimmie = 4,
    ZeroChan = 5,
    Rss = 6,
    IBSearch = 7,
    Philomena = 8,
    E621 = 9,
    Hydrus = 10,
    BooruIO = 11,
    BooruOnRails = 12,
    Kemono = 13,
}

public static class EngineTypeExtensions
{
    public static string DisplayName(this EngineType t) => t switch
    {
        EngineType.Danbooru1 => "Danbooru 1 / Moebooru",
        EngineType.Gelbooru => "Gelbooru 0.2",
        EngineType.Danbooru2 => "Danbooru 2",
        EngineType.Gelbooru011 => "Gelbooru 0.1.11",
        EngineType.Shimmie => "Shimmie 2",
        EngineType.ZeroChan => "Zerochan",
        EngineType.Rss => "RSS",
        EngineType.IBSearch => "IBSearch",
        EngineType.Philomena => "Philomena",
        EngineType.E621 => "e621",
        EngineType.Hydrus => "Hydrus",
        EngineType.BooruIO => "BooruIO",
        EngineType.BooruOnRails => "booru-on-rails",
        EngineType.Kemono => "Kemono",
        _ => t.ToString(),
    };
}
