namespace PoChopAudio.Services.Cutout;

/// <summary>
/// Where the u2netp ONNX model lives. The API resolves it under its content root; a desktop host
/// resolves it next to the executable. Injecting the path is what lets the engine live in a plain
/// library instead of depending on <c>IHostEnvironment</c>.
/// </summary>
/// <param name="ModelPath">Absolute path to <c>u2netp.onnx</c>. The file need not exist — a missing
/// model leaves the engine unavailable, which is the documented optional-capability behaviour.</param>
public sealed record CutoutModelOptions(string ModelPath);
