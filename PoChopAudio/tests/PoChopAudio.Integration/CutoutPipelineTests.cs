using Microsoft.Extensions.Logging.Abstractions;
using PoChopAudio.Services;
using PoChopAudio.Services.Cutout;
using PoChopAudio.Services.Cutout.Engines;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PoChopAudio.Integration;

/// <summary>
/// Exercises the cutout pipeline end to end: decode, engine, edge processing, head crop, PNG out.
/// The OnnxU2Net engine only runs when its model file is present, so the inference test skips
/// itself on a machine that has not fetched the model.
/// </summary>
public sealed class CutoutPipelineTests
{
    private readonly CutoutService _cutout;
    private readonly string _modelPath;

    public CutoutPipelineTests()
    {
        _modelPath = Path.Combine(AppContext.BaseDirectory, "Content", "Models", "u2netp.onnx");
        var remover = new OnnxU2NetRemover(new CutoutModelOptions(_modelPath), NullLogger<OnnxU2NetRemover>.Instance);
        var picker = new EnginePicker([remover], NullLogger<EnginePicker>.Instance);

        _cutout = new CutoutService(picker, NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task RejectsAnOversizedPhoto()
    {
        using var source = new MemoryStream(new byte[16]);
        var outcome = await _cutout.CutOutAsync(source, "huge.jpg", CutoutLimits.MaxUploadBytes + 1);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(OutcomeFailure.TooLarge, outcome.Failure);
    }

    [Fact]
    public async Task RejectsAnUnsupportedExtension()
    {
        using var source = new MemoryStream(new byte[16]);
        var outcome = await _cutout.CutOutAsync(source, "file.gif", 16);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(OutcomeFailure.UnsupportedMedia, outcome.Failure);
    }

    [Fact]
    public async Task RejectsAnEmptyPayload()
    {
        using var source = new MemoryStream();
        var outcome = await _cutout.CutOutAsync(source, "empty.png", 0);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(OutcomeFailure.Empty, outcome.Failure);
    }

    [Fact]
    public async Task RejectsOutOfRangeOptions()
    {
        var png = BuildTinyPng(64, 64);
        using var source = new MemoryStream(png);

        var outcome = await _cutout.CutOutAsync(
            source, "test.png", png.Length, new CutoutOptions { FeatherRadius = 99 });

        Assert.False(outcome.IsSuccess);
        Assert.Equal(OutcomeFailure.Invalid, outcome.Failure);
        Assert.True(outcome.Errors.ContainsKey(nameof(CutoutOptions.FeatherRadius)));
    }

    [Fact]
    public async Task RealOnnxPathProducesACroppedPng()
    {
        if (!File.Exists(_modelPath))
        {
            // The model is fetched by SCRIPTS/download-models.ps1; skip rather than fail without it.
            return;
        }

        Assert.True(_cutout.IsAvailable);

        var png = BuildTinyPng(64, 64);
        using var source = new MemoryStream(png);

        var outcome = await _cutout.CutOutAsync(source, "test.png", png.Length);
        Assert.True(outcome.IsSuccess, outcome.Message);

        var photo = outcome.Value;
        Assert.Equal(CutoutEngine.OnnxU2Net, photo.Engine);

        // The head crop must never grow the image beyond what came in.
        Assert.InRange(photo.Width, 1, 64);
        Assert.InRange(photo.Height, 1, 64);
        AssertIsPng(photo.Png);
    }

    [Fact]
    public void AMissingModelLeavesTheEngineUnavailableRatherThanThrowing()
    {
        var absent = new OnnxU2NetRemover(
            new CutoutModelOptions(Path.Combine(AppContext.BaseDirectory, "no-such-model.onnx")),
            NullLogger<OnnxU2NetRemover>.Instance);

        var service = new CutoutService(
            new EnginePicker([absent], NullLogger<EnginePicker>.Instance),
            NullLoggerFactory.Instance);

        Assert.False(service.IsAvailable);
    }

    private static void AssertIsPng(byte[] bytes)
    {
        Assert.True(bytes.Length > 8);
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal(0x50, bytes[1]);
        Assert.Equal(0x4E, bytes[2]);
        Assert.Equal(0x47, bytes[3]);
    }

    private static byte[] BuildTinyPng(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var cx = width / 2;
                var cy = height / 2;
                var inside = ((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) < (width * width / 16);
                image[x, y] = inside
                    ? new Rgba32(255, 0, 0, 255)
                    : new Rgba32(0, 0, 255, 255);
            }
        }

        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        return stream.ToArray();
    }
}
