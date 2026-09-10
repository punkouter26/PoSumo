namespace PoChopAudio.Services.Chop;

/// <summary>One direct-form-I biquad section, held per channel so state never leaks between them.</summary>
public sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
{
    private double _x1, _x2, _y1, _y2;

    public double Process(double x)
    {
        var y = (b0 * x) + (b1 * _x1) + (b2 * _x2) - (a1 * _y1) - (a2 * _y2);
        _x2 = _x1;
        _x1 = x;
        _y2 = _y1;
        _y1 = y;
        return y;
    }

    public void Reset() => _x1 = _x2 = _y1 = _y2 = 0;

    /// <summary>
    /// BS.1770 stage 1: the high-frequency shelf standing in for the acoustic effect of a head.
    ///
    /// Built by bilinear-transforming the analog prototype rather than with the RBJ cookbook shelf,
    /// which is a different filter — it lands the poles elsewhere and reads about 0.25 LU low. This
    /// derivation reproduces the coefficient table in BS.1770-4 exactly at 48 kHz and stays correct
    /// at every other rate, which matters because clips keep their source rate.
    /// </summary>
    public static Biquad HighShelf(double fc, double q, double gainDb, int sampleRate)
    {
        var k = Math.Tan(Math.PI * fc / sampleRate);
        var vh = Math.Pow(10, gainDb / 20);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var kk = k * k;

        var a0 = 1 + (k / q) + kk;

        return new Biquad(
            b0: (vh + (vb * k / q) + kk) / a0,
            b1: 2 * (kk - vh) / a0,
            b2: (vh - (vb * k / q) + kk) / a0,
            a1: 2 * (kk - 1) / a0,
            a2: (1 - (k / q) + kk) / a0);
    }

    /// <summary>
    /// BS.1770 stage 2: the RLB high-pass. Its numerator is [1, -2, 1] unnormalized, as the
    /// standard specifies — normalizing it the RBJ way would quietly shave 0.04 dB off every
    /// measurement.
    /// </summary>
    public static Biquad HighPass(double fc, double q, int sampleRate)
    {
        var k = Math.Tan(Math.PI * fc / sampleRate);
        var kk = k * k;
        var a0 = 1 + (k / q) + kk;

        return new Biquad(
            b0: 1,
            b1: -2,
            b2: 1,
            a1: 2 * (kk - 1) / a0,
            a2: (1 - (k / q) + kk) / a0);
    }
}

/// <summary>
/// Integrated loudness per ITU-R BS.1770-4: K-weight every channel, take the mean square over
/// 400 ms blocks overlapping by 75%, drop blocks below the absolute gate (-70 LUFS) and then
/// blocks more than 10 LU below the mean of what survived.
///
/// Two deliberate departures, both forced by what this app exports:
///
/// * A take shorter than one 400 ms block has no gated measurement to make. Rather than refuse,
///   the meter measures the whole clip as a single short block, ungated. Same formula, shorter
///   window, and it is the only honest answer for a 200 ms bark.
/// * Channels are weighted G = 1.0, the standard value for L/R. A mono clip is therefore measured
///   as one channel and reads about 3 LU below the same audio duplicated to stereo. That is
///   spec-correct and matches ffmpeg and pyloudnorm, but it does mean a mono take normalized to
///   -16 LUFS receives 3 dB more gain than a stereo one.
/// </summary>
public static class LoudnessMeter
{
    /// <summary>The offset that makes a 1 kHz sine read its own dBFS value, per BS.1770.</summary>
    public const double AbsoluteOffsetDb = -0.691;

    public const double AbsoluteGateLufs = -70;
    public const double RelativeGateLu = -10;

    public const double BlockMs = 400;
    public const double BlockStepMs = 100;

    // Stage 1: high-frequency shelf approximating the acoustic effect of a head.
    private const double ShelfFrequency = 1681.974450955533;
    private const double ShelfQ = 0.7071752369554196;
    private const double ShelfGainDb = 3.999843853973347;

    // Stage 2: the RLB high-pass.
    private const double HighPassFrequency = 38.13547087602444;
    private const double HighPassQ = 0.5003270373238773;

    /// <summary>Builds the two-stage K-weighting chain for one channel at the given rate.</summary>
    public static Biquad[] CreateKWeighting(int sampleRate) =>
    [
        Biquad.HighShelf(ShelfFrequency, ShelfQ, ShelfGainDb, sampleRate),
        Biquad.HighPass(HighPassFrequency, HighPassQ, sampleRate),
    ];

    /// <summary>
    /// Accumulates K-weighted mean squares one interleaved buffer at a time, so a clip of any
    /// length can be measured without ever being held in memory.
    /// </summary>
    public sealed class Accumulator
    {
        private readonly int _channels;
        private readonly Biquad[][] _filters;
        private readonly int _blockFrames;
        private readonly int _stepFrames;
        private readonly List<double> _blockMeanSquares = [];

        // Ring of per-step squared sums: a 400 ms block is the sum of the last four slots, so the
        // 75% overlap costs four doubles rather than a buffer of the audio.
        private readonly double[] _stepSums;
        private int _stepCursor;
        private int _framesInStep;
        private int _stepsClosed;

        private double _wholeSum;
        private long _wholeFrames;

        public Accumulator(int sampleRate, int channels)
        {
            _channels = Math.Max(1, channels);
            _filters = new Biquad[_channels][];
            for (var c = 0; c < _channels; c++)
            {
                _filters[c] = CreateKWeighting(sampleRate);
            }

            _stepFrames = Math.Max(1, (int)Math.Round(sampleRate * BlockStepMs / 1000d));
            var slots = Math.Max(1, (int)Math.Round(BlockMs / BlockStepMs));
            _blockFrames = _stepFrames * slots;
            _stepSums = new double[slots];
        }

        /// <summary>Feeds one interleaved buffer. <paramref name="count"/> is in samples, not frames.</summary>
        public void Add(float[] interleaved, int count)
        {
            for (var i = 0; i + _channels <= count; i += _channels)
            {
                double frameSum = 0;
                for (var c = 0; c < _channels; c++)
                {
                    var value = (double)interleaved[i + c];
                    foreach (var stage in _filters[c])
                    {
                        value = stage.Process(value);
                    }

                    frameSum += value * value;
                }

                _stepSums[_stepCursor] += frameSum;
                _wholeSum += frameSum;
                _wholeFrames++;

                if (++_framesInStep == _stepFrames)
                {
                    CloseStep();
                }
            }
        }

        private void CloseStep()
        {
            _stepsClosed++;
            if (_stepsClosed >= _stepSums.Length)
            {
                double blockSum = 0;
                foreach (var slot in _stepSums)
                {
                    blockSum += slot;
                }

                _blockMeanSquares.Add(blockSum / _blockFrames);
            }

            _stepCursor = (_stepCursor + 1) % _stepSums.Length;
            _stepSums[_stepCursor] = 0;
            _framesInStep = 0;
        }

        /// <summary>Integrated loudness in LUFS, or negative infinity when there is nothing to measure.</summary>
        public double Integrated()
        {
            if (_blockMeanSquares.Count == 0)
            {
                // Clip shorter than one 400 ms block: measure what there is, ungated.
                return _wholeFrames == 0 || _wholeSum <= 0
                    ? double.NegativeInfinity
                    : AbsoluteOffsetDb + (10 * Math.Log10(_wholeSum / _wholeFrames));
            }

            var aboveAbsolute = new List<double>(_blockMeanSquares.Count);
            foreach (var meanSquare in _blockMeanSquares)
            {
                if (meanSquare > 0 && AbsoluteOffsetDb + (10 * Math.Log10(meanSquare)) > AbsoluteGateLufs)
                {
                    aboveAbsolute.Add(meanSquare);
                }
            }

            if (aboveAbsolute.Count == 0)
            {
                return double.NegativeInfinity;
            }

            var relativeThreshold = AbsoluteOffsetDb + (10 * Math.Log10(Mean(aboveAbsolute))) + RelativeGateLu;

            double gatedSum = 0;
            var gatedCount = 0;
            foreach (var meanSquare in aboveAbsolute)
            {
                if (AbsoluteOffsetDb + (10 * Math.Log10(meanSquare)) > relativeThreshold)
                {
                    gatedSum += meanSquare;
                    gatedCount++;
                }
            }

            // The relative gate can only remove blocks; if it removed every one of them, the
            // absolute-gated mean is still the best available answer.
            return gatedCount == 0
                ? AbsoluteOffsetDb + (10 * Math.Log10(Mean(aboveAbsolute)))
                : AbsoluteOffsetDb + (10 * Math.Log10(gatedSum / gatedCount));
        }

        private static double Mean(List<double> values)
        {
            double sum = 0;
            foreach (var value in values)
            {
                sum += value;
            }

            return sum / values.Count;
        }
    }
}
