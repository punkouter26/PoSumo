using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PoChopAudio.Services.Chop;
using PoChopAudio.Services.Dsp;
using PoChopAudio.WinUI.Common;
using PoChopAudio.WinUI.Models;
using PoChopAudio.WinUI.Services;
using PoChopAudio.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace PoChopAudio.WinUI.Views;

/// <summary>
/// The chop page.
///
/// <para>
/// Every button here binds a command. The page used to answer half of them with <c>async void</c>
/// Click handlers instead, which swallowed any exception thrown past the first <c>await</c> and
/// bypassed the commands' own CanExecute — so the same button was guarded on one code path and
/// unguarded on the other. What is left in this file is the work XAML genuinely cannot express:
/// drag-and-drop, the accessibility wiring, and the two Win2D surfaces that are driven imperatively.
/// </para>
/// </summary>
public sealed partial class ChopPage : Page
{
    public ChopViewModel ViewModel { get; }

    public ChopPage()
    {
        ViewModel = App.GetService<ChopViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // App.MainWindow does not exist while the page is being constructed — the frame navigates
        // here from the window's own constructor — so the pickers' owner is wired up on load.
        ViewModel.Host = App.MainWindow;

        // A screen reader otherwise announces "Start recording, disabled" and stops there: the
        // sentence saying what to do about it is a sibling TextBlock it would never reach.
        var describedBy = AutomationProperties.GetDescribedBy(RecordBtn);
        if (!describedBy.Contains(RecordDisabledReason))
        {
            describedBy.Add(RecordDisabledReason);
        }

        // InputScopeView is driven imperatively rather than by binding: it paints a scrolling trace,
        // a peak-hold marker and a numeric readout from two different feeds - level figures on the
        // recording view model, and raw decimated samples straight off the capture thread.
        ViewModel.Recording.PropertyChanged += OnRecordingPropertyChanged;
        ViewModel.BatchCompleted += OnBatchCompleted;
        App.GetService<AudioRecorderService>().ScopeSamplesAvailable += OnScopeSamples;

        Motion.EnableCardTilt(RecordingCard, () => ViewModel.Cues.Play(AudioCue.CardHover));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Recording.PropertyChanged -= OnRecordingPropertyChanged;
        ViewModel.BatchCompleted -= OnBatchCompleted;
        App.GetService<AudioRecorderService>().ScopeSamplesAvailable -= OnScopeSamples;
    }

    private void OnRecordingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var recording = ViewModel.Recording;

        switch (e.PropertyName)
        {
            case nameof(RecordingViewModel.PeakDb):
            case nameof(RecordingViewModel.RmsDb):
            case nameof(RecordingViewModel.IsClipping):
                MicScope.UpdateLevel(recording.PeakDb, recording.RmsDb, recording.IsClipping);
                break;

            case nameof(RecordingViewModel.IsRecording):
                // Confetti while the microphone is open would be CPU taken from the capture path,
                // which shows up as dropped frames in someone's take.
                Confetti.IsSuppressed = recording.IsRecording;

                if (!recording.IsRecording)
                {
                    MicScope.Reset();
                }

                break;

            case nameof(RecordingViewModel.Countdown):
                if (ViewModel.Recording.Countdown > 0)
                {
                    Motion.Pulse(CountdownNumber, 1.35f, 220);
                }

                break;
        }
    }

    /// <summary>Pushes captured audio into the live scope. Arrives on the capture thread.</summary>
    private void OnScopeSamples(float[] points) => MicScope.Push(points);

    private void OnBatchCompleted(bool succeeded)
    {
        if (succeeded)
        {
            Confetti.Burst();
        }
    }

    /// <summary>Gives each card its entrance, repositioning, and 2.5D tilt animations as it is realised.</summary>
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

    private void OnWaveformScrubbed(ChopFileItem item, double seconds) => ViewModel.Seek(item, seconds);

    /// <summary>
    /// A click on the waveform itself. Routed through the same commands the buttons use, so a
    /// failure surfaces the same way whichever way the take was started.
    /// </summary>
    private void OnWaveformSegmentClicked(ChopFileItem item, ChopSegment? segment)
    {
        if (segment is null)
        {
            ViewModel.PlayWholeRecordingCommand.Execute(item);
        }
        else
        {
            ViewModel.PlaySegmentCommand.Execute(segment);
        }
    }

    /// <summary>
    /// The two buttons on a take row.
    ///
    /// <para>
    /// Every other button on this page binds its command. These cannot: they live in a nested
    /// DataTemplate whose item is a <see cref="ChopSegment"/> — a record in Services with no route
    /// back to the view model — and a DataTemplate's own namescope means an ElementName binding out
    /// to the page resolves to nothing. A plain void handler that executes the same command the
    /// rest of the page binds is the honest version: no async void, and the command's own error
    /// handling still applies.
    /// </para>
    /// </summary>
    private void OnPlaySegmentClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChopSegment segment })
        {
            ViewModel.PlaySegmentCommand.Execute(segment);
        }
    }

    private void OnSaveSegmentClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChopSegment segment })
        {
            ViewModel.SaveSegmentCommand.Execute(segment);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add to audio batch";
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
                var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
                if (paths.Count > 0)
                {
                    await ViewModel.AddFilesAsync(paths);
                }
            }
        }
        catch (Exception exception)
        {
            ViewModel.ErrorMessage = $"Could not add the dropped files: {exception.Message}";
        }
    }
}
