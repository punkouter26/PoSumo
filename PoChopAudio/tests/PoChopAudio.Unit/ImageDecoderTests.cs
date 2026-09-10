using PoChopAudio.Services.Cutout;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PoChopAudio.Unit;

public sealed class ImageDecoderTests
{
    [Fact]
    public void DetectMotionPhotoIsFalseForOrdinaryJpeg()
    {
        var bytes = RenderJpeg(width: 16, height: 16, fillAlpha: 255);

        Assert.False(ImageDecoder.DetectMotionPhoto(bytes));
    }

    [Fact]
    public void DetectMotionPhotoIsTrueWhenTrailingFtypBoxIsPresent()
    {
        var jpeg = RenderJpeg(width: 16, height: 16, fillAlpha: 255);

        var mp4Header = new byte[]
        {
            // Box size 32 (4 bytes big-endian)
            0x00, 0x00, 0x00, 0x20,
            // Box type 'ftyp'
            (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            // Major brand 'mp42'
            (byte)'m', (byte)'p', (byte)'4', (byte)'2',
        };

        var combined = new byte[jpeg.Length + mp4Header.Length];
        Buffer.BlockCopy(jpeg, 0, combined, 0, jpeg.Length);
        Buffer.BlockCopy(mp4Header, 0, combined, jpeg.Length, mp4Header.Length);

        Assert.True(ImageDecoder.DetectMotionPhoto(combined));
    }

    [Fact]
    public void DecodeBytesRejectsOversizedImages()
    {
        // 5000 px wide is over the 4096 limit.
        var bytes = RenderJpeg(width: 5000, height: 1, fillAlpha: 255);

        var exception = Assert.Throws<InvalidDataException>(() =>
            ImageDecoder.DecodeBytes(bytes, "huge.jpg"));

        Assert.Contains("4096", exception.Message);
    }

    [Fact]
    public void DecodeBytesRoundTripsASmallJpeg()
    {
        var bytes = RenderJpeg(width: 4, height: 4, fillAlpha: 255);

        var decoded = ImageDecoder.DecodeBytes(bytes, "tiny.jpg");

        Assert.Equal(4, decoded.Width);
        Assert.Equal(4, decoded.Height);
        Assert.Equal(4 * 4 * 4, decoded.Rgba.Length);
        Assert.False(decoded.WasMotionPhoto);
    }

    [Fact]
    public void DecodeBytesStripsMotionPhotoTrailerBeforeDecoding()
    {
        var jpeg = RenderJpeg(width: 4, height: 4, fillAlpha: 255);
        var trailer = new byte[]
        {
            0x00, 0x00, 0x00, 0x20,
            (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'m', (byte)'p', (byte)'4', (byte)'2',
        };
        var combined = new byte[jpeg.Length + trailer.Length];
        Buffer.BlockCopy(jpeg, 0, combined, 0, jpeg.Length);
        Buffer.BlockCopy(trailer, 0, combined, jpeg.Length, trailer.Length);

        var decoded = ImageDecoder.DecodeBytes(combined, "motion.MP.jpg");

        Assert.True(decoded.WasMotionPhoto);
        Assert.Equal(4, decoded.Width);
        Assert.Equal(4, decoded.Height);
    }

    private static byte[] RenderJpeg(int width, int height, byte fillAlpha)
    {
        using var image = new Image<Rgba32>(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image[x, y] = new Rgba32(200, 100, 50, fillAlpha);
            }
        }

        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        return ms.ToArray();
    }
}
