using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using PoChopAudio.WinUI.Services;

namespace PoChopAudio.WinUI.ViewModels;

/// <summary>
/// Capturing one take: the name it will be filed under, the count-in, the input meter, and the
/// bytes that come back when the user presses Stop.
///
/// <para>
/// Split out of <see cref="ChopViewModel"/>, which had grown to hold four features at once —
/// recording, playback, analysis and export — sharing nothing but an error string. Recording was
/// the cleanest seam: it owns the two services nothing else touches and hands the rest of the page
/// a single result through <see cref="TakeRecorded"/>.
/// </para>
/// <para>
/// It knows nothing about files, jobs or clips. What happens to a finished take is the chop view
/// model's problem.
/// </para>
/// </summary>
public partial class RecordingViewModel : ObservableObject, IDisposable
{
    private readonly AudioRecorderService _recorder;
    private readonly AudioCueService _cues;
    private readonly DispatcherQueue? _dispatcher = DispatcherQueue.GetForCurrentThread();

    public RecordingViewModel(AudioRecorderService recorder, AudioCueService cues)
    {
        _recorder = recorder;
        _cues = cues;

        // Both callbacks arrive on a background thread — LevelUpdated on NAudio's capture thread,
        // ElapsedUpdated on a System.Timers.Timer thread. Setting observable properties there
        // raises PropertyChanged off the UI thread, which the XAML bindings cannot act on: the
        // level meter stayed at -inf dB and the elapsed clock sat at 00:00 for the whole take.
        // Marshal first, then set.
        _recorder.LevelUpdated += (peak, rms, clip) => OnUiThread(() =>
        {
            PeakDb = peak;
            RmsDb = rms;
            IsClipping = clip;
            var normEnergy = (float)Math.Clamp((rms + 60.0) / 60.0, 0.0, 1.0);
            Controls.ShaderBackdrop.ReportAudioEnergy(normEnergy, clip);
        });

        _recorder.ElapsedUpdated += elapsed => OnUiThread(() =>
        {
            RecordingElapsed = $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        });
    }

    /// <summary>Raised on the UI thread with the captured WAV and the file name to give it.</summary>
    public event Action<byte[], string>? TakeRecorded;

    /// <summary>A line for the page's status area. Kept as an event so this class owns no page state.</summary>
    public event Action<string>? StatusReported;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    [NotifyPropertyChangedFor(nameof(CanStartRecording))]
    [NotifyPropertyChangedFor(nameof(NeedsRecordingName))]
    private bool _isRecording;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    [NotifyPropertyChangedFor(nameof(CanStartRecording))]
    [NotifyPropertyChangedFor(nameof(NeedsRecordingName))]
    private string _recordingName = string.Empty;

    [ObservableProperty]
    private string _recordingElapsed = "00:00";

    /// <summary>
    /// The visible count-in. Seconds remaining before capture starts, 0 when not counting in.
    /// <para>
    /// This was set to 3, 2, 1 and bound by nothing, so pressing Record produced three full
    /// seconds of no feedback at all — and, because cue sounds default to off, no audible
    /// feedback either. The page now shows it, and <see cref="IsCountingIn"/> keeps the button
    /// from being pressed a second time part-way through.
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    [NotifyPropertyChangedFor(nameof(CanStartRecording))]
    [NotifyPropertyChangedFor(nameof(NeedsRecordingName))]
    [NotifyPropertyChangedFor(nameof(IsCountingIn))]
    [NotifyPropertyChangedFor(nameof(CountdownText))]
    private int _countdown;

    [ObservableProperty]
    private double _peakDb = -100;

    [ObservableProperty]
    private double _rmsDb = -100;

    [ObservableProperty]
    private bool _isClipping;

    /// <summary>True while the count-in is running, between Record being pressed and capture.</summary>
    public bool IsCountingIn => Countdown > 0;

    public string CountdownText => Countdown > 0 ? Countdown.ToString() : string.Empty;

    /// <summary>
    /// A take has to be named before it can be recorded. The name becomes the WAV's filename and
    /// every clip stem chopped out of it, so an unnamed take lands as an opaque Take_[timestamp]
    /// that is impossible to tell apart from the next one in a batch.
    /// </summary>
    public bool CanStartRecording =>
        !IsRecording && !IsCountingIn && !string.IsNullOrWhiteSpace(RecordingName);

    /// <summary>True only when the missing name is what is holding recording up, so the hint does
    /// not stay on screen while a take is already running or counting in.</summary>
    public bool NeedsRecordingName =>
        !IsRecording && !IsCountingIn && string.IsNullOrWhiteSpace(RecordingName);

    [RelayCommand(CanExecute = nameof(CanStartRecording))]
    public async Task StartRecordingAsync()
    {
        if (!CanStartRecording)
        {
            return;
        }

        // The audible count-in is rendered as one buffer and started here, so its beats cannot
        // drift; the visible numbers are then stepped alongside it. When cue sounds are off this
        // degrades to exactly the silent countdown it replaced.
        const int beats = 3;
        const double bpm = 60;
        _cues.PlayCountIn(beats, bpm);

        try
        {
            for (var c = beats; c > 0; c--)
            {
                Countdown = c;
                await Task.Delay(TimeSpan.FromSeconds(60.0 / bpm));
            }
        }
        finally
        {
            Countdown = 0;
        }

        // Nothing may make a sound from here until Stop: a cue that leaks into a take does not
        // annoy the user, it corrupts their recording.
        _cues.IsSuppressed = true;
        _recorder.Start();
        IsRecording = true;
    }

    [RelayCommand]
    public void StopRecording()
    {
        if (!IsRecording)
        {
            return;
        }

        IsRecording = false;
        var wavBytes = _recorder.Stop();
        _cues.IsSuppressed = false;

        if (wavBytes.Length == 0)
        {
            return;
        }

        var stem = string.IsNullOrWhiteSpace(RecordingName)
            ? $"Take_{DateTime.Now:yyyyMMdd_HHmmss}"
            : RecordingName.Trim();

        TakeRecorded?.Invoke(wavBytes, $"{stem}.wav");
    }

    /// <summary>
    /// Plays a 1 kHz tone at -18 dBFS so the input meter can be read against a known level before a
    /// take rather than after one.
    /// </summary>
    [RelayCommand]
    public void PlayReferenceTone()
    {
        if (IsRecording)
        {
            return;
        }

        _cues.PlayReferenceTone();
        StatusReported?.Invoke("Playing a 1 kHz reference tone at -18 dBFS.");
    }

    /// <summary>
    /// Throws away a take in progress rather than finishing it, and clears the pending name.
    ///
    /// <para>
    /// Un-suppresses cues unconditionally, not only when a take was running: only the stop path
    /// used to lift the flag, so abandoning part-way through left every cue silenced for the rest
    /// of the session with nothing on screen to explain why.
    /// </para>
    /// </summary>
    public void Abandon()
    {
        if (IsRecording)
        {
            IsRecording = false;
            _ = _recorder.Stop();
        }

        _cues.IsSuppressed = false;
        Countdown = 0;
        RecordingName = string.Empty;
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread, inline when already there.</summary>
    private void OnUiThread(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.TryEnqueue(() => action());
        }
    }

    public void Dispose() => _recorder.Dispose();
}
