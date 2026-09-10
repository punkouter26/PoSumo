using System.Runtime.InteropServices.WindowsRuntime;
using PoChopAudio.Services.Cutout;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;

namespace PoChopAudio.WinUI.Services;

/// <summary>
/// Face detection through <see cref="FaceDetector"/>, which ships with Windows.
///
/// <para>
/// This costs nothing to ship: the OS already has a face detector, so there is no second ONNX
/// model to download and no extra package. The "no second model" rule in
/// <see cref="HeadFinder"/> is about not adding a dependency, and this adds none.
/// </para>
/// <para>
/// Follows the optional-capability pattern: probe once, report through
/// <see cref="IsAvailable"/>, and degrade to the mask-shape logic rather than throwing.
/// </para>
/// </summary>
public sealed class WindowsFaceLocator : IFaceLocator
{
    /// <summary>
    /// The detector, created once and awaited rather than blocked on.
    ///
    /// <para>
    /// This used to be a <c>Lazy&lt;FaceDetector?&gt;</c> that called
    /// <c>CreateAsync().GetAwaiter().GetResult()</c>. That runs on whichever thread asks first,
    /// which for the cutout path is the UI thread, and blocking it on a WinRT async operation is
    /// how a window stops repainting. Caching the Task instead means the construction happens once
    /// and every caller awaits the same one.
    /// </para>
    /// </summary>
    private readonly Lazy<Task<FaceDetector?>> _detector = new(CreateDetectorAsync);

    /// <summary>
    /// Whether the OS ships the component at all. Deliberately a cheap static check rather than
    /// "did construction succeed": the diagnostics page reads this, and a property getter is no
    /// place to wait on a device to spin up. A detector that fails to construct still degrades
    /// correctly, because <see cref="LocateAsync"/> returns null when it has none.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            try
            {
                return FaceDetector.IsSupported;
            }
            catch (Exception)
            {
                // A machine without the face-analysis component is a supported configuration.
                return false;
            }
        }
    }

    private static async Task<FaceDetector?> CreateDetectorAsync()
    {
        try
        {
            return FaceDetector.IsSupported
                ? await FaceDetector.CreateAsync().AsTask().ConfigureAwait(false)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<FaceBox?> LocateAsync(
        byte[] rgba, int width, int height, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rgba);

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var detector = await _detector.Value.ConfigureAwait(false);
        if (detector is null)
        {
            return null;
        }

        try
        {
            // FaceDetector only accepts a handful of pixel formats, and Gray8 is the one it is
            // guaranteed to take. Detection is luminance-based anyway, so nothing is lost.
            var format = BitmapPixelFormat.Gray8;
            if (!FaceDetector.GetSupportedBitmapPixelFormats().Contains(format))
            {
                format = FaceDetector.GetSupportedBitmapPixelFormats().FirstOrDefault();
                if (format == default)
                {
                    return null;
                }
            }

            using var gray = ToGray8(rgba, width, height);
            cancellationToken.ThrowIfCancellationRequested();

            var faces = await detector.DetectFacesAsync(gray).AsTask(cancellationToken).ConfigureAwait(false);
            if (faces is null || faces.Count == 0)
            {
                return null;
            }

            // Largest face wins: on a head shot there is one subject, and any extra detection is
            // a bystander or a false positive, both smaller than the person in front of the camera.
            var best = faces
                .OrderByDescending(f => (long)f.FaceBox.Width * f.FaceBox.Height)
                .First()
                .FaceBox;

            return new FaceBox((int)best.X, (int)best.Y, (int)best.Width, (int)best.Height);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Detection failing is not the caller's problem; the mask-shape fallback covers it.
            return null;
        }
    }

    private static SoftwareBitmap ToGray8(byte[] rgba, int width, int height)
    {
        var gray = new byte[width * height];
        for (var i = 0; i < gray.Length; i++)
        {
            var p = i * 4;
            // Rec. 601 luma, the weighting face detection expects.
            gray[i] = (byte)(((rgba[p] * 299) + (rgba[p + 1] * 587) + (rgba[p + 2] * 114)) / 1000);
        }

        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Gray8, width, height, BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(gray.AsBuffer());
        return bitmap;
    }
}
