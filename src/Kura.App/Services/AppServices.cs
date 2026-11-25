using System.IO;
using Kura.Core.Data;
using Kura.Core.Engines;
using Kura.Core.Net;
using Kura.Core.Search;
using Kura.Core.Security;
using Kura.Media;

namespace Kura.App.Services;

/// <summary>Composition root. One instance for the process; exposed statically for controls that can't take constructor args.</summary>
public sealed class AppServices : IDisposable
{
    public static AppServices Current { get; private set; } = null!;

    public AppServices()
    {
        AppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kura");
        LocalDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kura");
        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(LocalDataDir);

        Database = new KuraDatabase(Path.Combine(AppDataDir, "kura.db"));
        Database.Migrate();
        Servers = new ServerRepository(Database);
        Servers.EnsureDefault();
        Settings = new SettingsRepository(Database);
        ServerPresets.Seed(Servers, Settings);
        History = new SearchHistoryRepository(Database);
        TagCache = new TagCacheRepository(Database);
        Credentials = new WindowsCredentialStore();

        Http = new KuraHttp();
        Client = new BooruClient(Http, EngineRegistry.CreateDefault(), QuirkProfiles.LoadEmbedded());
        Autocomplete = new AutocompleteService(Client, History, TagCache);

        var diskGb = Settings.Get(SettingKeys.DiskCacheGb, 5.0);
        Disk = new DiskCache(Path.Combine(LocalDataDir, "cache"), (long)(diskGb * 1024 * 1024 * 1024));
        Images = new ImageLoader(Http, Disk, RamBudgetBytes(), host => Client.QuirksFor(host));
        Video = new VideoEngine();
        Overlay = new OverlayGate();
        Current = this;
    }

    public string AppDataDir { get; }
    public string LocalDataDir { get; }
    public KuraDatabase Database { get; }
    public ServerRepository Servers { get; }
    public SettingsRepository Settings { get; }
    public SearchHistoryRepository History { get; }
    public TagCacheRepository TagCache { get; }
    public ICredentialStore Credentials { get; }
    public KuraHttp Http { get; }
    public BooruClient Client { get; }
    public AutocompleteService Autocomplete { get; }
    public DiskCache Disk { get; }
    public ImageLoader Images { get; }
    public VideoEngine Video { get; }
    public OverlayGate Overlay { get; }

    /// <summary>Decoded-image RAM budget: ~10% of physical memory, clamped to 256 MB – 2 GB, overridable in settings.</summary>
    private long RamBudgetBytes()
    {
        var mb = Settings.Get(SettingKeys.RamCacheMb, 0);
        if (mb > 0) return mb * 1024L * 1024L;
        var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return Math.Clamp(total / 10, 256L * 1024 * 1024, 2048L * 1024 * 1024);
    }

    public void Dispose()
    {
        Video.Dispose();
        Http.Dispose();
    }
}

public static class SettingKeys
{
    public const string ThumbnailHeight = "ui.thumbnailHeight";
    public const string InfoPanelWidth = "ui.infoPanelWidth";
    public const string InfoPanelVisible = "ui.infoPanelVisible";
    public const string LastServerId = "search.lastServerId";
    public const string RamCacheMb = "cache.ramMb";
    public const string DiskCacheGb = "cache.diskGb";
    public const string PreloadAhead = "viewer.preloadAhead";
    public const string PreloadBehind = "viewer.preloadBehind";
    public const string VideoMuted = "video.muted";
    public const string VideoVolume = "video.volume";
}
