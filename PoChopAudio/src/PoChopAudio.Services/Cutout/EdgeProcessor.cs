namespace PoChopAudio.Services.Cutout;

/// <summary>
/// Post-processes a raw RGBA cutout. The mask lives in the alpha channel; RGB is preserved
/// untouched. Three operations are applied in order: threshold, morphology, feather.
/// </summary>
public static class EdgeProcessor
{
    /// <summary>Applies the four options against an RGBA image. Returns a new buffer.</summary>
    public static byte[] Apply(
        byte[] rgba,
        int width,
        int height,
        CutoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentNullException.ThrowIfNull(options);

        var buffer = (byte[])rgba.Clone();

        Threshold(buffer, width, height, options.AlphaThreshold, options.HardEdge);
        Morphology(buffer, width, height, options.Morphology);
        FeatherInPlace(buffer, width, height, options.FeatherRadius);
        ScaleAlpha(buffer, width, height, options.AlphaMultiplier);

        return buffer;
    }

    /// <summary>Sets any alpha below <paramref name="threshold"/> to 0. Otherwise leaves alpha unchanged.</summary>
    public static void Threshold(byte[] rgba, int width, int height, byte threshold) =>
        Threshold(rgba, width, height, threshold, hardEdge: false);

    /// <summary>
    /// Sets any alpha at or below <paramref name="threshold"/> to 0. With
    /// <paramref name="hardEdge"/> everything above it is snapped to 255, turning the model's soft
    /// saliency map into a clean stencil; without it the surviving alpha is left as the model
    /// produced it, which keeps a translucent fringe.
    /// </summary>
    public static void Threshold(byte[] rgba, int width, int height, byte threshold, bool hardEdge)
    {
        var stride = width * CutoutLimits.AlphaChannels;
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var a = row + (x * CutoutLimits.AlphaChannels) + 3;
                if (rgba[a] <= threshold)
                {
                    rgba[a] = 0;
                }
                else if (hardEdge)
                {
                    rgba[a] = 255;
                }
            }
        }
    }

    /// <summary>Erodes (negative) or dilates (positive) the alpha mask by N pixels using a 3x3 square structuring element.</summary>
    public static void Morphology(byte[] rgba, int width, int height, int radius)
    {
        if (radius == 0)
        {
            return;
        }

        var stride = width * CutoutLimits.AlphaChannels;
        var source = (byte[])rgba.Clone();

        if (radius > 0)
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var max = 0;
                    for (var dy = -radius; dy <= radius; dy++)
                    {
                        var yy = y + dy;
                        if ((uint)yy >= (uint)height)
                        {
                            continue;
                        }

                        for (var dx = -radius; dx <= radius; dx++)
                        {
                            var xx = x + dx;
                            if ((uint)xx >= (uint)width)
                            {
                                continue;
                            }

                            var a = yy * stride + xx * CutoutLimits.AlphaChannels + 3;
                            if (source[a] > max)
                            {
                                max = source[a];
                            }
                        }
                    }

                    rgba[y * stride + x * CutoutLimits.AlphaChannels + 3] = (byte)max;
                }
            }
        }
        else
        {
            var absR = -radius;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var min = 255;
                    for (var dy = -absR; dy <= absR; dy++)
                    {
                        var yy = y + dy;
                        if ((uint)yy >= (uint)height)
                        {
                            continue;
                        }

                        for (var dx = -absR; dx <= absR; dx++)
                        {
                            var xx = x + dx;
                            if ((uint)xx >= (uint)width)
                            {
                                continue;
                            }

                            var a = yy * stride + xx * CutoutLimits.AlphaChannels + 3;
                            if (source[a] < min)
                            {
                                min = source[a];
                            }
                        }
                    }

                    rgba[y * stride + x * CutoutLimits.AlphaChannels + 3] = (byte)min;
                }
            }
        }
    }

    /// <summary>Box-blurs the alpha channel in place. Radius 0 is a no-op. Radius 1 is a 3x3 blur.</summary>
    public static void FeatherInPlace(byte[] rgba, int width, int height, int radius)
    {
        if (radius <= 0)
        {
            return;
        }

        var stride = width * CutoutLimits.AlphaChannels;
        var source = (byte[])rgba.Clone();

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0;
                var count = 0;

                for (var dy = -radius; dy <= radius; dy++)
                {
                    var yy = y + dy;
                    if ((uint)yy >= (uint)height)
                    {
                        continue;
                    }

                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        var xx = x + dx;
                        if ((uint)xx >= (uint)width)
                        {
                            continue;
                        }

                        sum += source[yy * stride + xx * CutoutLimits.AlphaChannels + 3];
                        count++;
                    }
                }

                rgba[y * stride + x * CutoutLimits.AlphaChannels + 3] = (byte)(sum / Math.Max(1, count));
            }
        }
    }

    /// <summary>Multiplies alpha by <paramref name="multiplier"/>, clamped to 0-255.</summary>
    public static void ScaleAlpha(byte[] rgba, int width, int height, double multiplier)
    {
        if (Math.Abs(multiplier - 1.0) < 0.0001)
        {
            return;
        }

        var stride = width * CutoutLimits.AlphaChannels;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var a = y * stride + x * CutoutLimits.AlphaChannels + 3;
                var scaled = (int)Math.Round(rgba[a] * multiplier);
                rgba[a] = (byte)Math.Clamp(scaled, 0, 255);
            }
        }
    }
}
