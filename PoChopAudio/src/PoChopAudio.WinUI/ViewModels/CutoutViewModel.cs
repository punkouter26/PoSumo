using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using PoChopAudio.Services.Cutout;
using PoChopAudio.Services.Dsp;
using PoChopAudio.WinUI.Common;
using PoChopAudio.WinUI.Models;
using PoChopAudio.WinUI.Services;
using Windows.Storage.Pickers;

namespace PoChopAudio.WinUI.ViewModels;

/// <summary>
/// Take a photo, cut it out, save it. That is the whole page, so this is the whole view model —
/// the batch knobs, re-processing, file picking and ZIP export went with the controls that drove
/// them.
/// </summary>
public partial class CutoutViewModel : ObservableObject, IDisposable
{
    private readonly CutoutService _cutout;
    private readonly CameraService _camera;
    private readonly AppSettingsService _settings;
    private readonly AudioCueService _cues;
    private readonly CancellationTokenSource _cts = new();

    public CutoutViewModel(
        CutoutService cutout,
        CameraService camera,
        AppSettingsService settings,
        AudioCueService cues)
    {
        _cutout = cutout;
        _camera = camera;
        _settings = settings;
        _cues = cues;
    }

    public AudioCueService Cues => _cues;

    public ObservableCollection<CutoutFileItem> Files { get; } = [];

    /// <summary>
    /// The window file pickers hang off. Set by the page once it is loaded, because
    /// <c>App.MainWindow</c> does not exist yet while the page is being constructed. Holding it
    /// here is what lets the save buttons bind commands instead of Click handlers.
    /// </summary>
    public Window? Host { get; set; }

    /// <summary>The fine-tune knobs. Changing them does nothing until Re-apply is pressed.</summary>
    public CutoutTuning Tuning { get; } = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isCapturing;

    /// <summary>The live camera. The page binds its frames straight to a SoftwareBitmapSource.</summary>
    public CameraService Camera => _camera;

    /// <summary>
    /// Whether the viewfinder is live. Deliberately a projection of <see cref="CameraService"/>'s
    /// own flag rather than a second copy of it: when this was an <c>[ObservableProperty]</c> the
    /// two drifted apart the moment anything stopped the camera without going through
    /// <see cref="StopCameraAsync"/>. <see cref="StartCameraAsync"/> then saw "already running",
    /// returned early, and the page bound its <c>Image</c> to a reader that had been disposed —
    /// which took the process down with RO_E_CLOSED on the second visit to this page. One owner,
    /// no possible disagreement.
    /// </summary>
    public bool IsCameraRunning => _camera.IsRunning;

    public bool HasFiles => Files.Count > 0;

    /// <summary>False when u2netp.onnx is missing, which the page says out loud.</summary>
    public bool IsCutoutAvailable => _cutout.IsAvailable;

    /// <summary>Brings the viewfinder up. Safe to call repeatedly.</summary>
    public async Task<bool> StartCameraAsync()
    {
        if (IsCameraRunning) return true;

        try
        {
            if (await _camera.StartAsync())
            {
                OnPropertyChanged(nameof(IsCameraRunning));
                return true;
            }
        }
        finally
        {
            OnPropertyChanged(nameof(IsCameraRunning));
        }

        ErrorMessage = "Could not start the camera. Check that one is connected and that this app has camera permission.";
        return false;
    }

    /// <summary>
    /// The only way the page may stop the camera. Calling <c>Camera.StopAsync()</c> directly is
    /// what caused the crash described on <see cref="IsCameraRunning"/>.
    /// </summary>
    public async Task StopCameraAsync()
    {
        await _camera.StopAsync();
        OnPropertyChanged(nameof(IsCameraRunning));
    }

    /// <summary>
    /// Takes one frame and sends it straight to the results below. There is nothing to configure
    /// and nothing to confirm — the shot appears in the list and starts cutting itself out.
    /// </summary>
    [RelayCommand]
    public async Task TakePhotoAsync()
    {
        if (IsCapturing) return;

        IsCapturing = true;
        ErrorMessage = null;

        try
        {
            // Starting on demand means the button works from a cold page without a separate
            // "start camera" step; a running camera makes this a no-op.
            if (!IsCameraRunning && !await StartCameraAsync())
            {
                return;
            }

            var png = await _camera.CapturePngAsync();
            if (png is null || png.Length == 0)
            {
                ErrorMessage = "Could not capture a frame from the camera.";
                return;
            }

            await AddPhotoAsync(png);
        }
        finally
        {
            IsCapturing = false;
        }
    }

    [RelayCommand]
    public void ClearAll()
    {
        Files.Clear();
        ErrorMessage = null;
        OnPropertyChanged(nameof(HasFiles));
    }

    /// <summary>
    /// Cuts out images that already exist on disk instead of shooting them with the camera.
    ///
    /// The page used to be camera-only, which made it useless for a set of photos that was already
    /// taken — the source images for a character's face ladder, say, which arrive named
    /// <c>&lt;Name&gt;_Happy_1</c> and so carry the naming the app is otherwise asking the user to
    /// type. There is no second pipeline behind this: a picked file becomes the same in-memory
    /// bytes a capture becomes, and <see cref="AddPhotoAsync(byte[], string)"/> is shared.
    /// </summary>
    [RelayCommand]
    public async Task ImportPhotosAsync()
    {
        if (Host is null) return;

        ErrorMessage = null;
        var picker = new FileOpenPicker();
        WindowHelper.InitWithWindow(picker, Host);
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;

        foreach (var extension in ImageDecoder.SupportedExtensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        var picked = await picker.PickMultipleFilesAsync();
        if (picked is null || picked.Count == 0) return;

        await ImportPathsAsync(picked.Select(file => file.Path));
    }

    /// <summary>
    /// The drag-and-drop and picker entry point. Paths, not <c>StorageFile</c>s, because the
    /// drop handler only ever has paths — and because reading through <see cref="File"/> keeps
    /// this on the same code path the picker uses.
    /// </summary>
    public async Task ImportPathsAsync(IEnumerable<string> filePaths)
    {
        ErrorMessage = null;

        var available = CutoutLimits.MaxBatchFiles - Files.Count;
        if (available <= 0)
        {
            ErrorMessage = $"Batch limit reached (maximum {CutoutLimits.MaxBatchFiles} photos allowed).";
            return;
        }

        // Only formats the decoder actually reads, checked here rather than inside the pipeline so
        // a drop of a whole folder reports "3 of 12 skipped" instead of twelve failed rows.
        var accepted = filePaths
            .Where(path => !string.IsNullOrEmpty(path))
            .Where(path => ImageDecoder.IsSupportedExtension(Path.GetExtension(path)))
            .ToList();

        var skipped = filePaths.Count(path => !string.IsNullOrEmpty(path)) - accepted.Count;
        var toAdd = accepted.Take(available).ToList();

        if (toAdd.Count == 0)
        {
            ErrorMessage = accepted.Count == 0
                ? "None of those files are an image format this app reads."
                : $"Batch limit reached (maximum {CutoutLimits.MaxBatchFiles} photos allowed).";
            return;
        }

        IsBusy = true;
        try
        {
            foreach (var path in toAdd)
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(path, _cts.Token);
                    await AddPhotoAsync(bytes, Path.GetFileNameWithoutExtension(path) + ".png");
                }
                catch (Exception exception)
                {
                    // One unreadable file is a flagged row, never a fatal batch — the same rule the
                    // audio side holds to.
                    ErrorMessage = $"Could not read {Path.GetFileName(path)}: {exception.Message}";
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        if (skipped > 0)
        {
            ErrorMessage = $"Skipped {skipped} file(s) that are not a supported image format.";
        }
    }

    [RelayCommand]
    public async Task SaveAllToFolderAsync()
    {
        if (Host is null) return;

        var ready = Files.Where(f => f.IsReady).ToList();
        if (ready.Count == 0) return;

        var folderPath = await ExportService.ResolveBatchFolderAsync(Host, _settings);
        if (string.IsNullOrEmpty(folderPath)) return;

        IsBusy = true;

        try
        {
            foreach (var item in ready)
            {
                var target = Path.Combine(folderPath, item.FileName);
                await ExportService.SaveBytesToFileAsync(item.CutoutPngBytes!, target, _cts.Token);
            }

            _cues.Play(AudioCue.GrandSuccess);
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Failed to save images: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task SaveSingleCutoutAsync(CutoutFileItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (Host is null || item.CutoutPngBytes is null) return;

        var savePath = await ExportService.PickSaveFileAsync(Host, item.FileName, ".png", "PNG Image");
        if (string.IsNullOrEmpty(savePath)) return;

        try
        {
            await ExportService.SaveBytesToFileAsync(item.CutoutPngBytes, savePath, _cts.Token);
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Failed to save image: {exception.Message}";
        }
    }

    /// <summary>
    /// Re-cuts every photo with the current knob settings, from the frame as captured. This is why
    /// the original bytes are kept: re-cutting the cutout would compound the previous settings.
    /// </summary>
    [RelayCommand]
    public async Task ReapplyAllAsync()
    {
        if (IsBusy || Files.Count == 0) return;

        IsBusy = true;
        ErrorMessage = null;

        try
        {
            foreach (var item in Files.ToList())
            {
                if (item.OriginalPngBytes is null) continue;
                await CutOutIntoAsync(item, item.OriginalPngBytes);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task ResetTuningAsync()
    {
        Tuning.Reset();
        await ReapplyAllAsync();
    }

    /// <summary>Adds one in-memory photo and cuts it out. Nothing touches disk or a network.</summary>
    private async Task AddPhotoAsync(byte[] png)
    {
        await AddPhotoAsync(png, $"photo_{Files.Count + 1}.png");
    }

    /// <summary>
    /// The one place a photo enters the batch, whether it came off the camera or an imported file.
    /// </summary>
    /// <param name="fileName">
    /// Name the cut-out is saved under. For an imported file this is the source name with a
    /// <c>.png</c> extension, so <c>grandma_happy1.jpg</c> saves as <c>grandma_happy1.png</c> and a
    /// batch of takes arrives already named — which is the whole point of importing rather than
    /// photographing a screen.
    /// </param>
    private async Task AddPhotoAsync(byte[] png, string fileName)
    {
        var item = new CutoutFileItem
        {
            Owner = this,
            FileName = fileName,
            Bytes = png.Length,
            Status = ItemProcessingStatus.Analyzing,
            OriginalPngBytes = png,
        };

        try
        {
            using var preview = new MemoryStream(png);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(preview.AsRandomAccessStream());
            item.OriginalImage = bitmap;
        }
        catch
        {
            // Thumbnail only; the cutout below does not depend on it.
        }

        Files.Add(item);
        OnPropertyChanged(nameof(HasFiles));

        await CutOutIntoAsync(item, png);
    }

    /// <summary>Runs the pipeline over <paramref name="png"/> and puts the result on the item.</summary>
    private async Task CutOutIntoAsync(CutoutFileItem item, byte[] png)
    {
        item.Status = ItemProcessingStatus.Analyzing;
        item.ErrorMessage = null;

        try
        {
            using var source = new MemoryStream(png);
            var outcome = await _cutout.CutOutAsync(
                source, item.FileName, png.Length, Tuning.ToOptions(), _cts.Token);

            if (!outcome.IsSuccess)
            {
                item.Status = ItemProcessingStatus.Failed;
                item.ErrorMessage = outcome.Message;
                ErrorMessage = outcome.Message;
                return;
            }

            var photo = outcome.Value;
            item.CutoutPngBytes = photo.Png;
            item.Width = photo.Width;
            item.Height = photo.Height;
            item.Bytes = photo.Png.Length;

            using var cutoutStream = new MemoryStream(photo.Png);
            var cutoutBitmap = new BitmapImage();
            await cutoutBitmap.SetSourceAsync(cutoutStream.AsRandomAccessStream());
            item.CutoutImage = cutoutBitmap;

            item.Status = ItemProcessingStatus.Ready;
            _cues.Play(AudioCue.ScanReveal);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            item.Status = ItemProcessingStatus.Failed;
            item.ErrorMessage = exception.Message;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _camera.Dispose();
    }
}
