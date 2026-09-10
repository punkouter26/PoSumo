using Microsoft.Extensions.Logging;
using PoChopAudio.Services.Dsp;

namespace PoChopAudio.Services.Chop;

/// <summary>
/// The whole chop feature. Upload decodes once to a canonical WAV, analyze re-runs only the cheap
/// tuning step against it, and export renders on demand — so turning a knob never re-decodes.
///
/// <para>
/// Every method is synchronous apart from <see cref="UploadAsync"/>: the DSP here is real work and
/// the caller decides which thread pays for it. From a view model that means <c>Task.Run</c>.
/// </para>
/// </summary>
public sealed class ChopService(ChopJobStore store, ILoggerFactory loggerFactory)
{
    private const string NotFoundMessage =
        "That recording is no longer loaded. Add the file again.";

    private readonly ILogger _logger = ChopLog.CreateLogger(loggerFactory);

    /// <summary>Decodes an upload to canonical WAV inside a fresh job directory.</summary>
    /// <param name="length">Byte count, passed separately because a form stream cannot always report it.</param>
    public async Task<Outcome<UploadResult>> UploadAsync(
        Stream content,
        string fileName,
        long length,
        CancellationToken cancellationToken = default)
    {
        if (length == 0)
        {
            return Outcome<UploadResult>.Empty("The uploaded file is empty.");
        }

        if (length > ChopLimits.MaxUploadBytes)
        {
            return Outcome<UploadResult>.TooLarge(
                $"The file is larger than the {ChopLimits.MaxUploadBytes / (1024 * 1024)} MB limit.");
        }

        var extension = Path.GetExtension(fileName);
        if (!AudioDecoder.IsSupportedExtension(extension))
        {
            return Outcome<UploadResult>.UnsupportedMedia(
                $"'{extension}' is not a supported audio format. Supported here: {AudioDecoder.SupportedExtensionsDescription}.");
        }

        var job = store.Create(fileName);
        var sourcePath = Path.Combine(job.WorkingDirectory, $"source{extension}");

        try
        {
            await using (var target = File.Create(sourcePath))
            {
                await content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            var envelope = await Task.Run(
                () => AudioDecoder.DecodeToCanonical(sourcePath, job.CanonicalPath),
                cancellationToken).ConfigureAwait(false);

            job.Envelope = envelope;
            File.Delete(sourcePath);

            ChopLog.Decoded(_logger, fileName, job.Id.ToString(), envelope.DurationSeconds, envelope.SampleRate, envelope.Channels);

            return Outcome<UploadResult>.Ok(new UploadResult(
                JobId: job.Id,
                FileName: fileName,
                DurationSeconds: Math.Round(envelope.DurationSeconds, 3),
                SampleRate: envelope.SampleRate,
                Channels: envelope.Channels,
                PeakDb: Math.Round(envelope.PeakDb, 2),
                NoiseFloorDb: Math.Round(envelope.NoiseFloorDb, 2),
                Waveform: envelope.Waveform));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            store.Remove(job.Id);
            ChopLog.DecodeFailed(_logger, fileName, exception);
            return Outcome<UploadResult>.Undecodable(
                $"Could not decode '{fileName}'. It may be corrupt or use an unsupported codec.");
        }
    }

    /// <summary>Re-runs detection against the already-decoded audio. Cheap enough to call per knob turn.</summary>
    public Outcome<AnalysisResult> Analyze(JobId jobId, ChopOptions options)
    {
        if (store.Find(jobId) is not { Envelope: { } envelope } job)
        {
            return Outcome<AnalysisResult>.NotFound(NotFoundMessage);
        }

        if (Validate(options) is { Count: > 0 } errors)
        {
            return Outcome<AnalysisResult>.Invalid(errors);
        }

        // Silence trim (when enabled) shrinks the working envelope. Detection runs on the
        // trimmed slice, then the segment timestamps are shifted so the user sees times
        // relative to the original recording, not the trimmed one.
        var (start, end) = SilenceTrimmer.Trim(envelope, options);
        var frameMs = SegmentDetector.FrameMs;
        var trimStart = start * frameMs / 1000d;
        var trimEnd = end * frameMs / 1000d;

        var working = SliceEnvelope(envelope, start, end);
        var result = SegmentDetector.Detect(working, options) with { JobId = job.Id };

        if (trimStart > 0 || trimEnd < envelope.DurationSeconds)
        {
            var shifted = result.Segments
                .Select(s => s with { StartSeconds = s.StartSeconds + trimStart, EndSeconds = s.EndSeconds + trimStart })
                .ToArray();
            result = result with
            {
                Segments = shifted,
                DurationSeconds = envelope.DurationSeconds - trimStart - (envelope.DurationSeconds - trimEnd),
            };
        }

        job.Segments = result.Segments;
        job.TrimStart = trimStart;
        job.TrimEnd = envelope.DurationSeconds - trimEnd;

        ChopLog.Analyzed(_logger, job.Id.ToString(), result.Segments.Count, result.ThresholdDb);

        return Outcome<AnalysisResult>.Ok(result);
    }

    /// <summary>
    /// Builds a spectrogram of the whole recording.
    ///
    /// <para>
    /// Reads the canonical WAV rather than the envelope: the envelope is one loudness figure per
    /// 10 ms frame, which is everything detection needs and nothing a frequency view can use. The
    /// samples are downmixed to mono on the way through, because a spectrogram of a stereo take
    /// showing two near-identical panels answers no question anyone asked.
    /// </para>
    /// <para>
    /// Synchronous like the rest of this class, so the caller decides which thread pays for it —
    /// this is firmly in the "call it through Task.Run or the window freezes" category.
    /// </para>
    /// </summary>
    public Outcome<SpectrogramData> GetSpectrogram(JobId jobId, int columns, int bins)
    {
        if (columns < 1 || bins < 1)
        {
            return Outcome<SpectrogramData>.Invalid(nameof(columns), "Columns and bins must both be at least 1.");
        }

        if (store.Find(jobId) is not { Envelope: { } envelope } job)
        {
            return Outcome<SpectrogramData>.NotFound(NotFoundMessage);
        }

        if (!File.Exists(job.CanonicalPath))
        {
            return Outcome<SpectrogramData>.NotFound(NotFoundMessage);
        }

        try
        {
            var mono = ClipExporter.ReadMono(job.CanonicalPath);

            return mono.Length == 0
                ? Outcome<SpectrogramData>.Empty("That recording holds no audio.")
                : Outcome<SpectrogramData>.Ok(Spectrogram.Build(mono, envelope.SampleRate, columns, bins));
        }
        catch (IOException exception)
        {
            return Outcome<SpectrogramData>.Undecodable(exception.Message);
        }
        catch (InvalidDataException exception)
        {
            return Outcome<SpectrogramData>.Undecodable(exception.Message);
        }
    }

    /// <summary>Renders one take as a WAV file.</summary>
    public Outcome<ExportedFile> GetClip(JobId jobId, int index, ExportOptions export)
    {
        if (ValidateExport(export) is { Count: > 0 } errors)
        {
            return Outcome<ExportedFile>.Invalid(errors);
        }

        if (store.Find(jobId) is not { } job)
        {
            return Outcome<ExportedFile>.NotFound(NotFoundMessage);
        }

        if (job.Segments.FirstOrDefault(s => s.Index == index) is not { } segment)
        {
            return Outcome<ExportedFile>.NotFound($"Clip {index} does not exist. Run analyze first.");
        }

        return Outcome<ExportedFile>.Ok(new ExportedFile(
            ClipExporter.RenderClip(job.CanonicalPath, segment, export),
            ClipExporter.ClipFileName(job, segment),
            ExportedFile.Wav));
    }

    /// <summary>
    /// Renders the takes of several recordings as one flat ZIP. Unknown ids are skipped rather than
    /// fatal: one expired job should not block the rest of the batch.
    /// </summary>
    public Outcome<ExportedFile> GetBatchZip(IReadOnlyList<JobId> jobIds, ExportOptions export)
    {
        if (ValidateExport(export) is { Count: > 0 } errors)
        {
            return Outcome<ExportedFile>.Invalid(errors);
        }

        if (jobIds.Count > ChopLimits.MaxBatchFiles)
        {
            return Outcome<ExportedFile>.Invalid(
                "jobs", $"A batch download covers at most {ChopLimits.MaxBatchFiles} recordings.");
        }

        var ready = jobIds
            .Select(store.Find)
            .OfType<ChopJob>()
            .Where(job => job.Segments.Count > 0)
            .ToArray();

        if (ready.Length == 0)
        {
            return Outcome<ExportedFile>.NotFound(
                "There is nothing to download. Upload the files again and re-split.");
        }

        return Outcome<ExportedFile>.Ok(new ExportedFile(
            ClipExporter.RenderZip(ready, export),
            "clips.zip",
            ExportedFile.Zip));
    }

    /// <summary>Discards a recording. An unknown id is a no-op, so this is safe to call twice.</summary>
    public void Delete(JobId jobId) => store.Remove(jobId);

    /// <summary>Drops the envelope to the trimmed frame range, rescaling the display waveform with it.</summary>
    internal static AudioEnvelope SliceEnvelope(AudioEnvelope envelope, int startFrame, int endFrame)
    {
        if (startFrame <= 0 && endFrame >= envelope.FrameDb.Count - 1)
        {
            return envelope;
        }

        var sliceLength = endFrame - startFrame + 1;
        var slicedDb = new double[sliceLength];
        var slicedWave = new float[envelope.Waveform.Count];
        var bucketRatio = (double)envelope.Waveform.Count / envelope.FrameDb.Count;

        for (var i = 0; i < sliceLength; i++)
        {
            slicedDb[i] = envelope.FrameDb[startFrame + i];
        }

        for (var i = 0; i < envelope.Waveform.Count; i++)
        {
            var sourceFrame = (int)(i / bucketRatio);
            if (sourceFrame >= startFrame && sourceFrame <= endFrame)
            {
                var targetIndex = (int)((sourceFrame - startFrame) * bucketRatio);
                if (targetIndex >= 0 && targetIndex < slicedWave.Length)
                {
                    slicedWave[targetIndex] = envelope.Waveform[i];
                }
            }
        }

        var duration = sliceLength * SegmentDetector.FrameMs / 1000d;
        return new AudioEnvelope
        {
            FrameDb = slicedDb,
            Waveform = slicedWave,
            DurationSeconds = duration,
            SampleRate = envelope.SampleRate,
            Channels = envelope.Channels,
            PeakDb = envelope.PeakDb,
            NoiseFloorDb = envelope.NoiseFloorDb,
        };
    }

    /// <summary>Field-keyed errors for the detection knobs, empty when they are usable.</summary>
    internal static Dictionary<string, string[]> Validate(ChopOptions options)
    {
        var errors = new Dictionary<string, string[]>();

        Check(nameof(options.ExpectedSegments), options.ExpectedSegments is >= 1 and <= 64, "Expect between 1 and 64 sounds.");
        Check(nameof(options.MinSegmentMs), options.MinSegmentMs is >= 10 and <= 60_000, "Minimum length must be 10-60000 ms.");
        Check(nameof(options.MinGapMs), options.MinGapMs is >= 10 and <= 60_000, "Minimum gap must be 10-60000 ms.");
        Check(nameof(options.PadMs), options.PadMs is >= 0 and <= 5_000, "Padding must be 0-5000 ms.");
        Check(nameof(options.ThresholdDb), options.ThresholdDb is null or (>= -100 and <= 0), "Threshold must be -100 to 0 dBFS.");

        return errors;

        void Check(string field, bool ok, string message)
        {
            if (!ok)
            {
                errors[field] = [message];
            }
        }
    }

    /// <summary>Field-keyed errors for the export knobs, empty when they are usable.</summary>
    internal static Dictionary<string, string[]> ValidateExport(ExportOptions export)
    {
        var errors = new Dictionary<string, string[]>();

        Check(
            nameof(export.TargetDb),
            export.TargetDb is >= ExportLimits.MinTargetDb and <= ExportLimits.MaxTargetDb,
            $"Target must be {ExportLimits.MinTargetDb} to {ExportLimits.MaxTargetDb}.");

        Check(
            nameof(export.CeilingDb),
            export.CeilingDb is >= ExportLimits.MinCeilingDb and <= ExportLimits.MaxCeilingDb,
            $"Ceiling must be {ExportLimits.MinCeilingDb} to {ExportLimits.MaxCeilingDb} dBFS.");

        Check(
            nameof(export.FadeInMs),
            export.FadeInMs is >= 0 and <= ExportLimits.MaxFadeMs,
            $"Fade in must be 0-{ExportLimits.MaxFadeMs} ms.");

        Check(
            nameof(export.FadeOutMs),
            export.FadeOutMs is >= 0 and <= ExportLimits.MaxFadeMs,
            $"Fade out must be 0-{ExportLimits.MaxFadeMs} ms.");

        return errors;

        void Check(string field, bool ok, string message)
        {
            if (!ok)
            {
                errors[field] = [message];
            }
        }
    }
}
