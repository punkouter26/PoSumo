using PoChopAudio.Services.Cutout;

namespace PoChopAudio.Unit;

/// <summary>
/// Covers the head crop, which had no tests and one silent failure mode: when a collar or hair hid
/// the neck, the mask never narrowed enough and the whole torso was returned as "head".
/// </summary>
public class HeadFinderTests
{
    private const int Width = 400;
    private const int Height = 600;

    // Head proportions matter here: these tests judge a rule that reasons about how tall a head is
    // relative to its width, so a "head" that is wider than it is tall would test nothing real.
    // A head is roughly 1.35x taller than wide, hair included.
    private const int HeadWidth = 160;
    private const int HeadRows = 216;
    private const int NeckRows = 40;
    private const int ShoulderWidth = 400;

    /// <summary>Head band, neck band, then shoulders running to the frame edges.</summary>
    private static byte[] Silhouette(int neckWidth)
    {
        var rgba = new byte[Width * Height * CutoutLimits.AlphaChannels];
        for (var y = 0; y < Height; y++)
        {
            var w = y < HeadRows ? HeadWidth
                  : y < HeadRows + NeckRows ? neckWidth
                  : ShoulderWidth;

            var x0 = (Width - w) / 2;
            for (var x = x0; x < x0 + w && x < Width; x++)
            {
                rgba[(((y * Width) + x) * CutoutLimits.AlphaChannels) + 3] = 255;
            }
        }
        return rgba;
    }

    /// <summary>A head and nothing else, in correct proportion and no body below it.</summary>
    private static byte[] HeadOnlySilhouette()
    {
        var rgba = new byte[Width * Height * CutoutLimits.AlphaChannels];
        var x0 = (Width - HeadWidth) / 2;
        for (var y = 0; y < HeadRows; y++)
        {
            for (var x = x0; x < x0 + HeadWidth; x++)
            {
                rgba[(((y * Width) + x) * CutoutLimits.AlphaChannels) + 3] = 255;
            }
        }
        return rgba;
    }

    [Theory]
    [InlineData(0.50)]
    [InlineData(0.88)]
    public void CutsAtTheNeckAcrossRealisticNarrowing(double neckRatio)
    {
        var mask = Silhouette((int)(HeadWidth * neckRatio));

        var box = HeadFinder.Find(mask, Width, Height, paddingPx: 0);

        Assert.False(box.Empty);
        // The cut must land in the neck band, not down in the shoulders.
        Assert.InRange(box.Y + box.Height, HeadRows, HeadRows + NeckRows);
    }

    /// <summary>
    /// The regression this whole change exists for: a neck barely narrower than the head, which is
    /// what a crew neck or a hood produces. This previously returned the full 400 px frame.
    /// </summary>
    [Fact]
    public void CropsHeadWhenCollarHidesTheNeck()
    {
        var mask = Silhouette(neckWidth: (int)(HeadWidth * 0.95));

        var box = HeadFinder.Find(mask, Width, Height, paddingPx: 0);

        Assert.False(box.Empty);
        Assert.True(
            box.Height <= HeadRows + NeckRows,
            $"Expected a head-sized crop, got {box.Height}px of a {Height}px frame.");
    }

    /// <summary>A photo that is only a head must keep its full height, not be cut by proportion.</summary>
    [Fact]
    public void KeepsFullHeightForHeadOnlyPhoto()
    {
        var mask = HeadOnlySilhouette();

        var box = HeadFinder.Find(mask, Width, Height, paddingPx: 0);

        Assert.False(box.Empty);
        Assert.Equal(HeadRows, box.Height);
    }

    /// <summary>A detected chin overrides the mask-shape inference outright.</summary>
    [Fact]
    public void FaceChinRowOverridesShapeInference()
    {
        var mask = Silhouette(neckWidth: (int)(HeadWidth * 0.95));
        const int chin = 120;

        var box = HeadFinder.Find(mask, Width, Height, paddingPx: 0, cutBiasPercent: 0, faceChinRow: chin);

        Assert.False(box.Empty);
        Assert.Equal(chin + 1, box.Y + box.Height);
    }

    [Fact]
    public void EmptyMaskReportsEmpty()
    {
        var rgba = new byte[Width * Height * CutoutLimits.AlphaChannels];

        var box = HeadFinder.Find(rgba, Width, Height, paddingPx: 0);

        Assert.True(box.Empty);
    }
}
