using Microsoft.Extensions.Logging;
using PoChopAudio.Services.Chop;

namespace PoChopAudio.Services.Cutout;

/// <summary>Source-generated log methods — the cutout path runs once per image.</summary>
public static partial class CutoutLog
{
    public static ILogger CreateLogger(ILoggerFactory factory) => factory.CreateLogger(typeof(CutoutLog));

    [LoggerMessage(EventId = 2000, Level = LogLevel.Information,
        Message = "Decoded {FileName} as job {JobId}: {Width}x{Height}, {Bytes} bytes{IsMotionPhoto}")]
    public static partial void Decoded(ILogger logger, string fileName, string jobId, int width, int height, long bytes, bool isMotionPhoto);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information,
        Message = "Job {JobId} cut out via {Engine}: {Width}x{Height}, {Bytes} bytes")]
    public static partial void Cutout(ILogger logger, string jobId, CutoutEngine engine, int width, int height, long bytes);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning,
        Message = "Could not decode {FileName}")]
    public static partial void DecodeFailed(ILogger logger, string fileName, Exception exception);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Warning,
        Message = "Could not cut out {JobId} via {Engine}")]
    public static partial void CutoutFailed(ILogger logger, string jobId, CutoutEngine engine, Exception exception);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Information,
        Message = "Removed {Count} expired cutout job(s)")]
    public static partial void JobsExpired(ILogger logger, int count);

    /// <summary>
    /// Records how the head crop was decided. Without this the only evidence of a wrong crop is the
    /// picture itself, which cannot tell you whether the face detector ran, found nothing, or was
    /// overruled -- three different bugs that look identical on screen.
    /// </summary>
    [LoggerMessage(EventId = 2005, Level = LogLevel.Information,
        Message = "Head crop {FileName}: source {SourceWidth}x{SourceHeight}, faceDetector={FaceAvailable}, " +
                  "face={FaceBox}, chinRow={ChinRow}, result {ResultWidth}x{ResultHeight}")]
    public static partial void HeadCrop(
        ILogger logger, string fileName, int sourceWidth, int sourceHeight,
        bool faceAvailable, string faceBox, string chinRow, int resultWidth, int resultHeight);
}
