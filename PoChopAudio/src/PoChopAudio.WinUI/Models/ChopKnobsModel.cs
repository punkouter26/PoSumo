using CommunityToolkit.Mvvm.ComponentModel;
using PoChopAudio.Services.Chop;

namespace PoChopAudio.WinUI.Models;

public partial class ChopKnobsModel : ObservableObject
{
    [ObservableProperty]
    private int _expectedSegments = 5;

    [ObservableProperty]
    private double _minSegmentMs = 150;

    [ObservableProperty]
    private double _minGapMs = 250;

    [ObservableProperty]
    private double _padMs = 40;

    [ObservableProperty]
    private bool _autoThreshold = true;

    [ObservableProperty]
    private double _thresholdDb = ChopLimits.DefaultThresholdDb;

    [ObservableProperty]
    private double _trimSilenceMs = 0;

    public ChopOptions ToOptions() => new()
    {
        ExpectedSegments = ExpectedSegments,
        MinSegmentMs = MinSegmentMs,
        MinGapMs = MinGapMs,
        PadMs = PadMs,
        ThresholdDb = AutoThreshold ? null : ThresholdDb,
        TrimSilenceMs = TrimSilenceMs,
    };

    public void LoadFrom(ChopOptions options)
    {
        ExpectedSegments = options.ExpectedSegments;
        MinSegmentMs = options.MinSegmentMs;
        MinGapMs = options.MinGapMs;
        PadMs = options.PadMs;
        AutoThreshold = options.ThresholdDb is null;
        ThresholdDb = options.ThresholdDb ?? ChopLimits.DefaultThresholdDb;
        TrimSilenceMs = options.TrimSilenceMs;
    }

    public ChopKnobsModel Clone()
    {
        var clone = new ChopKnobsModel();
        clone.LoadFrom(ToOptions());
        return clone;
    }
}

