using System;
using UnityEngine;

namespace PoSumo
{
    /// The project's mix levels, persisted, plus the one shared builder for the
    /// arena reverb send.
    ///
    /// **Why this is not an AudioMixer asset.** Unity exposes no public API for
    /// authoring an `.mixer`, so a mixer here would have to be hand-built in the
    /// Editor and then resolved by GUID or Resources path at runtime — and a mixer
    /// that silently fails to load leaves the game with no audio at all. That is
    /// the same argument `Systems_UiKit` makes for keeping design tokens in C#,
    /// and it lands the same way. What a mixer would actually have bought us is
    /// three things: named category levels, persistence, and one place that owns
    /// the room. All three are here.
    ///
    /// What is genuinely lost by not having a mixer asset is a master LIMITER —
    /// there is no scripted equivalent — so the category defaults below are set
    /// conservatively enough that the gong plus a full crowd cannot clip.
    ///
    /// Static, and therefore carries the mandatory `SubsystemRegistration` reload:
    /// Enter Play Mode domain reload is disabled in this project, so without it
    /// the levels a player set would leak into the next Play session and, worse,
    /// a fresh session would read whatever the last one left in memory rather
    /// than what is on disk.
    public static class Systems_AudioMix
    {
        private const string PREF_PREFIX = "audio.";

        // Defaults are attenuations, never boosts. AudioSource.volume clamps at 1
        // and the per-clip gain table is already normalised so its maximum is
        // exactly 1.0 — a category default above 1 would silently pin, which is
        // the exact failure the VoiceGains asset shipped with for months.
        private const float DEFAULT_MASTER = 1f;
        private const float DEFAULT_SFX = 1f;
        private const float DEFAULT_CROWD = 0.9f;
        private const float DEFAULT_MUSIC = 0.7f;
        private const float DEFAULT_VOICE = 1f;

        private static float _master = DEFAULT_MASTER;
        private static float _sfx = DEFAULT_SFX;
        private static float _crowd = DEFAULT_CROWD;
        private static float _music = DEFAULT_MUSIC;
        private static float _voice = DEFAULT_VOICE;

        /// Raised whenever any level changes, so looping sources (the crowd murmur
        /// and all four music stems) can re-apply their volume immediately instead
        /// of waiting for the next thing that happens to retrigger them.
        public static event Action Changed;

        public static float Master
        {
            get => _master;
            set => Set(ref _master, value, nameof(Master));
        }

        public static float Sfx
        {
            get => _sfx;
            set => Set(ref _sfx, value, nameof(Sfx));
        }

        public static float Crowd
        {
            get => _crowd;
            set => Set(ref _crowd, value, nameof(Crowd));
        }

        public static float Music
        {
            get => _music;
            set => Set(ref _music, value, nameof(Music));
        }

        public static float Voice
        {
            get => _voice;
            set => Set(ref _voice, value, nameof(Voice));
        }

        /// Final multiplier for a body/impact sound.
        public static float SfxLevel => _master * _sfx;

        /// Final multiplier for anything on the crowd bus.
        public static float CrowdLevel => _master * _crowd;

        /// Final multiplier for the music stems.
        public static float MusicLevel => _master * _music;

        /// Final multiplier for a spoken fighter line.
        public static float VoiceLevel => _master * _voice;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reload()
        {
            _master = PlayerPrefs.GetFloat(PREF_PREFIX + nameof(Master), DEFAULT_MASTER);
            _sfx = PlayerPrefs.GetFloat(PREF_PREFIX + nameof(Sfx), DEFAULT_SFX);
            _crowd = PlayerPrefs.GetFloat(PREF_PREFIX + nameof(Crowd), DEFAULT_CROWD);
            _music = PlayerPrefs.GetFloat(PREF_PREFIX + nameof(Music), DEFAULT_MUSIC);
            _voice = PlayerPrefs.GetFloat(PREF_PREFIX + nameof(Voice), DEFAULT_VOICE);
            Changed = null;   // subscribers from the previous Play session are dead
        }

        public static void ResetToDefaults()
        {
            _master = DEFAULT_MASTER;
            _sfx = DEFAULT_SFX;
            _crowd = DEFAULT_CROWD;
            _music = DEFAULT_MUSIC;
            _voice = DEFAULT_VOICE;
            PlayerPrefs.SetFloat(PREF_PREFIX + nameof(Master), _master);
            PlayerPrefs.SetFloat(PREF_PREFIX + nameof(Sfx), _sfx);
            PlayerPrefs.SetFloat(PREF_PREFIX + nameof(Crowd), _crowd);
            PlayerPrefs.SetFloat(PREF_PREFIX + nameof(Music), _music);
            PlayerPrefs.SetFloat(PREF_PREFIX + nameof(Voice), _voice);
            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        private static void Set(ref float field, float value, string key)
        {
            float clamped = Mathf.Clamp01(value);
            if (Mathf.Approximately(field, clamped))
            {
                return;
            }
            field = clamped;
            PlayerPrefs.SetFloat(PREF_PREFIX + key, clamped);
            Changed?.Invoke();
        }

        /// The arena send, in one place so every bus in the game sits in the same
        /// room. `Systems_MatchAudio` built this inline for its two buses and
        /// `Systems_FighterVoice` had no send at all — so during a slow-mo finish
        /// the whole mix went dark and wet except the spoken line, which stayed
        /// bone-dry and full-bandwidth in front of it.
        ///
        /// `room`, `decay` and `level` default to the near-field settings the SFX
        /// bus has always used. The crowd bus passes wetter, longer, darker values
        /// so the two sit at different depths.
        public static AudioReverbFilter AddArenaReverb(
            GameObject host, float room = -400f, float decay = 1.4f, float level = -1200f)
        {
            var reverb = host.AddComponent<AudioReverbFilter>();
            reverb.reverbPreset = AudioReverbPreset.Off;   // required before manual values take
            reverb.dryLevel = 0f;
            reverb.room = room;
            reverb.roomHF = -900f;
            reverb.decayTime = decay;
            reverb.decayHFRatio = 0.55f;
            reverb.reflectionsLevel = -1400f;
            reverb.reflectionsDelay = 0.02f;
            reverb.reverbLevel = level;
            reverb.reverbDelay = 0.035f;
            reverb.diffusion = 100f;
            reverb.density = 100f;
            reverb.hfReference = 5000f;
            return reverb;
        }
    }
}
