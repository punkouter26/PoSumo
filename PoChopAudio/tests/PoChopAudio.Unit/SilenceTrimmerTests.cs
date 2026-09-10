using PoChopAudio.Services.Chop;

namespace PoChopAudio.Unit;

public sealed class SilenceTrimmerTests
{
    private static AudioEnvelope MakeEnvelope(double noiseFloorDb, params double[] frameDb)
    {
        var length = frameDb.Length;
        return new AudioEnvelope
        {
            FrameDb = frameDb,
            Waveform = Enumerable.Range(0, length).Select(i => 0.5f).ToArray(),
            DurationSeconds = length * SegmentDetector.FrameMs / 1000d,
            SampleRate = 44100,
            Channels = 1,
            PeakDb = 0,
            NoiseFloorDb = noiseFloorDb,
        };
    }

    [Fact]
    public void TrimReturnsFullRangeWhenOptionDisabled()
    {
        var envelope = MakeEnvelope(-70, -70, -70, -70, -70);
        var options = new ChopOptions { TrimSilenceMs = 0 };

        var (start, end) = SilenceTrimmer.Trim(envelope, options);

        Assert.Equal(0, start);
        Assert.Equal(envelope.FrameDb.Count - 1, end);
    }

    [Fact]
    public void TrimCropsLeadingAndTrailingSilence()
    {
        // 5 frames of silence, then 10 frames of signal, then 5 frames of silence.
        var frames = new double[]
        {
            -80, -80, -80, -80, -80,
            -10, -10, -10, -10, -10, -10, -10, -10, -10, -10,
            -80, -80, -80, -80, -80,
        };
        var envelope = MakeEnvelope(-70, frames);
        var options = new ChopOptions { TrimSilenceMs = 50 };

        var (start, end) = SilenceTrimmer.Trim(envelope, options);

        Assert.True(start >= 4, $"expected start >= 4, got {start}");
        Assert.True(end <= 15, $"expected end <= 15, got {end}");
        Assert.True(end >= start);
    }
}
