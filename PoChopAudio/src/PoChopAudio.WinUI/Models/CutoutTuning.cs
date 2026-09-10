using CommunityToolkit.Mvvm.ComponentModel;
using PoChopAudio.Services.Cutout;

namespace PoChopAudio.WinUI.Models;

/// <summary>
/// The fine-tune knobs behind the Cutout page's expander.
///
/// Defaults are tuned for a sharp head shot: a hard edge with no feather, which leaves zero
/// translucent pixels, plus a 1 px erode to pull the boundary inside the fringe u2netp leaves
/// around hair. Turning feather up deliberately softens that again.
/// </summary>
public partial class CutoutTuning : ObservableObject
{
    /// <summary>Alpha at or below this is background. Higher = more aggressive.</summary>
    [ObservableProperty]
    private double _alphaThreshold = 160;

    /// <summary>Snap surviving pixels to fully opaque. Off keeps the model's soft alpha.</summary>
    [ObservableProperty]
    private bool _hardEdge = true;

    /// <summary>Negative erodes (tighter), positive dilates (looser).</summary>
    [ObservableProperty]
    private double _morphology = -1;

    /// <summary>Blur radius on the mask edge. 0 keeps the edge perfectly sharp.</summary>
    [ObservableProperty]
    private double _featherRadius;

    /// <summary>Pixels of breathing room around the head crop.</summary>
    [ObservableProperty]
    private double _cropPadding = 24;

    /// <summary>Moves the neck cut up (negative) or down (positive).</summary>
    [ObservableProperty]
    private double _headCutBias;

    /// <summary>Crop to the head alone. On by default — the app is for head shots.</summary>
    [ObservableProperty]
    private bool _headOnly = true;

    public CutoutOptions ToOptions() => new()
    {
        Engine = CutoutEngine.OnnxU2Net,
        AlphaThreshold = (byte)Math.Clamp((int)AlphaThreshold, 0, 255),
        HardEdge = HardEdge,
        Morphology = (int)Morphology,
        FeatherRadius = (int)FeatherRadius,
        AlphaMultiplier = 1.0,
        TrimPaddingPx = (int)CropPadding,
        HeadCutBiasPercent = (int)HeadCutBias,
        HeadOnly = HeadOnly,
    };

    public void Reset()
    {
        AlphaThreshold = 160;
        HardEdge = true;
        Morphology = -1;
        FeatherRadius = 0;
        CropPadding = 24;
        HeadCutBias = 0;
        HeadOnly = true;
    }
}
