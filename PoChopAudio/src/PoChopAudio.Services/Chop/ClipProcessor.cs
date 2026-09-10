namespace PoChopAudio.Services.Chop;

/// <summary>What a measuring pass found, and what the writing pass should therefore do.</summary>
/// <param name="MeasuredDb">Level found by the chosen mode, in dBFS or LUFS. -Infinity for silence.</param>
/// <param name="GainDb">Gain the writing pass will apply, after every guard below.</param>
/// <param name="CeilingLimited">True when the ceiling, not the target, decided the gain.</param>
/// <param name="GainCapped">True when <see cref="ExportLimits.MaxGainDb"/> decided the gain.</param>
/// <param name="Silent">True when the clip was too quiet to normalize and was left alone.</param>
public readonly record struct ClipGain(
    double MeasuredDb,
    double GainDb,
    bool CeilingLimited,
    bool GainCapped,
    bool Silent)
{
    public double Linear => Math.Pow(10, GainDb / 20);

    public bool IsUnity => Math.Abs(GainDb) < 1e-9;

    public static ClipGain Unity => new(double.NegativeInfinity, 0, false, false, false);
}

/// <summary>
/// The export maths, with no file I/O in sight so it can be tested on arrays. Deciding the gain is
/// separate from applying it because normalization needs the whole clip measured before the first
/// sample can be written.
/// </summary>
public static class ClipProcessor
{
    /// <summary>
    /// Turns a measurement into the gain that will actually be applied. Three things can hold the
    /// gain back, in this order: the clip is silence, the ceiling would be breached, or the gain
    /// would exceed <see cref="ExportLimits.MaxGainDb"/>.
    /// </summary>
    /// <param name="measuredDb">Level from the chosen mode, in dBFS or LUFS.</param>
    /// <param name="peakDb">Sample peak of the clip in dBFS, needed to honour the ceiling.</param>
    public static ClipGain DecideGain(double measuredDb, double peakDb, ExportOptions options)
    {
        if (options.Normalize is NormalizeMode.None)
        {
            return ClipGain.Unity;
        }

        // Nothing to normalize: an all-zero or near-silent clip would need infinite gain, and
        // scaling room tone up to a speech target is never what the user meant.
        if (double.IsNegativeInfinity(measuredDb) || measuredDb <= ExportLimits.SilenceFloorDb)
        {
            return new ClipGain(measuredDb, 0, false, false, Silent: true);
        }

        var wanted = options.TargetDb - measuredDb;

        var capped = false;
        if (wanted > ExportLimits.MaxGainDb)
        {
            wanted = ExportLimits.MaxGainDb;
            capped = true;
        }

        // A loudness target must never be met by clipping: if the gain would push the loudest
        // sample past the ceiling, the ceiling wins and the clip lands quieter than asked.
        var ceilingLimited = false;
        if (!double.IsNegativeInfinity(peakDb))
        {
            var headroom = options.CeilingDb - peakDb;
            if (wanted > headroom)
            {
                wanted = headroom;
                ceilingLimited = true;
                capped = false;
            }
        }

        return new ClipGain(measuredDb, wanted, ceilingLimited, capped, Silent: false);
    }

    /// <summary>
    /// Raised-cosine fade gain for a frame. Smooth at both ends, unlike a linear ramp, so a 5 ms
    /// fade removes an edge click without leaving a corner of its own.
    /// </summary>
    /// <param name="frame">Frame index within the clip.</param>
    /// <param name="totalFrames">Length of the clip in frames.</param>
    public static double FadeGain(long frame, long totalFrames, long fadeInFrames, long fadeOutFrames)
    {
        double gain = 1;

        if (fadeInFrames > 0 && frame < fadeInFrames)
        {
            gain *= RaisedCosine((frame + 0.5) / fadeInFrames);
        }

        if (fadeOutFrames > 0)
        {
            var fromEnd = totalFrames - 1 - frame;
            if (fromEnd < fadeOutFrames)
            {
                gain *= RaisedCosine((fromEnd + 0.5) / fadeOutFrames);
            }
        }

        return gain;
    }

    /// <summary>
    /// Converts the fade knobs into frame counts. A clip shorter than the two fades together would
    /// otherwise have its head and tail ramps overlap and cancel, so both are scaled down to fit.
    /// </summary>
    public static (long FadeIn, long FadeOut) FadeFrames(long totalFrames, int sampleRate, ExportOptions options)
    {
        if (totalFrames <= 0)
        {
            return (0, 0);
        }

        var fadeIn = (long)Math.Round(Math.Max(0, options.FadeInMs) * sampleRate / 1000d);
        var fadeOut = (long)Math.Round(Math.Max(0, options.FadeOutMs) * sampleRate / 1000d);

        fadeIn = Math.Min(fadeIn, totalFrames);
        fadeOut = Math.Min(fadeOut, totalFrames);

        var combined = fadeIn + fadeOut;
        if (combined > totalFrames && combined > 0)
        {
            var scale = (double)totalFrames / combined;
            fadeIn = (long)(fadeIn * scale);
            fadeOut = (long)(fadeOut * scale);
        }

        return (fadeIn, fadeOut);
    }

    /// <summary>Sample peak of an interleaved buffer as dBFS, or -Infinity when it is all zeroes.</summary>
    public static double PeakDb(float peak) =>
        peak <= 0 ? double.NegativeInfinity : 20 * Math.Log10(peak);

    /// <summary>Mean-square level as dBFS, or -Infinity when there is nothing above zero.</summary>
    public static double RmsDb(double sumOfSquares, long sampleCount) =>
        sampleCount <= 0 || sumOfSquares <= 0
            ? double.NegativeInfinity
            : 10 * Math.Log10(sumOfSquares / sampleCount);

    private static double RaisedCosine(double position) =>
        0.5 * (1 - Math.Cos(Math.PI * Math.Clamp(position, 0, 1)));
}
