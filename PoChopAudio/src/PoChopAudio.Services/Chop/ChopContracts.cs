namespace PoChopAudio.Services.Chop;

/// <summary>Bounds the UI and the service both hold to, so the picker never offers what upload will reject.</summary>
public static class ChopLimits
{
    /// <summary>Largest single recording the service will decode.</summary>
    public const long MaxUploadBytes = 250L * 1024 * 1024;

    /// <summary>Most recordings one batch may hold.</summary>
    public const int MaxBatchFiles = 32;

    /// <summary>Starting position of the threshold slider — the gate a clean take usually clears.</summary>
    public const double DefaultThresholdDb = -40;
}

/// <summary>
/// Identifies one decoded source recording.
///
/// <para>
/// A struct rather than a bare <see cref="Guid"/> so an id cannot be confused with any other
/// identifier at a call site. There is no <c>TryParse</c>: ids are minted by
/// <see cref="ChopJobStore.Create"/> and handed straight back to the service, so no id is ever
/// reconstructed from text. Parsing existed for route parameters, and there are no routes.
/// </para>
/// </summary>
public readonly record struct JobId(Guid Value)
{
    public static JobId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

/// <summary>Result of decoding a recording: enough to draw the waveform and start tuning.</summary>
public sealed record UploadResult(
    JobId JobId,
    string FileName,
    double DurationSeconds,
    int SampleRate,
    int Channels,
    double PeakDb,
    double NoiseFloorDb,
    IReadOnlyList<float> Waveform);

/// <summary>Knobs the user turns when the automatic split needs a nudge.</summary>
public sealed record ChopOptions
{
    /// <summary>How many sounds the recording is expected to contain.</summary>
    public int ExpectedSegments { get; init; } = 5;

    /// <summary>Runs of sound shorter than this are treated as clicks or marker pulses, not takes.</summary>
    public double MinSegmentMs { get; init; } = 150;

    /// <summary>Quiet stretches shorter than this do not split a take in two.</summary>
    public double MinGapMs { get; init; } = 250;

    /// <summary>Extra audio kept either side of a detected take.</summary>
    public double PadMs { get; init; } = 40;

    /// <summary>Loudness gate in dBFS. Null asks the detector to find one.</summary>
    public double? ThresholdDb { get; init; }

    /// <summary>
    /// Drops any leading or trailing silence longer than this (in ms). 0 disables trimming.
    /// 200 ms is a sensible default: it eats a typical pre-roll breath but keeps an intentional
    /// short pause between takes.
    /// </summary>
    public double TrimSilenceMs { get; init; } = 0;
}

public sealed record ChopSegment(
    int Index,
    double StartSeconds,
    double EndSeconds,
    double PeakDb)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
}

public sealed record AnalysisResult(
    JobId JobId,
    double DurationSeconds,
    double ThresholdDb,
    double NoiseFloorDb,
    double PeakDb,
    IReadOnlyList<ChopSegment> Segments,
    string? Warning);
