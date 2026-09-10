namespace PoChopAudio.Services.Cutout;

/// <summary>A detected face rectangle, in source-image pixels.</summary>
public readonly record struct FaceBox(int X, int Y, int Width, int Height)
{
    public int Bottom => Y + Height - 1;
}

/// <summary>
/// Optional face detection, supplied by the host.
///
/// <para>
/// HeadFinder infers the head from the shape of the alpha mask alone, which is all it can do with
/// no second model. That inference has a real failure mode: when a collar, a hood or long hair
/// hides the neck the mask never narrows, and the head has to be guessed from proportion. A face
/// rectangle removes the guess -- the chin gives the cut row directly.
/// </para>
/// <para>
/// The interface lives here but no implementation does: Services must not reference a UI or OS
/// framework, so the host registers one. Following the optional-capability pattern, absence is
/// normal -- <see cref="CutoutService"/> falls back to the mask-shape logic and nothing throws.
/// </para>
/// </summary>
public interface IFaceLocator
{
    /// <summary>False when the platform cannot detect faces; callers should not bother asking.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// The largest face in the image, or null when none is found. Never throws for an image it
    /// cannot handle -- an undetectable face is an ordinary outcome, not an error.
    /// </summary>
    Task<FaceBox?> LocateAsync(byte[] rgba, int width, int height, CancellationToken cancellationToken);
}
