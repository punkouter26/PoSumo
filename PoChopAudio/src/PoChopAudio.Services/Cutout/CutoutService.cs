using Microsoft.Extensions.Logging;

namespace PoChopAudio.Services.Cutout;

/// <summary>One finished cutout: the PNG and the size it ended up.</summary>
public sealed record CutoutPhoto(byte[] Png, int Width, int Height, CutoutEngine Engine);

/// <summary>
/// Turns a photo into a cutout in one call.
///
/// This used to be four calls: upload, analyze, fetch image, delete — with a job store holding
/// decoded pixels in between. That shape existed because HTTP is stateless and the browser needed
/// a handle to refer back to. In-process there is nothing to refer back to: the caller already
/// holds the bytes, so the job store, its 2 h expiry and its temp directory were pure ceremony.
/// </summary>
/// <param name="faceLocator">
/// Optional platform face detection. When the host registers one, a measured chin replaces
/// HeadFinder's inference of where the neck is; when it is null or reports itself unavailable,
/// the mask-shape logic runs exactly as before.
/// </param>
public sealed class CutoutService(
    EnginePicker picker, ILoggerFactory loggerFactory, IFaceLocator? faceLocator = null)
{
    private readonly ILogger _logger = CutoutLog.CreateLogger(loggerFactory);

    /// <summary>True when some engine is available. False means the model is missing.</summary>
    public bool IsAvailable => picker.AvailableEngines.Count > 0;

    /// <summary>
    /// Decodes the photo, removes the background, cleans the mask edge, crops to the head, and
    /// encodes a PNG.
    /// </summary>
    /// <param name="length">Byte count, passed separately because a stream cannot always report it.</param>
    public async Task<Outcome<CutoutPhoto>> CutOutAsync(
        Stream source,
        string fileName,
        long length,
        CutoutOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (length == 0)
        {
            return Outcome<CutoutPhoto>.Empty("The photo is empty.");
        }

        if (length > CutoutLimits.MaxUploadBytes)
        {
            return Outcome<CutoutPhoto>.TooLarge(
                $"The photo is larger than the {CutoutLimits.MaxUploadBytes / (1024 * 1024)} MB limit.");
        }

        var extension = Path.GetExtension(fileName);
        if (!ImageDecoder.IsSupportedExtension(extension))
        {
            return Outcome<CutoutPhoto>.UnsupportedMedia(
                $"'{extension}' is not a supported image format. Supported: {string.Join(", ", ImageDecoder.SupportedExtensions)}.");
        }

        var opts = options ?? new CutoutOptions();

        // Validation comes before the engine lookup. A knob out of range is wrong about the
        // request, not about the host, so gating it behind an optional capability meant a machine
        // with no model answered every bad value with "u2netp.onnx is missing" — hiding the field
        // that was actually at fault, and only on the machines least able to work it out.
        if (Validate(opts) is { Count: > 0 } errors)
        {
            return Outcome<CutoutPhoto>.Invalid(errors);
        }

        var engine = picker.Resolve(opts.Engine);
        if (engine is null)
        {
            return Outcome<CutoutPhoto>.EngineUnavailable(
                "Background removal is unavailable because u2netp.onnx is missing. Run SCRIPTS/download-models.ps1.");
        }

        try
        {
            var decoded = await Task.Run(
                () => ImageDecoder.Decode(source, fileName),
                cancellationToken).ConfigureAwait(false);

            CutoutLog.Decoded(_logger, fileName, "-", decoded.Width, decoded.Height, decoded.Rgba.Length, decoded.WasMotionPhoto);

            var mask = await engine.RemoveAsync(decoded.Rgba, decoded.Width, decoded.Height, cancellationToken)
                .ConfigureAwait(false);

            // Run against the original pixels, not the mask: the detector needs a real face, and it
            // has to happen out here because it is async while the crop below is not.
            var face = opts.HeadOnly
                ? await LocateFaceAsync(decoded.Rgba, decoded.Width, decoded.Height, cancellationToken)
                    .ConfigureAwait(false)
                : null;

            var chinRow = face is { } f
                ? Math.Clamp(f.Bottom + (int)(f.Height * ChinMargin), 0, decoded.Height - 1)
                : (int?)null;

            return await Task.Run(
                () =>
                {
                    var processed = EdgeProcessor.Apply(mask, decoded.Width, decoded.Height, opts);

                    var rgba = processed;
                    var width = decoded.Width;
                    var height = decoded.Height;

                    if (opts.HeadOnly)
                    {
                        var head = HeadFinder.Find(
                            processed, decoded.Width, decoded.Height,
                            opts.TrimPaddingPx, opts.HeadCutBiasPercent, chinRow);
                        if (!head.Empty)
                        {
                            rgba = HeadFinder.Crop(processed, decoded.Width, head);
                            width = head.Width;
                            height = head.Height;
                        }
                    }

                    CutoutLog.HeadCrop(
                        _logger, fileName, decoded.Width, decoded.Height,
                        faceLocator is { IsAvailable: true },
                        face?.ToString() ?? "none",
                        chinRow?.ToString() ?? "none",
                        width, height);

                    var png = ImageDecoder.EncodePng(rgba, width, height);
                    CutoutLog.Cutout(_logger, fileName, engine.Engine, width, height, png.Length);

                    return Outcome<CutoutPhoto>.Ok(new CutoutPhoto(png, width, height, engine.Engine));
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CutoutLog.CutoutFailed(_logger, fileName, engine.Engine, exception);
            return Outcome<CutoutPhoto>.Undecodable(
                $"Could not cut out '{fileName}'. It may be corrupt or use an unsupported codec.");
        }
    }

    /// <summary>
    /// Chin row for the head cut, or null to let HeadFinder infer it from the mask shape.
    ///
    /// A detected face box covers brow to chin, so its bottom edge is the chin. A little is added
    /// below it because a crop that lands exactly on the chin looks decapitated -- the jawline needs
    /// somewhere to sit.
    /// </summary>
    private async Task<FaceBox?> LocateFaceAsync(
        byte[] rgba, int width, int height, CancellationToken cancellationToken)
    {
        if (faceLocator is not { IsAvailable: true })
        {
            return null;
        }

        return await faceLocator.LocateAsync(rgba, width, height, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Extra room below the detected chin, as a fraction of face height.</summary>
    private const double ChinMargin = 0.18;

    /// <summary>Field-keyed errors for the edge knobs, empty when they are usable.</summary>
    internal static Dictionary<string, string[]> Validate(CutoutOptions options)
    {
        var errors = new Dictionary<string, string[]>();

        Check(nameof(options.AlphaThreshold), options.AlphaThreshold <= 255, "Alpha threshold must be 0-255.");
        Check(nameof(options.FeatherRadius), options.FeatherRadius is >= 0 and <= 5, "Feather radius must be 0-5 px.");
        Check(nameof(options.Morphology), options.Morphology is >= -3 and <= 3, "Morphology must be -3 to +3 px.");
        Check(nameof(options.AlphaMultiplier), options.AlphaMultiplier is > 0 and <= 2, "Alpha multiplier must be 0-2.");
        Check(nameof(options.Engine), options.Engine is null or (CutoutEngine)0 or (CutoutEngine)1, "Unknown engine.");
        Check(nameof(options.TrimPaddingPx), options.TrimPaddingPx is >= 0 and <= 200, "Crop padding must be 0-200 px.");
        Check(nameof(options.HeadCutBiasPercent), options.HeadCutBiasPercent is >= -40 and <= 40, "Head cut must be -40 to +40 %.");

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
