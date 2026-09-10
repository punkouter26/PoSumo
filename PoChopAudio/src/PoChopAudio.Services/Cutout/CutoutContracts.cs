namespace PoChopAudio.Services.Cutout;

/// <summary>Knobs the user can turn when the automatic cutout needs a tweak. All optional.</summary>
public sealed record CutoutOptions
{
    /// <summary>Engine to run. Null asks the service to pick its default.</summary>
    public CutoutEngine? Engine { get; init; }

    /// <summary>Keep pixels above this alpha value (0-255). Lower = more of the image kept.</summary>
    public byte AlphaThreshold { get; init; } = CutoutLimits.DefaultAlphaThreshold;

    /// <summary>Feather radius in pixels. Smooths the mask edge with a box blur.</summary>
    public int FeatherRadius { get; init; } = CutoutLimits.DefaultFeatherRadius;

    /// <summary>Erode (negative) or dilate (positive) the mask by N pixels.</summary>
    public int Morphology { get; init; } = CutoutLimits.DefaultMorphology;

    /// <summary>Final alpha multiplier on the mask.</summary>
    public double AlphaMultiplier { get; init; } = CutoutLimits.DefaultAlphaMultiplier;

    /// <summary>
    /// Optional padding (in pixels) added to the trimmed bounding box on every side. 0 = tight crop.
    /// </summary>
    public int TrimPaddingPx { get; init; } = 16;

    /// <summary>
    /// Snaps every surviving pixel to fully opaque instead of keeping the model's soft alpha.
    /// This is what removes the wispy halo around hair: thresholding alone deletes the faint
    /// pixels but leaves everything above the cut translucent, which reads as a glow.
    /// </summary>
    public bool HardEdge { get; init; } = true;

    /// <summary>
    /// Moves the head/neck cut up (negative) or down (positive), as a percentage of the subject's
    /// height. The neck is found automatically; this is the manual override for when a collar or a
    /// beard makes the automatic choice land wrong.
    /// </summary>
    public int HeadCutBiasPercent { get; init; }

    /// <summary>
    /// Crop the result to the head alone, dropping neck, shoulders and torso. On by default: the
    /// app exists to make head shots, and u2netp is a saliency model that always returns the whole
    /// person. Turn off to keep everything the background removal left behind.
    /// </summary>
    public bool HeadOnly { get; init; } = true;
}
