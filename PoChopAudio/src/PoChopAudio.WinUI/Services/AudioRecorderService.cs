using NAudio.Wave;

namespace PoChopAudio.WinUI.Services;

public sealed class AudioRecorderService : IDisposable
{
    private WaveInEvent? _waveIn;
    private MemoryStream? _pcmStream;
    private WaveFileWriter? _writer;
    private System.Timers.Timer? _timer;
    private DateTimeOffset _startTime;
    private double _peakDb = -100;
    private double _rmsDb = -100;
    private bool _isClipping;
    private readonly object _lock = new();

    public bool IsRecording { get; private set; }

    /// <summary>How many envelope points each captured buffer is reduced to for the live scope.</summary>
    private const int ScopePointsPerBuffer = 48;

    public event Action<double, double, bool>? LevelUpdated;
    public event Action<TimeSpan>? ElapsedUpdated;

    /// <summary>
    /// A decimated peak envelope of the buffer just captured, for the live scope.
    ///
    /// <para>
    /// Decimated here rather than in the control, because the alternative is handing roughly 1100
    /// samples per 25 ms buffer across a thread boundary forty times a second to draw a few dozen
    /// pixels. Peak within each window rather than mean: a scope that averages away transients is
    /// exactly the wrong instrument for spotting a clipped consonant.
    /// </para>
    /// </summary>
    public event Action<float[]>? ScopeSamplesAvailable;

    public void Start(int sampleRate = 44100, int channels = 1)
    {
        lock (_lock)
        {
            if (IsRecording) return;

            _pcmStream = new MemoryStream();
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(sampleRate, 16, channels),
                BufferMilliseconds = 25
            };

            _writer = new WaveFileWriter(_pcmStream, _waveIn.WaveFormat);

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;

            _waveIn.StartRecording();
            IsRecording = true;
            _startTime = DateTimeOffset.Now;

            _timer = new System.Timers.Timer(50);
            _timer.Elapsed += (s, e) =>
            {
                if (IsRecording)
                {
                    ElapsedUpdated?.Invoke(DateTimeOffset.Now - _startTime);
                }
            };
            _timer.Start();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            if (_writer is null || e.BytesRecorded == 0) return;

            _writer.Write(e.Buffer, 0, e.BytesRecorded);

            // Compute peak and rms
            double maxSample = 0;
            double sumSquares = 0;
            int sampleCount = e.BytesRecorded / 2;

            for (int i = 0; i < e.BytesRecorded; i += 2)
            {
                short sample = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
                double normalized = Math.Abs(sample / 32768.0);
                if (normalized > maxSample) maxSample = normalized;
                sumSquares += normalized * normalized;
            }

            var peakDb = maxSample > 0.00001 ? 20 * Math.Log10(maxSample) : -100;
            var rms = sampleCount > 0 ? Math.Sqrt(sumSquares / sampleCount) : 0;
            var rmsDb = rms > 0.00001 ? 20 * Math.Log10(rms) : -100;
            var clipping = peakDb >= -0.2;

            _peakDb = peakDb;
            _rmsDb = rmsDb;
            _isClipping = _isClipping || clipping;

            LevelUpdated?.Invoke(peakDb, rmsDb, _isClipping);

            if (ScopeSamplesAvailable is { } scope && sampleCount > 0)
            {
                var points = new float[ScopePointsPerBuffer];
                var perPoint = Math.Max(1, sampleCount / ScopePointsPerBuffer);

                for (var p = 0; p < ScopePointsPerBuffer; p++)
                {
                    var peak = 0f;

                    for (var k = 0; k < perPoint; k++)
                    {
                        var index = ((p * perPoint) + k) * 2;
                        if (index + 1 >= e.BytesRecorded)
                        {
                            break;
                        }

                        var sample = (short)(e.Buffer[index] | (e.Buffer[index + 1] << 8));
                        peak = Math.Max(peak, Math.Abs(sample / 32768f));
                    }

                    points[p] = peak;
                }

                scope(points);
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // Handled in StopAsync
    }

    public byte[] Stop()
    {
        lock (_lock)
        {
            if (!IsRecording) return [];

            _timer?.Stop();
            _timer?.Dispose();
            _timer = null;

            _waveIn?.StopRecording();
            _waveIn?.Dispose();
            _waveIn = null;

            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;

            var bytes = _pcmStream?.ToArray() ?? [];
            _pcmStream?.Dispose();
            _pcmStream = null;

            IsRecording = false;
            _isClipping = false;

            return bytes;
        }
    }

    public void ResetClipping()
    {
        _isClipping = false;
        LevelUpdated?.Invoke(_peakDb, _rmsDb, false);
    }

    public void Dispose()
    {
        Stop();
    }
}

