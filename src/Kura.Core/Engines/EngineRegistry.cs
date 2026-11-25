using Kura.Core.Models;

namespace Kura.Core.Engines;

public sealed class EngineRegistry
{
    private readonly Dictionary<EngineType, IBooruEngine> _engines;

    public EngineRegistry(IEnumerable<IBooruEngine> engines) => _engines = engines.ToDictionary(e => e.Type);

    /// <summary>Phase 1 engines.</summary>
    public static EngineRegistry CreateDefault() => new([new Danbooru2Engine(), new GelbooruEngine(), new E621Engine()]);

    public IReadOnlyCollection<EngineType> Supported => _engines.Keys;

    public bool IsSupported(EngineType t) => _engines.ContainsKey(t);

    public IBooruEngine Get(EngineType t) =>
        _engines.TryGetValue(t, out var e) ? e : throw new NotSupportedException($"{t.DisplayName()} isn't supported yet.");
}
