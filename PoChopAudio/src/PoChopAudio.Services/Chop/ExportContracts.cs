namespace PoChopAudio.Services.Chop;

/// <summary>How a clip's level is measured before the export gain is applied.</summary>
public enum NormalizeMode
{
    /// <summary>Leave the samples exactly as they were cut. The default.</summary>
    None,

    /// <summary>Match the loudest sample to the target — predictable, ignores perceived loudness.</summary>
    Peak,

    /// <summary>Match the mean square level to the target — cheap and stable on very short takes.</summary>
    Rms,

    /// <summary>Match ITU-R BS.1770-4 integrated loudness to the target, in LUFS.</summary>
    Lufs,
}

/// <summary>Bounds the export knobs are held to, wherever they are set.</summary>
public static class ExportLimits
{
    /// <summary>Quietest level worth normalizing. Below this a clip is treated as silence and left alone.</summary>
    public const double SilenceFloorDb = -70;

    /// <summary>
    /// Most gain normalization will ever apply. Without this, targeting -16 LUFS on a clip that is
    /// mostly room tone would raise the noise by 40 dB and call it a take.
    /// </summary>
    public const double MaxGainDb = 24;

    public const double MinTargetDb = -60;
    public const double MaxTargetDb = 0;

    /// <summary>The ceiling can be pulled down for headroom but never above 0 dBFS — 16-bit PCM has no room above it.</summary>
    public const double MinCeilingDb = -12;
    public const double MaxCeilingDb = 0;

    public const double MaxFadeMs = 5_000;

    public const double DefaultPeakTargetDb = -1;
    public const double DefaultRmsTargetDb = -20;
    public const double DefaultLufsTargetDb = -16;
    public const double DefaultCeilingDb = -1;

    /// <summary>The target that suits a mode when the user has not moved the slider yet.</summary>
    public static double DefaultTargetFor(NormalizeMode mode) => mode switch
    {
        NormalizeMode.Peak => DefaultPeakTargetDb,
        NormalizeMode.Rms => DefaultRmsTargetDb,
        NormalizeMode.Lufs => DefaultLufsTargetDb,
        _ => DefaultPeakTargetDb,
    };
}

/// <summary>
/// What happens to a clip between being sliced out of the canonical WAV and being written as
/// 16-bit PCM. Every value defaults to "do nothing", so an export with no options set is a
/// bit-for-bit plain slice — the guarantee the app started with.
/// </summary>
public sealed record ExportOptions
{
    public NormalizeMode Normalize { get; init; } = NormalizeMode.None;

    /// <summary>Level to hit, in dBFS for Peak/Rms and LUFS for Lufs.</summary>
    public double TargetDb { get; init; } = ExportLimits.DefaultPeakTargetDb;

    /// <summary>
    /// Hard ceiling for the loudest sample after gain. Normalization gain is reduced when it would
    /// push a peak past this, so a loudness target is never met by clipping.
    /// </summary>
    public double CeilingDb { get; init; } = ExportLimits.DefaultCeilingDb;

    /// <summary>Raised-cosine fade applied to the head of the clip. 0 disables it.</summary>
    public double FadeInMs { get; init; }

    /// <summary>Raised-cosine fade applied to the tail of the clip. 0 disables it.</summary>
    public double FadeOutMs { get; init; }

    /// <summary>True when the export would not change a single sample, so the fast path can be taken.</summary>
    public bool IsPassThrough =>
        Normalize is NormalizeMode.None && FadeInMs <= 0 && FadeOutMs <= 0;

    public static ExportOptions PassThrough { get; } = new();
}
