using System.IO.Compression;
using NAudio.Wave;

namespace PoChopAudio.Services.Chop;

/// <summary>
/// Cuts clips out of a job's canonical WAV and writes them as 16-bit PCM WAV.
///
/// Normalization needs the whole clip measured before the first sample can be written, so an
/// export that asks for it reads the slice twice: once to measure, once to write. Reading twice
/// rather than buffering keeps memory flat whether the take is 200 ms or the entire recording.
/// An export with no options set skips the measuring pass and is the plain slice it always was.
/// </summary>
public static class ClipExporter
{
    private const int BitsPerSample = 16;
    private const string FallbackStem = "clip";

    public static byte[] RenderClip(string canonicalPath, ChopSegment segment) =>
        RenderClip(canonicalPath, segment, ExportOptions.PassThrough);

    public static byte[] RenderClip(string canonicalPath, ChopSegment segment, ExportOptions export)
    {
        using var reader = new WaveFileReader(canonicalPath);
        return RenderClip(reader, segment, export);
    }

    /// <summary>
    /// Reads the whole canonical WAV as mono samples, averaging channels.
    ///
    /// <para>
    /// Streams through a fixed buffer rather than calling <c>ToArray</c> on the reader: a 43-minute
    /// recording is roughly 250 MB of 32-bit float per channel, and the point of the canonical file
    /// living on disk is that the app never has to hold two copies of that in managed memory. The
    /// mono result is a quarter the size of a stereo float pair and is what every analysis view
    /// actually wants.
    /// </para>
    /// </summary>
    public static float[] ReadMono(string canonicalPath)
    {
        using var reader = new WaveFileReader(canonicalPath);
        var samples = reader.ToSampleProvider();
        var channels = samples.WaveFormat.Channels;

        if (channels < 1)
        {
            return [];
        }

        var totalFrames = (int)Math.Min(int.MaxValue, reader.SampleCount);
        var mono = new float[Math.Max(0, totalFrames)];
        var buffer = new float[channels * 4096];
        var written = 0;

        int read;
        while ((read = samples.Read(buffer, 0, buffer.Length)) > 0 && written < mono.Length)
        {
            for (var i = 0; i + channels <= read && written < mono.Length; i += channels)
            {
                var sum = 0f;
                for (var c = 0; c < channels; c++)
                {
                    sum += buffer[i + c];
                }

                mono[written++] = sum / channels;
            }
        }

        return written == mono.Length ? mono : mono[..written];
    }

    public static byte[] RenderZip(ChopJob job) => RenderZip(job, ExportOptions.PassThrough);

    public static byte[] RenderZip(ChopJob job, ExportOptions export)
    {
        using var reader = new WaveFileReader(job.CanonicalPath);
        var zipStream = new MemoryStream();

        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntries(archive, reader, Stem(job.OriginalFileName), job.Segments, export);
        }

        return zipStream.ToArray();
    }

    public static byte[] RenderZip(IReadOnlyList<ChopJob> jobs) => RenderZip(jobs, ExportOptions.PassThrough);

    /// <summary>
    /// Packs the clips of several jobs into one flat ZIP. Every clip keeps its source name as the
    /// prefix, so the archive reads like the output folder: Nick_Happy_1.wav … Nick_Sad_5.wav.
    /// </summary>
    public static byte[] RenderZip(IReadOnlyList<ChopJob> jobs, ExportOptions export)
    {
        var stems = UniqueStems([.. jobs.Select(j => j.OriginalFileName)]);
        var zipStream = new MemoryStream();

        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < jobs.Count; i++)
            {
                using var reader = new WaveFileReader(jobs[i].CanonicalPath);
                WriteEntries(archive, reader, stems[i], jobs[i].Segments, export);
            }
        }

        return zipStream.ToArray();
    }

    public static string ClipFileName(ChopJob job, ChopSegment segment) =>
        ClipFileName(Stem(job.OriginalFileName), segment);

    // Source name as the prefix, take number as the suffix: Matt_Happy_1.wav … Matt_Happy_5.wav.
    public static string ClipFileName(string stem, ChopSegment segment) => $"{stem}_{segment.Index}.wav";

    /// <summary>Strips the extension and anything a file system would reject.</summary>
    public static string Stem(string originalFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(originalFileName);
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            stem = stem.Replace(invalid, '_');
        }

        stem = stem.Trim();
        return stem.Length == 0 ? FallbackStem : stem;
    }

    /// <summary>
    /// Gives every source a distinct stem so a flat ZIP cannot drop a clip to a name collision —
    /// two uploads called Nick_Happy.m4a become Nick_Happy and Nick_Happy(2).
    /// </summary>
    public static IReadOnlyList<string> UniqueStems(IReadOnlyList<string> originalFileNames)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stems = new string[originalFileNames.Count];

        for (var i = 0; i < originalFileNames.Count; i++)
        {
            var stem = Stem(originalFileNames[i]);
            var candidate = stem;

            for (var suffix = 2; !taken.Add(candidate); suffix++)
            {
                candidate = $"{stem}({suffix})";
            }

            stems[i] = candidate;
        }

        return stems;
    }

    /// <summary>
    /// Measures one clip exactly as an export would, without writing it. The UI uses this to show
    /// what normalization is about to do before the user commits to a download.
    /// </summary>
    public static ClipGain MeasureClip(string canonicalPath, ChopSegment segment, ExportOptions export)
    {
        using var reader = new WaveFileReader(canonicalPath);
        var (startFrame, clipFrames) = FrameRange(reader, segment);
        return Measure(reader, startFrame, clipFrames, export);
    }

    private static void WriteEntries(
        ZipArchive archive,
        WaveFileReader reader,
        string stem,
        IReadOnlyList<ChopSegment> segments,
        ExportOptions export)
    {
        foreach (var segment in segments)
        {
            var entry = archive.CreateEntry(ClipFileName(stem, segment), CompressionLevel.Fastest);
            using var entryStream = entry.Open();
            entryStream.Write(RenderClip(reader, segment, export));
        }
    }

    private static byte[] RenderClip(WaveFileReader reader, ChopSegment segment, ExportOptions export)
    {
        var format = reader.WaveFormat;
        var (startFrame, clipFrames) = FrameRange(reader, segment);

        var gain = export.IsPassThrough
            ? ClipGain.Unity
            : Measure(reader, startFrame, clipFrames, export);

        var (fadeInFrames, fadeOutFrames) = ClipProcessor.FadeFrames(clipFrames, format.SampleRate, export);
        var linear = (float)gain.Linear;
        var fading = fadeInFrames > 0 || fadeOutFrames > 0;

        var output = new MemoryStream();
        using (var writer = new WaveFileWriter(output, new WaveFormat(format.SampleRate, BitsPerSample, format.Channels)))
        {
            var channels = format.Channels;
            long frame = 0;

            ReadClip(reader, startFrame, clipFrames, (buffer, read) =>
            {
                for (var i = 0; i + channels <= read; i += channels)
                {
                    var sampleGain = fading
                        ? linear * (float)ClipProcessor.FadeGain(frame, clipFrames, fadeInFrames, fadeOutFrames)
                        : linear;

                    for (var c = 0; c < channels; c++)
                    {
                        writer.WriteSample(Math.Clamp(buffer[i + c] * sampleGain, -1f, 1f));
                    }

                    frame++;
                }
            });
        }

        return output.ToArray();
    }

    /// <summary>Reads the clip once, gathering whatever the chosen normalize mode needs.</summary>
    private static ClipGain Measure(WaveFileReader reader, long startFrame, long clipFrames, ExportOptions export)
    {
        var format = reader.WaveFormat;

        // The peak is gathered whatever the mode, because the ceiling is enforced against it.
        float peak = 0;
        double sumOfSquares = 0;
        long sampleCount = 0;

        var meter = export.Normalize is NormalizeMode.Lufs
            ? new LoudnessMeter.Accumulator(format.SampleRate, format.Channels)
            : null;

        ReadClip(reader, startFrame, clipFrames, (buffer, read) =>
        {
            for (var i = 0; i < read; i++)
            {
                var value = buffer[i];
                var magnitude = Math.Abs(value);
                if (magnitude > peak)
                {
                    peak = magnitude;
                }

                sumOfSquares += (double)value * value;
                sampleCount++;
            }

            meter?.Add(buffer, read);
        });

        var peakDb = ClipProcessor.PeakDb(peak);
        var measuredDb = export.Normalize switch
        {
            NormalizeMode.Peak => peakDb,
            NormalizeMode.Rms => ClipProcessor.RmsDb(sumOfSquares, sampleCount),
            NormalizeMode.Lufs => meter!.Integrated(),
            _ => double.NegativeInfinity,
        };

        return ClipProcessor.DecideGain(measuredDb, peakDb, export);
    }

    private static (long StartFrame, long ClipFrames) FrameRange(WaveFileReader reader, ChopSegment segment)
    {
        var format = reader.WaveFormat;
        var totalFrames = reader.SampleCount;

        var startFrame = Math.Clamp((long)(segment.StartSeconds * format.SampleRate), 0, totalFrames);
        var endFrame = Math.Clamp((long)Math.Ceiling(segment.EndSeconds * format.SampleRate), startFrame, totalFrames);

        return (startFrame, endFrame - startFrame);
    }

    /// <summary>
    /// Streams one clip's samples to <paramref name="onBuffer"/>. Reads are frame-aligned: the
    /// underlying stream is a whole number of blocks and the request size is always a multiple of
    /// the channel count, so a buffer never splits a frame across two callbacks.
    /// </summary>
    private static void ReadClip(WaveFileReader reader, long startFrame, long clipFrames, Action<float[], int> onBuffer)
    {
        var format = reader.WaveFormat;
        reader.Position = startFrame * format.BlockAlign;

        var samples = reader.ToSampleProvider();
        var remaining = clipFrames * format.Channels;
        var buffer = new float[format.SampleRate * format.Channels];

        while (remaining > 0)
        {
            var want = (int)Math.Min(remaining, buffer.Length);
            var read = samples.Read(buffer, 0, want);
            if (read == 0)
            {
                break;
            }

            onBuffer(buffer, read);
            remaining -= read;
        }
    }
}
