using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using PoChopAudio.Services.Chop;
using PoChopAudio.Services.Dsp;
using PoChopAudio.Services.Cutout;
using PoChopAudio.WinUI.Common;
using PoChopAudio.WinUI.Services;
using Windows.ApplicationModel.DataTransfer;

namespace PoChopAudio.WinUI.ViewModels;

/// <summary>
/// The Settings page: appearance, where saves go, what the scratch folder is doing, and what this
/// machine can and cannot do.
///
/// <para>
/// The diagnostics half is not decoration. Six capabilities in this app degrade quietly when
/// something is missing — the ONNX model, the Windows face detector, Media Foundation codecs, a
/// camera, a microphone, Mica — and five of them had no user-visible report at all. Someone whose
/// head crops land wrong has no other way to learn that face detection never started.
/// </para>
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsService _settings;
    private readonly CutoutService _cutout;
    private readonly EnginePicker _picker;
    private readonly IFaceLocator? _faceLocator;
    private readonly ChopJobStore _jobs;
    private readonly CutoutModelOptions _model;
    private readonly FileLoggerProvider _log;
    private readonly AudioCueService _cues;

    public SettingsViewModel(
        AppSettingsService settings,
        CutoutService cutout,
        EnginePicker picker,
        ChopJobStore jobs,
        CutoutModelOptions model,
        FileLoggerProvider log,
        AudioCueService cues,
        IFaceLocator? faceLocator = null)
    {
        _cues = cues;
        _settings = settings;
        _cutout = cutout;
        _picker = picker;
        _jobs = jobs;
        _model = model;
        _log = log;
        _faceLocator = faceLocator;

        _themeIndex = settings.Current.Theme switch
        {
            ElementTheme.Light => 1,
            ElementTheme.Dark => 2,
            _ => 0,
        };
        _defaultSaveFolder = settings.Current.DefaultSaveFolder;
        _saveWithoutAsking = settings.Current.SaveWithoutAsking;
        _cueSoundsEnabled = settings.Current.CueSoundsEnabled;
    }

    public ObservableCollection<DiagnosticSection> Diagnostics { get; } = [];

    /// <summary>
    /// The window the folder picker hangs off. Set by the page once it is loaded, because
    /// <c>App.MainWindow</c> does not exist yet while the page is being constructed.
    /// </summary>
    public Window? Host { get; set; }

    /// <summary>System, Light, Dark — in the order the radio buttons appear.</summary>
    [ObservableProperty]
    private int _themeIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDefaultSaveFolder))]
    private string? _defaultSaveFolder;

    [ObservableProperty]
    private bool _saveWithoutAsking;

    [ObservableProperty]
    private bool _cueSoundsEnabled;

    [ObservableProperty]
    private string _scratchSizeText = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasDefaultSaveFolder => !string.IsNullOrWhiteSpace(DefaultSaveFolder);

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    public string SettingsFilePath => _settings.FilePath;

    public string LogDirectory => _log.LogDirectory;

    public string ScratchFolder => ChopJobStore.ParentRoot;

    partial void OnThemeIndexChanged(int value)
    {
        var theme = value switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        _settings.Update(s => s.Theme = theme);
        StatusMessage = "Appearance saved.";
    }

    partial void OnSaveWithoutAskingChanged(bool value)
    {
        _settings.Update(s => s.SaveWithoutAsking = value);
    }

    partial void OnCueSoundsEnabledChanged(bool value)
    {
        _settings.Update(s => s.CueSoundsEnabled = value);
        _cues.IsEnabled = value;

        if (value)
        {
            // Play the thing being switched on, so the choice is answered immediately rather than
            // three screens later at the start of a take.
            _cues.Play(AudioCue.Success);
        }
    }

    /// <summary>
    /// Whether Windows currently wants animation. Reported rather than offered as a setting: this
    /// is a system-wide accessibility choice, and an app-level override would be the app deciding
    /// it knows better.
    /// </summary>
    public bool AnimationsEnabled => Motion.AnimationsEnabled;

    public string MotionStatusText => Motion.AnimationsEnabled
        ? "Windows has animations on, so the gradients, transitions and bursts are running."
        : "Windows has animations off, so motion is disabled here too. Backgrounds stay still and nothing bursts.";

    partial void OnDefaultSaveFolderChanged(string? value)
    {
        _settings.Update(s => s.DefaultSaveFolder = value);
    }

    /// <summary>Rebuilds the capability report. Cheap enough to run on every page visit.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        var sections = await DiagnosticsReport.BuildAsync(
            _cutout, _picker, _faceLocator, _jobs, _model, _log, _settings);

        Diagnostics.Clear();
        foreach (var section in sections)
        {
            Diagnostics.Add(section);
        }

        ScratchSizeText = DiagnosticsReport.FormatBytes(ChopJobStore.ScratchBytes());
    }

    [RelayCommand]
    public async Task PickDefaultSaveFolderAsync()
    {
        if (Host is null) return;

        var folder = await ExportService.PickFolderAsync(Host);
        if (!string.IsNullOrEmpty(folder))
        {
            DefaultSaveFolder = folder;
            StatusMessage = "Saves will start from this folder.";
        }
    }

    [RelayCommand]
    public void ClearDefaultSaveFolder()
    {
        DefaultSaveFolder = null;
        SaveWithoutAsking = false;
        StatusMessage = "The app will ask where to save again.";
    }

    /// <summary>
    /// Deletes scratch left by earlier runs. Nothing in use is touched: the store skips its own
    /// directory and any directory a second running copy of the app still holds a lock on. Doing
    /// otherwise would delete a canonical WAV out from under the clips on someone's screen.
    /// </summary>
    [RelayCommand]
    public async Task CleanUpScratchAsync()
    {
        IsBusy = true;

        try
        {
            var before = ChopJobStore.ScratchBytes();
            var removed = await Task.Run(_jobs.SweepAbandoned);
            var freed = Math.Max(0, before - ChopJobStore.ScratchBytes());

            StatusMessage = removed == 0
                ? "Nothing to clean up — everything here is still in use."
                : $"Removed {removed} leftover folder{(removed == 1 ? string.Empty : "s")}, freeing {DiagnosticsReport.FormatBytes(freed)}.";

            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void OpenLogFolder() => DiagnosticsReport.RevealInExplorer(_log.LogDirectory, _log.LogFilePath);

    [RelayCommand]
    public void OpenScratchFolder() => DiagnosticsReport.RevealInExplorer(ChopJobStore.ParentRoot);

    [RelayCommand]
    public void CopyDiagnostics()
    {
        var package = new DataPackage();
        package.SetText(DiagnosticsReport.ToText([.. Diagnostics]));
        Clipboard.SetContent(package);

        StatusMessage = "Diagnostics copied to the clipboard.";
    }

    [RelayCommand]
    public void ResetAllSettings()
    {
        _settings.ResetToDefaults();

        ThemeIndex = 0;
        DefaultSaveFolder = null;
        SaveWithoutAsking = false;
        StatusMessage = "Settings are back to their defaults.";
    }
}
