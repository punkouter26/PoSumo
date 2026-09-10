using PoChopAudio.Services.Chop;

namespace PoChopAudio.Unit;

/// <summary>
/// Names only — the audio path needs a real WAV on disk and is covered by hand against the
/// recordings in <c>output/</c>. What matters here is that a flat batch ZIP never loses a clip.
/// </summary>
public sealed class ClipExporterTests
{
    [Theory]
    [InlineData("  padded.mp3  ", "padded")]
    [InlineData("", "clip")]
    public void StemDropsTheExtension(string fileName, string expected) =>
        Assert.Equal(expected, ClipExporter.Stem(fileName));

    [Fact]
    public void StemReplacesCharactersAFileSystemWouldReject()
    {
        var stem = ClipExporter.Stem("bad:name?.wav");

        Assert.DoesNotContain(':', stem);
        Assert.DoesNotContain('?', stem);
    }

    [Fact]
    public void ClipFileNameIsSourceThenTakeNumber()
    {
        var name = ClipExporter.ClipFileName("Nick_Happy", new ChopSegment(3, 1.0, 2.0, -6));

        Assert.Equal("Nick_Happy_3.wav", name);
    }

    [Fact]
    public void UniqueStemsDisambiguatesRepeatedNames()
    {
        var stems = ClipExporter.UniqueStems(["take.wav", "take.mp3", "take.m4a"]);

        Assert.Equal(["take", "take(2)", "take(3)"], stems);
    }

    [Fact]
    public void UniqueStemsTreatsCaseAsTheSameName()
    {
        // Windows would overwrite the first clip with the second; the ZIP must not rely on case.
        var stems = ClipExporter.UniqueStems(["Take.wav", "take.wav"]);

        Assert.Equal(["Take", "take(2)"], stems);
    }

    [Fact]
    public void UniqueStemsDoesNotCollideWithAnAlreadySuffixedName()
    {
        var stems = ClipExporter.UniqueStems(["take.wav", "take(2).wav", "take.mp3"]);

        Assert.Equal(3, stems.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(["take", "take(2)", "take(3)"], stems);
    }
}
