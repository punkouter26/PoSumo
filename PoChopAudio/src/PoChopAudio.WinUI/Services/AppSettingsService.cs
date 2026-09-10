using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;

namespace PoChopAudio.WinUI.Services;

/// <summary>
/// The handful of choices that survive a restart.
///
/// <para>
/// Deliberately small. The chop and cutout knobs are <em>not</em> here: those belong to a take, and
/// carrying the last session's tuning silently into the next one is how a user ends up with clips
/// cut by settings they cannot remember choosing. What persists is the shape of the app, not the
/// shape of the work.
/// </para>
/// </summary>
public sealed class AppSettings
{
    /// <summary>Light, Dark, or follow the system.</summary>
    public ElementTheme Theme { get; set; } = ElementTheme.Default;

    /// <summary>Where "save" starts from. Null means the app has no opinion and always asks.</summary>
    public string? DefaultSaveFolder { get; set; }

    /// <summary>
    /// Whether the synthesised count-in and completion cues are allowed to sound. Off by default:
    /// this is an app for recording and judging audio, and it should make no noise of its own until
    /// someone has said they want it to.
    /// </summary>
    public bool CueSoundsEnabled { get; set; }

    /// <summary>
    /// When true, batch saves write straight to <see cref="DefaultSaveFolder"/> with no picker.
    /// Off by default: silently writing files to a folder the user did not just confirm is the
    /// kind of helpfulness that loses work.
    /// </summary>
    public bool SaveWithoutAsking { get; set; }
}

/// <summary>
/// Loads and stores <see cref="AppSettings"/> as JSON under
/// <c>%LOCALAPPDATA%\PoChopAudio\settings.json</c>.
///
/// <para>
/// Follows the same probe-report-degrade rule as the ONNX model: a missing file is the normal
/// first-run case, and an unreadable or corrupt one falls back to defaults rather than throwing.
/// Settings are a convenience, so failing to read them must never be what stops the app starting.
/// </para>
/// </summary>
public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _gate = new();

    public AppSettingsService()
    {
        Directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PoChopAudio");
        FilePath = Path.Combine(Directory, "settings.json");
        Current = Load(FilePath);
    }

    /// <summary>Raised after a successful <see cref="Update"/>, so the shell can re-theme itself.</summary>
    public event Action<AppSettings>? Changed;

    /// <summary>The folder holding both the settings file and the log directory.</summary>
    public string Directory { get; }

    public string FilePath { get; }

    /// <summary>True once something has actually been written, which the About section reports.</summary>
    public bool Exists => File.Exists(FilePath);

    public AppSettings Current { get; private set; }

    /// <summary>
    /// Mutates the settings and writes them. Taking a callback rather than exposing setters keeps
    /// "changed" and "saved" from drifting apart — there is no way to change a value and forget.
    /// </summary>
    public void Update(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_gate)
        {
            mutate(Current);
            Save();
        }

        Changed?.Invoke(Current);
    }

    public void ResetToDefaults()
    {
        lock (_gate)
        {
            Current = new AppSettings();
            Save();
        }

        Changed?.Invoke(Current);
    }

    private void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, SerializerOptions));
        }
        catch (IOException)
        {
            // A settings file that will not write is not worth taking the app down for.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), SerializerOptions)
                ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Hand-edited or half-written. Defaults are always a valid answer here.
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }
}
