using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PoSumo.EditorTools
{
    /// Synthesises every sound effect and music stem the game uses, straight into
    /// Assets/Resources/Audio as 16-bit mono WAVs.
    ///
    /// The nine clips that shipped were flat single-shot placeholders — one thud
    /// pitch-shifted for every impact from a stumble to a match-winning slam, and
    /// two crowd clips that no code ever loaded. This replaces them with a layered
    /// set: impacts are three separate layers (body mass, flesh slap, clay dust)
    /// that Systems_MatchAudio crossfades by impact velocity, plus round-robin
    /// variants of everything so repetition never becomes audible.
    ///
    /// It is synthesis, not recording, so it is not going to beat a real foley
    /// session — but it is layered, velocity-mapped and varied, which is what
    /// actually makes impacts read. Overwrite the WAVs with recordings and every
    /// system downstream keeps working unchanged.
    ///
    /// Menu: PoSumo/Generate Audio.
    public static class GenerateAudio
    {
        private const int SAMPLE_RATE = 44100;
        private const string OUTPUT_DIR = "Assets/Resources/Audio";

        // One loop length for every music stem so they layer in phase.
        // 8 s at 120 BPM = 4 bars of 4/4, 16 steps of 0.5 s.
        private const float MUSIC_SECONDS = 8f;
        private const float MUSIC_STEP = 0.5f;

        [MenuItem("PoSumo/Generate Audio")]
        public static void Run()
        {
            int written = 0;
            try
            {
                Directory.CreateDirectory(OUTPUT_DIR);

                // --- Impact layers. Three per hit, three variants of each, so a
                // scrum never plays the same triple twice in a row.
                for (int v = 0; v < 3; v++)
                {
                    char suffix = (char)('A' + v);
                    written += Write($"SFX_ThudLow{suffix}", ThudLow(v));
                    written += Write($"SFX_ThudMid{suffix}", ThudMid(v));
                    written += Write($"SFX_ThudDust{suffix}", ThudDust(v));
                }

                // Legacy names, still loaded as a fallback by Systems_MatchAudio if
                // the layered set is missing. Kept in sync deliberately.
                written += Write("SFX_ThudA", Mix(ThudLow(0), ThudMid(0), 0.7f));
                written += Write("SFX_ThudB", Mix(ThudLow(1), ThudMid(1), 0.7f));
                written += Write("SFX_ThudC", Mix(ThudLow(2), ThudMid(2), 0.7f));

                for (int v = 0; v < 3; v++)
                {
                    written += Write($"SFX_Scuff{(char)('A' + v)}", Scuff(v));
                }
                written += Write("SFX_Scuff", Scuff(0));

                // --- Effort vocals. The loudest thing missing from a wrestling
                // game is the wrestlers making noise.
                for (int v = 0; v < 4; v++)
                {
                    written += Write($"SFX_Grunt{(char)('A' + v)}", Grunt(v));
                }
                for (int v = 0; v < 2; v++)
                {
                    written += Write($"SFX_Breath{(char)('A' + v)}", Breath(v));
                }
                written += Write("SFX_Strain", Strain());

                // --- Crowd.
                written += Write("SFX_CrowdMurmur", CrowdMurmur());
                written += Write("SFX_CrowdOoh", CrowdOoh());
                written += Write("SFX_CrowdGasp", CrowdGasp());
                written += Write("SFX_CrowdCheer", CrowdCheer());
                written += Write("SFX_CrowdApplause", CrowdApplause());

                // --- Ceremony.
                for (int v = 0; v < 3; v++)
                {
                    written += Write($"SFX_Taiko{(char)('A' + v)}", Taiko(v));
                }
                written += Write("SFX_Taiko", Taiko(0));
                written += Write("SFX_Gong", Gong());
                written += Write("SFX_Salt", Salt());

                // --- Adaptive music stems, all MUSIC_SECONDS long and loop-safe.
                written += Write("MUS_Bed", MusicBed());
                written += Write("MUS_Pulse", MusicPulse());
                written += Write("MUS_Drums", MusicDrums());
                written += Write("MUS_Tension", MusicTension());

                AssetDatabase.Refresh();
                Debug.Log($"AUDIO RESULT: OK — wrote {written} clips to {OUTPUT_DIR}");
            }
            catch (Exception e)
            {
                Debug.LogError($"AUDIO RESULT: FAILED — {e}");
            }
        }

        // ============================================================ impacts

        /// Body mass hitting body mass: a low sine dropping in pitch, which is
        /// what gives an impact its weight. No noise at all in this layer.
        private static float[] ThudLow(int variant)
        {
            var rng = new System.Random(1000 + variant);
            float[] buffer = New(0.4f);
            float startHz = 125f + variant * 9f;
            float endHz = 44f + variant * 3f;
            double phase = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                float t = i / (float)SAMPLE_RATE;
                // Pitch drop is exponential: a struck mass loses pitch fast, then
                // settles. Linear here reads as a synth sweep, not a body.
                float hz = Mathf.Lerp(endHz, startHz, Mathf.Exp(-t * 26f));
                phase += 2.0 * Math.PI * hz / SAMPLE_RATE;
                float env = Mathf.Exp(-t * 11f) * (1f - Mathf.Exp(-t * 900f));
                buffer[i] = (float)Math.Sin(phase) * env;
            }
            // A touch of very low noise for the "give" of soft tissue.
            var lp = Biquad.LowPass(180f, 0.7f, SAMPLE_RATE);
            for (int i = 0; i < buffer.Length; i++)
            {
                float t = i / (float)SAMPLE_RATE;
                float n = lp.Process((float)(rng.NextDouble() * 2 - 1));
                buffer[i] += n * Mathf.Exp(-t * 30f) * 0.5f;
            }
            return Normalize(buffer, 0.92f);
        }

        /// The slap: a short band-limited noise crack. This is the layer that
        /// carries how HARD the hit was, so it is the one weighted most by velocity.
        private static float[] ThudMid(int variant)
        {
            var rng = new System.Random(2000 + variant);
            float[] buffer = New(0.24f);
            var bp = Biquad.BandPass(620f + variant * 130f, 1.1f, SAMPLE_RATE);
            var lp = Biquad.LowPass(3200f, 0.6f, SAMPLE_RATE);
            for (int i = 0; i < buffer.Length; i++)
            {
                float t = i / (float)SAMPLE_RATE;
                float n = (float)(rng.NextDouble() * 2 - 1);
                float s = lp.Process(bp.Process(n));
                float env = Mathf.Exp(-t * 42f) * (1f - Mathf.Exp(-t * 2600f));
                buffer[i] = s * env;
            }
            return Normalize(buffer, 0.85f);
        }

        /// Clay dust kicked up by the contact: quiet, high, and longer than the
        /// other two, so a hit has a tail instead of stopping dead.
        private static float[] ThudDust(int variant)
        {
            var rng = new System.Random(3000 + variant);
            float[] buffer = New(0.42f);
            var hp = Biquad.HighPass(2100f + variant * 260f, 0.7f, SAMPLE_RATE);
            for (int i = 0; i < buffer.Length; i++)
            {
                float t = i / (float)SAMPLE_RATE;
                float n = hp.Process((float)(rng.NextDouble() * 2 - 1));
                float env = Mathf.Exp(-t * 7.5f) * (1f - Mathf.Exp(-t * 120f));
                buffer[i] = n * env;
            }
            return Normalize(buffer, 0.55f);
        }

        /// A foot dragging on clay.
        private static float[] Scuff(int variant)
        {
            var rng = new System.Random(4000 + variant);
            float[] buffer = New(0.22f);
            var bp = Biquad.BandPass(1500f + variant * 320f, 0.8f, SAMPLE_RATE);
            for (int i = 0; i < buffer.Length; i++)
            {
                float t = i / (float)SAMPLE_RATE;
                float n = bp.Process((float)(rng.NextDouble() * 2 - 1));
                // Swells then cuts: a scuff is a movement, not a strike.
                float env = Mathf.Sin(Mathf.Clamp01(t / 0.2f) * Mathf.PI);
                buffer[i] = n * env;
            }
            return Normalize(buffer, 0.6f);
        }

        // ============================================================= vocals

        /// A voiced effort grunt: a glottal buzz through three formant resonators.
        /// Variants walk the pitch and the vowel so the four do not read as one
        /// sample repitched.
        private static float[] Grunt(int variant)
        {
            var rng = new System.Random(5000 + variant);
            float length = 0.34f + variant * 0.05f;
            float[] buffer = New(length);

            float f0 = 104f - variant * 7f;
            // /a/ through /u/: an open shout to a closed strain.
            float[][] vowels =
            {
                new[] { 730f, 1090f, 2440f },
                new[] { 640f, 1190f, 2390f },
                new[] { 530f, 1840f, 2480f },
                new[] { 400f,  900f, 2300f },
            };
            float[] formants = vowels[variant % vowels.Length];

            var r1 = Biquad.BandPass(formants[0], 7f, SAMPLE_RATE);
            var r2 = Biquad.BandPass(formants[1], 9f, SAMPLE_RATE);
            var r3 = Biquad.BandPass(formants[2], 11f, SAMPLE_RATE);
            var breathBp = Biquad.BandPass(1700f, 0.8f, SAMPLE_RATE);

            double phase = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                float t = i / (float)SAMPLE_RATE;
                float progress = t / length;
                // Pitch sags through the grunt — the sound of running out of air.
                float hz = f0 * (1f + 0.16f * Mathf.Exp(-progress * 6f) - 0.1f * progress);
                phase += hz / SAMPLE_RATE;
                if (phase >= 1.0) phase -= 1.0;

                // Sawtooth glottal source.
                float source = (float)(2.0 * phase - 1.0);
                float voiced = r1.Process(source) * 1f
                             + r2.Process(source) * 0.5f
                             + r3.Process(source) * 0.22f;
                float breath = breathBp.Process((float)(rng.NextDouble() * 2 - 1)) * 0.25f;

                float env = Mathf.Min(1f, progress / 0.09f) * Mathf.Exp(-progress * 3.1f);
                buffer[i] = (voiced + breath) * env;
            }
            return Normalize(buffer, 0.8f);
        }

        /// Breathing between exchanges — unvoiced, just moving air.
        private static float[] Breath(int variant)
        {
            var rng = new System.Random(6000 + variant);
            float length = 0.5f + variant * 0.12f;
            float[] buffer = New(length);
            var bp = Biquad.BandPass(variant == 0 ? 760f : 520f, 0.55f, SAMPLE_RATE);
            for (int i = 0; i < buffer.Length; i++)
            {
                float progress = i / (float)buffer.Length;
                float n = bp.Process((float)(rng.NextDouble() * 2 - 1));
                float env = Mathf.Sin(progress * Mathf.PI);
                buffer[i] = n * env * env;
            }
            return Normalize(buffer, 0.42f);
        }

        /// The held, teeth-gritted push of a locked-up shove.
        private static float[] Strain()
        {
            var rng = new System.Random(6500);
            float length = 0.9f;
            float[] buffer = New(length);
            var r1 = Biquad.BandPass(420f, 8f, SAMPLE_RATE);
            var r2 = Biquad.BandPass(1250f, 10f, SAMPLE_RATE);
            double phase = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                float progress = i / (float)buffer.Length;
                float hz = 96f + 14f * progress;      // rises as the effort mounts
                phase += hz / SAMPLE_RATE;
                if (phase >= 1.0) phase -= 1.0;
                float source = (float)(2.0 * phase - 1.0);
                float voiced = r1.Process(source) + r2.Process(source) * 0.45f;
                float jitter = (float)(rng.NextDouble() * 2 - 1) * 0.06f;
                float env = Mathf.Min(1f, progress / 0.15f) * Mathf.Min(1f, (1f - progress) / 0.25f);
                buffer[i] = (voiced + jitter) * env;
            }
            return Normalize(buffer, 0.62f);
        }

        // ============================================================== crowd

        /// A loopable bed of many distant voices. Built as broadband noise shaped
        /// by a few slowly-wandering resonators, then wrapped so the loop point is
        /// inaudible.
        private static float[] CrowdMurmur()
        {
            var rng = new System.Random(7000);
            float[] buffer = New(6f);
            var voices = new[]
            {
                Biquad.BandPass(320f, 1.4f, SAMPLE_RATE),
                Biquad.BandPass(700f, 1.1f, SAMPLE_RATE),
                Biquad.BandPass(1400f, 0.9f, SAMPLE_RATE),
                Biquad.BandPass(2600f, 0.8f, SAMPLE_RATE),
            };
            var rumble = Biquad.LowPass(140f, 0.7f, SAMPLE_RATE);
            for (int i = 0; i < buffer.Length; i++)
            {
                float t = i / (float)SAMPLE_RATE;
                float n = (float)(rng.NextDouble() * 2 - 1);
                float s = 0f;
                for (int v = 0; v < voices.Length; v++)
                {
                    // Each band breathes on its own slow cycle, which is what stops
                    // a noise bed sounding like a fan.
                    float lfo = 0.7f + 0.3f * Mathf.Sin(t * (0.21f + v * 0.13f) * Mathf.PI * 2f);
                    s += voices[v].Process(n) * lfo * (1f - v * 0.16f);
                }
                s += rumble.Process(n) * 0.6f;
                buffer[i] = s;
            }
            return Normalize(LoopWrap(buffer, 0.35f), 0.5f);
        }

        /// The intake when someone is nearly pushed out — rising, then held.
        private static float[] CrowdOoh()
        {
            var rng = new System.Random(7100);
            float length = 1.35f;
            float[] buffer = New(length);
            // Twelve detuned voices on /o/, which is what makes it a crowd rather
            // than a person.
            const int VOICES = 12;
            var phases = new double[VOICES];
            var rates = new float[VOICES];
            for (int v = 0; v < VOICES; v++)
            {
                rates[v] = 155f * (0.86f + (float)rng.NextDouble() * 0.3f);
                phases[v] = rng.NextDouble();
            }
            var f1 = Biquad.BandPass(480f, 6f, SAMPLE_RATE);
            var f2 = Biquad.BandPass(900f, 7f, SAMPLE_RATE);
            for (int i = 0; i < buffer.Length; i++)
            {
                float progress = i / (float)buffer.Length;
                float bend = 1f + 0.11f * Mathf.Sin(progress * Mathf.PI);
                float source = 0f;
                for (int v = 0; v < VOICES; v++)
                {
                    phases[v] += rates[v] * bend / SAMPLE_RATE;
                    if (phases[v] >= 1.0) phases[v] -= 1.0;
                    source += (float)(2.0 * phases[v] - 1.0);
                }
                source /= VOICES;
                float shaped = f1.Process(source) + f2.Process(source) * 0.6f;
                float env = Mathf.Min(1f, progress / 0.18f) * Mathf.Min(1f, (1f - progress) / 0.4f);
                buffer[i] = shaped * env;
            }
            return Normalize(buffer, 0.7f);
        }

        /// A sharp collective inhale on a sudden reversal.
        private static float[] CrowdGasp()
        {
            var rng = new System.Random(7200);
            float[] buffer = New(0.55f);
            var bp = Biquad.BandPass(1150f, 0.7f, SAMPLE_RATE);
            for (int i = 0; i < buffer.Length; i++)
            {
                float progress = i / (float)buffer.Length;
                float n = bp.Process((float)(rng.NextDouble() * 2 - 1));
                // Fast in, slow out: the shape of a gasp.
                float env = Mathf.Min(1f, progress / 0.05f) * Mathf.Exp(-progress * 4.5f);
                buffer[i] = n * env;
            }
            return Normalize(buffer, 0.65f);
        }

        /// The full-throated roar for a decided match.
        private static float[] CrowdCheer()
        {
            var rng = new System.Random(7300);
            float length = 2.6f;
            float[] buffer = New(length);
            const int VOICES = 20;
            var phases = new double[VOICES];
            var rates = new float[VOICES];
            for (int v = 0; v < VOICES; v++)
            {
                rates[v] = 210f * (0.7f + (float)rng.NextDouble() * 0.7f);
                phases[v] = rng.NextDouble();
            }
            var shout = Biquad.BandPass(950f, 2.2f, SAMPLE_RATE);
            var air = Biquad.BandPass(3000f, 0.6f, SAMPLE_RATE);
            for (int i = 0; i < buffer.Length; i++)
            {
                float progress = i / (float)buffer.Length;
                float source = 0f;
                for (int v = 0; v < VOICES; v++)
                {
                    phases[v] += rates[v] / SAMPLE_RATE;
                    if (phases[v] >= 1.0) phases[v] -= 1.0;
                    source += (float)(2.0 * phases[v] - 1.0);
                }
                source /= VOICES;
                float n = (float)(rng.NextDouble() * 2 - 1);
                float s = shout.Process(source) * 0.9f + air.Process(n) * 0.55f;
                // Swell in fast, decay long — a roar peaks early and dies slowly.
                float env = Mathf.Min(1f, progress / 0.07f) * Mathf.Exp(-progress * 2.2f);
                buffer[i] = s * env;
            }
            return Normalize(buffer, 0.85f);
        }

        /// Scattered applause, for the walk-in and the result card.
        private static float[] CrowdApplause()
        {
            var rng = new System.Random(7400);
            float length = 3f;
            float[] buffer = New(length);
            var bp = Biquad.BandPass(2400f, 0.9f, SAMPLE_RATE);
            // ~900 individual claps, each a filtered noise tick.
            int claps = 900;
            for (int c = 0; c < claps; c++)
            {
                float at = (float)rng.NextDouble() * (length - 0.02f);
                int start = (int)(at * SAMPLE_RATE);
                float gain = 0.25f + (float)rng.NextDouble() * 0.75f;
                int clapLength = (int)(0.02f * SAMPLE_RATE);
                for (int i = 0; i < clapLength && start + i < buffer.Length; i++)
                {
                    float t = i / (float)SAMPLE_RATE;
                    buffer[start + i] += (float)(rng.NextDouble() * 2 - 1)
                                       * Mathf.Exp(-t * 260f) * gain;
                }
            }
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = bp.Process(buffer[i]);
            }
            return Normalize(LoopWrap(buffer, 0.3f), 0.6f);
        }

        // =========================================================== ceremony

        /// Taiko: a struck membrane. A low pitched body, a mid skin tone and a
        /// stick transient on top.
        private static float[] Taiko(int variant)
        {
            var rng = new System.Random(8000 + variant);
            float[] buffer = New(0.85f);
            float body = 78f + variant * 6f;
            double p1 = 0, p2 = 0;
            var stick = Biquad.BandPass(3400f, 0.8f, SAMPLE_RATE);
            for (int i = 0; i < buffer.Length; i++)
            {
                float t = i / (float)SAMPLE_RATE;
                float bendHz = body * (1f + 0.35f * Mathf.Exp(-t * 30f));
                p1 += 2.0 * Math.PI * bendHz / SAMPLE_RATE;
                // A drum head's second mode is inharmonic — 1.59x for a circular
                // membrane, which is why a drum is not a sine.
                p2 += 2.0 * Math.PI * bendHz * 1.59f / SAMPLE_RATE;

                float low = (float)Math.Sin(p1) * Mathf.Exp(-t * 5.5f);
                float mid = (float)Math.Sin(p2) * Mathf.Exp(-t * 13f) * 0.4f;
                float hit = stick.Process((float)(rng.NextDouble() * 2 - 1)) * Mathf.Exp(-t * 130f) * 0.5f;
                buffer[i] = (low + mid + hit) * (1f - Mathf.Exp(-t * 3000f));
            }
            return Normalize(buffer, 0.95f);
        }

        /// The match-deciding gong: many inharmonic partials that beat against
        /// each other and take four seconds to die.
        private static float[] Gong()
        {
            float[] buffer = New(4.2f);
            float[] partials = { 1f, 2.41f, 3.13f, 4.77f, 6.02f, 7.31f, 9.14f, 11.6f };
            float[] gains = { 1f, 0.65f, 0.5f, 0.38f, 0.3f, 0.22f, 0.16f, 0.1f };
            float fundamental = 96f;
            for (int p = 0; p < partials.Length; p++)
            {
                // Detune each partial slightly so pairs beat — the shimmer is the
                // whole character of a struck metal plate.
                float hz = fundamental * partials[p] * (1f + (p % 2 == 0 ? 0.004f : -0.005f));
                float decay = 1.1f + p * 0.42f;
                double phase = 0;
                for (int i = 0; i < buffer.Length; i++)
                {
                    float t = i / (float)SAMPLE_RATE;
                    phase += 2.0 * Math.PI * hz / SAMPLE_RATE;
                    buffer[i] += (float)Math.Sin(phase) * gains[p] * Mathf.Exp(-t * decay);
                }
            }
            // Strike transient.
            var rng = new System.Random(8500);
            var bp = Biquad.BandPass(2200f, 0.7f, SAMPLE_RATE);
            for (int i = 0; i < buffer.Length; i++)
            {
                float t = i / (float)SAMPLE_RATE;
                buffer[i] += bp.Process((float)(rng.NextDouble() * 2 - 1)) * Mathf.Exp(-t * 40f) * 0.5f;
                buffer[i] *= 1f - Mathf.Exp(-t * 2000f);
            }
            return Normalize(buffer, 0.95f);
        }

        /// A handful of salt hitting the clay: hundreds of tiny high ticks.
        private static float[] Salt()
        {
            var rng = new System.Random(8600);
            float length = 0.9f;
            float[] buffer = New(length);
            var hp = Biquad.HighPass(4200f, 0.7f, SAMPLE_RATE);
            for (int grain = 0; grain < 420; grain++)
            {
                // Front-loaded: the throw lands as a burst, then scatters.
                float u = (float)rng.NextDouble();
                float at = u * u * (length - 0.01f);
                int start = (int)(at * SAMPLE_RATE);
                int grainLength = (int)(0.006f * SAMPLE_RATE);
                float gain = 0.3f + (float)rng.NextDouble() * 0.7f;
                for (int i = 0; i < grainLength && start + i < buffer.Length; i++)
                {
                    float t = i / (float)SAMPLE_RATE;
                    buffer[start + i] += (float)(rng.NextDouble() * 2 - 1) * Mathf.Exp(-t * 700f) * gain;
                }
            }
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = hp.Process(buffer[i]);
            }
            return Normalize(buffer, 0.5f);
        }

        // ============================================================== music

        /// Sustained drone plus breath noise: the shakuhachi-ish bed that plays
        /// under everything, including the ceremony.
        private static float[] MusicBed()
        {
            var rng = new System.Random(9000);
            float[] buffer = New(MUSIC_SECONDS);
            // A2 root and its fifth. No third — the mode stays open and neither
            // major nor minor, which keeps it from sounding like a cue.
            float[] tones = { 110f, 165f, 220f };
            float[] gains = { 1f, 0.5f, 0.28f };
            for (int n = 0; n < tones.Length; n++)
            {
                double phase = 0;
                for (int i = 0; i < buffer.Length; i++)
                {
                    float t = i / (float)SAMPLE_RATE;
                    // Slow vibrato at an irrational rate per voice so the drone
                    // never lands back in phase across the loop.
                    float hz = tones[n] * (1f + 0.0025f * Mathf.Sin(t * (0.7f + n * 0.31f) * Mathf.PI * 2f));
                    phase += 2.0 * Math.PI * hz / SAMPLE_RATE;
                    buffer[i] += (float)Math.Sin(phase) * gains[n] * 0.32f;
                }
            }
            var air = Biquad.BandPass(1500f, 0.5f, SAMPLE_RATE);
            for (int i = 0; i < buffer.Length; i++)
            {
                float t = i / (float)SAMPLE_RATE;
                float breath = 0.55f + 0.45f * Mathf.Sin(t * 0.25f * Mathf.PI * 2f);
                buffer[i] += air.Process((float)(rng.NextDouble() * 2 - 1)) * 0.14f * breath;
            }
            return Normalize(LoopWrap(buffer, 0.5f), 0.55f);
        }

        /// A slow heartbeat on the downbeats: the layer that enters when a round
        /// starts and turns the bed into a countdown.
        private static float[] MusicPulse()
        {
            float[] buffer = New(MUSIC_SECONDS);
            float[] beats = { 0f, 2f, 4f, 6f };          // one per bar
            foreach (float beat in beats)
            {
                AddHit(buffer, beat * MUSIC_STEP * 2f, Taiko(0), 0.9f);
                AddHit(buffer, (beat + 1.5f) * MUSIC_STEP * 2f, Taiko(2), 0.32f);
            }
            return Normalize(LoopWrap(buffer, 0.15f), 0.75f);
        }

        /// The driving pattern for an active round.
        private static float[] MusicDrums()
        {
            float[] buffer = New(MUSIC_SECONDS);
            // A 16-step pattern, accents on 0 and 6 of each bar — a lopsided
            // groove rather than a march, which suits a grappling sport.
            float[] accents =
            {
                1f, 0f, 0.35f, 0f, 0.5f, 0f, 0.85f, 0.3f,
                1f, 0f, 0.35f, 0.4f, 0.5f, 0f, 0.7f, 0.45f,
            };
            for (int bar = 0; bar < 2; bar++)
            {
                for (int step = 0; step < accents.Length; step++)
                {
                    float gain = accents[step];
                    if (gain <= 0f)
                    {
                        continue;
                    }
                    float at = (bar * accents.Length + step) * (MUSIC_SECONDS / 32f);
                    AddHit(buffer, at, Taiko(step % 3), gain * 0.8f);
                }
            }
            return Normalize(LoopWrap(buffer, 0.12f), 0.8f);
        }

        /// High shimmering cluster for match point: unstable, and never resolves.
        private static float[] MusicTension()
        {
            float[] buffer = New(MUSIC_SECONDS);
            // A minor second above the root, two octaves up. The interval is the
            // tension; nothing else has to do any work.
            float[] tones = { 880f, 932.33f, 1318.5f };
            var rng = new System.Random(9500);
            for (int n = 0; n < tones.Length; n++)
            {
                double phase = 0;
                float tremoloHz = 5.5f + n * 1.3f;
                for (int i = 0; i < buffer.Length; i++)
                {
                    float t = i / (float)SAMPLE_RATE;
                    phase += 2.0 * Math.PI * tones[n] / SAMPLE_RATE;
                    float tremolo = 0.55f + 0.45f * Mathf.Sin(t * tremoloHz * Mathf.PI * 2f);
                    buffer[i] += (float)Math.Sin(phase) * tremolo * 0.2f;
                }
            }
            var air = Biquad.HighPass(5000f, 0.7f, SAMPLE_RATE);
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] += air.Process((float)(rng.NextDouble() * 2 - 1)) * 0.05f;
            }
            return Normalize(LoopWrap(buffer, 0.4f), 0.45f);
        }

        private static void AddHit(float[] buffer, float atSeconds, float[] hit, float gain)
        {
            int start = (int)(atSeconds * SAMPLE_RATE);
            for (int i = 0; i < hit.Length; i++)
            {
                // Wrap rather than clip: a drum struck near the end of the loop
                // must ring over the loop point, or every repeat has a hole in it.
                int index = (start + i) % buffer.Length;
                if (index < 0)
                {
                    continue;
                }
                buffer[index] += hit[i] * gain;
            }
        }

        // ============================================================ helpers

        private static float[] New(float seconds) => new float[Mathf.Max(1, (int)(seconds * SAMPLE_RATE))];

        private static float[] Mix(float[] a, float[] b, float bGain)
        {
            float[] result = new float[Mathf.Max(a.Length, b.Length)];
            for (int i = 0; i < a.Length; i++) result[i] += a[i];
            for (int i = 0; i < b.Length; i++) result[i] += b[i] * bGain;
            return Normalize(result, 0.95f);
        }

        /// Crossfade the tail back over the head so the clip loops without a click.
        /// The returned buffer is shorter by the fade length, which is the part
        /// that has been folded in.
        private static float[] LoopWrap(float[] buffer, float fadeSeconds)
        {
            int fade = Mathf.Min((int)(fadeSeconds * SAMPLE_RATE), buffer.Length / 3);
            if (fade <= 1)
            {
                return buffer;
            }
            int length = buffer.Length - fade;
            float[] result = new float[length];
            Array.Copy(buffer, result, length);
            for (int i = 0; i < fade; i++)
            {
                float t = i / (float)fade;
                result[i] = Mathf.Lerp(buffer[length + i], buffer[i], t);
            }
            return result;
        }

        private static float[] Normalize(float[] buffer, float peak)
        {
            float max = 0f;
            for (int i = 0; i < buffer.Length; i++)
            {
                float a = Mathf.Abs(buffer[i]);
                if (a > max) max = a;
            }
            if (max < 1e-6f)
            {
                return buffer;
            }
            float scale = peak / max;
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] *= scale;
            }
            return buffer;
        }

        private static int Write(string clipName, float[] samples)
        {
            string path = Path.Combine(OUTPUT_DIR, clipName + ".wav");
            using (var stream = new FileStream(path, FileMode.Create))
            using (var writer = new BinaryWriter(stream))
            {
                int dataBytes = samples.Length * 2;
                writer.Write(new[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + dataBytes);
                writer.Write(new[] { 'W', 'A', 'V', 'E' });
                writer.Write(new[] { 'f', 'm', 't', ' ' });
                writer.Write(16);                 // PCM chunk size
                writer.Write((short)1);           // PCM
                writer.Write((short)1);           // mono
                writer.Write(SAMPLE_RATE);
                writer.Write(SAMPLE_RATE * 2);    // byte rate
                writer.Write((short)2);           // block align
                writer.Write((short)16);          // bits per sample
                writer.Write(new[] { 'd', 'a', 't', 'a' });
                writer.Write(dataBytes);
                for (int i = 0; i < samples.Length; i++)
                {
                    writer.Write((short)(Mathf.Clamp(samples[i], -1f, 1f) * short.MaxValue));
                }
            }
            return 1;
        }

        /// Direct-form-I biquad, RBJ cookbook coefficients. Stateful — build one
        /// per voice, do not share across signals.
        private sealed class Biquad
        {
            private double _b0, _b1, _b2, _a1, _a2;
            private double _x1, _x2, _y1, _y2;

            public static Biquad LowPass(double freq, double q, double sampleRate)
            {
                double w0 = 2 * Math.PI * freq / sampleRate;
                double cos = Math.Cos(w0);
                double alpha = Math.Sin(w0) / (2 * q);
                return Make((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
            }

            public static Biquad HighPass(double freq, double q, double sampleRate)
            {
                double w0 = 2 * Math.PI * freq / sampleRate;
                double cos = Math.Cos(w0);
                double alpha = Math.Sin(w0) / (2 * q);
                return Make((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
            }

            public static Biquad BandPass(double freq, double q, double sampleRate)
            {
                double w0 = 2 * Math.PI * freq / sampleRate;
                double cos = Math.Cos(w0);
                double alpha = Math.Sin(w0) / (2 * q);
                return Make(alpha, 0, -alpha, 1 + alpha, -2 * cos, 1 - alpha);
            }

            private static Biquad Make(double b0, double b1, double b2, double a0, double a1, double a2)
            {
                return new Biquad
                {
                    _b0 = b0 / a0,
                    _b1 = b1 / a0,
                    _b2 = b2 / a0,
                    _a1 = a1 / a0,
                    _a2 = a2 / a0,
                };
            }

            public float Process(float input)
            {
                double output = _b0 * input + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
                _x2 = _x1;
                _x1 = input;
                _y2 = _y1;
                _y1 = output;
                return (float)output;
            }
        }
    }
}
