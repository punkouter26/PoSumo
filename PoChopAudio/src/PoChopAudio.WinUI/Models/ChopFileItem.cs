using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PoChopAudio.Services.Dsp;
using PoChopAudio.Services.Chop;

namespace PoChopAudio.WinUI.Models;

public partial class ChopFileItem : ObservableObject
{
    /// <summary>
    /// The view model this card belongs to, so the buttons inside its DataTemplate can reach the
    /// commands with a compiled binding.
    ///
    /// <para>
    /// A DataTemplate has its own namescope, so <c>{Binding ElementName=...}</c> inside one cannot
    /// see the page and resolves to nothing at all — silently, which is how "Split again" once did
    /// nothing whatsoever with no error to show for it. <c>x:Bind</c> inside the template reaches
    /// only this object, so the route out has to hang off this object.
    /// </para>
    /// </summary>
    public ViewModels.ChopViewModel? Owner { get; init; }

    /// <summary>
    /// The service-side handle for this recording. <c>default</c> until the decode succeeds, which
    /// is what the export and re-split paths check before calling anything.
    /// </summary>
    [ObservableProperty]
    private JobId _jobId;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string? _localFilePath;

    [ObservableProperty]
    private long _sizeBytes;

    // AudioInfoText and TakesCountText are computed from these, so every field they read has to
    // raise a change for them too. Without this the card header stayed at "0 takes / 0.0s / 0 Hz"
    // while the waveform underneath correctly showed the takes that had just been detected.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioInfoText))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private double _durationSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioInfoText))]
    private int _sampleRate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioInfoText))]
    private int _channels;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioInfoText))]
    private double _peakDb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioInfoText))]
    private double _noiseFloorDb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioInfoText))]
    private double _detectedThresholdDb;

    [ObservableProperty]
    private IReadOnlyList<float> _waveform = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TakesCountText))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    [NotifyPropertyChangedFor(nameof(HasTakes))]
    private ObservableCollection<ChopSegment> _segments = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    private string? _warning;

    [ObservableProperty]
    private bool _usesOwnSettings;

    [ObservableProperty]
    private ChopKnobsModel _settings = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    private ItemProcessingStatus _status = ItemProcessingStatus.Queued;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private int? _playingSegmentIndex;

    [ObservableProperty]
    private double _playheadRatio;

    /// <summary>
    /// Frequency view, computed on demand. Null until the user asks for it: building one reads the
    /// whole canonical WAV back off disk and runs a few hundred FFTs, which is not work to do for
    /// every file in a batch on the chance that someone looks.
    /// </summary>
    [ObservableProperty]
    private SpectrogramData? _spectrogram;

    /// <summary>Whether this card is showing frequency rather than amplitude.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewModeGlyph))]
    [NotifyPropertyChangedFor(nameof(ViewModeLabel))]
    private bool _showSpectrogram;

    [ObservableProperty]
    private bool _isBuildingSpectrogram;

    public string ViewModeGlyph => ShowSpectrogram ? "" : "";

    public string ViewModeLabel => ShowSpectrogram ? "Show waveform" : "Show frequencies";

    public bool IsReady => Status == ItemProcessingStatus.Ready && Segments.Count > 0;

    public bool NeedsAttention => Warning is not null || (IsReady && Segments.Count != Settings.ExpectedSegments);

    public bool HasTakes => Segments.Count > 0;

    public string AudioInfoText => $"{DurationSeconds:F1}s · {SampleRate} Hz · {Channels} ch · Peak {PeakDb:F1} dB · Noise {NoiseFloorDb:F1} dB · Gate {DetectedThresholdDb:F1} dB";

    /// <summary>Short, plain-language summary for the record-first view: how long, how many sounds.</summary>
    public string SummaryText => Segments.Count == 1
        ? $"1 sound · {DurationSeconds:F1}s recording"
        : $"{Segments.Count} sounds · {DurationSeconds:F1}s recording";

    public string TakesCountText => Segments.Count == 1 ? "1 sound" : $"{Segments.Count} sounds";

    public int Version { get; set; } = 1;
}

