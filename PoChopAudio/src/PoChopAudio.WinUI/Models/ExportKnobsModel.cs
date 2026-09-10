using CommunityToolkit.Mvvm.ComponentModel;
using PoChopAudio.Services.Chop;

namespace PoChopAudio.WinUI.Models;

public partial class ExportKnobsModel : ObservableObject
{
    [ObservableProperty]
    private NormalizeMode _normalize = NormalizeMode.None;

    [ObservableProperty]
    private double _targetDb = ExportLimits.DefaultPeakTargetDb;

    [ObservableProperty]
    private double _ceilingDb = ExportLimits.DefaultCeilingDb;

    [ObservableProperty]
    private double _fadeInMs = 0;

    [ObservableProperty]
    private double _fadeOutMs = 0;

    partial void OnNormalizeChanged(NormalizeMode value)
    {
        TargetDb = ExportLimits.DefaultTargetFor(value);
    }

    public ExportOptions ToOptions() => new()
    {
        Normalize = Normalize,
        TargetDb = TargetDb,
        CeilingDb = CeilingDb,
        FadeInMs = FadeInMs,
        FadeOutMs = FadeOutMs,
    };
}

