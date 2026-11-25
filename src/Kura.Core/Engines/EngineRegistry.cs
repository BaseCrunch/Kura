using Kura.Core.Models;

namespace Kura.Core.Engines;

public sealed class EngineRegistry
{
    private readonly Dictionary<EngineType, IBooruEngine> _engines;

    public EngineRegistry(IEnumerable<IBooruEngine> engines) => _engines = engines.ToDictionary(e => e.Type);

    /// <summary>Every engine the original app's server form offers.</summary>
    public static EngineRegistry CreateDefault() => new(
    [
        new Danbooru2Engine(), new GelbooruEngine(), new E621Engine(), new Danbooru1Engine(), new Gelbooru011Engine(),
        new PhilomenaEngine(), new BooruOnRailsEngine(), new HydrusEngine(), new ShimmieEngine(), new KemonoEngine(),
    ]);

    public IReadOnlyCollection<EngineType> Supported => _engines.Keys;

    public bool IsSupported(EngineType t) => _engines.ContainsKey(t);

    public IBooruEngine Get(EngineType t) =>
        _engines.TryGetValue(t, out var e) ? e : throw new NotSupportedException($"{t.DisplayName()} isn't supported yet.");
}
