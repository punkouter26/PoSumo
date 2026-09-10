using NAudio.Wave;
using NLayer.NAudioSupport;

namespace PoChopAudio.Services.Chop;

/// <summary>
/// Decodes an upload once into a canonical 32-bit float WAV and, in the same pass, the loudness
/// envelope everything downstream works from. Clip export then slices the canonical file, so a
/// compressed source is never decoded twice.
/// </summary>
public static class AudioDecoder
{
    private const int WaveformBuckets = 900;
    private const double SilenceDb = -100;
    private const double NoiseFloorPercentile = 0.10;

    private static readonly string[] AlwaysSupported = [".wav", ".wave", ".mp3", ".aiff", ".aif"];

    private static readonly string[] WindowsOnly = [".m4a", ".aac", ".wma"];

    /// <summary>The user-visible list. Aliases like <c>.wave</c> are still decoded via <see cref="IsSupportedExtension"/> but not advertised in the picker.</summary>
    private static readonly string[] AdvertisedAlways = [".wav", ".mp3", ".aiff", ".aif"];

    /// <summary>The exact list the running platform can decode — drives the UI's file picker.</summary>
    public static IReadOnlyList<string> SupportedExtensions =>
        OperatingSystem.IsWindows()
            ? [.. AdvertisedAlways, .. WindowsOnly]
            : AdvertisedAlways;

    public static bool IsSupportedExtension(string extension) =>
        AlwaysSupported.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
        (OperatingSystem.IsWindows() && WindowsOnly.Contains(extension, StringComparer.OrdinalIgnoreCase));

    public static string SupportedExtensionsDescription =>
        OperatingSystem.IsWindows() ? "WAV, MP3, AIFF, M4A, AAC, WMA" : "WAV, MP3, AIFF";

    public static AudioEnvelope DecodeToCanonical(string sourcePath, string canonicalPath)
    {
        using var source = OpenReader(sourcePath);
        var samples = source.Reader.ToSampleProvider();
        var channels = samples.WaveFormat.Channels;
        var sampleRate = samples.WaveFormat.SampleRate;

        if (channels < 1 || sampleRate < 1)
        {
            throw new InvalidDataException("The file does not contain a readable audio stream.");
        }

        var frameSamples = Math.Max(1, (int)Math.Round(sampleRate * SegmentDetector.FrameMs / 1000d));
        var frameDb = new List<double>();
        var framePeak = new List<float>();

        var buffer = new float[frameSamples * channels * 8];
        var totalFrames = 0L;
        var frameSumSquares = 0d;
        var frameMaxAbs = 0f;
        var frameFill = 0;

        using (var writer = new WaveFileWriter(canonicalPath, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels)))
        {
            int read;
            while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
            {
                writer.WriteSamples(buffer, 0, read);

                for (var i = 0; i + channels <= read; i += channels)
                {
                    var mono = 0f;
                    for (var c = 0; c < channels; c++)
                    {
                        mono += buffer[i + c];
                    }

                    mono /= channels;

                    frameSumSquares += (double)mono * mono;
                    frameMaxAbs = Math.Max(frameMaxAbs, Math.Abs(mono));
                    totalFrames++;

                    if (++frameFill < frameSamples)
                    {
                        continue;
                    }

                    frameDb.Add(ToDb(Math.Sqrt(frameSumSquares / frameFill)));
                    framePeak.Add(frameMaxAbs);
                    frameSumSquares = 0;
                    frameMaxAbs = 0;
                    frameFill = 0;
                }
            }
        }

        if (frameFill > 0)
        {
            frameDb.Add(ToDb(Math.Sqrt(frameSumSquares / frameFill)));
            framePeak.Add(frameMaxAbs);
        }

        if (frameDb.Count == 0)
        {
            throw new InvalidDataException("The file contains no audio samples.");
        }

        return new AudioEnvelope
        {
            FrameDb = frameDb,
            Waveform = Downsample(framePeak, WaveformBuckets),
            DurationSeconds = (double)totalFrames / sampleRate,
            SampleRate = sampleRate,
            Channels = channels,
            PeakDb = frameDb.Max(),
            NoiseFloorDb = Percentile(frameDb, NoiseFloorPercentile),
        };
    }

    private static ReaderHandle OpenReader(string path)
    {
        var extension = Path.GetExtension(path);

        switch (extension.ToLowerInvariant())
        {
            case ".wav":
            case ".wave":
                return new ReaderHandle(new WaveFileReader(path), null);

            case ".aiff":
            case ".aif":
                return new ReaderHandle(new AiffFileReader(path), null);

            case ".mp3":
                // NLayer decodes MP3 in managed code, so this works off Windows too.
                var stream = File.OpenRead(path);
                try
                {
                    return new ReaderHandle(new Mp3FileReaderBase(stream, wf => new Mp3FrameDecompressor(wf)), stream);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
        }

        if (OperatingSystem.IsWindows())
        {
            return new ReaderHandle(new MediaFoundationReader(path), null);
        }

        throw new NotSupportedException($"{extension} files are not supported. Supported: {SupportedExtensionsDescription}.");
    }

    private static double ToDb(double amplitude) =>
        amplitude <= 0 ? SilenceDb : Math.Max(SilenceDb, 20 * Math.Log10(amplitude));

    private static double Percentile(List<double> values, double percentile)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var index = Math.Clamp((int)(sorted.Length * percentile), 0, sorted.Length - 1);
        return sorted[index];
    }

    private static float[] Downsample(List<float> values, int buckets)
    {
        if (values.Count <= buckets)
        {
            return [.. values];
        }

        var result = new float[buckets];
        for (var i = 0; i < buckets; i++)
        {
            var from = (int)((long)i * values.Count / buckets);
            var to = (int)((long)(i + 1) * values.Count / buckets);
            var peak = 0f;
            for (var j = from; j < to; j++)
            {
                peak = Math.Max(peak, values[j]);
            }

            result[i] = peak;
        }

        return result;
    }

    private sealed class ReaderHandle(WaveStream reader, IDisposable? owned) : IDisposable
    {
        public WaveStream Reader { get; } = reader;

        public void Dispose()
        {
            Reader.Dispose();
            owned?.Dispose();
        }
    }
}
