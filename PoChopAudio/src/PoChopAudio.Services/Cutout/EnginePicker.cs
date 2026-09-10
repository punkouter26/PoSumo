using Microsoft.Extensions.Logging;

namespace PoChopAudio.Services.Cutout;

/// <summary>
/// Resolves a <see cref="CutoutEngine"/> to its registered <see cref="IBackgroundRemover"/>. The
/// Default engine is the OnnxU2Net in-process one; clients pick the engine per batch and the
/// picker falls back to Default when the requested one is unavailable.
/// </summary>
public sealed class EnginePicker
{
    private readonly IReadOnlyDictionary<CutoutEngine, IBackgroundRemover> _removers;
    private readonly ILogger<EnginePicker> _logger;

    public EnginePicker(IEnumerable<IBackgroundRemover> removers, ILogger<EnginePicker> logger)
    {
        _removers = removers
            .Where(r => r.IsAvailable)
            .ToDictionary(r => r.Engine);
        _logger = logger;

        if (_removers.Count == 0)
        {
            logger.LogWarning("No background-removal engines are available. The /api/cutout endpoints will return 503.");
        }
        else
        {
            logger.LogInformation("Background-removal engines ready: {Engines}", string.Join(", ", _removers.Keys));
        }
    }

    public IReadOnlyList<CutoutEngine> AvailableEngines => _removers.Keys.ToArray();

    /// <summary>Same as <see cref="AvailableEngines"/> but exposes a method-style accessor for diagnostic endpoints.</summary>
    public IReadOnlyList<CutoutEngine> Snapshot() => AvailableEngines;

    public IBackgroundRemover? Resolve(CutoutEngine? requested)
    {
        if (requested is { } engine && _removers.TryGetValue(engine, out var remover))
        {
            return remover;
        }

        return _removers.GetValueOrDefault(CutoutEngine.OnnxU2Net);
    }
}
