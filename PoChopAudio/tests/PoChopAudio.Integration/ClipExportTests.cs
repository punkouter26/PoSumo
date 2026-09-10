using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using PoChopAudio.Services;
using PoChopAudio.Services.Chop;
using Xunit;

namespace PoChopAudio.Integration;

/// <summary>
/// Drives the export knobs through the real service against a synthesised recording, then decodes
/// what came back and measures it. Asserting on the returned samples is the only way to know the
/// options reached the exporter and the gain landed where it was asked to.
///
/// These ran over HTTP until the API was removed. Nothing about what they prove changed: upload,
/// analyze and export are the same calls the desktop app makes, minus the transport.
/// </summary>
public sealed class ClipExportTests : IDisposable
{
    private const int SampleRate = 48_000;
    private const double TonePeakDb = -12;

    private readonly ChopJobStore _store = new();
    private readonly ChopService _chop;

    public ClipExportTests() => _chop = new ChopService(_store, NullLoggerFactory.Instance);

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task PlainDownloadIsUnchangedByAnEmptyExportQuery()
    {
        var jobId = await UploadAndAnalyzeAsync();

        var bare = Clip(jobId, new ExportOptions());
        var explicitlyNone = Clip(jobId, new ExportOptions { Normalize = NormalizeMode.None });

        Assert.Equal(bare, explicitlyNone);
    }

    [Fact]
    public async Task PeakNormalizationLandsThePeakOnTheTarget()
    {
        var jobId = await UploadAndAnalyzeAsync();

        var wav = Clip(jobId, new ExportOptions { Normalize = NormalizeMode.Peak, TargetDb = -3 });
        var (peakDb, _) = Measure(wav);

        Assert.InRange(peakDb, -3.1, -2.9);
    }

    [Fact]
    public async Task LoudnessNormalizationLandsTheClipOnTheLufsTarget()
    {
        var jobId = await UploadAndAnalyzeAsync();

        var wav = Clip(jobId, new ExportOptions { Normalize = NormalizeMode.Lufs, TargetDb = -16 });
        var (_, lufs) = Measure(wav);

        Assert.InRange(lufs, -16.3, -15.7);
    }

    [Fact]
    public async Task TheCeilingIsNeverBreachedEvenByAnAbsurdTarget()
    {
        var jobId = await UploadAndAnalyzeAsync();

        // Asking for 0 LUFS on a -15 LUFS clip wants +15 dB, which would drive the peak well past
        // full scale. The ceiling has to win, and nothing may clip.
        var wav = Clip(jobId, new ExportOptions { Normalize = NormalizeMode.Lufs, TargetDb = 0, CeilingDb = -1 });
        var (peakDb, _) = Measure(wav);

        Assert.InRange(peakDb, -1.15, -0.95);
    }

    [Fact]
    public async Task FadesSilenceTheEdgesOfTheClip()
    {
        var jobId = await UploadAndAnalyzeAsync();

        var plain = ReadSamples(Clip(jobId, new ExportOptions()));
        var faded = ReadSamples(Clip(jobId, new ExportOptions { FadeInMs = 50, FadeOutMs = 50 }));

        Assert.Equal(plain.Length, faded.Length);
        Assert.True(Math.Abs(faded[0]) <= Math.Abs(plain[0]));
        Assert.InRange(Math.Abs(faded[0]), 0, 0.0005);
        Assert.InRange(Math.Abs(faded[^1]), 0, 0.0005);

        // The middle of the clip must be left alone; a fade is an edge treatment, not a gain.
        var middle = plain.Length / 2;
        Assert.Equal(plain[middle], faded[middle], 3);
    }

    [Fact]
    public async Task ExportOptionsApplyToTheBatchZipToo()
    {
        var jobId = await UploadAndAnalyzeAsync();

        var outcome = _chop.GetBatchZip([jobId], new ExportOptions { Normalize = NormalizeMode.Peak, TargetDb = -3 });
        Assert.True(outcome.IsSuccess);

        using var archive = new ZipArchive(new MemoryStream(outcome.Value.Content));
        Assert.NotEmpty(archive.Entries);

        using var entry = archive.Entries[0].Open();
        using var buffer = new MemoryStream();
        await entry.CopyToAsync(buffer);

        var (peakDb, _) = Measure(buffer.ToArray());
        Assert.InRange(peakDb, -3.1, -2.9);
    }

    [Theory]
    [MemberData(nameof(OutOfRangeExports))]
    public async Task OutOfRangeExportOptionsAreRejected(ExportOptions export)
    {
        var jobId = await UploadAndAnalyzeAsync();

        var outcome = _chop.GetClip(jobId, 1, export);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(OutcomeFailure.Invalid, outcome.Failure);
        Assert.NotEmpty(outcome.Errors);
    }

    public static TheoryData<ExportOptions> OutOfRangeExports() =>
    [
        new ExportOptions { TargetDb = 5 },
        new ExportOptions { TargetDb = -999 },
        new ExportOptions { CeilingDb = 3 },
        new ExportOptions { FadeInMs = -1 },
        new ExportOptions { FadeOutMs = 99_999 },
    ];

    [Fact]
    public async Task ASilentClipIsLeftAloneRatherThanAmplified()
    {
        // Normalizing digital silence would be a divide by zero dressed up as a feature.
        var jobId = await UploadAndAnalyzeAsync(silent: true);

        var outcome = _chop.GetClip(jobId, 1, new ExportOptions { Normalize = NormalizeMode.Peak, TargetDb = -1 });

        // Either no segment was found in silence, or the one found came back untouched.
        if (outcome.IsSuccess)
        {
            var samples = ReadSamples(outcome.Value.Content);
            Assert.All(samples, sample => Assert.InRange(Math.Abs(sample), 0, 0.01f));
        }
        else
        {
            Assert.Equal(OutcomeFailure.NotFound, outcome.Failure);
        }
    }

    private byte[] Clip(JobId jobId, ExportOptions export)
    {
        var outcome = _chop.GetClip(jobId, 1, export);
        Assert.True(outcome.IsSuccess, outcome.Message);
        return outcome.Value.Content;
    }

    private async Task<JobId> UploadAndAnalyzeAsync(bool silent = false)
    {
        using var source = new MemoryStream(BuildRecording(silent));
        var upload = await _chop.UploadAsync(source, "takes.wav", source.Length);
        Assert.True(upload.IsSuccess, upload.Message);

        var analyze = _chop.Analyze(upload.Value.JobId, new ChopOptions { ExpectedSegments = 5 });
        Assert.True(analyze.IsSuccess, analyze.Message);

        return upload.Value.JobId;
    }

    /// <summary>
    /// Five 400 ms tones separated by 400 ms of very low noise. The noise matters: a detector
    /// handed digital silence has no floor to measure a contrast against.
    /// </summary>
    private static byte[] BuildRecording(bool silent)
    {
        var amplitude = silent ? 0.0 : Math.Pow(10, TonePeakDb / 20);
        var stream = new MemoryStream();

        using (var writer = new WaveFileWriter(stream, new WaveFormat(SampleRate, 16, 1)))
        {
            // Deterministic dither so the noise floor is real but the test never flakes.
            var seed = 12345u;
            float Noise()
            {
                seed = (seed * 1664525u) + 1013904223u;
                return ((seed >> 8) / (float)(1 << 24) * 2 - 1) * 0.0005f;
            }

            void WriteNoise(double seconds)
            {
                for (var i = 0; i < (int)(SampleRate * seconds); i++)
                {
                    writer.WriteSample(Noise());
                }
            }

            void WriteTone(double seconds)
            {
                var frames = (int)(SampleRate * seconds);
                for (var i = 0; i < frames; i++)
                {
                    var value = amplitude * Math.Sin(2 * Math.PI * 1000 * i / SampleRate);
                    writer.WriteSample((float)value + Noise());
                }
            }

            WriteNoise(0.3);
            for (var take = 0; take < 5; take++)
            {
                WriteTone(0.4);
                WriteNoise(0.4);
            }
        }

        return stream.ToArray();
    }

    private static float[] ReadSamples(byte[] wav)
    {
        using var reader = new WaveFileReader(new MemoryStream(wav));
        var provider = reader.ToSampleProvider();
        var samples = new List<float>();
        var buffer = new float[4096];

        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            samples.AddRange(buffer[..read]);
        }

        return [.. samples];
    }

    private static (double PeakDb, double Lufs) Measure(byte[] wav)
    {
        using var reader = new WaveFileReader(new MemoryStream(wav));
        var channels = reader.WaveFormat.Channels;
        var meter = new LoudnessMeter.Accumulator(reader.WaveFormat.SampleRate, channels);
        var provider = reader.ToSampleProvider();
        var buffer = new float[4096 * channels];

        float peak = 0;
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                peak = Math.Max(peak, Math.Abs(buffer[i]));
            }

            meter.Add(buffer, read);
        }

        return (ClipProcessor.PeakDb(peak), meter.Integrated());
    }
}
