using NAudio.Wave;
using PoChopAudio.Services.Dsp;

namespace PoChopAudio.WinUI.Services;

/// <summary>
/// Plays the app's synthesised cues on their own output device.
///
/// <para>
/// Separate from <see cref="AudioPlayerService"/> on purpose. That one owns clip audition and is
/// stopped and restarted constantly; routing cues through it would mean a count-in tick tearing
/// down the take a user is in the middle of listening to.
/// </para>
/// <para>
/// <b>The suppression rule is the point of this class.</b> In an app whose whole job is recording
/// and judging audio, a UI sound at the wrong moment is not a small annoyance — a tick that leaks
/// into a take corrupts the recording, and a blip over a clip corrupts the judgement being made
/// about it. <see cref="IsSuppressed"/> is set while recording, and every entry point checks it.
/// </para>
/// </summary>
public sealed class AudioCueService : IDisposable
{
    private const int SampleRate = 44100;

    private readonly Lock _gate = new();
    private WaveOutEvent? _output;

    /// <summary>Master switch. Off means this class produces no sound at all.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// True while the microphone is live. Set by the recording flow; while it holds, nothing here
    /// makes a sound, because the one place a cue must never appear is inside a take.
    /// </summary>
    public bool IsSuppressed { get; set; }

    private bool CanPlay => IsEnabled && !IsSuppressed;

    /// <summary>Plays one short cue. Returns immediately; the sound finishes on its own.</summary>
    public void Play(AudioCue cue, float amplitude = CueSynth.DefaultAmplitude)
    {
        if (!CanPlay)
        {
            return;
        }

        PlayBuffer(CueSynth.Render(cue, SampleRate, amplitude));
    }

    /// <summary>Plays an acoustic plucked string tone at <paramref name="frequencyHz"/>.</summary>
    public void PlayPluck(double frequencyHz, float amplitude = CueSynth.DefaultAmplitude)
    {
        if (!CanPlay)
        {
            return;
        }

        PlayBuffer(CueSynth.Pluck(SampleRate, frequencyHz, amplitude));
    }

    /// <summary>
    /// Plays a count-in and returns how long the caller must wait before the downbeat. Ignores
    /// <see cref="IsSuppressed"/> deliberately: a count-in runs <em>before</em> the microphone
    /// opens, which is the one moment when making a sound is the entire intent.
    /// </summary>
    public TimeSpan PlayCountIn(int beats, double bpm)
    {
        if (!IsEnabled)
        {
            return TimeSpan.Zero;
        }

        PlayBuffer(CueSynth.CountIn(beats, bpm, SampleRate));
        return CueSynth.CountInDuration(beats, bpm);
    }

    /// <summary>
    /// Plays a 1 kHz tone at -18 dBFS so the input meter can be read against a known level. Plays
    /// regardless of <see cref="IsEnabled"/> — it is an explicit user action, not a UI flourish.
    /// </summary>
    public void PlayReferenceTone(double seconds = 2.0)
    {
        if (IsSuppressed)
        {
            return;
        }

        PlayBuffer(CueSynth.ReferenceTone(SampleRate, seconds));
    }

    public void Stop()
    {
        lock (_gate)
        {
            DisposeOutput();
        }
    }

    private void PlayBuffer(float[] samples)
    {
        if (samples.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                // One cue at a time. Overlapping ticks on a slow machine would pile up output
                // devices; the newest cue is always the one worth hearing.
                DisposeOutput();

                var output = new WaveOutEvent { DesiredLatency = 120 };
                output.Init(new BufferSampleProvider(samples, SampleRate));
                output.Play();
                _output = output;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // No output device, or one that will not open. A missing cue sound is never worth
                // surfacing an error over — this is the same probe-report-degrade rule the rest of
                // the app follows, minus the reporting, because there is nothing to report.
                DisposeOutput();
            }
        }
    }

    private void DisposeOutput()
    {
        if (_output is null)
        {
            return;
        }

        try
        {
            _output.Stop();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Already gone.
        }

        _output.Dispose();
        _output = null;
    }

    public void Dispose() => Stop();

    /// <summary>Streams a float array once, then reports end of stream.</summary>
    private sealed class BufferSampleProvider(float[] samples, int sampleRate) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var available = Math.Min(count, samples.Length - _position);

            if (available <= 0)
            {
                return 0;
            }

            Array.Copy(samples, _position, buffer, offset, available);
            _position += available;
            return available;
        }
    }
}
