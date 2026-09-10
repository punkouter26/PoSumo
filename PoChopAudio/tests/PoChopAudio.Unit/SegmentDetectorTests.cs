using PoChopAudio.Services.Chop;

namespace PoChopAudio.Unit;

public sealed class SegmentDetectorTests
{
    private const double LoudDb = -12;
    private const double QuietDb = -70;

    [Fact]
    public void FindsFiveTakesSeparatedBySilence()
    {
        var envelope = Envelope(Pattern(takes: 5, takeMs: 800, gapMs: 700, leadInMs: 500));

        var result = SegmentDetector.Detect(envelope, new ChopOptions());

        Assert.Equal(5, result.Segments.Count);
        Assert.Null(result.Warning);
        Assert.Equal([1, 2, 3, 4, 5], result.Segments.Select(s => s.Index));
    }

    [Fact]
    public void SegmentsCoverTheSoundAndStayInOrder()
    {
        var envelope = Envelope(Pattern(takes: 5, takeMs: 800, gapMs: 700, leadInMs: 500));

        var segments = SegmentDetector.Detect(envelope, new ChopOptions { PadMs = 0 }).Segments;

        // The first take starts 0.5s in and runs 0.8s; boundaries land on it, not on the silence.
        Assert.InRange(segments[0].StartSeconds, 0.40, 0.55);
        Assert.InRange(segments[0].EndSeconds, 1.25, 1.40);

        for (var i = 1; i < segments.Count; i++)
        {
            Assert.True(segments[i].StartSeconds >= segments[i - 1].EndSeconds,
                $"Clip {i + 1} overlaps clip {i}.");
        }
    }

    [Fact]
    public void PaddingWidensEveryClip()
    {
        var envelope = Envelope(Pattern(takes: 5, takeMs: 800, gapMs: 700, leadInMs: 500));

        var tight = SegmentDetector.Detect(envelope, new ChopOptions { PadMs = 0 }).Segments;
        var padded = SegmentDetector.Detect(envelope, new ChopOptions { PadMs = 100 }).Segments;

        Assert.Equal(tight.Count, padded.Count);
        for (var i = 0; i < tight.Count; i++)
        {
            Assert.True(padded[i].DurationSeconds > tight[i].DurationSeconds);
        }
    }

    [Fact]
    public void ShortMarkerPulsesBetweenTakesAreNotTreatedAsTakes()
    {
        // Five 800 ms takes with a 60 ms pulse sitting in the middle of every gap.
        var frames = Pattern(takes: 5, takeMs: 800, gapMs: 900, leadInMs: 300);
        for (var take = 0; take < 4; take++)
        {
            var pulseStart = (int)((0.3 + (take + 1) * 0.8 + take * 0.9 + 0.42) / (SegmentDetector.FrameMs / 1000));
            for (var f = pulseStart; f < pulseStart + 6; f++)
            {
                frames[f] = LoudDb;
            }
        }

        var result = SegmentDetector.Detect(Envelope(frames), new ChopOptions());

        Assert.Equal(5, result.Segments.Count);
        Assert.All(result.Segments, segment => Assert.True(segment.DurationSeconds > 0.5));
    }

    [Fact]
    public void ExtraTakesBeyondTheExpectedCountAreDroppedShortestFirst()
    {
        var frames = Pattern(takes: 6, takeMs: 800, gapMs: 700, leadInMs: 300);

        var result = SegmentDetector.Detect(Envelope(frames), new ChopOptions { ExpectedSegments = 5 });

        Assert.Equal(5, result.Segments.Count);
    }

    [Fact]
    public void WarnsWhenFewerTakesAreFoundThanExpected()
    {
        var result = SegmentDetector.Detect(
            Envelope(Pattern(takes: 3, takeMs: 800, gapMs: 700, leadInMs: 300)),
            new ChopOptions());

        Assert.Equal(3, result.Segments.Count);
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public void AnExplicitThresholdIsUsedVerbatim()
    {
        var result = SegmentDetector.Detect(
            Envelope(Pattern(takes: 5, takeMs: 800, gapMs: 700, leadInMs: 300)),
            new ChopOptions { ThresholdDb = -45 });

        Assert.Equal(-45, result.ThresholdDb);
    }

    [Fact]
    public void SilenceProducesNoClips()
    {
        var frames = Enumerable.Repeat(QuietDb, 500).ToList();

        var result = SegmentDetector.Detect(Envelope(frames), new ChopOptions());

        Assert.Empty(result.Segments);
        Assert.NotNull(result.Warning);
    }

    /// <summary>Builds a loudness envelope: lead-in silence, then takes separated by equal gaps.</summary>
    private static List<double> Pattern(int takes, double takeMs, double gapMs, double leadInMs)
    {
        var frames = new List<double>();
        Add(QuietDb, leadInMs);

        for (var i = 0; i < takes; i++)
        {
            Add(LoudDb, takeMs);
            Add(QuietDb, gapMs);
        }

        return frames;

        void Add(double db, double milliseconds)
        {
            for (var f = 0; f < (int)Math.Round(milliseconds / SegmentDetector.FrameMs); f++)
            {
                frames.Add(db);
            }
        }
    }

    private static AudioEnvelope Envelope(List<double> frameDb) => new()
    {
        FrameDb = frameDb,
        Waveform = [],
        DurationSeconds = frameDb.Count * SegmentDetector.FrameMs / 1000d,
        SampleRate = 44_100,
        Channels = 1,
        PeakDb = frameDb.Max(),
        NoiseFloorDb = frameDb.Min(),
    };
}
