using PoChopAudio.Services.Chop;

namespace PoChopAudio.Unit;

/// <summary>
/// The export maths, tested on numbers rather than files. The guards matter more than the happy
/// path here: normalization that clips, or that amplifies room tone by 40 dB, is worse than no
/// normalization at all.
/// </summary>
public sealed class ClipProcessorTests
{
    private static ExportOptions Normalizing(NormalizeMode mode, double target, double ceiling = -1) =>
        new() { Normalize = mode, TargetDb = target, CeilingDb = ceiling };

    [Fact]
    public void NoNormalizationIsUnityWhateverTheMeasurement()
    {
        var gain = ClipProcessor.DecideGain(measuredDb: -30, peakDb: -30, ExportOptions.PassThrough);

        Assert.True(gain.IsUnity);
        Assert.False(gain.Silent);
    }

    [Fact]
    public void PeakNormalizationLiftsTheClipToTheTarget()
    {
        // Peak at -12 dBFS, asked for -3, ceiling at -1: 9 dB of gain with room to spare.
        var gain = ClipProcessor.DecideGain(measuredDb: -12, peakDb: -12, Normalizing(NormalizeMode.Peak, -3));

        Assert.Equal(9, gain.GainDb, 6);
        Assert.False(gain.CeilingLimited);
        Assert.False(gain.GainCapped);
    }

    [Fact]
    public void TheCeilingWinsWhenTheTargetWouldClip()
    {
        // RMS is -30 and the target is -14, so it wants +16 dB — but the peak is already at -6,
        // and +16 would put it 15 dB past full scale. The ceiling pulls it back to +5.
        var gain = ClipProcessor.DecideGain(measuredDb: -30, peakDb: -6, Normalizing(NormalizeMode.Rms, -14));

        Assert.Equal(5, gain.GainDb, 6);
        Assert.True(gain.CeilingLimited);
    }

    [Fact]
    public void CeilingLimitedGainLandsThePeakExactlyOnTheCeiling()
    {
        var options = Normalizing(NormalizeMode.Lufs, -5, ceiling: -1);
        var gain = ClipProcessor.DecideGain(measuredDb: -40, peakDb: -8, options);

        Assert.Equal(options.CeilingDb, -8 + gain.GainDb, 6);
    }

    [Fact]
    public void GainIsCappedSoRoomToneIsNotAmplifiedIntoATake()
    {
        // -60 LUFS wanting -16 is 44 dB of gain. Without the cap this turns a silent take into
        // a wall of hiss at full level.
        var gain = ClipProcessor.DecideGain(measuredDb: -60, peakDb: -55, Normalizing(NormalizeMode.Lufs, -16));

        Assert.Equal(ExportLimits.MaxGainDb, gain.GainDb, 6);
        Assert.True(gain.GainCapped);
    }

    [Fact]
    public void SilenceIsLeftAloneRatherThanGivenInfiniteGain()
    {
        var gain = ClipProcessor.DecideGain(double.NegativeInfinity, double.NegativeInfinity, Normalizing(NormalizeMode.Peak, -1));

        Assert.True(gain.Silent);
        Assert.True(gain.IsUnity);
    }

    [Fact]
    public void AClipUnderTheSilenceFloorIsTreatedAsSilence()
    {
        var gain = ClipProcessor.DecideGain(ExportLimits.SilenceFloorDb - 1, -80, Normalizing(NormalizeMode.Rms, -20));

        Assert.True(gain.Silent);
        Assert.True(gain.IsUnity);
    }

    [Fact]
    public void NormalizationCanAlsoAttenuate()
    {
        var gain = ClipProcessor.DecideGain(measuredDb: -2, peakDb: -2, Normalizing(NormalizeMode.Peak, -12));

        Assert.Equal(-10, gain.GainDb, 6);
        Assert.False(gain.CeilingLimited);
    }

    [Fact]
    public void LinearGainMatchesTheDecibelFigure()
    {
        var gain = ClipProcessor.DecideGain(measuredDb: -12, peakDb: -12, Normalizing(NormalizeMode.Peak, -6));

        Assert.Equal(Math.Pow(10, 6 / 20d), gain.Linear, 9);
    }

    [Fact]
    public void FadeRisesFromSilenceToUnityAcrossTheFadeIn()
    {
        const long total = 1000;
        const long fade = 100;

        var first = ClipProcessor.FadeGain(0, total, fade, 0);
        var middle = ClipProcessor.FadeGain(fade / 2, total, fade, 0);
        var after = ClipProcessor.FadeGain(fade, total, fade, 0);

        Assert.InRange(first, 0, 0.001);
        // Gains are evaluated at the centre of each sample, so the halfway frame sits a fraction
        // past the ramp's midpoint rather than exactly on it.
        Assert.InRange(middle, 0.49, 0.52);
        Assert.Equal(1, after, 9);
    }

    [Fact]
    public void FadeFallsBackToSilenceAtTheVeryLastFrame()
    {
        const long total = 1000;
        const long fade = 100;

        Assert.Equal(1, ClipProcessor.FadeGain(total - 1 - fade, total, 0, fade), 9);
        Assert.InRange(ClipProcessor.FadeGain(total - 1 - (fade / 2), total, 0, fade), 0.49, 0.52);
        Assert.InRange(ClipProcessor.FadeGain(total - 1, total, 0, fade), 0, 0.001);
    }

    [Fact]
    public void FadeIsMonotonicSoItCannotWobble()
    {
        const long total = 500;
        var previous = -1.0;

        for (long frame = 0; frame < 50; frame++)
        {
            var gain = ClipProcessor.FadeGain(frame, total, 50, 0);
            Assert.True(gain >= previous, $"Fade went backwards at frame {frame}.");
            previous = gain;
        }
    }

    [Fact]
    public void NoFadeMeansNoChange()
    {
        Assert.Equal(1, ClipProcessor.FadeGain(0, 100, 0, 0), 9);
        Assert.Equal(1, ClipProcessor.FadeGain(99, 100, 0, 0), 9);
    }

    [Fact]
    public void FadesThatWouldOverlapAreScaledToFit()
    {
        // A 100 ms take with 80 ms of fade at each end: without scaling the two ramps would
        // overlap in the middle and multiply down to near-silence.
        var options = new ExportOptions { FadeInMs = 80, FadeOutMs = 80 };
        var (fadeIn, fadeOut) = ClipProcessor.FadeFrames(totalFrames: 4800, sampleRate: 48_000, options);

        Assert.True(fadeIn + fadeOut <= 4800);
        Assert.Equal(fadeIn, fadeOut);
    }

    [Fact]
    public void FadeFramesConvertMillisecondsAtTheClipRate()
    {
        var options = new ExportOptions { FadeInMs = 5, FadeOutMs = 10 };
        var (fadeIn, fadeOut) = ClipProcessor.FadeFrames(totalFrames: 48_000, sampleRate: 48_000, options);

        Assert.Equal(240, fadeIn);
        Assert.Equal(480, fadeOut);
    }

    [Fact]
    public void AnEmptyClipHasNoFades()
    {
        var options = new ExportOptions { FadeInMs = 5, FadeOutMs = 5 };
        var (fadeIn, fadeOut) = ClipProcessor.FadeFrames(0, 48_000, options);

        Assert.Equal(0, fadeIn);
        Assert.Equal(0, fadeOut);
    }

    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(0.1, -20.0)]
    public void PeakDbConvertsAmplitudeToDecibels(float peak, double expected) =>
        Assert.Equal(expected, ClipProcessor.PeakDb(peak), 3);

    [Fact]
    public void SilentPeakHasNoDecibelValue() =>
        Assert.True(double.IsNegativeInfinity(ClipProcessor.PeakDb(0)));

    [Fact]
    public void RmsDbUsesMeanSquare()
    {
        // Four samples at amplitude 0.5: mean square 0.25, which is -6.02 dBFS.
        Assert.Equal(-6.0206, ClipProcessor.RmsDb(4 * 0.25, 4), 3);
    }

    [Fact]
    public void RmsOfSilenceHasNoDecibelValue()
    {
        Assert.True(double.IsNegativeInfinity(ClipProcessor.RmsDb(0, 100)));
        Assert.True(double.IsNegativeInfinity(ClipProcessor.RmsDb(1, 0)));
    }

    [Fact]
    public void PassThroughIsTheDefaultAndChangesNothing()
    {
        Assert.True(new ExportOptions().IsPassThrough);
        Assert.True(ExportOptions.PassThrough.IsPassThrough);
        Assert.False(new ExportOptions { FadeInMs = 1 }.IsPassThrough);
        Assert.False(new ExportOptions { Normalize = NormalizeMode.Peak }.IsPassThrough);
    }
}
