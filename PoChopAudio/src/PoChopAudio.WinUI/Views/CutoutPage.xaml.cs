using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using PoChopAudio.Services.Dsp;
using PoChopAudio.WinUI.Common;
using PoChopAudio.WinUI.Models;
using PoChopAudio.WinUI.Services;
using PoChopAudio.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;

namespace PoChopAudio.WinUI.Views;

public sealed partial class CutoutPage : Page
{
    /// <summary>
    /// The viewfinder's pixel sink, rebuilt on every load rather than shared across them.
    /// <para>
    /// This used to be one instance created in the field initializer and handed to
    /// <c>PreviewImage.Source</c> in the constructor. Because the page is
    /// <c>NavigationCacheMode="Required"</c> that single source outlived every unload — and a
    /// <c>SoftwareBitmapSource</c> does not survive its <c>Image</c> leaving the visual tree. The
    /// second visit pushed frames into a source whose composition surface was gone, which faults
    /// inside Microsoft.UI.Xaml.dll as a stowed 0xC000027B: native, so no managed handler ever saw
    /// it and the log stayed empty. One source per load, disposed with the page.
    /// </para>
    /// </summary>
    private SoftwareBitmapSource? _previewSource;

    /// <summary>
    /// Guards the viewfinder against frame pile-up. Frames arrive faster than
    /// <see cref="SoftwareBitmapSource.SetBitmapAsync"/> completes, so without this the queue grows
    /// without bound and the preview drifts seconds behind the room. Dropping frames is correct
    /// here — only the newest one is worth showing.
    /// </summary>
    private int _previewBusy;

    /// <summary>
    /// Whether <see cref="OnCameraFrame"/> is currently subscribed. The subscription used to be
    /// taken in the constructor and dropped in <c>Unloaded</c>, which is not a pair: this page is
    /// <c>NavigationCacheMode="Required"</c>, so the instance is reused and the constructor never
    /// runs a second time. Coming back to the page left a live camera with nobody listening.
    /// Loaded/Unloaded is the symmetric pair; this flag keeps it idempotent if Loaded fires twice.
    /// </summary>
    private bool _frameHandlerAttached;

    public CutoutViewModel ViewModel { get; }

    public CutoutPage()
    {
        ViewModel = App.GetService<CutoutViewModel>();
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // App.MainWindow does not exist while the page is being constructed — the frame navigates
        // here from the window's own constructor — so the pickers' owner is wired up on load.
        ViewModel.Host = App.MainWindow;

        _previewSource = new SoftwareBitmapSource();
        PreviewImage.Source = _previewSource;

        // A frame that was mid-flight when the page unloaded never ran its continuation, so the
        // guard could be left latched and the new viewfinder would take no frames at all.
        Interlocked.Exchange(ref _previewBusy, 0);

        if (!_frameHandlerAttached)
        {
            ViewModel.Camera.FrameArrived += OnCameraFrame;
            _frameHandlerAttached = true;
        }

        // Bring the viewfinder up straight away so TAKE PHOTO is the only thing to click.
        //
        // Guarded because this is an async void handler: StartCameraAsync has a try/finally but no
        // catch, so a device that faults on open would throw past the await into the runtime's
        // unhandled hook and take the process down on page load.
        try
        {
            await ViewModel.StartCameraAsync();
        }
        catch (Exception exception)
        {
            ViewModel.ErrorMessage = $"Could not start the camera: {exception.Message}";
        }
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Order matters: stop the frames, then take the sink down. The other way round leaves a
        // window in which a frame is handed to a source that is already being disposed.
        if (_frameHandlerAttached)
        {
            ViewModel.Camera.FrameArrived -= OnCameraFrame;
            _frameHandlerAttached = false;
        }

        // Through the view model, never CameraService directly: the view model's IsCameraRunning
        // is what the viewfinder binds to, and stopping behind its back left the page bound to a
        // disposed frame reader. Guarded for the same reason as OnLoaded — and here a throw would
        // also leave the sink below undisposed.
        try
        {
            await ViewModel.StopCameraAsync();
        }
        catch (Exception)
        {
            // Navigating away from a camera that is already gone is not worth reporting.
        }

        var source = _previewSource;
        _previewSource = null;
        PreviewImage.Source = null;
        source?.Dispose();
    }

    private void OnCameraFrame(SoftwareBitmap frame)
    {
        if (Interlocked.CompareExchange(ref _previewBusy, 1, 0) != 0)
        {
            return;
        }

        // The frame belongs to the camera thread and is disposed the moment this returns, so copy
        // before handing it to the UI thread. The copy itself can still throw RO_E_CLOSED if the
        // reader was torn down mid-frame — on the camera thread, where nothing would catch it.
        SoftwareBitmap copy;
        try
        {
            copy = SoftwareBitmap.Copy(frame);
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref _previewBusy, 0);
            return;
        }

        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    // Re-read on the UI thread: Unloaded may have cleared it since the enqueue.
                    var sink = _previewSource;
                    if (sink is not null)
                    {
                        await sink.SetBitmapAsync(copy);
                    }
                }
                catch
                {
                    // The page went away between frames.
                }
                finally
                {
                    copy.Dispose();
                    Interlocked.Exchange(ref _previewBusy, 0);
                }
            }))
        {
            copy.Dispose();
            Interlocked.Exchange(ref _previewBusy, 0);
        }
    }


    /// <summary>
    /// The shutter acknowledgement: a brief white flash over the viewfinder and a small burst.
    /// Runs alongside the command rather than inside the view model, because none of it is state -
    /// it is feedback that the frame was taken, at the moment it was taken.
    /// </summary>
    private void OnTakePhotoVisualFeedback(object sender, RoutedEventArgs e)
    {
        Motion.Pulse(TakePhotoButton, to: 0.98f);

        if (!Motion.AnimationsEnabled)
        {
            return;
        }

        var flash = new Storyboard();
        var fade = new DoubleAnimationUsingKeyFrames();

        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0.85 });
        fade.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = TimeSpan.FromMilliseconds(320),
            Value = 0,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

        Storyboard.SetTarget(fade, ShutterFlash);
        Storyboard.SetTargetProperty(fade, "Opacity");
        flash.Children.Add(fade);
        flash.Begin();

        Confetti.Burst(48);
    }

    /// <summary>Gives each result card its entrance, repositioning, and 2.5D tilt animations.</summary>
    private void OnCardVisualLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Parent: FrameworkElement card })
        {
            Motion.EnableListItemAnimations(card);
            Motion.EnableCardTilt(card, () => ViewModel.Cues.Play(AudioCue.CardHover));
        }
    }

    private void OnExpanderExpanding(Expander sender, ExpanderExpandingEventArgs args) =>
        ViewModel.Cues.Play(AudioCue.GlassTapOpen);

    private void OnExpanderCollapsed(Expander sender, ExpanderCollapsedEventArgs args) =>
        ViewModel.Cues.Play(AudioCue.GlassTapClose);

    private DateTimeOffset _lastPluckTime = DateTimeOffset.MinValue;
    private static readonly double[] PentatonicScale = [261.63, 293.66, 329.63, 392.00, 440.00, 523.25, 587.33, 659.25, 783.99, 880.00, 1046.50];

    private void OnSliderValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (sender is not Slider slider)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if ((now - _lastPluckTime).TotalMilliseconds < 65)
        {
            return;
        }

        var span = slider.Maximum - slider.Minimum;
        if (span <= 0)
        {
            return;
        }

        var norm = Math.Clamp((e.NewValue - slider.Minimum) / span, 0.0, 1.0);
        var index = Math.Clamp((int)(norm * (PentatonicScale.Length - 1)), 0, PentatonicScale.Length - 1);
        ViewModel.Cues.PlayPluck(PentatonicScale[index], 0.16f);
        _lastPluckTime = now;
    }

    /// <summary>
    /// Accepts a drop anywhere on the page. The caption names what will happen rather than saying
    /// "copy", because the result is destructive of the background, not a copy of a file.
    /// </summary>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Remove the background";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        // Drop has no command equivalent: the paths only exist inside the event args. Everything
        // inside is guarded, because an exception escaping an async void handler reaches the
        // runtime's unhandled hook and takes the process down.
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var paths = items.Select(item => item.Path).Where(path => !string.IsNullOrEmpty(path)).ToList();
                if (paths.Count > 0)
                {
                    await ViewModel.ImportPathsAsync(paths);
                }
            }
        }
        catch (Exception exception)
        {
            ViewModel.ErrorMessage = $"Could not add the dropped files: {exception.Message}";
        }
    }
}
