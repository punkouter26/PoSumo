namespace PoChopAudio.Services.Cutout;

/// <summary>Bounds the UI and the service both hold to, so the picker never offers what cutout will reject.</summary>
public static class CutoutLimits
{
    /// <summary>Largest image the service will accept. Larger JPEGs from phones are the common case.</summary>
    public const long MaxUploadBytes = 50L * 1024 * 1024;

    /// <summary>Most images one batch may hold.</summary>
    public const int MaxBatchFiles = 32;

    /// <summary>Longest edge permitted on either input or output. Anything bigger is rejected.</summary>
    public const int MaxDimension = 4096;

    /// <summary>SixLabors.ImageSharp 8-bit RGBA, used for in-process mask math.</summary>
    public const int AlphaChannels = 4;

    /// <summary>Default alpha threshold: keep pixels with alpha above this 0-255 value.</summary>
    public const byte DefaultAlphaThreshold = 0;

    /// <summary>Default feather radius in pixels. 1 px is enough to remove most JPEG halos.</summary>
    public const int DefaultFeatherRadius = 1;

    /// <summary>Default morphology (erode negative, dilate positive) in pixels.</summary>
    public const int DefaultMorphology = 0;

    /// <summary>Default alpha multiplier for alpha matting.</summary>
    public const double DefaultAlphaMultiplier = 1.0;
}

/// <summary>Which background-removal engine to run.</summary>
public enum CutoutEngine
{
    /// <summary>Microsoft.ML.OnnxRuntime + u2netp.onnx, runs in-process. The only engine.</summary>
    OnnxU2Net = 0,
}
