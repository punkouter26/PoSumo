using PoChopAudio.Services.Cutout;
using Xunit;

namespace PoChopAudio.Unit;

/// <summary>
/// u2netp returns a soft saliency map, and every partially transparent pixel that survives reads
/// as a halo around hair. These pin the promise the Cutout page's "Sharp" edge makes.
/// </summary>
public sealed class HardEdgeTests
{
    private const int Width = 200;
    private const int Height = 200;

    [Fact]
    public void HardEdgeLeavesNoTranslucentPixels()
    {
        var mask = SoftBlob();

        var processed = EdgeProcessor.Apply(mask, Width, Height, new CutoutOptions
        {
            AlphaThreshold = 160,
            HardEdge = true,
            Morphology = -1,
            FeatherRadius = 0,
            AlphaMultiplier = 1.0,
        });

        Assert.Equal(0, CountTranslucent(processed));
        Assert.True(CountOpaque(processed) > 0, "the subject must survive the cut");
    }

    [Fact]
    public void WithoutHardEdgeTheSoftFringeSurvives()
    {
        var mask = SoftBlob();

        var processed = EdgeProcessor.Apply(mask, Width, Height, new CutoutOptions
        {
            AlphaThreshold = 160,
            HardEdge = false,
            Morphology = 0,
            FeatherRadius = 0,
            AlphaMultiplier = 1.0,
        });

        Assert.True(CountTranslucent(processed) > 0);
    }

    [Fact]
    public void FeatherDeliberatelySoftensAHardEdgeAgain()
    {
        // Feather is the escape hatch when a hard cut looks pasted on, so it has to still work.
        var mask = SoftBlob();

        var processed = EdgeProcessor.Apply(mask, Width, Height, new CutoutOptions
        {
            AlphaThreshold = 160,
            HardEdge = true,
            Morphology = 0,
            FeatherRadius = 2,
            AlphaMultiplier = 1.0,
        });

        Assert.True(CountTranslucent(processed) > 0);
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(30)]
    public void HeadCutBiasMovesTheCutWithoutLeavingTheImage(int biasPercent)
    {
        var mask = SoftBlob();

        var bounds = HeadFinder.Find(mask, Width, Height, paddingPx: 0, cutBiasPercent: biasPercent);

        Assert.False(bounds.Empty);
        Assert.InRange(bounds.Y, 0, Height - 1);
        Assert.InRange(bounds.Height, 1, Height - bounds.Y);
        Assert.InRange(bounds.X, 0, Width - 1);
        Assert.InRange(bounds.Width, 1, Width - bounds.X);
    }

    /// <summary>A solid core fading to nothing over 20 px — the shape the model actually returns.</summary>
    private static byte[] SoftBlob()
    {
        var rgba = new byte[Width * Height * 4];
        const int cx = Width / 2;
        const int cy = Height / 2;
        const int core = 50;
        const int skirt = 20;

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var i = ((y * Width) + x) * 4;
                rgba[i] = 200;
                rgba[i + 1] = 170;
                rgba[i + 2] = 150;

                var d = Math.Sqrt(((x - cx) * (double)(x - cx)) + ((y - cy) * (double)(y - cy)));
                var a = d <= core ? 255 : d >= core + skirt ? 0 : 255 * (1 - ((d - core) / skirt));
                rgba[i + 3] = (byte)Math.Clamp(a, 0, 255);
            }
        }

        return rgba;
    }

    private static int CountTranslucent(byte[] rgba)
    {
        var count = 0;
        for (var i = 3; i < rgba.Length; i += 4)
        {
            if (rgba[i] is > 0 and < 255) count++;
        }

        return count;
    }

    private static int CountOpaque(byte[] rgba)
    {
        var count = 0;
        for (var i = 3; i < rgba.Length; i += 4)
        {
            if (rgba[i] == 255) count++;
        }

        return count;
    }
}
