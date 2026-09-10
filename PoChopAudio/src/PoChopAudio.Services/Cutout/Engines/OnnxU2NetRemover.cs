using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PoChopAudio.Services.Cutout;

namespace PoChopAudio.Services.Cutout.Engines;

/// <summary>
/// On-device background removal using the u2netp ONNX model. The model is a single forward pass
/// that produces a saliency map the size of the input. We composite the saliency map onto the
/// original image as the alpha channel, then leave RGB untouched.
/// </summary>
public sealed class OnnxU2NetRemover : IBackgroundRemover, IDisposable
{
    private const int InputSize = 320;
    private const float Mean = 0.485f;
    private const float Std = 0.229f;

    /// <summary>u2netp (BritishWerewolf mirror) takes a single NCHW float32 input.</summary>
    private const string InputName = "input.1";

    /// <summary>U^2-Net has 7 side outputs; the deepest, "1965", is the final saliency map.</summary>
    private const string OutputName = "1965";

    private readonly string _modelPath;
    private readonly ILogger<OnnxU2NetRemover> _logger;
    private readonly Lazy<InferenceSession?> _session;

    public OnnxU2NetRemover(CutoutModelOptions options, ILogger<OnnxU2NetRemover> logger)
    {
        _logger = logger;
        _modelPath = options.ModelPath;
        _session = new Lazy<InferenceSession?>(CreateSession, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public CutoutEngine Engine => CutoutEngine.OnnxU2Net;

    public bool IsAvailable => _session.Value is not null;

    public async Task<byte[]> RemoveAsync(byte[] image, int width, int height, CancellationToken cancellationToken)
    {
        var session = _session.Value
            ?? throw new InvalidOperationException(
                "u2netp.onnx is not present. Run SCRIPTS/download-models.ps1 to download the model.");

        cancellationToken.ThrowIfCancellationRequested();

        var mask = await Task.Run(() => InferMask(session, image, width, height), cancellationToken).ConfigureAwait(false);
        return CompositeMask(mask, image, width, height);
    }

    private InferenceSession? CreateSession()
    {
        if (!File.Exists(_modelPath))
        {
            _logger.LogWarning(
                "u2netp.onnx not found at {Path}. The OnnxU2Net engine will be unavailable until the model is downloaded.",
                _modelPath);
            return null;
        }

        try
        {
            var options = new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            };

            // Bound the intra-op thread pool so a single image does not eat the box.
            options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);

            var session = new InferenceSession(_modelPath, options);
            _logger.LogInformation(
                "Loaded u2netp.onnx. Inputs: {Inputs}. Outputs: {Outputs}.",
                string.Join(", ", session.InputMetadata.Keys),
                string.Join(", ", session.OutputMetadata.Keys));
            return session;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to load u2netp.onnx from {Path}.", _modelPath);
            return null;
        }
    }

    private static float[] InferMask(InferenceSession session, byte[] rgba, int width, int height)
    {
        var input = new float[1 * 3 * InputSize * InputSize];

        // Nearest-neighbour sample the source down to 320x320 in NCHW order, normalising with
        // ImageNet mean/std. u2netp expects a normalised input in this exact layout.
        for (var y = 0; y < InputSize; y++)
        {
            var srcY = Math.Min(height - 1, (int)(y * ((double)height / InputSize)));
            for (var x = 0; x < InputSize; x++)
            {
                var srcX = Math.Min(width - 1, (int)(x * ((double)width / InputSize)));
                var srcIdx = (srcY * width + srcX) * CutoutLimits.AlphaChannels;

                var r = rgba[srcIdx + 0] / 255f;
                var g = rgba[srcIdx + 1] / 255f;
                var b = rgba[srcIdx + 2] / 255f;

                var dstIdx = y * InputSize + x;
                input[0 * InputSize * InputSize + dstIdx] = (r - Mean) / Std;
                input[1 * InputSize * InputSize + dstIdx] = (g - Mean) / Std;
                input[2 * InputSize * InputSize + dstIdx] = (b - Mean) / Std;
            }
        }

        var tensor = new DenseTensor<float>(input, new[] { 1, 3, InputSize, InputSize });
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(InputName, tensor) });

        var output = results.First(r => r.Name == OutputName).AsEnumerable<float>().ToArray();

        // The model returns logits normalised to roughly [0, 1]; a sigmoid is unnecessary for
        // argmax-of-foreground work, but clamping is safer when we quantise to 0-255 alpha.
        var mask = new float[InputSize * InputSize];
        for (var i = 0; i < mask.Length; i++)
        {
            mask[i] = Math.Clamp(output[i], 0f, 1f);
        }

        // Resize the 320x320 mask back to the source resolution using nearest-neighbour.
        var fullMask = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            var my = Math.Min(InputSize - 1, (int)(y * ((double)InputSize / height)));
            for (var x = 0; x < width; x++)
            {
                var mx = Math.Min(InputSize - 1, (int)(x * ((double)InputSize / width)));
                fullMask[y * width + x] = mask[my * InputSize + mx];
            }
        }

        return fullMask;
    }

    private static byte[] CompositeMask(float[] mask, byte[] rgba, int width, int height)
    {
        var output = (byte[])rgba.Clone();
        for (var i = 0; i < mask.Length; i++)
        {
            var alpha = (byte)Math.Clamp((int)Math.Round(mask[i] * 255), 0, 255);
            output[i * CutoutLimits.AlphaChannels + 3] = alpha;
        }

        return output;
    }

    public void Dispose()
    {
        if (_session.IsValueCreated)
        {
            _session.Value?.Dispose();
        }
    }
}
