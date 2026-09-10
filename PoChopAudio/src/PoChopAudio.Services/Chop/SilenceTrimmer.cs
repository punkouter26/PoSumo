namespace PoChopAudio.Services.Chop;

/// <summary>
/// Trims leading and trailing silence from a decoded audio envelope. The "silence" threshold is
/// expressed in dBFS below the recording's noise floor; a head and tail that both stay below
/// the threshold for at least <see cref="MinRunFrames"/> frames get cropped.
/// </summary>
public static class SilenceTrimmer
{
    /// <summary>Number of consecutive below-threshold frames needed to count as a real silence run.</summary>
    public const int MinRunFrames = 5;

    /// <summary>How much quieter than the noise floor a frame must be to count as silence.</summary>
    public const double SilenceMarginDb = 6;

    /// <summary>Returns the (start, end) frame indices of the trimmed audio. Both are inclusive.</summary>
    public static (int StartFrame, int EndFrame) Trim(AudioEnvelope envelope, ChopOptions options)
    {
        if (options.TrimSilenceMs <= 0 || envelope.FrameDb.Count == 0)
        {
            return (0, envelope.FrameDb.Count - 1);
        }

        var threshold = envelope.NoiseFloorDb - SilenceMarginDb;

        var first = FindFirstLoud(envelope.FrameDb, threshold, options.TrimSilenceMs);
        if (first < 0)
        {
            return (0, envelope.FrameDb.Count - 1);
        }

        var last = FindLastLoud(envelope.FrameDb, threshold, options.TrimSilenceMs);
        if (last < first)
        {
            return (0, envelope.FrameDb.Count - 1);
        }

        return (first, last);
    }

    private static int FindFirstLoud(IReadOnlyList<double> frameDb, double threshold, double minRunMs)
    {
        var requiredFrames = Math.Max(MinRunFrames, (int)Math.Ceiling(minRunMs / SegmentDetector.FrameMs));
        var run = 0;
        for (var i = 0; i < frameDb.Count; i++)
        {
            if (frameDb[i] > threshold)
            {
                run++;
                if (run >= requiredFrames)
                {
                    return i - run + 1;
                }
            }
            else
            {
                run = 0;
            }
        }

        return -1;
    }

    private static int FindLastLoud(IReadOnlyList<double> frameDb, double threshold, double minRunMs)
    {
        var requiredFrames = Math.Max(MinRunFrames, (int)Math.Ceiling(minRunMs / SegmentDetector.FrameMs));
        var run = 0;
        for (var i = frameDb.Count - 1; i >= 0; i--)
        {
            if (frameDb[i] > threshold)
            {
                run++;
                if (run >= requiredFrames)
                {
                    return i + run - 1;
                }
            }
            else
            {
                run = 0;
            }
        }

        return -1;
    }
}
