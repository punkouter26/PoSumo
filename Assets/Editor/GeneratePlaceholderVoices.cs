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

        /// Every fighter that should own a voice.
        ///
        /// `Bot` USED TO BE EXCLUDED HERE, on the stated grounds that it is "the
        /// intentionally brainless roster entry". That was wrong: Bot_v01 carries
        /// `useBot: 1`, so Agent_Biped puts it on BehaviorType.HeuristicOnly and it
        /// is driven by Agent_Bot — 822 lines of hand-written rules — not by a limp
        /// ragdoll. Measured in a played bracket it won its quarterfinal 2-0 by
        /// ring-out. It is the project's rules-based baseline and it fights, so a
        /// silent fighter was the odd one out rather than the correct default.
        private static readonly string[] Behaviors = { "Matt", "Standard", "Nick", "Kim", "Bot" };

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
                // Lowest and flattest of the five. Bot is the machine in the
                // bracket, so it reads as gruff and unbothered next to four
                // human-pitched voices rather than as a fifth personality.
                case "Bot":      return new VoiceId(88f,  0.90f, 97);
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

        /// Rewrites every GENERATED placeholder with the current synthesiser, and
        /// leaves every real recording exactly where it is.
        ///
        /// WHY THIS IS SAFE, and why it is not just "delete the folder and re-run".
        /// The voice folder is a MIXTURE: 35 of the 75 clips are real recordings
        /// (Matt and Nick complete, Kim's Happy set) and 40 are synthesised. A
        /// blanket regenerate would silently destroy the real ones — including
        /// Kim's 7.96 s level 5, which the audio notes call out by name.
        ///
        /// The two are told apart by WAV header, not by filename or length: this
        /// generator always writes mono 44.1 kHz, while the real clips are stereo
        /// 48 kHz (Matt, Nick) or mono 24 kHz (Kim). Anything that is not mono
        /// 44.1 kHz is treated as hand-authored and skipped, so dropping a real
        /// recording in at any sample rate this tool does not itself produce makes
        /// that clip permanently safe from it.
        [MenuItem("PoSumo/Regenerate Placeholder Voices (keeps real recordings)")]
        public static void Regenerate()
        {
            try
            {
                Directory.CreateDirectory(VOICE_DIR);

                int rewritten = 0;
                int created = 0;
                var kept = new List<string>();

                foreach (string behavior in Behaviors)
                {
                    foreach (string mood in Moods)
                    {
                        for (int level = 1; level <= 5; level++)
                        {
                            string path = PathFor(behavior, mood, level);

                            if (File.Exists(path))
                            {
                                if (!IsGeneratedPlaceholder(path))
                                {
                                    kept.Add($"{behavior}_{mood}_{level}");
                                    continue;
                                }

                                Write(path, Synthesize(behavior, mood, level));
                                rewritten++;
                                continue;
                            }

                            Write(path, Synthesize(behavior, mood, level));
                            created++;
                        }
                    }
                }

                AssetDatabase.Refresh();

                Debug.Log($"VOICE REGEN RESULT: OK — rewrote {rewritten}, created {created}, " +
                          $"kept {kept.Count} real recording(s).\n" +
                          $"  kept: {(kept.Count == 0 ? "nothing" : string.Join(", ", kept))}\n" +
                          "  Now run PoSumo -> Normalize Voice Levels to rebuild VoiceGains.asset.");
            }
            catch (Exception e)
            {
                Debug.LogError($"VOICE REGEN RESULT: FAILED — {e}");
            }
        }

        /// True only for a clip this generator could have written: mono, 44.1 kHz.
        /// Reads the RIFF header directly rather than importing the asset, so it
        /// cannot be fooled by Unity's import settings, which are a VIEW of the file
        /// and not the file itself.
        private static bool IsGeneratedPlaceholder(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 44)
                    {
                        return false;
                    }

                    stream.Seek(22, SeekOrigin.Begin);
                    int channels = reader.ReadInt16();
                    int sampleRate = reader.ReadInt32();
                    return channels == 1 && sampleRate == SAMPLE_RATE;
                }
            }
            catch
            {
                // Unreadable means "not ours" — the conservative answer, because the
                // cost of a false positive here is destroying a real recording.
                return false;
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
        /// The GESTURE of a mood, which is what actually makes a voice read as
        /// happy or sad — not its steady-state pitch.
        ///
        /// The first version of this synthesiser gave every clip the same shape: one
        /// held vowel under a decaying envelope, with only pitch and formants moving
        /// per mood. It sounded like one instrument playing three notes, because the
        /// cues a listener actually uses for emotion are RHYTHM and CONTOUR
        /// DIRECTION. A cheer is short, punchy and rises; a groan is one long fall;
        /// a taunt is several clipped syllables stepping down. Those are structural,
        /// so they are modelled structurally here.
        private readonly struct MoodShape
        {
            public readonly int Syllables;

            /// Relative loudness per syllable — a cheer peaks on its LAST, a jeer on
            /// its first.
            public readonly float[] Gains;

            /// Pitch multiplier at the start of each syllable. Direction is the cue:
            /// up for happy, stepping down for a taunt, one long slide for sad.
            public readonly float[] Steps;

            /// Pitch movement WITHIN a syllable, as a multiplier across it.
            public readonly float IntraGlide;

            public readonly float Attack;
            public readonly float Decay;
            public readonly float Breath;
            public readonly float VibratoHz;
            public readonly float VibratoDepth;

            /// A short noise burst at each syllable onset. This is what makes a
            /// syllable read as an articulated consonant rather than a tone fading
            /// up, and it is most of why the jeer reads as speech at all.
            public readonly float Onset;

            /// Fraction of each syllable slot spent silent, so syllables separate.
            public readonly float Gap;

            public MoodShape(int syllables, float[] gains, float[] steps, float intraGlide,
                             float attack, float decay, float breath,
                             float vibratoHz, float vibratoDepth, float onset, float gap)
            {
                Syllables = syllables;
                Gains = gains;
                Steps = steps;
                IntraGlide = intraGlide;
                Attack = attack;
                Decay = decay;
                Breath = breath;
                VibratoHz = vibratoHz;
                VibratoDepth = vibratoDepth;
                Onset = onset;
                Gap = gap;
            }
        }

        private static MoodShape ShapeFor(string mood, float intensity)
        {
            switch (mood)
            {
                // A GROAN. One long syllable, a slow swell rather than a hit, and a
                // continuous fall of nearly a third — the single most recognisable
                // sad cue. Heavy breath and a slow, deep waver, so it sounds like a
                // body running out of air rather than a held note.
                case "Sad":
                    return new MoodShape(
                        syllables: 1,
                        gains: new[] { 1f },
                        steps: new[] { 1f },
                        intraGlide: Mathf.Lerp(0.74f, 0.66f, intensity),
                        attack: Mathf.Lerp(0.30f, 0.20f, intensity),
                        decay: Mathf.Lerp(1.5f, 1.9f, intensity),
                        breath: Mathf.Lerp(0.42f, 0.30f, intensity),
                        vibratoHz: 3.2f,
                        vibratoDepth: 0.030f,
                        onset: 0.04f,
                        gap: 0f);

                // A JEER. Three clipped syllables stepping DOWN, each punched in and
                // cut off, with almost no breath — the control is the insult. Nearly
                // flat within a syllable, so it reads as delivered rather than felt.
                case "Insult":
                    return new MoodShape(
                        syllables: 3,
                        gains: new[] { 1f, 0.82f, 0.92f },
                        steps: new[] { 1f, 0.94f, 0.88f },
                        intraGlide: 0.97f,
                        attack: 0.05f,
                        decay: Mathf.Lerp(5.5f, 4.5f, intensity),
                        breath: 0.10f,
                        vibratoHz: 6.5f,
                        vibratoDepth: 0.008f,
                        onset: 0.55f,
                        gap: 0.30f);

                // A CHEER. Two syllables with the weight on the SECOND and the pitch
                // rising into it — a rise at the end is the cue that separates
                // delight from mere effort. Fast attack, bright, light quick vibrato.
                default:
                    return new MoodShape(
                        syllables: 2,
                        gains: new[] { 0.72f, 1f },
                        steps: new[] { 1f, Mathf.Lerp(1.10f, 1.20f, intensity) },
                        intraGlide: Mathf.Lerp(1.05f, 1.12f, intensity),
                        attack: Mathf.Lerp(0.09f, 0.04f, intensity),
                        decay: Mathf.Lerp(3.0f, 2.4f, intensity),
                        breath: Mathf.Lerp(0.22f, 0.14f, intensity),
                        vibratoHz: 5.8f,
                        vibratoDepth: 0.014f,
                        onset: 0.30f,
                        gap: 0.14f);
            }
        }

        private static float[] Synthesize(string behavior, string mood, int level)
        {
            VoiceId id = IdFor(behavior);
            var rng = new System.Random(id.Seed * 31 + Math.Abs(mood.GetHashCode() % 997) + level);

            float intensity = (level - 1) / 4f;              // 0 at L1, 1 at L5
            float length = LengthFor(mood, intensity);
            MoodShape shape = ShapeFor(mood, intensity);
            float[] buffer = new float[Mathf.Max(1, (int)(length * SAMPLE_RATE))];

            // Effort raises the fundamental. Sadness lowers it; a taunt sits mid.
            float f0 = id.F0 * MoodPitch(mood) * (1f + 0.28f * intensity);
            float[] formants = FormantsFor(mood, intensity, id.Tract);

            var r1 = Biquad.BandPass(formants[0], 7f, SAMPLE_RATE);
            var r2 = Biquad.BandPass(formants[1], 9f, SAMPLE_RATE);
            var r3 = Biquad.BandPass(formants[2], 11f, SAMPLE_RATE);
            var breathBp = Biquad.BandPass(1700f, 0.8f, SAMPLE_RATE);
            var onsetBp = Biquad.BandPass(2600f, 1.4f, SAMPLE_RATE);

            float slot = 1f / shape.Syllables;
            double phase = 0;

            for (int i = 0; i < buffer.Length; i++)
            {
                float progress = i / (float)buffer.Length;

                // Which syllable we are in, and how far through it.
                int syllable = Mathf.Min(shape.Syllables - 1, (int)(progress / slot));
                float within = (progress - syllable * slot) / slot;

                // The gap sits at the END of each slot, so a syllable is punched in
                // and then cut — which is what separates three jeers from one long
                // wobble. The last syllable keeps its tail.
                float voicedSpan = syllable == shape.Syllables - 1 ? 1f : 1f - shape.Gap;
                if (within > voicedSpan)
                {
                    buffer[i] = 0f;
                    continue;
                }

                float inSyllable = within / voicedSpan;

                // Pitch: the syllable's step, glided across the syllable, plus a
                // vibrato whose rate and depth are themselves a mood cue.
                float hz = f0 * shape.Steps[syllable]
                           * Mathf.Lerp(1f, shape.IntraGlide, inSyllable)
                           * (1f + shape.VibratoDepth
                                   * Mathf.Sin(progress * length * Mathf.PI * 2f * shape.VibratoHz));

                phase += hz / SAMPLE_RATE;
                if (phase >= 1.0)
                {
                    phase -= 1.0;
                }

                // Sawtooth glottal source through three formant resonators.
                float source = (float)(2.0 * phase - 1.0);
                float voiced = r1.Process(source)
                             + r2.Process(source) * 0.5f
                             + r3.Process(source) * 0.22f;

                float noise = (float)(rng.NextDouble() * 2 - 1);
                float breath = breathBp.Process(noise) * shape.Breath;

                // Consonant burst: a fast-decaying noise transient at the syllable
                // onset. Cheap, and it is most of what makes these read as speech.
                float onset = onsetBp.Process(noise)
                              * shape.Onset * Mathf.Exp(-inSyllable * 42f);

                float env = Mathf.Min(1f, inSyllable / shape.Attack)
                            * Mathf.Exp(-inSyllable * shape.Decay)
                            * shape.Gains[syllable];

                buffer[i] = (voiced + breath + onset) * env;
            }

            // Peak below 1 so NormalizeVoice has headroom and nothing clips first.
            return Normalize(buffer, Mathf.Lerp(0.55f, 0.85f, intensity));
        }

        /// Kept short on purpose — see the class note on `_nextAllowedTime`. Sad runs
        /// longest because a groan IS its length; the jeer needs room for three
        /// syllables without hurrying them.
        private static float LengthFor(string mood, float intensity)
        {
            switch (mood)
            {
                case "Sad":    return Mathf.Lerp(0.70f, 1.15f, intensity);
                case "Insult": return Mathf.Lerp(0.58f, 0.95f, intensity);
                default:       return Mathf.Lerp(0.44f, 0.80f, intensity);   // Happy
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
