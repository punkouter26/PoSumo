using PoChopAudio.Services.Cutout;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PoChopAudio.Unit;

public sealed class EdgeProcessorTests
{
    private const int W = 8;
    private const int H = 8;

    [Fact]
    public void ThresholdZerosAlphaAtOrBelowThresholdAndLeavesHigherAlone()
    {
        var rgba = Solid(Rgba: 0, R: 200, G: 100, B: 50, width: W, height: H);
        SetAlpha(rgba, 0, 5, 10);
        SetAlpha(rgba, 5, W, 200);

        EdgeProcessor.Threshold(rgba, W, H, threshold: 50);

        Assert.Equal(0, AlphaAt(rgba, 0));
        Assert.Equal(0, AlphaAt(rgba, 4));
        Assert.Equal(200, AlphaAt(rgba, 5));
        Assert.Equal(200, AlphaAt(rgba, 7));
    }

    [Fact]
    public void MorphologyWithPositiveRadiusDilatesMask()
    {
        // Single pixel of 200 alpha in the middle, everything else 0.
        var rgba = Solid(0, 0, 0, 0, W, H);
        SetAlphaAt(rgba, 3, 3, 200);

        EdgeProcessor.Morphology(rgba, W, H, radius: 1);

        // The 3x3 neighbourhood around (3, 3) should now have alpha > 0.
        Assert.True(AlphaAt(rgba, 3, 3) > 0);
        Assert.True(AlphaAt(rgba, 2, 3) > 0);
        Assert.True(AlphaAt(rgba, 4, 4) > 0);
        Assert.Equal(0, AlphaAt(rgba, 0, 0));
    }

    [Fact]
    public void MorphologyWithNegativeRadiusErodesMask()
    {
        // A 5x5 fully opaque block — its inner 3x3 core survives a 3x3 erosion.
        var rgba = Solid(0, 0, 0, 0, W, H);
        for (var y = 1; y <= 5; y++)
        {
            for (var x = 1; x <= 5; x++)
            {
                SetAlphaAt(rgba, x, y, 200);
            }
        }

        EdgeProcessor.Morphology(rgba, W, H, radius: -1);

        // The 3x3 core of the block stays opaque.
        Assert.True(AlphaAt(rgba, 3, 3) > 0);
        // The edge pixels are eroded away because they have zero neighbours.
        Assert.Equal(0, AlphaAt(rgba, 1, 1));
    }

    [Fact]
    public void FeatherBlursAlphaAcrossNeighbourhood()
    {
        var rgba = Solid(0, 0, 0, 0, W, H);
        SetAlphaAt(rgba, 4, 4, 255);

        EdgeProcessor.FeatherInPlace(rgba, W, H, radius: 1);

        // The pixel next to the source has averaged alpha (~255 / 9).
        var neighbour = AlphaAt(rgba, 4, 5);
        Assert.InRange(neighbour, 1, 100);
    }

    [Fact]
    public void ScaleAlphaMultipliesAndClampsTo255()
    {
        var rgba = Solid(0, 0, 0, 0, W, H);
        SetAlphaAt(rgba, 2, 2, 100);

        EdgeProcessor.ScaleAlpha(rgba, W, H, multiplier: 3.0);

        Assert.Equal(255, AlphaAt(rgba, 2, 2));
    }

    [Fact]
    public void ScaleAlphaMultipliesAndClampsToZero()
    {
        var rgba = Solid(0, 0, 0, 0, W, H);
        SetAlphaAt(rgba, 2, 2, 100);

        EdgeProcessor.ScaleAlpha(rgba, W, H, multiplier: -1.0);

        Assert.Equal(0, AlphaAt(rgba, 2, 2));
    }

    [Fact]
    public void ApplyChainsAllOperationsInOrder()
    {
        // Two opaque pixels. After threshold + morphology + feather, the pixels are still opaque
        // and the RGB channels have not been touched.
        var rgba = Solid(Rgba: 0, R: 200, G: 100, B: 50, width: W, height: H);
        SetAlphaAt(rgba, 3, 3, 255);
        SetAlphaAt(rgba, 4, 4, 255);

        var output = EdgeProcessor.Apply(rgba, W, H, new CutoutOptions
        {
            AlphaThreshold = 0,
            Morphology = 1,
            FeatherRadius = 1,
            AlphaMultiplier = 1.0,
        });

        // RGB at (3, 3) is preserved.
        var (r, g, b) = RgbAt(output, 3, 3);
        Assert.Equal(200, r);
        Assert.Equal(100, g);
        Assert.Equal(50, b);
        // Alpha is non-zero.
        Assert.True(AlphaAt(output, 3, 3) > 0);
    }

    private static byte[] Solid(byte Rgba, byte R, byte G, byte B, int width, int height)
    {
        var buffer = new byte[CutoutLimits.AlphaChannels * width * height];
        for (var i = 0; i < buffer.Length; i += CutoutLimits.AlphaChannels)
        {
            buffer[i + 0] = R;
            buffer[i + 1] = G;
            buffer[i + 2] = B;
            buffer[i + 3] = Rgba;
        }

        return buffer;
    }

    private static void SetAlpha(byte[] rgba, int xStart, int xEnd, byte value)
    {
        for (var x = xStart; x < xEnd; x++)
        {
            SetAlphaAt(rgba, x, 0, value);
        }
    }

    private static void SetAlphaAt(byte[] rgba, int x, int y, byte value)
    {
        rgba[(y * W + x) * CutoutLimits.AlphaChannels + 3] = value;
    }

    private static byte AlphaAt(byte[] rgba, int x, int y = 0) =>
        rgba[(y * W + x) * CutoutLimits.AlphaChannels + 3];

    private static (byte R, byte G, byte B) RgbAt(byte[] rgba, int x, int y)
    {
        var offset = (y * W + x) * CutoutLimits.AlphaChannels;
        return (rgba[offset + 0], rgba[offset + 1], rgba[offset + 2]);
    }
}
