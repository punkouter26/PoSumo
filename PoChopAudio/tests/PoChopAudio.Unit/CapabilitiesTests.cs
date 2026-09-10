using PoChopAudio.Services.Chop;

namespace PoChopAudio.Unit;

/// <summary>
/// The UI's file picker advertises only what this list says it can decode, so any change to
/// <see cref="AudioDecoder.SupportedExtensions"/> that drops a core format is caught here.
/// </summary>
public sealed class CapabilitiesTests
{
    [Fact]
    public void UniversalFormatsAreAlwaysSupported()
    {
        var supported = AudioDecoder.SupportedExtensions;

        Assert.Contains(".wav", supported);
        Assert.Contains(".mp3", supported);
        Assert.Contains(".aiff", supported);
    }

    [Fact]
    public void WindowsOnlyFormatsAreNeverAdvertisedOffWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var supported = AudioDecoder.SupportedExtensions;

        Assert.DoesNotContain(".m4a", supported);
        Assert.DoesNotContain(".aac", supported);
        Assert.DoesNotContain(".wma", supported);
    }

    [Fact]
    public void DescriptionMatchesAdvertisedExtensions()
    {
        // The description is what the picker hint shows the user — keep them in sync.
        var description = AudioDecoder.SupportedExtensionsDescription;
        var supported = AudioDecoder.SupportedExtensions;

        foreach (var extension in supported)
        {
            // Each advertised format shows up at least as its bare name in the description
            // (e.g. "M4A" for ".m4a"). The test passes if every name appears.
            var name = extension.TrimStart('.').ToUpperInvariant();
            Assert.Contains(name, description, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(".WAV")]
    [InlineData(".WAVE")]
    public void ExtensionLookupIsCaseInsensitive(string extension) =>
        Assert.True(AudioDecoder.IsSupportedExtension(extension));

    [Fact]
    public void UnknownExtensionIsRejected() =>
        Assert.False(AudioDecoder.IsSupportedExtension(".txt"));
}
