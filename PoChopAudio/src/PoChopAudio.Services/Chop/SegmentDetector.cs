namespace PoChopAudio.Services.Chop;

/// <summary>
/// Splits a recording of repeated takes into one clip per take.
///
/// The recording is reduced to a per-frame loudness envelope (<see cref="FrameMs"/> frames).
/// Anything at or above a loudness gate counts as sound; quiet stretches longer than
/// <see cref="ChopOptions.MinGapMs"/> separate takes. The gate is swept rather than guessed:
/// whichever gate value yields the expected number of takes over the widest run of candidate
/// gates is the one the recording actually supports.
/// </summary>
public static class SegmentDetector
{
    public const double FrameMs = 10;

    private const double SilenceDb = -100;
    private const double GateStepDb = 0.5;

    /// <summary>How far below the gate a take's attack and decay are still followed outward.</summary>
    private const double TailDropDb = 9;

    /// <summary>Below this much difference between the loudest and quietest moment there is no split to make.</summary>
    private const double MinContrastDb = 6;

    public static AnalysisResult Detect(AudioEnvelope envelope, ChopOptions options)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(options);

        if (envelope.PeakDb - envelope.NoiseFloorDb < MinContrastDb)
        {
            return Empty(envelope, "This recording is either silent or has no quiet gaps, so there is nothing to split.");
        }

        var expected = Math.Max(1, options.ExpectedSegments);
        var minFrames = Math.Max(1, (int)Math.Round(options.MinSegmentMs / FrameMs));
        var gapFrames = Math.Max(1, (int)Math.Round(options.MinGapMs / FrameMs));

        var (gateDb, warning) = options.ThresholdDb is { } fixedGate
            ? (fixedGate, null)
            : ChooseGate(envelope, expected, minFrames, gapFrames);

        var runs = FindRuns(envelope.FrameDb, gateDb, minFrames, gapFrames);

        if (runs.Count > expected)
        {
            // More runs than takes: the extras are marker pulses or stray noise. Keep the longest.
            runs = [.. runs.OrderByDescending(r => r.End - r.Start).Take(expected).OrderBy(r => r.Start)];
        }
        else if (runs.Count < expected)
        {
            warning ??= $"Found {runs.Count} sound{(runs.Count == 1 ? "" : "s")}, expected {expected}. " +
                        "Try a lower threshold, a shorter minimum length, or a shorter minimum gap.";
        }

        var segments = BuildSegments(envelope, runs, gateDb, options.PadMs);

        return new AnalysisResult(
            JobId: default,
            DurationSeconds: envelope.DurationSeconds,
            ThresholdDb: Math.Round(gateDb, 2),
            NoiseFloorDb: Math.Round(envelope.NoiseFloorDb, 2),
            PeakDb: Math.Round(envelope.PeakDb, 2),
            Segments: segments,
            Warning: warning);
    }

    private static AnalysisResult Empty(AudioEnvelope envelope, string warning) => new(
        JobId: default,
        DurationSeconds: envelope.DurationSeconds,
        ThresholdDb: Math.Round(envelope.NoiseFloorDb + MinContrastDb / 2, 2),
        NoiseFloorDb: Math.Round(envelope.NoiseFloorDb, 2),
        PeakDb: Math.Round(envelope.PeakDb, 2),
        Segments: [],
        Warning: warning);

    /// <summary>
    /// Sweeps every plausible gate and keeps the one sitting in the middle of the widest band of
    /// gates that all agree on the expected take count. A wide band means the split is insensitive
    /// to the exact number, which is the definition of a clean split.
    /// </summary>
    private static (double GateDb, string? Warning) ChooseGate(
        AudioEnvelope envelope,
        int expected,
        int minFrames,
        int gapFrames)
    {
        var low = envelope.NoiseFloorDb + 3;
        var high = envelope.PeakDb - 3;

        double bestGate = 0;
        var bestWidth = 0;
        var bandStart = double.NaN;
        var bandWidth = 0;
        var previousCount = -1;

        for (var gate = low; gate <= high; gate += GateStepDb)
        {
            var count = FindRuns(envelope.FrameDb, gate, minFrames, gapFrames).Count;

            if (count == expected && count == previousCount)
            {
                bandWidth++;
            }
            else
            {
                bandStart = gate;
                bandWidth = 1;
            }

            if (count == expected && bandWidth > bestWidth)
            {
                bestWidth = bandWidth;
                bestGate = bandStart + (bandWidth - 1) * GateStepDb / 2;
            }

            previousCount = count;
        }

        if (bestWidth > 0)
        {
            return (bestGate, null);
        }

        // No gate produces the expected count. Fall back to a gate well clear of the noise floor and
        // let the caller's longest-runs rule sort out any extras.
        var fallback = Math.Min(envelope.NoiseFloorDb + 12, envelope.PeakDb - 25);
        return (Math.Clamp(fallback, low, high), null);
    }

    /// <summary>Contiguous frame ranges above the gate, merged across short gaps and length-filtered.</summary>
    private static List<FrameRun> FindRuns(IReadOnlyList<double> frameDb, double gateDb, int minFrames, int gapFrames)
    {
        var runs = new List<FrameRun>();
        var start = -1;
        var quiet = 0;

        for (var i = 0; i < frameDb.Count; i++)
        {
            if (frameDb[i] >= gateDb)
            {
                if (start < 0)
                {
                    start = i;
                }

                quiet = 0;
                continue;
            }

            if (start < 0)
            {
                continue;
            }

            quiet++;

            if (quiet >= gapFrames)
            {
                runs.Add(new FrameRun(start, i - quiet + 1));
                start = -1;
                quiet = 0;
            }
        }

        if (start >= 0)
        {
            runs.Add(new FrameRun(start, frameDb.Count - quiet));
        }

        runs.RemoveAll(r => r.End - r.Start < minFrames);
        return runs;
    }

    /// <summary>
    /// Turns frame runs into timed segments: follows each take's attack and decay outward below the
    /// gate, adds padding, then trims so neighbours never overlap or run past the recording.
    /// </summary>
    private static List<ChopSegment> BuildSegments(
        AudioEnvelope envelope,
        List<FrameRun> runs,
        double gateDb,
        double padMs)
    {
        var frameDb = envelope.FrameDb;
        var tailDb = Math.Max(gateDb - TailDropDb, envelope.NoiseFloorDb + 1);
        var pad = padMs / 1000d;
        var segments = new List<ChopSegment>(runs.Count);

        for (var i = 0; i < runs.Count; i++)
        {
            var (start, end) = runs[i];

            while (start > 0 && frameDb[start - 1] >= tailDb)
            {
                start--;
            }

            while (end < frameDb.Count && frameDb[end] >= tailDb)
            {
                end++;
            }

            var startSeconds = start * FrameMs / 1000d - pad;
            var endSeconds = end * FrameMs / 1000d + pad;

            // Never eat into a neighbouring take.
            var floor = i == 0 ? 0 : Midpoint(runs[i - 1].End, runs[i].Start);
            var ceiling = i == runs.Count - 1 ? envelope.DurationSeconds : Midpoint(runs[i].End, runs[i + 1].Start);

            startSeconds = Math.Clamp(startSeconds, floor, envelope.DurationSeconds);
            endSeconds = Math.Clamp(endSeconds, startSeconds, ceiling);

            var peak = SilenceDb;
            for (var f = runs[i].Start; f < runs[i].End; f++)
            {
                peak = Math.Max(peak, frameDb[f]);
            }

            segments.Add(new ChopSegment(
                Index: i + 1,
                StartSeconds: Math.Round(startSeconds, 4),
                EndSeconds: Math.Round(endSeconds, 4),
                PeakDb: Math.Round(peak, 2)));
        }

        return segments;
    }

    private static double Midpoint(int endFrame, int startFrame) => (endFrame + startFrame) / 2d * FrameMs / 1000d;

    private readonly record struct FrameRun(int Start, int End);
}
