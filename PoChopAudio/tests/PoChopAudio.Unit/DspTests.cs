using PoChopAudio.Services.Dsp;

namespace PoChopAudio.Unit;

public sealed class FftTests
{
    [Fact]
    public void Forward_OfADcSignal_PutsAllEnergyInBinZero()
    {
        var real = new double[16];
        var imaginary = new double[16];
        Array.Fill(real, 1.0);

        Fft.Forward(real, imaginary);

        Assert.Equal(16.0, real[0], 6);
        for (var k = 1; k < 16; k++)
        {
            Assert.Equal(0.0, Math.Sqrt((real[k] * real[k]) + (imaginary[k] * imaginary[k])), 6);
        }
    }

    [Fact]
    public void Forward_OfASineOnABinCentre_PeaksInThatBin()
    {
        const int n = 64;
        const int bin = 5;
        var real = new double[n];
        var imaginary = new double[n];

        for (var i = 0; i < n; i++)
        {
            real[i] = Math.Sin(2.0 * Math.PI * bin * i / n);
        }

        Fft.Forward(real, imaginary);

        var peak = 0;
        var peakMagnitude = 0.0;
        for (var k = 1; k < n / 2; k++)
        {
            var magnitude = Math.Sqrt((real[k] * real[k]) + (imaginary[k] * imaginary[k]));
            if (magnitude > peakMagnitude)
            {
                peakMagnitude = magnitude;
                peak = k;
            }
        }

        Assert.Equal(bin, peak);
    }

    [Fact]
    public void Forward_RejectsALengthThatIsNotAPowerOfTwo()
    {
        Assert.Throws<ArgumentException>(() => Fft.Forward(new double[6], new double[6]));
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(1000, 512)]
    [InlineData(4096, 4096)]
    public void FloorPowerOfTwo_RoundsDown(int input, int expected)
    {
        Assert.Equal(expected, Fft.FloorPowerOfTwo(input));
    }

    [Fact]
    public void HannWindow_IsZeroAtTheStartAndPeaksInTheMiddle()
    {
        var window = new double[64];
        Fft.HannWindow(window);

        Assert.Equal(0.0, window[0], 9);
        Assert.Equal(1.0, window[32], 9);
    }
}

public sealed class SpectrogramTests
{
    private static float[] Sine(double hz, int sampleRate, double seconds)
    {
        var samples = new float[(int)(sampleRate * seconds)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)Math.Sin(2.0 * Math.PI * hz * i / sampleRate);
        }

        return samples;
    }

    [Fact]
    public void Build_PutsTheEnergyOfATone_NearItsOwnFrequency()
    {
        const int sampleRate = 44100;
        const int bins = 64;
        var data = Spectrogram.Build(Sine(1000, sampleRate, 1.0), sampleRate, columns: 32, bins: bins);

        // Where 1 kHz falls on the log axis this grid uses.
        var expected = (int)(Math.Log(1000 / data.MinFrequencyHz) / Math.Log(data.MaxFrequencyHz / data.MinFrequencyHz) * bins);

        var loudest = 0;
        var loudestValue = 0f;
        for (var b = 0; b < bins; b++)
        {
            var value = data.At(16, b);
            if (value > loudestValue)
            {
                loudestValue = value;
                loudest = b;
            }
        }

        Assert.InRange(loudest, expected - 2, expected + 2);
        Assert.True(loudestValue > 0.5f, $"the tone should be well above the floor, was {loudestValue}");
    }

    [Fact]
    public void Build_OfSilence_IsAllFloor()
    {
        var data = Spectrogram.Build(new float[44100], 44100, columns: 8, bins: 16);

        Assert.All(data.Magnitudes, m => Assert.Equal(0f, m));
    }

    [Fact]
    public void Build_NormalisesAgainstAFixedWindow_NotTheLoudestBin()
    {
        // The same tone 20 dB quieter must read lower, not be auto-levelled back to the same value.
        const int sampleRate = 44100;
        var loud = Sine(1000, sampleRate, 0.5);
        var quiet = loud.Select(s => s * 0.1f).ToArray();

        var loudData = Spectrogram.Build(loud, sampleRate, 8, 32);
        var quietData = Spectrogram.Build(quiet, sampleRate, 8, 32);

        Assert.True(loudData.Magnitudes.Max() > quietData.Magnitudes.Max() + 0.15f);
    }
}

public sealed class CueSynthTests
{
    [Theory]
    [InlineData(AudioCue.Tick)]
    [InlineData(AudioCue.Failure)]
    public void Render_ProducesAudioWithinTheRequestedAmplitude(AudioCue cue)
    {
        var samples = CueSynth.Render(cue, 44100, 0.2f);

        Assert.NotEmpty(samples);
        Assert.True(samples.Max(Math.Abs) <= 0.2f + 1e-4f, "a cue must never exceed the amplitude asked for");
        Assert.True(samples.Max(Math.Abs) > 0.05f, "and must be audible");
    }

    [Fact]
    public void Render_MakesTheAuditionBlipsQuieterThanTheCountIn()
    {
        // A blip marking the edge of a clip plays over the audio being judged, so it is deliberately
        // held below the count-in ticks, which play against silence.
        var tick = CueSynth.Render(AudioCue.Tick, 44100).Max(Math.Abs);
        var start = CueSynth.Render(AudioCue.ClipStart, 44100).Max(Math.Abs);
        var end = CueSynth.Render(AudioCue.ClipEnd, 44100).Max(Math.Abs);

        Assert.True(start < tick);
        Assert.True(end < tick);
    }

    [Fact]
    public void Render_DecaysToSilenceByTheEnd()
    {
        var samples = CueSynth.Render(AudioCue.Tick, 44100);
        var tail = samples[^32..];

        Assert.True(tail.Max(Math.Abs) < 0.01f, "a ringing tail turns a count-in into a chord");
    }

    [Fact]
    public void Render_ClampsAnAmplitudeAboveOne()
    {
        var samples = CueSynth.Render(AudioCue.Tick, 44100, 5f);

        Assert.True(samples.Max(Math.Abs) <= 1f);
    }

    [Fact]
    public void ReferenceTone_HitsItsStatedLevel()
    {
        var samples = CueSynth.ReferenceTone(48000, seconds: 1.0, frequencyHz: 1000, dbfs: -18);

        var expected = Math.Pow(10, -18 / 20.0);
        Assert.Equal(expected, samples.Max(Math.Abs), 2);
    }
}

public sealed class ParticleFieldTests
{
    [Fact]
    public void Emit_NeverExceedsCapacity()
    {
        var field = new ParticleField(10, seed: 1);

        var created = field.Emit(0, 0, 25);

        Assert.Equal(10, created);
        Assert.Equal(10, field.AliveCount);
    }

    [Fact]
    public void Step_ClampsAnEnormousDelta()
    {
        var field = new ParticleField(1, seed: 4);
        field.Emit(0, 0, 1, speed: 100, spreadRadians: 0, lifetimeSeconds: 100f);
        field.Drag = 1f;

        // A tab-out or a long DSP pass; integrating five seconds in one go would fling it away.
        field.Step(5f);

        Assert.True(Math.Abs(field.Alive[0].Y) < 100, "a huge frame delta must be clamped, not integrated");
    }
}
