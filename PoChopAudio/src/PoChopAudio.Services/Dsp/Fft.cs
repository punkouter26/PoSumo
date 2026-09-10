namespace PoChopAudio.Services.Dsp;

/// <summary>
/// In-place iterative radix-2 Cooley-Tukey FFT.
///
/// <para>
/// Deliberately not a dependency. The only transform this app needs is a power-of-two forward FFT
/// of a few thousand real samples per spectrogram column, which is about forty lines; pulling in a
/// numerics package for that would add a shipped assembly to a self-contained app to save nothing.
/// </para>
/// <para>
/// The twiddle factors are recomputed per call rather than cached in a static table. A table would
/// have to be keyed by size and guarded for thread safety, and the spectrogram builds its columns
/// on one background thread — the sines are not what costs.
/// </para>
/// </summary>
public static class Fft
{
    /// <summary>
    /// Transforms <paramref name="real"/> and <paramref name="imaginary"/> in place. Both must be
    /// the same length and that length must be a power of two.
    /// </summary>
    public static void Forward(Span<double> real, Span<double> imaginary)
    {
        var n = real.Length;

        if (n != imaginary.Length)
        {
            throw new ArgumentException("The real and imaginary parts must be the same length.", nameof(imaginary));
        }

        if (n <= 1)
        {
            return;
        }

        if ((n & (n - 1)) != 0)
        {
            throw new ArgumentException($"Length must be a power of two, but was {n}.", nameof(real));
        }

        // Bit-reversal permutation. Without it the butterflies below combine the wrong pairs.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;

            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;

            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2.0 * Math.PI / length;
            var wReal = Math.Cos(angle);
            var wImaginary = Math.Sin(angle);

            for (var start = 0; start < n; start += length)
            {
                double currentReal = 1, currentImaginary = 0;

                for (var k = 0; k < length / 2; k++)
                {
                    var evenIndex = start + k;
                    var oddIndex = start + k + (length / 2);

                    var oddReal = (real[oddIndex] * currentReal) - (imaginary[oddIndex] * currentImaginary);
                    var oddImaginary = (real[oddIndex] * currentImaginary) + (imaginary[oddIndex] * currentReal);

                    real[oddIndex] = real[evenIndex] - oddReal;
                    imaginary[oddIndex] = imaginary[evenIndex] - oddImaginary;
                    real[evenIndex] += oddReal;
                    imaginary[evenIndex] += oddImaginary;

                    var nextReal = (currentReal * wReal) - (currentImaginary * wImaginary);
                    currentImaginary = (currentReal * wImaginary) + (currentImaginary * wReal);
                    currentReal = nextReal;
                }
            }
        }
    }

    /// <summary>
    /// Fills <paramref name="window"/> with a periodic Hann window.
    /// <para>
    /// Periodic (<c>/ n</c>) rather than symmetric (<c>/ (n - 1)</c>): the symmetric form is for
    /// filter design, and using it for spectral analysis leaks a little energy into neighbouring
    /// bins because consecutive frames no longer tile the signal evenly.
    /// </para>
    /// </summary>
    public static void HannWindow(Span<double> window)
    {
        var n = window.Length;

        for (var i = 0; i < n; i++)
        {
            window[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / n));
        }
    }

    /// <summary>The largest power of two less than or equal to <paramref name="value"/>, minimum 2.</summary>
    public static int FloorPowerOfTwo(int value)
    {
        if (value < 2)
        {
            return 2;
        }

        var result = 1;
        while (result << 1 <= value)
        {
            result <<= 1;
        }

        return result;
    }
}
