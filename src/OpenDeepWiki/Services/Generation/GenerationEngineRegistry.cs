namespace OpenDeepWiki.Services.Generation;

public sealed class GenerationEngineRegistry : IGenerationEngineRegistry
{
    private readonly IReadOnlyDictionary<string, IGenerationEngine> _engines;
    private readonly string _defaultEngineId;

    public GenerationEngineRegistry(
        IEnumerable<IGenerationEngine> engines,
        string defaultEngineId = GenerationEngineIds.Legacy)
    {
        _engines = engines.ToDictionary(
            engine => engine.Capabilities.EngineId,
            engine => engine,
            StringComparer.OrdinalIgnoreCase);

        if (_engines.Count == 0)
        {
            throw new InvalidOperationException("No generation engines registered.");
        }

        _defaultEngineId = _engines.ContainsKey(defaultEngineId)
            ? defaultEngineId
            : _engines.Keys.First();
    }

    public IGenerationEngine GetRequired(string engineId)
    {
        if (string.IsNullOrWhiteSpace(engineId))
        {
            return GetDefault();
        }

        if (_engines.TryGetValue(engineId.Trim(), out var engine))
        {
            return engine;
        }

        throw new KeyNotFoundException(
            $"Generation engine '{engineId}' is not registered. Available: {string.Join(", ", _engines.Keys)}");
    }

    public IGenerationEngine GetDefault() => _engines[_defaultEngineId];

    public IReadOnlyList<GenerationEngineCapabilities> ListCapabilities()
        => _engines.Values
            .Select(engine => engine.Capabilities)
            .OrderBy(item => item.EngineId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
