namespace PoChopAudio.Services.Cutout;

/// <summary>
/// Strips the background from one image and returns an RGBA PNG stream. There is one
/// implementation, <see cref="Engines.OnnxU2NetRemover"/>; the interface survives because
/// <see cref="EnginePicker"/> is what makes the model optional — it reports an empty engine list
/// when u2netp.onnx is absent rather than throwing at startup.
/// </summary>
public interface IBackgroundRemover
{
    /// <summary>Which engine this implementation represents.</summary>
    CutoutEngine Engine { get; }

    /// <summary>True if the model file is present and the engine can be used.</summary>
    bool IsAvailable { get; }

    /// <summary>Strips the background. Output is a PNG byte stream ready to write to disk.</summary>
    /// <param name="image">RGBA pixels, top-down, no padding. Read-only.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>PNG-encoded RGBA bytes with the background removed.</returns>
    Task<byte[]> RemoveAsync(
        byte[] image,
        int width,
        int height,
        CancellationToken cancellationToken);
}
