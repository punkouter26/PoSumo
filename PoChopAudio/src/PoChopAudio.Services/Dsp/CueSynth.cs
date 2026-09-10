namespace PoChopAudio.Services.Dsp;

/// <summary>Which cue to synthesise. Closed set, so it is an enum rather than a frequency.</summary>
public enum AudioCue
{
    /// <summary>Count-in tick. Short, dry, unpitched-sounding.</summary>
    Tick,

    /// <summary>The downbeat of a count-in, a fifth above the tick so it is distinguishable.</summary>
    Accent,

    /// <summary>Played at the start of an auditioned clip.</summary>
    ClipStart,

    /// <summary>Played at the end of an auditioned clip.</summary>
    ClipEnd,

    /// <summary>Rising two-note chime when a batch finishes.</summary>
    Success,

    /// <summary>Falling two-note chime when something failed.</summary>
    Failure,

    /// <summary>Razor transient blade chop when a take or recording is split.</summary>
    Slice,

    /// <summary>Holographic ascending frequency sweep and crystal bell when photo cutout reveals.</summary>
    ScanReveal,

    /// <summary>Ultra-short high micro-tick for magnetic playhead snapping.</summary>
    ScrubTick,

    /// <summary>Crystal dual-harmonic chime on card expansion.</summary>
    GlassTapOpen,

    /// <summary>Descending crystal tone on card collapse.</summary>
    GlassTapClose,

    /// <summary>Subtle aerodynamic air puff when hovering or picking up a card.</summary>
    CardHover,

    /// <summary>Cascading major 9th arpeggio chime on batch completion.</summary>
    GrandSuccess,
}

/// <summary>
/// Generates the app's cue sounds as samples, with no files to ship and no assets to license.
///
/// <para>
/// Everything here is a sine with an exponential decay envelope. That is not a limitation being
/// apologised for: in an app where the user is judging recorded audio, a cue has to be immediately
/// distinguishable from the material, and a pure decaying tone is about as far from a voice or a
/// footstep as a sound can get. A sampled "ding" would be one more thing to mistake for content.
/// </para>
/// <para>
/// Pure and I/O-free by design, per the same rule as the rest of this project — the playback device
/// lives in the app, the arithmetic lives here where it can be tested.
/// </para>
/// </summary>
public static class CueSynth
{
    /// <summary>Peak amplitude of a generated cue. Deliberately quiet next to a normalised take.</summary>
    public const float DefaultAmplitude = 0.22f;

    /// <summary>
    /// Renders <paramref name="cue"/> as mono samples at <paramref name="sampleRate"/>.
    /// </summary>
    /// <param name="amplitude">Peak amplitude, 0..1. Clamped.</param>
    public static float[] Render(AudioCue cue, int sampleRate, float amplitude = DefaultAmplitude)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);

        amplitude = Math.Clamp(amplitude, 0f, 1f);

        return cue switch
        {
            AudioCue.Tick => Tone(sampleRate, amplitude, 0.045, (1000, 0.0)),
            AudioCue.Accent => Tone(sampleRate, amplitude, 0.055, (1500, 0.0)),
            AudioCue.ClipStart => Tone(sampleRate, amplitude * 0.7f, 0.05, (1320, 0.0)),
            AudioCue.ClipEnd => Tone(sampleRate, amplitude * 0.7f, 0.05, (880, 0.0)),
            AudioCue.Success => Tone(sampleRate, amplitude, 0.26, (880, 0.0), (1320, 0.09)),
            AudioCue.Failure => Tone(sampleRate, amplitude, 0.30, (660, 0.0), (440, 0.11)),
            AudioCue.Slice => SliceTone(sampleRate, amplitude),
            AudioCue.ScanReveal => Tone(sampleRate, amplitude, 0.32, (523.25, 0.0), (1046.5, 0.08), (2093.0, 0.16)),
            AudioCue.ScrubTick => Tone(sampleRate, amplitude * 0.5f, 0.012, (2600, 0.0)),
            AudioCue.GlassTapOpen => Tone(sampleRate, amplitude * 0.6f, 0.06, (2093.0, 0.0), (3135.96, 0.01)),
            AudioCue.GlassTapClose => Tone(sampleRate, amplitude * 0.5f, 0.05, (2637.0, 0.0), (1760.0, 0.015)),
            AudioCue.CardHover => Tone(sampleRate, amplitude * 0.4f, 0.035, (130.0, 0.0)),
            AudioCue.GrandSuccess => Tone(sampleRate, amplitude, 0.48,
                (523.25, 0.0), (659.25, 0.06), (783.99, 0.12), (987.77, 0.18), (1174.66, 0.24)),
            _ => [],
        };
    }

    /// <summary>
    /// Renders a whole count-in as one buffer: <paramref name="beats"/> ticks at
    /// <paramref name="bpm"/>, the first accented, followed by one beat of silence so recording
    /// starts on the downbeat after the last tick rather than on top of it.
    ///
    /// <para>
    /// Rendered as a single buffer rather than sequenced with a timer because a count-in whose
    /// beats drift is worse than none — a timer on a busy UI thread will drift, and sample offsets
    /// cannot.
    /// </para>
    /// </summary>
    public static float[] CountIn(int beats, double bpm, int sampleRate, float amplitude = DefaultAmplitude)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(beats, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bpm, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);

        var samplesPerBeat = (int)(sampleRate * 60.0 / bpm);
        var buffer = new float[samplesPerBeat * (beats + 1)];

        double[] pitches = [500.0, 750.0, 1000.0, 1250.0];

        for (var beat = 0; beat < beats; beat++)
        {
            var freq = pitches[Math.Min(beat, pitches.Length - 1)];
            var tick = Tone(sampleRate, amplitude, 0.045, (freq, 0.0), (freq * 1.5, 0.0));
            var start = beat * samplesPerBeat;

            for (var i = 0; i < tick.Length && start + i < buffer.Length; i++)
            {
                buffer[start + i] += tick[i];
            }
        }

        // Downbeat slate snap right before recording opens
        var snap = SlateSnap(sampleRate, amplitude);
        var snapStart = beats * samplesPerBeat;
        for (var i = 0; i < snap.Length && snapStart + i < buffer.Length; i++)
        {
            buffer[snapStart + i] += snap[i];
        }

        return buffer;
    }

    /// <summary>The exact length of a count-in, so a caller can wait it out without guessing.</summary>
    public static TimeSpan CountInDuration(int beats, double bpm) =>
        TimeSpan.FromSeconds(60.0 / bpm * (beats + 1));

    /// <summary>
    /// A reference tone for setting input gain: a steady sine at a known level, so the meter can be
    /// trusted before a take rather than after one.
    /// </summary>
    /// <param name="dbfs">Level in dBFS. -18 is the usual alignment level for spoken word.</param>
    public static float[] ReferenceTone(int sampleRate, double seconds = 2.0, double frequencyHz = 1000, double dbfs = -18)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);

        var count = (int)(sampleRate * seconds);
        var samples = new float[count];
        var peak = Math.Pow(10, dbfs / 20.0);
        var step = 2.0 * Math.PI * frequencyHz / sampleRate;

        // 10 ms raised-cosine edges, for the same reason clip export uses them: a tone that starts
        // and stops on a discontinuity clicks, and a click is exactly what a calibration tone must
        // not contain.
        var edge = Math.Min(count / 2, (int)(sampleRate * 0.01));

        for (var i = 0; i < count; i++)
        {
            var gain = 1.0;

            if (edge > 0 && i < edge)
            {
                gain = 0.5 * (1 - Math.Cos(Math.PI * i / edge));
            }
            else if (edge > 0 && i >= count - edge)
            {
                gain = 0.5 * (1 - Math.Cos(Math.PI * (count - 1 - i) / edge));
            }

            samples[i] = (float)(Math.Sin(step * i) * peak * gain);
        }

        return samples;
    }

    /// <summary>
    /// Synthesizes an acoustic plucked string tone with harmonic decay at <paramref name="frequencyHz"/>.
    /// </summary>
    public static float[] Pluck(int sampleRate, double frequencyHz, float amplitude = DefaultAmplitude)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(frequencyHz, 0);

        amplitude = Math.Clamp(amplitude, 0f, 1f);
        var seconds = Math.Clamp(60.0 / frequencyHz, 0.08, 0.35);
        var count = Math.Max(1, (int)(sampleRate * seconds));
        var samples = new float[count];

        var step = 2.0 * Math.PI * frequencyHz / sampleRate;
        var step2 = step * 2.0;
        var step3 = step * 3.0;
        var decay = 16.0 / seconds;

        for (var i = 0; i < count; i++)
        {
            var t = i / (double)sampleRate;
            var envelope = Math.Exp(-decay * t);
            var s = (Math.Sin(step * i) * 0.65)
                  + (Math.Sin(step2 * i) * 0.25 * Math.Exp(-decay * 1.5 * t))
                  + (Math.Sin(step3 * i) * 0.10 * Math.Exp(-decay * 2.0 * t));
            samples[i] = (float)(s * envelope);
        }

        Normalize(samples, amplitude);
        return samples;
    }

    private static float[] SliceTone(int sampleRate, float amplitude)
    {
        var seconds = 0.085;
        var count = Math.Max(1, (int)(sampleRate * seconds));
        var samples = new float[count];

        // Transient noise burst for the cutting edge (first 14 ms)
        var rng = new Random(1337);
        var noiseLen = Math.Min(count, (int)(sampleRate * 0.015));
        for (var i = 0; i < noiseLen; i++)
        {
            var t = i / (double)noiseLen;
            var env = (1.0 - t) * (1.0 - t);
            var white = (float)((rng.NextDouble() * 2.0) - 1.0);
            samples[i] += white * (float)env * 0.6f;
        }

        // Low body acoustic resonant thud
        var step = 2.0 * Math.PI * 110.0 / sampleRate;
        var decay = 20.0 / seconds;
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)sampleRate;
            samples[i] += (float)(Math.Sin(step * i) * Math.Exp(-decay * t) * 0.45);
        }

        Normalize(samples, amplitude);
        return samples;
    }

    private static float[] SlateSnap(int sampleRate, float amplitude)
    {
        var seconds = 0.04;
        var count = Math.Max(1, (int)(sampleRate * seconds));
        var samples = new float[count];

        var step = 2.0 * Math.PI * 1200.0 / sampleRate;
        var decay = 35.0 / seconds;
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)sampleRate;
            samples[i] = (float)(Math.Sin(step * i) * Math.Exp(-decay * t));
        }

        Normalize(samples, amplitude);
        return samples;
    }

    private static void Normalize(float[] samples, float amplitude)
    {
        var peak = 0f;
        foreach (var s in samples)
        {
            peak = Math.Max(peak, Math.Abs(s));
        }

        if (peak > 0)
        {
            var scale = amplitude / peak;
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] *= scale;
            }
        }
    }

    /// <summary>
    /// Sums one decaying sine per <paramref name="partials"/> entry, each starting at its own
    /// offset, and normalises so a two-note chime is no louder than a one-note tick.
    /// </summary>
    private static float[] Tone(
        int sampleRate, float amplitude, double seconds, params (double Hz, double StartSeconds)[] partials)
    {
        var count = Math.Max(1, (int)(sampleRate * seconds));
        var samples = new float[count];

        foreach (var (hz, startSeconds) in partials)
        {
            var start = (int)(startSeconds * sampleRate);
            var step = 2.0 * Math.PI * hz / sampleRate;

            // Decay chosen so the tail is inaudible by the end of the buffer; a cue that is still
            // ringing when the next one starts turns a count-in into a chord.
            var decay = 14.0 / Math.Max(0.001, seconds - startSeconds);

            for (var i = start; i < count; i++)
            {
                var t = (i - start) / (double)sampleRate;
                samples[i] += (float)(Math.Sin(step * (i - start)) * Math.Exp(-decay * t));
            }
        }

        Normalize(samples, amplitude);
        return samples;
    }
}
