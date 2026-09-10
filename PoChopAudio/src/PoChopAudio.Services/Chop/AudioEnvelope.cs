namespace PoChopAudio.Services.Chop;

/// <summary>
/// A recording reduced to what the detector and the waveform view need: one loudness reading per
/// <see cref="SegmentDetector.FrameMs"/> of audio, plus a drawable peak trace.
/// </summary>
public sealed class AudioEnvelope
{
    public required IReadOnlyList<double> FrameDb { get; init; }
    public required IReadOnlyList<float> Waveform { get; init; }
    public required double DurationSeconds { get; init; }
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required double PeakDb { get; init; }
    public required double NoiseFloorDb { get; init; }
}
