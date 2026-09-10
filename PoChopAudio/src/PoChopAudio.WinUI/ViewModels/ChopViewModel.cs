using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PoChopAudio.Services.Chop;
using PoChopAudio.Services.Dsp;
using PoChopAudio.WinUI.Common;
using PoChopAudio.WinUI.Models;
using PoChopAudio.WinUI.Services;
using Windows.Storage.Pickers;

namespace PoChopAudio.WinUI.ViewModels;

/// <summary>
/// The chop page: the loaded recordings, the knobs, and everything that turns them into clips.
/// Capturing a take lives in <see cref="RecordingViewModel"/>, reached through
/// <see cref="Recording"/>, which hands finished audio back through its TakeRecorded event.
/// </summary>
public partial class ChopViewModel : ObservableObject, IDisposable
{
    private readonly ChopService _chop;
    private readonly AudioPlayerService _player;
    private readonly AppSettingsService _settings;
    private readonly AudioCueService _cues;
    private readonly Debouncer _debouncer = new(TimeSpan.FromMilliseconds(350));
    private readonly DispatcherQueue? _dispatcher = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource _cts = new();

    public ChopViewModel(
        ChopService chop,
        RecordingViewModel recording,
        AudioPlayerService player,
        AppSettingsService settings,
        AudioCueService cues)
    {
        _chop = chop;
        _player = player;
        _settings = settings;
        _cues = cues;
        Recording = recording;

        _cues.IsEnabled = settings.Current.CueSoundsEnabled;
        settings.Changed += s => _cues.IsEnabled = s.CueSoundsEnabled;

        Recording.TakeRecorded += OnTakeRecorded;
        Recording.StatusReported += message => StatusMessage = message;

        // The player's callbacks arrive on its own thread. Setting observable properties there
        // raises PropertyChanged off the UI thread, which the XAML bindings cannot act on.
        _player.PositionUpdated += (curr, total) => OnUiThread(() =>
        {
            if (ActivePlayingItem is not null && total > 0)
            {
                ActivePlayingItem.PlayheadRatio = curr / total;
            }
        });

        _player.PlaybackStopped += () => OnUiThread(() =>
        {
            if (ActivePlayingItem is not null)
            {
                // The closing blip sounds here, once the clip has finished, so it marks the end
                // rather than covering the tail of what was being judged.
                var wasClip = ActivePlayingItem.PlayingSegmentIndex is not null;

                ActivePlayingItem.IsPlaying = false;
                ActivePlayingItem.PlayheadRatio = 0;
                ActivePlayingItem.PlayingSegmentIndex = null;
                ActivePlayingItem = null;

                if (wasClip)
                {
                    _cues.Play(AudioCue.ClipEnd);
                }
            }
        });

        BatchKnobs.PropertyChanged += (s, e) =>
        {
            _debouncer.Debounce(async () => await ResplitBatchAutoAsync());
        };

        Files.CollectionChanged += (s, e) => NotifyCountProperties();
    }

    /// <summary>Capturing a take. Owns the recorder, the count-in and the input meter.</summary>
    public RecordingViewModel Recording { get; }

    /// <summary>Synthesized audio cues service for UI and feedback sounds.</summary>
    public AudioCueService Cues => _cues;

    /// <summary>
    /// The window file pickers hang off. Set by the page once it is loaded, because
    /// <c>App.MainWindow</c> does not exist yet while the page is being constructed.
    /// <para>
    /// This is what lets every button on the page bind a command instead of a Click handler: the
    /// commands that save a file no longer need a Window passed in as a parameter, which XAML had
    /// no way to supply.
    /// </para>
    /// </summary>
    public Window? Host { get; set; }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread, inline when already there.
    /// </summary>
    private void OnUiThread(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.TryEnqueue(() => action());
        }
    }

    /// <summary>
    /// Re-reads the counts the page shows. This used to raise six changes, five of which were
    /// bound by nothing at all — FilesSummaryText, ReadyCount, AttentionCount and UntunedCount
    /// were computed and notified on every collection change and displayed nowhere.
    /// </summary>
    public void NotifyCountProperties()
    {
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(TotalClips));
    }

    [ObservableProperty]
    private ObservableCollection<ChopFileItem> _files = [];

    [ObservableProperty]
    private ChopKnobsModel _batchKnobs = new();

    [ObservableProperty]
    private ExportKnobsModel _exportKnobs = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private ChopFileItem? _activePlayingItem;

    public bool HasFiles => Files.Count > 0;

    /// <summary>How many clips the whole batch would export. Used by the save progress message.</summary>
    public int TotalClips => Files.Sum(f => f.Segments.Count);

    [RelayCommand]
    public async Task PickFilesAsync()
    {
        if (Host is null)
        {
            return;
        }

        ErrorMessage = null;
        var picker = new FileOpenPicker();
        WindowHelper.InitWithWindow(picker, Host);
        picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;

        foreach (var ext in AudioDecoder.SupportedExtensions)
        {
            picker.FileTypeFilter.Add(ext);
        }

        var picked = await picker.PickMultipleFilesAsync();
        if (picked is not null && picked.Count > 0)
        {
            await AddFilesAsync(picked.Select(p => p.Path).ToList());
        }
    }

    public async Task AddFilesAsync(IEnumerable<string> filePaths)
    {
        ErrorMessage = null;
        var list = filePaths.ToList();
        var available = ChopLimits.MaxBatchFiles - Files.Count;
        if (available <= 0)
        {
            ErrorMessage = $"Batch limit reached (maximum {ChopLimits.MaxBatchFiles} files allowed).";
            return;
        }

        var toAdd = list.Take(available).ToList();
        var newItems = new List<ChopFileItem>();

        foreach (var path in toAdd)
        {
            var fileInfo = new FileInfo(path);
            var item = new ChopFileItem
            {
                Owner = this,
                FileName = fileInfo.Name,
                LocalFilePath = path,
                SizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
                Settings = BatchKnobs.Clone(),
                IsExpanded = Files.Count == 0 && toAdd.Count == 1,
                Status = ItemProcessingStatus.Queued
            };
            Files.Add(item);
            newItems.Add(item);
        }

        NotifyCountProperties();
        IsBusy = true;

        try
        {
            for (int i = 0; i < newItems.Count; i++)
            {
                var item = newItems[i];
                StatusMessage = $"Decoding {item.FileName} ({i + 1} of {newItems.Count})…";

                if (File.Exists(item.LocalFilePath))
                {
                    await using var fs = File.OpenRead(item.LocalFilePath);
                    await UploadAndAnalyzeItemAsync(item, fs, item.FileName);
                }
            }
        }
        finally
        {
            IsBusy = false;
            StatusMessage = string.Empty;
            NotifyCountProperties();
        }
    }

    private async Task UploadAndAnalyzeItemAsync(ChopFileItem item, Stream stream, string fileName)
    {
        try
        {
            item.Status = ItemProcessingStatus.Uploading;
            var uploadResult = (await _chop.UploadAsync(stream, fileName, stream.Length, _cts.Token)).OrThrow();

            item.JobId = uploadResult.JobId;
            item.DurationSeconds = uploadResult.DurationSeconds;
            item.SampleRate = uploadResult.SampleRate;
            item.Channels = uploadResult.Channels;
            item.PeakDb = uploadResult.PeakDb;
            item.NoiseFloorDb = uploadResult.NoiseFloorDb;
            item.Waveform = uploadResult.Waveform;

            item.Status = ItemProcessingStatus.Analyzing;
            var analysis = await Task.Run(
                () => _chop.Analyze(item.JobId, item.Settings.ToOptions()).OrThrow(), _cts.Token);

            item.DetectedThresholdDb = analysis.ThresholdDb;
            item.Warning = analysis.Warning;
            item.Segments = new ObservableCollection<ChopSegment>(analysis.Segments);
            item.Status = ItemProcessingStatus.Ready;
        }
        catch (Exception ex)
        {
            item.Status = ItemProcessingStatus.Failed;
            item.ErrorMessage = ex.Message;
        }
    }

    /// <summary>Takes a finished recording off <see cref="Recording"/> and runs it through the batch.</summary>
    private async void OnTakeRecorded(byte[] wavBytes, string fileName)
    {
        var item = new ChopFileItem
        {
            Owner = this,
            FileName = fileName,
            SizeBytes = wavBytes.Length,
            Settings = BatchKnobs.Clone(),
            IsExpanded = true,
            Status = ItemProcessingStatus.Queued
        };

        Files.Add(item);
        NotifyCountProperties();

        IsBusy = true;
        StatusMessage = $"Processing recording {fileName}…";

        try
        {
            using var ms = new MemoryStream(wavBytes);
            await UploadAndAnalyzeItemAsync(item, ms, fileName);
        }
        catch (Exception exception)
        {
            // async void because an event handler has nowhere to return a Task to. Nothing may
            // escape it: an exception here reaches the runtime's unhandled handler and takes the
            // process down mid-session with the take unsaved.
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
            StatusMessage = string.Empty;
            NotifyCountProperties();
        }

        _cues.Play(item.IsReady ? AudioCue.Success : AudioCue.Failure);
        BatchCompleted?.Invoke(item.IsReady);
    }

    /// <summary>
    /// Raised when a batch finishes, with whether it worked. The page uses it to fire the
    /// celebration burst; the view model deliberately knows nothing about particles.
    /// </summary>
    public event Action<bool>? BatchCompleted;

    /// <summary>
    /// Shows or hides the frequency view for one file, building it on first use.
    ///
    /// <para>
    /// Built on demand rather than during analysis: it reads the whole canonical WAV back off disk
    /// and runs a few hundred FFTs, which is not work to do for every file in a batch on the chance
    /// that someone looks at one of them.
    /// </para>
    /// </summary>
    [RelayCommand]
    public async Task ToggleSpectrogramAsync(ChopFileItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.ShowSpectrogram)
        {
            item.ShowSpectrogram = false;
            return;
        }

        if (item.Spectrogram is null)
        {
            item.IsBuildingSpectrogram = true;

            try
            {
                var outcome = await Task.Run(
                    () => _chop.GetSpectrogram(item.JobId, columns: 720, bins: 128), _cts.Token);

                if (!outcome.IsSuccess)
                {
                    ErrorMessage = outcome.Message;
                    return;
                }

                item.Spectrogram = outcome.Value;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ErrorMessage = $"Could not build the frequency view: {exception.Message}";
                return;
            }
            finally
            {
                item.IsBuildingSpectrogram = false;
            }
        }

        item.ShowSpectrogram = true;
    }

    /// <summary>Moves playback to a position dragged on the waveform.</summary>
    public void Seek(ChopFileItem item, double seconds)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (ActivePlayingItem == item && _player.IsPlaying)
        {
            _player.Seek(seconds);
        }
    }

    private async Task ResplitBatchAutoAsync()
    {
        var targets = Files.Where(f => !f.UsesOwnSettings && f.IsReady).ToList();
        if (targets.Count == 0) return;

        foreach (var item in targets)
        {
            item.Settings.LoadFrom(BatchKnobs.ToOptions());
            await ResplitOneAsync(item);
        }
    }

    [RelayCommand]
    public async Task ResplitAllAsync()
    {
        var targets = Files.Where(f => !f.UsesOwnSettings && f.IsReady).ToList();
        if (targets.Count == 0) return;

        IsBusy = true;
        StatusMessage = "Re-splitting recordings…";
        try
        {
            foreach (var item in targets)
            {
                item.Settings.LoadFrom(BatchKnobs.ToOptions());
                await ResplitOneAsync(item);
            }
        }
        finally
        {
            IsBusy = false;
            StatusMessage = string.Empty;
            NotifyCountProperties();
        }
    }

    [RelayCommand]
    public async Task ResplitOneAsync(ChopFileItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.JobId == default) return;

        try
        {
            var result = await Task.Run(
                () => _chop.Analyze(item.JobId, item.Settings.ToOptions()).OrThrow(), _cts.Token);
            item.DetectedThresholdDb = result.ThresholdDb;
            item.Warning = result.Warning;
            item.Segments = new ObservableCollection<ChopSegment>(result.Segments);
            item.Version++;
            NotifyCountProperties();
        }
        catch (Exception ex)
        {
            item.ErrorMessage = ex.Message;
        }
    }

    /// <summary>Detaches one file from the batch knobs and re-splits it with its own.</summary>
    [RelayCommand]
    public async Task ResplitDetachedAsync(ChopFileItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        item.UsesOwnSettings = true;
        await ResplitOneAsync(item);
    }

    /// <summary>Puts a detached file back under the batch knobs and re-splits it with them.</summary>
    [RelayCommand]
    public async Task FollowBatchAsync(ChopFileItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        item.UsesOwnSettings = false;
        item.Settings.LoadFrom(BatchKnobs.ToOptions());
        await ResplitOneAsync(item);
    }

    /// <summary>Plays a whole recording — the original file when it is still on disk, else take 1.</summary>
    [RelayCommand]
    public Task PlayWholeRecordingAsync(ChopFileItem item) => PlayTakeAsync(item, null);

    /// <summary>
    /// Plays one detected sound. Takes the segment alone because that is all a row inside the
    /// takes list can bind; the file it belongs to is the one holding it.
    /// </summary>
    [RelayCommand]
    public Task PlaySegmentAsync(ChopSegment segment) =>
        FileOf(segment) is { } item ? PlayTakeAsync(item, segment) : Task.CompletedTask;

    public async Task PlayTakeAsync(ChopFileItem item, ChopSegment? segment)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.IsPlaying && item.PlayingSegmentIndex == segment?.Index)
        {
            _player.Stop();
            return;
        }

        try
        {
            byte[] wavBytes;
            if (segment is not null)
            {
                wavBytes = await Task.Run(
                    () => _chop.GetClip(item.JobId, segment.Index, ExportKnobs.ToOptions()).OrThrow().Content, _cts.Token);
            }
            else if (!string.IsNullOrEmpty(item.LocalFilePath) && File.Exists(item.LocalFilePath))
            {
                wavBytes = await File.ReadAllBytesAsync(item.LocalFilePath, _cts.Token);
            }
            else if (item.Segments.Count > 0)
            {
                wavBytes = await Task.Run(
                    () => _chop.GetClip(item.JobId, item.Segments[0].Index, ExportKnobs.ToOptions()).OrThrow().Content, _cts.Token);
            }
            else
            {
                return;
            }

            if (ActivePlayingItem is not null && ActivePlayingItem != item)
            {
                ActivePlayingItem.IsPlaying = false;
                ActivePlayingItem.PlayingSegmentIndex = null;
            }

            ActivePlayingItem = item;
            item.IsPlaying = true;
            item.PlayingSegmentIndex = segment?.Index;

            // Brackets the clip rather than playing over it: the blip finishes before the audio
            // starts, and its partner sounds only once playback has stopped.
            if (segment is not null)
            {
                _cues.Play(AudioCue.ClipStart);
            }

            _player.Play(wavBytes);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not play take: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task ExportBatchZipAsync()
    {
        if (Host is null) return;

        var ready = Files.Where(f => f.IsReady).ToList();
        if (ready.Count == 0) return;

        var savePath = await ExportService.PickSaveFileAsync(Host, "clips.zip", ".zip", "ZIP Archive");
        if (string.IsNullOrEmpty(savePath)) return;

        IsBusy = true;
        StatusMessage = "Creating batch ZIP…";

        try
        {
            var jobIds = ready.Select(f => f.JobId).ToList();
            var zip = await Task.Run(
                () => _chop.GetBatchZip(jobIds, ExportKnobs.ToOptions()).OrThrow(), _cts.Token);
            await ExportService.SaveBytesToFileAsync(zip.Content, savePath, _cts.Token);
            StatusMessage = $"Saved batch ZIP to {Path.GetFileName(savePath)}";
            _cues.Play(AudioCue.GrandSuccess);
            BatchCompleted?.Invoke(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to export ZIP: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task SaveAllToFolderAsync()
    {
        if (Host is null) return;

        var ready = Files.Where(f => f.IsReady).ToList();
        if (ready.Count == 0) return;

        var folderPath = await ExportService.ResolveBatchFolderAsync(Host, _settings);
        if (string.IsNullOrEmpty(folderPath)) return;

        IsBusy = true;
        try
        {
            int totalSaved = 0;
            foreach (var fileItem in ready)
            {
                var stem = Path.GetFileNameWithoutExtension(fileItem.FileName);

                foreach (var seg in fileItem.Segments)
                {
                    StatusMessage = $"Saving {stem}_{seg.Index}.wav ({totalSaved + 1} of {TotalClips})…";

                    var clipBytes = await Task.Run(
                        () => _chop.GetClip(fileItem.JobId, seg.Index, ExportKnobs.ToOptions()).OrThrow().Content, _cts.Token);
                    var targetFile = Path.Combine(folderPath, $"{stem}_{seg.Index}.wav");
                    await ExportService.SaveBytesToFileAsync(clipBytes, targetFile, _cts.Token);
                    totalSaved++;
                }
            }
            StatusMessage = $"Exported {totalSaved} clips to {folderPath}";
            _cues.Play(AudioCue.GrandSuccess);
            BatchCompleted?.Invoke(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save clips to folder: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Saves one detected sound as its own WAV.</summary>
    [RelayCommand]
    public async Task SaveSegmentAsync(ChopSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        if (Host is null || FileOf(segment) is not { } item) return;

        var stem = Path.GetFileNameWithoutExtension(item.FileName);
        var defaultName = $"{stem}_{segment.Index}.wav";
        var savePath = await ExportService.PickSaveFileAsync(Host, defaultName, ".wav", "WAV Audio");
        if (string.IsNullOrEmpty(savePath)) return;

        try
        {
            var bytes = await Task.Run(
                () => _chop.GetClip(item.JobId, segment.Index, ExportKnobs.ToOptions()).OrThrow().Content, _cts.Token);
            await ExportService.SaveBytesToFileAsync(bytes, savePath, _cts.Token);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save clip: {ex.Message}";
        }
    }

    /// <summary>The recording a segment was cut from.</summary>
    private ChopFileItem? FileOf(ChopSegment segment) =>
        Files.FirstOrDefault(f => f.Segments.Contains(segment));

    [RelayCommand]
    public void ClearAll()
    {
        _player.Stop();
        foreach (var file in Files)
        {
            _chop.Delete(file.JobId);
        }
        Files.Clear();
        ErrorMessage = null;
        StatusMessage = string.Empty;
        NotifyCountProperties();
    }

    [RelayCommand]
    public void ResetSettings()
    {
        BatchKnobs.LoadFrom(new ChopOptions());
        ExportKnobs = new ExportKnobsModel();
    }

    /// <summary>
    /// Returns the page to its opening state: drops every loaded file and job, resets both knob
    /// sets, and clears the pending take name. ClearAll on its own leaves the tuned knobs and the
    /// typed filename behind, which is not what "start over" means to someone staring at a bad run.
    /// </summary>
    [RelayCommand]
    public void StartOver()
    {
        Recording.Abandon();
        ClearAll();
        ResetSettings();
    }

    public void Dispose()
    {
        Recording.TakeRecorded -= OnTakeRecorded;
        _cts.Cancel();
        _cts.Dispose();
        _debouncer.Dispose();
        Recording.Dispose();
        _player.Dispose();
    }
}
