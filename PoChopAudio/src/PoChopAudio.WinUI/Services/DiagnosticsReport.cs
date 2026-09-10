using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Composition.SystemBackdrops;
using NAudio.Wave;
using PoChopAudio.Services.Chop;
using PoChopAudio.Services.Cutout;
using Windows.Media.Capture.Frames;

namespace PoChopAudio.WinUI.Services;

/// <summary>How a diagnostic reads at a glance. Drives the dot beside it, nothing else.</summary>
public enum DiagnosticState
{
    /// <summary>A fact, neither good nor bad — a path, a version, a count.</summary>
    Neutral,

    /// <summary>A capability that is present and working.</summary>
    Good,

    /// <summary>A capability that is missing. Never fatal; the app degrades around all of these.</summary>
    Missing,
}

public sealed record DiagnosticItem(string Label, string Value, DiagnosticState State = DiagnosticState.Neutral);

public sealed record DiagnosticSection(string Title, IReadOnlyList<DiagnosticItem> Items);

/// <summary>
/// Gathers everything the app knows about its own environment into a list a page can render and a
/// user can paste into a bug report.
///
/// <para>
/// Every optional capability here follows the probe-report-degrade rule, and until now only one of
/// them — the missing cutout model — was reported anywhere a user could see. The rest degrade
/// silently and correctly, which is worse: the app quietly does less and never says which part.
/// This is the one place that answers "why is that not doing what I expect".
/// </para>
/// </summary>
public static class DiagnosticsReport
{
    /// <summary>
    /// Async because enumerating camera groups is, and because doing it here keeps the page from
    /// having to know that only one of these probes touches hardware.
    /// </summary>
    public static async Task<IReadOnlyList<DiagnosticSection>> BuildAsync(
        CutoutService cutout,
        EnginePicker picker,
        IFaceLocator? faceLocator,
        ChopJobStore jobs,
        CutoutModelOptions model,
        FileLoggerProvider log,
        AppSettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(cutout);
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(settings);

        return
        [
            new DiagnosticSection("Application", BuildApplication()),
            new DiagnosticSection("Audio", BuildAudio()),
            new DiagnosticSection("Images and camera", await BuildImagingAsync(cutout, picker, faceLocator, model)),
            new DiagnosticSection("Storage", BuildStorage(jobs, log, settings)),
        ];
    }

    /// <summary>The whole report as plain text, for the Copy button.</summary>
    public static string ToText(IReadOnlyList<DiagnosticSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);

        var text = new StringBuilder();
        text.AppendLine("PoChopAudio diagnostics");
        text.Append("Captured ").AppendLine(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));

        foreach (var section in sections)
        {
            text.AppendLine();
            text.Append("## ").AppendLine(section.Title);

            foreach (var item in section.Items)
            {
                text.Append("  ").Append(item.Label).Append(": ").AppendLine(item.Value);
            }
        }

        return text.ToString();
    }

    private static List<DiagnosticItem> BuildApplication()
    {
        var hasBackdrop = MicaController.IsSupported() || DesktopAcrylicController.IsSupported();

        return
        [
            new DiagnosticItem("Version", AppVersion()),
            new DiagnosticItem("Framework", RuntimeInformation.FrameworkDescription),
            new DiagnosticItem("Architecture", $"{RuntimeInformation.ProcessArchitecture} process on {RuntimeInformation.OSArchitecture}"),
            new DiagnosticItem("Windows", Environment.OSVersion.VersionString),
            new DiagnosticItem("Installed as", "Unpackaged, self-contained"),
            new DiagnosticItem(
                "Window backdrop",
                MicaController.IsSupported() ? "Mica"
                    : DesktopAcrylicController.IsSupported() ? "Acrylic"
                    : "Plain — neither Mica nor Acrylic is supported here",
                hasBackdrop ? DiagnosticState.Good : DiagnosticState.Missing),
        ];
    }

    private static List<DiagnosticItem> BuildAudio()
    {
        var items = new List<DiagnosticItem>
        {
            new("Decodes", AudioDecoder.SupportedExtensionsDescription, DiagnosticState.Good),
            new("Largest recording", $"{ChopLimits.MaxUploadBytes / (1024 * 1024)} MB"),
            new("Files per batch", ChopLimits.MaxBatchFiles.ToString(CultureInfo.CurrentCulture)),
        };

        // NAudio reports capture devices synchronously, so this costs nothing. The app always uses
        // the default device; naming what that resolves to is how someone catches a take recorded
        // through the wrong microphone before they have recorded forty of them.
        try
        {
            var count = WaveInEvent.DeviceCount;

            items.Add(count > 0
                ? new DiagnosticItem("Microphones", $"{count} found; recording uses \"{WaveInEvent.GetCapabilities(0).ProductName}\"", DiagnosticState.Good)
                : new DiagnosticItem("Microphones", "None found — recording is unavailable", DiagnosticState.Missing));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            items.Add(new DiagnosticItem("Microphones", $"Could not enumerate: {exception.Message}", DiagnosticState.Missing));
        }

        return items;
    }

    private static async Task<List<DiagnosticItem>> BuildImagingAsync(
        CutoutService cutout, EnginePicker picker, IFaceLocator? faceLocator, CutoutModelOptions model)
    {
        var modelExists = File.Exists(model.ModelPath);
        var hasFaces = faceLocator?.IsAvailable == true;

        var items = new List<DiagnosticItem>
        {
            new("Decodes", string.Join(", ", ImageDecoder.SupportedExtensions), DiagnosticState.Good),
            new(
                "Background removal",
                cutout.IsAvailable
                    ? $"Ready ({string.Join(", ", picker.AvailableEngines)})"
                    : "Unavailable — no engine could start",
                cutout.IsAvailable ? DiagnosticState.Good : DiagnosticState.Missing),
            new(
                "Cutout model",
                modelExists
                    ? $"{Path.GetFileName(model.ModelPath)}, {FormatBytes(new FileInfo(model.ModelPath).Length)}"
                    : "Missing — run SCRIPTS/download-models.ps1, then restart",
                modelExists ? DiagnosticState.Good : DiagnosticState.Missing),
            new("Model path", model.ModelPath),
            new(
                "Face detection",
                hasFaces
                    ? "Windows FaceDetector — a measured chin places the neck line"
                    : "Unavailable — the head crop reads the mask shape instead",
                hasFaces ? DiagnosticState.Good : DiagnosticState.Missing),
            new("Largest photo", $"{CutoutLimits.MaxUploadBytes / (1024 * 1024)} MB, {CutoutLimits.MaxDimension} px longest edge"),
        };

        try
        {
            var groups = await MediaFrameSourceGroup.FindAllAsync();

            items.Add(groups.Count > 0
                ? new DiagnosticItem("Cameras", $"{groups.Count} found; the viewfinder uses \"{groups[0].DisplayName}\"", DiagnosticState.Good)
                : new DiagnosticItem("Cameras", "None found — Take photo will not work", DiagnosticState.Missing));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            items.Add(new DiagnosticItem("Cameras", $"Could not enumerate: {exception.Message}", DiagnosticState.Missing));
        }

        return items;
    }

    private static List<DiagnosticItem> BuildStorage(
        ChopJobStore jobs, FileLoggerProvider log, AppSettingsService settings) =>
    [
        new DiagnosticItem("Scratch folder", ChopJobStore.ParentRoot),
        new DiagnosticItem("Scratch in use", FormatBytes(ChopJobStore.ScratchBytes())),
        new DiagnosticItem("Open recordings", $"{jobs.Count}, held until cleared or the app closes"),
        new DiagnosticItem("Log file", log.LogFilePath),
        new DiagnosticItem("Settings file", settings.Exists ? settings.FilePath : $"{settings.FilePath} — not written yet"),
    ];

    /// <summary>
    /// Prefers the informational version, which carries whatever the build stamped on it, and falls
    /// back to the assembly version so this never renders blank.
    /// </summary>
    private static string AppVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip the "+<commit sha>" the SDK appends; the sha helps nobody reading a settings page.
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    /// <summary>Opens a folder in File Explorer, selecting <paramref name="selectPath"/> when given.</summary>
    public static void RevealInExplorer(string folder, string? selectPath = null)
    {
        try
        {
            var argument = selectPath is not null && File.Exists(selectPath)
                ? $"/select,\"{selectPath}\""
                : $"\"{folder}\"";

            using var process = Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Explorer refusing to open is not worth an error banner on a diagnostics page.
        }
    }
}
