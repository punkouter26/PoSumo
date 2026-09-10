using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace PoChopAudio.WinUI.Services;

/// <summary>
/// Live camera preview and still capture.
///
/// WinUI 3 has no <c>CaptureElement</c> — that was UWP — so the viewfinder is built from a
/// <see cref="MediaFrameReader"/>: frames arrive as BGRA8 software bitmaps and the page pushes each
/// one into a <c>SoftwareBitmapSource</c>. Stills come from the most recent preview frame rather
/// than <c>CapturePhotoToStreamAsync</c>, which fights the frame reader for the device on many
/// webcams. Preview resolution is ample for a head shot and the capture is instant.
/// </summary>
public sealed class CameraService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private SoftwareBitmap? _latest;

    public bool IsRunning { get; private set; }

    /// <summary>
    /// Raised per frame with a BGRA8 premultiplied bitmap. The handler must copy what it needs and
    /// must not retain the instance — it is disposed as soon as the handler returns.
    /// </summary>
    public event Action<SoftwareBitmap>? FrameArrived;

    /// <summary>
    /// Brings the preview up. Serialized against <see cref="StopAsync"/>: the two used to run
    /// unsynchronized over the same <c>_capture</c> and <c>_reader</c> fields, and navigating away
    /// from the page and back fast enough to overlap them left one disposing what the other was
    /// still building. The result was an RO_E_CLOSED stowed exception that killed the process.
    /// </summary>
    public async Task<bool> StartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            return await StartCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> StartCoreAsync()
    {
        if (IsRunning) return true;

        try
        {
            var capture = new MediaCapture();
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                // Cpu memory is what makes frames arrive as SoftwareBitmap rather than a GPU surface.
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            });

            var source = capture.FrameSources
                .FirstOrDefault(s => s.Value.Info.SourceKind == MediaFrameSourceKind.Color).Value;

            if (source is null)
            {
                capture.Dispose();
                return false;
            }

            var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            reader.FrameArrived += OnFrameArrived;

            if (await reader.StartAsync() != MediaFrameReaderStartStatus.Success)
            {
                reader.FrameArrived -= OnFrameArrived;
                reader.Dispose();
                capture.Dispose();
                return false;
            }

            _capture = capture;
            _reader = reader;
            IsRunning = true;
            return true;
        }
        catch
        {
            // No camera, no permission, or the device is already held exclusively. The caller
            // reports it; everything else on the page keeps working. StopCoreAsync, not StopAsync:
            // this already holds the gate.
            await StopCoreAsync();
            return false;
        }
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap is null) return;

        // SoftwareBitmapSource only accepts Bgra8 premultiplied.
        var display = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                      && bitmap.BitmapAlphaMode == BitmapAlphaMode.Premultiplied
            ? SoftwareBitmap.Copy(bitmap)
            : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        var still = SoftwareBitmap.Copy(display);
        var previous = Interlocked.Exchange(ref _latest, still);
        previous?.Dispose();

        try
        {
            FrameArrived?.Invoke(display);
        }
        finally
        {
            display.Dispose();
        }
    }

    /// <summary>Encodes the most recent preview frame as PNG. Null when no frame has arrived yet.</summary>
    public async Task<byte[]?> CapturePngAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var latest = _latest;
            if (latest is null) return null;

            using var source = SoftwareBitmap.Copy(latest);
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);

            // The encoder rejects premultiplied alpha; the preview is opaque anyway.
            using var opaque = SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            encoder.SetSoftwareBitmap(opaque);
            await encoder.FlushAsync();

            var bytes = new byte[stream.Size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        IsRunning = false;

        // Detach the fields before touching them. OnFrameArrived runs on the camera thread and
        // reads nothing here, but CapturePngAsync and a re-entrant start both do: taking the
        // objects private first means neither can ever see a half-disposed reader.
        var reader = _reader;
        var capture = _capture;
        _reader = null;
        _capture = null;

        if (reader is not null)
        {
            reader.FrameArrived -= OnFrameArrived;
            try
            {
                await reader.StopAsync();
            }
            catch
            {
                // Already stopped or the device vanished; nothing left to do.
            }

            try
            {
                reader.Dispose();
            }
            catch
            {
                // The reader can already be closed underneath us; disposing twice is not fatal
                // but it does throw, and this runs on the UI thread where that ends the process.
            }
        }

        try
        {
            capture?.Dispose();
        }
        catch
        {
            // Same again: MediaCapture throws rather than no-oping on a second dispose.
        }

        Interlocked.Exchange(ref _latest, null)?.Dispose();
    }

    public void Dispose()
    {
        _ = StopAsync();
    }
}
