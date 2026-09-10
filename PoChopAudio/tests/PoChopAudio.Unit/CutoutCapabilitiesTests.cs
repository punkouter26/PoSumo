using PoChopAudio.Services.Cutout;

namespace PoChopAudio.Unit;

public sealed class CutoutCapabilitiesTests
{
    [Fact]
    public void SupportedExtensionsAdvertisesJpegPngWebp()
    {
        var supported = ImageDecoder.SupportedExtensions;

        Assert.Contains(".jpg", supported);
        Assert.Contains(".jpeg", supported);
        Assert.Contains(".png", supported);
        Assert.Contains(".webp", supported);
    }

    [Fact]
    public void IsSupportedExtensionIsCaseInsensitive()
    {
        Assert.True(ImageDecoder.IsSupportedExtension(".JPG"));
        Assert.True(ImageDecoder.IsSupportedExtension(".WEBP"));
        Assert.False(ImageDecoder.IsSupportedExtension(".gif"));
    }

}
