using NAudio.Wave;

namespace PoChopAudio.WinUI.Services;

public sealed class AudioPlayerService : IDisposable
{
    private WaveOutEvent? _output;
    private WaveFileReader? _reader;
    private MemoryStream? _stream;
    private System.Timers.Timer? _timer;
    private double _startSeconds;
    private double? _endSeconds;
    private readonly object _lock = new();

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public double CurrentTimeSeconds { get; private set; }
    public double TotalDurationSeconds { get; private set; }

    public event Action<double, double>? PositionUpdated;
    public event Action? PlaybackStopped;

    public void Play(byte[] wavBytes, double startSeconds = 0, double? endSeconds = null)
    {
        lock (_lock)
        {
            Stop();

            _stream = new MemoryStream(wavBytes);
            _reader = new WaveFileReader(_stream);
            _output = new WaveOutEvent();

            _startSeconds = startSeconds;
            _endSeconds = endSeconds;
            TotalDurationSeconds = _reader.TotalTime.TotalSeconds;

            if (startSeconds > 0)
            {
                _reader.CurrentTime = TimeSpan.FromSeconds(startSeconds);
            }

            _output.Init(_reader);
            _output.PlaybackStopped += OnPlaybackStopped;
            _output.Play();

            _timer = new System.Timers.Timer(40);
            _timer.Elapsed += (s, e) =>
            {
                lock (_lock)
                {
                    if (_reader is null || _output?.PlaybackState != PlaybackState.Playing) return;

                    var current = _reader.CurrentTime.TotalSeconds;
                    CurrentTimeSeconds = current;

                    if (_endSeconds.HasValue && current >= _endSeconds.Value)
                    {
                        Stop();
                        return;
                    }

                    PositionUpdated?.Invoke(current, TotalDurationSeconds);
                }
            };
            _timer.Start();
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            _output?.Pause();
            _timer?.Stop();
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            _output?.Play();
            _timer?.Start();
        }
    }

    public void Seek(double seconds)
    {
        lock (_lock)
        {
            if (_reader is not null)
            {
                _reader.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, TotalDurationSeconds));
                CurrentTimeSeconds = _reader.CurrentTime.TotalSeconds;
                PositionUpdated?.Invoke(CurrentTimeSeconds, TotalDurationSeconds);
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _timer?.Stop();
            _timer?.Dispose();
            _timer = null;

            if (_output is not null)
            {
                _output.PlaybackStopped -= OnPlaybackStopped;
                _output.Stop();
                _output.Dispose();
                _output = null;
            }

            _reader?.Dispose();
            _reader = null;

            _stream?.Dispose();
            _stream = null;

            CurrentTimeSeconds = 0;
            PlaybackStopped?.Invoke();
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        Stop();
    }

    public void Dispose()
    {
        Stop();
    }
}

