using Microsoft.Extensions.Logging;
namespace PoChopAudio.Services.Chop;

/// <summary>Source-generated log methods — the upload path runs per request and per frame of audio.</summary>
public static partial class ChopLog
{
    public static ILogger CreateLogger(ILoggerFactory factory) => factory.CreateLogger(typeof(ChopLog));

    [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
        Message = "Decoded {FileName} as job {JobId}: {DurationSeconds:F2}s, {SampleRate} Hz, {Channels} ch")]
    public static partial void Decoded(ILogger logger, string fileName, string jobId, double durationSeconds, int sampleRate, int channels);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information,
        Message = "Job {JobId} split into {SegmentCount} clip(s) at a {ThresholdDb:F1} dBFS gate")]
    public static partial void Analyzed(ILogger logger, string jobId, int segmentCount, double thresholdDb);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning,
        Message = "Could not decode {FileName}")]
    public static partial void DecodeFailed(ILogger logger, string fileName, Exception exception);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information,
        Message = "Removed {Count} expired job(s)")]
    public static partial void JobsExpired(ILogger logger, int count);
}
