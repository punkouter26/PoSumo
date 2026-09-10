using PoChopAudio.Services.Chop;

namespace PoChopAudio.Unit;

/// <summary>
/// Checks the BS.1770-4 meter against the property the standard is built around: a dual-mono
/// 1 kHz sine reads its own peak amplitude in dBFS as its LUFS value. That is what the -0.691
/// offset exists to arrange, so hitting it means the K-weighting filters and the gating are both
/// doing what they should. EBU Tech 3341 case 1 is exactly this signal at -23 dBFS.
/// </summary>
public sealed class LoudnessMeterTests
{
    private const int SampleRate = 48_000;

    [Theory]
    [InlineData(-23.0)]
    [InlineData(-40.0)]
    public void StereoSineReadsItsOwnLevelInLufs(double levelDb)
    {
        var measured = MeasureSine(levelDb, channels: 2, seconds: 3);

        Assert.InRange(measured, levelDb - 0.2, levelDb + 0.2);
    }

    [Fact]
    public void MonoIsMeasuredAsOneChannelSoItReadsThreeLuLower()
    {
        // Spec-correct: channel weights are 1.0 and mono is not upmixed, so the same tone in one
        // channel sums to half the power of two. Documented because it means a mono take gets 3 dB
        // more gain than a stereo one for the same LUFS target.
        var stereo = MeasureSine(-23, channels: 2, seconds: 3);
        var mono = MeasureSine(-23, channels: 1, seconds: 3);

        Assert.InRange(stereo - mono, 2.8, 3.2);
    }

    [Fact]
    public void SilenceHasNoLoudness()
    {
        var accumulator = new LoudnessMeter.Accumulator(SampleRate, 2);
        accumulator.Add(new float[SampleRate * 2], SampleRate * 2);

        Assert.True(double.IsNegativeInfinity(accumulator.Integrated()));
    }

    [Fact]
    public void ATakeShorterThanOneBlockIsStillMeasured()
    {
        // 200 ms is half a gating block. The meter must fall back to measuring the whole clip
        // rather than returning nothing, because a short bark is a perfectly normal take here.
        var measured = MeasureSine(-23, channels: 2, seconds: 0.2);

        Assert.False(double.IsNegativeInfinity(measured));
        Assert.InRange(measured, -23.5, -22.5);
    }

    [Fact]
    public void QuietPassagesBelowTheRelativeGateDoNotDragTheAnswerDown()
    {
        // Two seconds of tone followed by two seconds of near-silence should measure close to the
        // tone alone: that is the entire point of gating.
        var tone = Sine(-23, channels: 2, seconds: 2);
        var quiet = Sine(-75, channels: 2, seconds: 2);

        var accumulator = new LoudnessMeter.Accumulator(SampleRate, 2);
        accumulator.Add(tone, tone.Length);
        accumulator.Add(quiet, quiet.Length);

        Assert.InRange(accumulator.Integrated(), -23.4, -22.6);
    }

    [Fact]
    public void TheRlbStageAttenuatesSubsonicRumble()
    {
        // Checks the filter build rather than the gating: the RLB high-pass must discount a 20 Hz
        // rumble, or "loudness" would partly be measuring energy nobody can hear. The K-weighting
        // response at 20 Hz is -13.3 dB, so the gap against 1 kHz lands just under 14 dB.
        var rumble = MeasureSine(-23, channels: 2, seconds: 3, frequency: 20);
        var reference = MeasureSine(-23, channels: 2, seconds: 3, frequency: 1000);

        Assert.InRange(reference - rumble, 13, 15);
    }

    private static double MeasureSine(double levelDb, int channels, double seconds, double frequency = 1000)
    {
        var buffer = Sine(levelDb, channels, seconds, frequency);
        var accumulator = new LoudnessMeter.Accumulator(SampleRate, channels);
        accumulator.Add(buffer, buffer.Length);
        return accumulator.Integrated();
    }

    private static float[] Sine(double levelDb, int channels, double seconds, double frequency = 1000)
    {
        var frames = (int)(SampleRate * seconds);
        var amplitude = Math.Pow(10, levelDb / 20);
        var buffer = new float[frames * channels];

        for (var frame = 0; frame < frames; frame++)
        {
            var value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * frame / SampleRate));
            for (var c = 0; c < channels; c++)
            {
                buffer[(frame * channels) + c] = value;
            }
        }

        return buffer;
    }
}
