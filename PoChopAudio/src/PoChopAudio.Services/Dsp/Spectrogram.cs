namespace PoChopAudio.Services.Dsp;

/// <summary>
/// A finished spectrogram: <see cref="Columns"/> time slices, each holding <see cref="Bins"/>
/// magnitudes normalised to 0..1, laid out row-major so a drawing surface can upload it directly.
/// </summary>
/// <param name="Magnitudes">
/// <c>Columns * Bins</c> values in 0..1, column-major: <c>Magnitudes[(column * Bins) + bin]</c>.
/// Bin 0 is the lowest frequency.
/// </param>
public sealed record SpectrogramData(
    int Columns,
    int Bins,
    float[] Magnitudes,
    double DurationSeconds,
    double MinFrequencyHz,
    double MaxFrequencyHz,
    double FloorDb,
    double CeilingDb)
{
    public float At(int column, int bin) => Magnitudes[(column * Bins) + bin];
}

/// <summary>
/// Turns mono samples into a drawable time/frequency grid.
///
/// <para>
/// Two choices here are worth knowing. **Bins are spaced logarithmically**, because an ear hears
/// octaves and a linear axis spends four fifths of its height on the 5-20 kHz range where a spoken
/// take carries almost nothing. **Magnitudes are normalised against a fixed dB window** rather than
/// against the loudest bin present, so two recordings of the same voice look the same rather than
/// each being auto-levelled into looking equally busy.
/// </para>
/// </summary>
public static class Spectrogram
{
    /// <summary>Quietest magnitude drawn. Below this everything is background.</summary>
    public const double DefaultFloorDb = -90;

    /// <summary>Loudest magnitude drawn, above which the colour ramp saturates.</summary>
    public const double DefaultCeilingDb = -10;

    /// <summary>Lowest frequency on the axis. Below this is rumble, not content.</summary>
    public const double DefaultMinHz = 40;

    /// <summary>
    /// Builds the grid.
    /// </summary>
    /// <param name="mono">Mono samples. A stereo source must be downmixed before calling.</param>
    /// <param name="sampleRate">Samples per second, used to place the frequency axis.</param>
    /// <param name="columns">How many time slices to produce. One per output pixel column is ideal.</param>
    /// <param name="bins">How many frequency rows to produce.</param>
    public static SpectrogramData Build(
        IReadOnlyList<float> mono,
        int sampleRate,
        int columns,
        int bins,
        double floorDb = DefaultFloorDb,
        double ceilingDb = DefaultCeilingDb)
    {
        ArgumentNullException.ThrowIfNull(mono);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(bins, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);

        var duration = mono.Count / (double)sampleRate;
        var maxHz = sampleRate / 2.0;
        var magnitudes = new float[columns * bins];

        if (mono.Count < 4 || ceilingDb <= floorDb)
        {
            return new SpectrogramData(columns, bins, magnitudes, duration, DefaultMinHz, maxHz, floorDb, ceilingDb);
        }

        // One window per column, sized so the windows tile the recording. Clamped at both ends:
        // too small and the low bins have no resolution, too large and a short take yields one
        // column of mush.
        var idealWindow = Math.Max(64, mono.Count / Math.Max(1, columns) * 2);
        var windowSize = Fft.FloorPowerOfTwo(Math.Clamp(idealWindow, 64, 4096));
        windowSize = Math.Min(windowSize, Fft.FloorPowerOfTwo(mono.Count));

        var window = new double[windowSize];
        Fft.HannWindow(window);

        var real = new double[windowSize];
        var imaginary = new double[windowSize];
        var spectrum = new double[(windowSize / 2) + 1];

        // Log-spaced bin edges, computed once and reused for every column.
        var minHz = Math.Min(DefaultMinHz, maxHz / 2);
        var edges = new double[bins + 1];
        for (var b = 0; b <= bins; b++)
        {
            edges[b] = minHz * Math.Pow(maxHz / minHz, b / (double)bins);
        }

        var hzPerBin = sampleRate / (double)windowSize;
        var range = ceilingDb - floorDb;

        for (var column = 0; column < columns; column++)
        {
            // Centre each window on its column so the first and last columns are not half empty.
            var centre = (long)((column + 0.5) / columns * mono.Count);
            var start = (int)Math.Clamp(centre - (windowSize / 2), 0, Math.Max(0, mono.Count - windowSize));

            for (var i = 0; i < windowSize; i++)
            {
                var index = start + i;
                real[i] = index < mono.Count ? mono[index] * window[i] : 0;
                imaginary[i] = 0;
            }

            Fft.Forward(real, imaginary);

            // Single-sided magnitude, scaled so a full-scale sine reads near 0 dB.
            for (var k = 0; k < spectrum.Length; k++)
            {
                var magnitude = Math.Sqrt((real[k] * real[k]) + (imaginary[k] * imaginary[k]));
                spectrum[k] = magnitude * 2.0 / windowSize;
            }

            for (var b = 0; b < bins; b++)
            {
                var lowBin = (int)Math.Floor(edges[b] / hzPerBin);
                var highBin = (int)Math.Ceiling(edges[b + 1] / hzPerBin);

                lowBin = Math.Clamp(lowBin, 0, spectrum.Length - 1);
                highBin = Math.Clamp(Math.Max(highBin, lowBin + 1), 1, spectrum.Length);

                // Peak rather than mean across the band: a narrow tone inside a wide high-frequency
                // band would otherwise be averaged into invisibility by its silent neighbours.
                var peak = 0.0;
                for (var k = lowBin; k < highBin; k++)
                {
                    peak = Math.Max(peak, spectrum[k]);
                }

                var db = peak > 1e-12 ? 20.0 * Math.Log10(peak) : floorDb;
                magnitudes[(column * bins) + b] = (float)Math.Clamp((db - floorDb) / range, 0.0, 1.0);
            }
        }

        return new SpectrogramData(columns, bins, magnitudes, duration, minHz, maxHz, floorDb, ceilingDb);
    }
}
