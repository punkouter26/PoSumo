// NOTE: the `#if UNITY_EDITOR` guard below is REDUNDANT — this file lives in
// `Assets/Editor/`, which is already excluded from player builds, and its
// neighbours (GenerateAudio.cs, NormalizeVoice.cs) carry no such guard.
//
// It is here only to satisfy `.claude/hooks/guard-editor-runtime.sh`, whose
// Editor-folder exemption matches `*/Editor/*` with forward slashes while the
// tool hands it a Windows path with backslashes — so the exemption never fires
// and the hook blocks every write to this folder. Harmless to keep; remove it if
// that hook is ever taught about `\`.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PoSumo.EditorTools
{
    /// Synthesizes PLACEHOLDER fighter voice clips so every fighter has a complete
    /// set until real recordings arrive.
    ///
    /// Why this is its own tool rather than a branch inside `GenerateAudio`: that
    /// tool regenerates the whole SFX bank on every run and overwrites what it
    /// finds. Voice clips are the one part of the audio tree that will eventually be
    /// REAL RECORDINGS, and a regenerate-everything button that can silently replace
    /// a recording with a synthesized bleat is a trap waiting to be stepped on.
    ///
    /// **This tool never overwrites an existing file.** It writes only the clips
    /// that are missing and reports what it skipped. Drop a real `.wav` in at the
    /// same path and it becomes untouchable — that is the intended migration path
    /// from placeholder to recording, one clip at a time.
    ///
    /// Naming follows the existing scheme exactly, because `Systems_FighterVoice`
    /// resolves by string and there is no second accepted spelling:
    ///
    ///     Assets/Resources/Audio/Voice/(Behavior)_(Happy|Sad|Insult)_(1-5).wav
    ///
    /// where the behavior is `behaviorName` on the character asset — Matt, Standard,
    /// Nick, Kim — not the folder or asset name.
    ///
    /// Two rules from `CLAUDE.md` shape the output and both are load-bearing:
    ///
    /// - **All five levels or none.** `LoadSet` returns null silently at zero clips,
    ///   but `Debug.LogWarning`s on every match when it finds 1-4 of 5. A partially
    ///   delivered set is worse than an absent one, so this tool fills a set
    ///   completely or leaves it alone.
    /// - **Level 5 fires on the match win and level 1 is the mildest read**, so the
    ///   set is ordered by intensity. Clip LENGTH matters at level 5 specifically:
    ///   `_nextAllowedTime` is `clip.length * 0.6`, so a long line mutes that
    ///   fighter for a proportional stretch afterwards. Placeholders stay short
    ///   (under 1.1 s) so one never gags a fighter the way Kim's real 7.96 s level 5
    ///   deliberately does.
    ///
    /// Gains are NOT written here. Run *PoSumo → Normalize Voice Levels* afterwards;
    /// it rebases so max gain is exactly 1.0, which matters because
    /// `AudioSource.volume` clamps at 1 and `Systems_FighterVoice` multiplies by 0.9.
    public static class GeneratePlaceholderVoices
    {
        private const int SAMPLE_RATE = 44100;
        private const string VOICE_DIR = "Assets/Resources/Audio/Voice";

        /// Every fighter that should own a voice. `Bot` is excluded deliberately —
        /// it is the intentionally brainless roster entry.
        private static readonly string[] Behaviors = { "Matt", "Standard", "Nick", "Kim" };

        private static readonly string[] Moods = { "Happy", "Sad", "Insult" };

        /// Per-fighter vocal identity. `F0` is the glottal fundamental in Hz and
        /// `Tract` scales the formants — a bigger fighter has a longer vocal tract
        /// and therefore LOWER formants, not merely a lower pitch. Chosen to sit
        /// alongside each physique rather than at random: Kim is the heavy planted
        /// anchor, Nick the light mobile one.
        private readonly struct VoiceId
        {
            public readonly float F0;
            public readonly float Tract;
            public readonly int Seed;

            public VoiceId(float f0, float tract, int seed)
            {
                F0 = f0;
                Tract = tract;
                Seed = seed;
            }
        }

        private static VoiceId IdFor(string behavior)
        {
            switch (behavior)
            {
                case "Kim":      return new VoiceId(196f, 1.14f, 21);
                case "Nick":     return new VoiceId(128f, 1.04f, 37);
                case "Standard": return new VoiceId(110f, 1.00f, 53);
                case "Matt":     return new VoiceId(98f,  0.94f, 71);
                default:         return new VoiceId(112f, 1.00f, 11);
            }
        }

        [MenuItem("PoSumo/Generate Placeholder Voices")]
        public static void Run()
        {
            try
            {
                Directory.CreateDirectory(VOICE_DIR);

                int written = 0;
                var filled = new List<string>();
                var skipped = new List<string>();

                foreach (string behavior in Behaviors)
                {
                    foreach (string mood in Moods)
                    {
                        int present = CountPresent(behavior, mood);

                        // A COMPLETE set is left entirely alone — that is a real
                        // recording set and none of our business.
                        if (present == 5)
                        {
                            skipped.Add($"{behavior}_{mood} (complete)");
                            continue;
                        }

                        for (int level = 1; level <= 5; level++)
                        {
                            string path = PathFor(behavior, mood, level);
                            // Never clobber. A half-real set fills its gaps only.
                            if (File.Exists(path))
                            {
                                continue;
                            }
                            Write(path, Synthesize(behavior, mood, level));
                            written++;
                        }
                        filled.Add($"{behavior}_{mood}" + (present > 0 ? $" (+{5 - present} gaps)" : ""));
                    }
                }

                AssetDatabase.Refresh();

                Debug.Log($"PLACEHOLDER VOICE RESULT: OK — wrote {written} clips to {VOICE_DIR}\n" +
                          $"  filled:  {(filled.Count == 0 ? "nothing" : string.Join(", ", filled))}\n" +
                          $"  skipped: {(skipped.Count == 0 ? "nothing" : string.Join(", ", skipped))}\n" +
                          "  Now run PoSumo -> Normalize Voice Levels to rebuild VoiceGains.asset.");
            }
            catch (Exception e)
            {
                Debug.LogError($"PLACEHOLDER VOICE RESULT: FAILED — {e}");
            }
        }

        private static string PathFor(string behavior, string mood, int level) =>
            Path.Combine(VOICE_DIR, $"{behavior}_{mood}_{level}.wav");

        private static int CountPresent(string behavior, string mood)
        {
            int n = 0;
            for (int level = 1; level <= 5; level++)
            {
                if (File.Exists(PathFor(behavior, mood, level))) n++;
            }
            return n;
        }

        // ============================================================== synthesis

        /// One placeholder line. Deliberately a WORDLESS vocalisation — a shout, a
        /// groan, a jeer — rather than an attempt at speech: a synthesized word
        /// sounds broken, whereas a synthesized shout sounds like a shout.
        ///
        /// Intensity rises with `level` in three ways at once, because loudness
        /// alone reads as a volume change rather than as effort: pitch rises, the
        /// vowel opens toward /a/, and the attack sharpens.
        private static float[] Synthesize(string behavior, string mood, int level)
        {
            VoiceId id = IdFor(behavior);
            var rng = new System.Random(id.Seed * 31 + Math.Abs(mood.GetHashCode() % 997) + level);

            float intensity = (level - 1) / 4f;              // 0 at L1, 1 at L5
            float length = LengthFor(mood, intensity);
            float[] buffer = new float[Mathf.Max(1, (int)(length * SAMPLE_RATE))];

            // Effort raises the fundamental. Sadness lowers it and flattens the
            // contour; an insult sits mid and stays level, which is what makes a
            // taunt read as controlled rather than desperate.
            float f0 = id.F0 * MoodPitch(mood) * (1f + 0.28f * intensity);

            float[] formants = FormantsFor(mood, intensity, id.Tract);

            var r1 = Biquad.BandPass(formants[0], 7f, SAMPLE_RATE);
            var r2 = Biquad.BandPass(formants[1], 9f, SAMPLE_RATE);
            var r3 = Biquad.BandPass(formants[2], 11f, SAMPLE_RATE);
            var breathBp = Biquad.BandPass(1700f, 0.8f, SAMPLE_RATE);

            float attack = Mathf.Lerp(0.14f, 0.035f, intensity);
            float decay = Mathf.Lerp(2.6f, 3.4f, intensity);
            float breathAmount = Mathf.Lerp(0.30f, 0.16f, intensity)
                                 * (mood == "Sad" ? 1.6f : 1f);

            double phase = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                float t = i / (float)SAMPLE_RATE;
                float progress = t / length;

                // A rise into the vowel then a sag as air runs out, plus a slow
                // vibrato so a held note is not a dead tone.
                float contour = 1f
                    + 0.15f * Mathf.Exp(-progress * 6f)
                    - MoodSag(mood) * progress
                    + 0.012f * Mathf.Sin(progress * Mathf.PI * 2f * 5.5f);
                float hz = f0 * contour;

                phase += hz / SAMPLE_RATE;
                if (phase >= 1.0) phase -= 1.0;

                // Sawtooth glottal source, same shape as GenerateAudio.Grunt.
                float source = (float)(2.0 * phase - 1.0);
                float voiced = r1.Process(source) * 1f
                             + r2.Process(source) * 0.5f
                             + r3.Process(source) * 0.22f;
                float breath = breathBp.Process((float)(rng.NextDouble() * 2 - 1)) * breathAmount;

                float env = Mathf.Min(1f, progress / attack) * Mathf.Exp(-progress * decay);
                buffer[i] = (voiced + breath) * env;
            }

            // Peak below 1 so NormalizeVoice has headroom and nothing clips first.
            return Normalize(buffer, Mathf.Lerp(0.55f, 0.85f, intensity));
        }

        /// Kept short on purpose — see the class note on `_nextAllowedTime`.
        private static float LengthFor(string mood, float intensity)
        {
            switch (mood)
            {
                case "Sad":    return Mathf.Lerp(0.55f, 1.10f, intensity);
                case "Insult": return Mathf.Lerp(0.40f, 0.75f, intensity);
                default:       return Mathf.Lerp(0.38f, 0.90f, intensity);   // Happy
            }
        }

        private static float MoodPitch(string mood)
        {
            switch (mood)
            {
                case "Sad":    return 0.84f;
                case "Insult": return 1.05f;
                default:       return 1.12f;   // Happy — a win is shouted high
            }
        }

        /// How far pitch falls across the line. A groan sags hard; a taunt holds.
        private static float MoodSag(string mood)
        {
            switch (mood)
            {
                case "Sad":    return 0.22f;
                case "Insult": return 0.04f;
                default:       return 0.10f;
            }
        }

        /// Vowel colour. Effort opens the vowel toward /a/ (730/1090/2440); a groan
        /// closes toward /u/ (400/900/2300); a jeer sits near /e/ (530/1840/2480),
        /// the bright nasal end that reads as mocking.
        private static float[] FormantsFor(string mood, float intensity, float tract)
        {
            float[] open   = { 730f, 1090f, 2440f };
            float[] closed = { 400f,  900f, 2300f };
            float[] jeer   = { 530f, 1840f, 2480f };

            float[] baseF;
            float openness;
            switch (mood)
            {
                case "Sad":    baseF = closed; openness = 0.35f * intensity; break;
                case "Insult": baseF = jeer;   openness = 0.25f * intensity; break;
                default:       baseF = open;   openness = 0.15f * intensity; break;
            }

            var result = new float[3];
            for (int i = 0; i < 3; i++)
            {
                // Longer tract (bigger fighter) => LOWER formants, hence divide.
                result[i] = Mathf.Lerp(baseF[i], open[i], openness) / Mathf.Max(0.5f, tract);
            }
            return result;
        }

        private static float[] Normalize(float[] buffer, float peak)
        {
            float max = 0f;
            for (int i = 0; i < buffer.Length; i++)
            {
                max = Mathf.Max(max, Mathf.Abs(buffer[i]));
            }
            if (max < 1e-6f) return buffer;

            float scale = peak / max;
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] *= scale;
            }
            return buffer;
        }

        /// 16-bit mono PCM, matching `GenerateAudio.Write` exactly so both banks
        /// import identically.
        private static void Write(string path, float[] samples)
        {
            using (var stream = new FileStream(path, FileMode.Create))
            using (var writer = new BinaryWriter(stream))
            {
                int dataBytes = samples.Length * 2;
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + dataBytes);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(SAMPLE_RATE);
                writer.Write(SAMPLE_RATE * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                writer.Write(dataBytes);
                for (int i = 0; i < samples.Length; i++)
                {
                    writer.Write((short)(Mathf.Clamp(samples[i], -1f, 1f) * short.MaxValue));
                }
            }
        }

        /// Direct-form-I biquad, RBJ cookbook. Stateful — one per voice, never
        /// shared across signals.
        private sealed class Biquad
        {
            private double _a0, _a1, _a2, _b1, _b2;
            private double _x1, _x2, _y1, _y2;

            public static Biquad BandPass(double freq, double q, double sampleRate)
            {
                double omega = 2.0 * Math.PI * freq / sampleRate;
                double sn = Math.Sin(omega);
                double cs = Math.Cos(omega);
                double alpha = sn / (2.0 * q);
                double b0 = 1.0 + alpha;

                var f = new Biquad();
                f._a0 = alpha / b0;
                f._a1 = 0.0;
                f._a2 = -alpha / b0;
                f._b1 = -2.0 * cs / b0;
                f._b2 = (1.0 - alpha) / b0;
                return f;
            }

            public float Process(float input)
            {
                double output = _a0 * input + _a1 * _x1 + _a2 * _x2 - _b1 * _y1 - _b2 * _y2;
                _x2 = _x1;
                _x1 = input;
                _y2 = _y1;
                _y1 = output;
                return (float)output;
            }
        }
    }
}
#endif
