using System.Collections.Generic;
using UnityEngine;

namespace PoSumo
{
    /// Physics-driven match audio.
    ///
    /// Three things changed from the version that shipped:
    ///
    /// 1. Impacts are LAYERED. A hit plays a body-mass thump, a flesh slap and a
    ///    clay-dust tail as three separate clips whose gains are crossfaded by
    ///    impact velocity — a shove and a slam are different sounds, not the same
    ///    sound at different pitches. Every layer round-robins three variants.
    /// 2. The wrestlers make NOISE. Effort grunts on being hit, breathing between
    ///    exchanges, and a held strain while locked up chest to chest.
    /// 3. The crowd REACTS. The murmur bed tracks match tension, gasps at a heavy
    ///    hit, oohs when someone is walked to the edge, and roars at the finish.
    ///    SFX_CrowdOoh and SFX_CrowdCheer shipped in Resources and no code had ever
    ///    loaded them.
    ///
    /// There is no AudioMixer asset — mixing is done with two bus GameObjects, each
    /// carrying its own AudioLowPassFilter and AudioReverbFilter, because filters
    /// apply to every AudioSource on their GameObject. That buys the two effects
    /// that matter most: an arena reverb tail, and a low-pass sweep during slow
    /// motion that makes every finish feel expensive.
    ///
    /// Spawned at runtime by Systems_GameMatchManager.
    public sealed class Systems_MatchAudio : MonoBehaviour
    {
        public float masterVolume = 1f;

        [Header("Impact mapping")]
        [Tooltip("Relative velocity below which body contact is inaudible.")]
        public float thudMinSpeed = 1.2f;
        [Tooltip("Relative velocity treated as a maximum-strength hit.")]
        public float thudFullSpeed = 6.5f;
        // RAISED 0.5 -> 1.2 (2026-09-06). Scuff is filtered WHITE NOISE, and at
        // 0.5 m/s it fired on essentially every foot contact — the single most
        // constant sound in a bout was hiss. It now takes a real slide.
        public float scuffMinSpeed = 1.2f;
        public float scuffMaxSpeed = 3.5f;

        [Header("Voices")]
        [Tooltip("Impact speed above which the fighter taking the hit grunts.")]
        // 3.4 -> 2.2 (2026-09-06). Grunts are the only VOICED impact layer — a
        // real vocal fold model, not noise — and at 3.4 m/s they fired only on
        // the hardest slams while the noise layers ran constantly. Measured
        // impact speeds in a live bout run 3.9-8.8 m/s at the top end, so 2.2
        // also catches the clinch work, which is most of what a bout IS.
        public float gruntThreshold = 2.2f;
        /// BREATHING IS OFF (2026-09-06, at the player's request).
        ///
        /// It was filtered white noise on a timer, and stretching the timer was
        /// tried first and did not fix it: 2.6 -> 4.5 s the same day, on the
        /// reasoning that a 2.6 s cadence "laid a near-continuous hiss under the
        /// fight". At 4.5 s it was the same hiss less often — the problem was the
        /// LAYER, not its rate. Every impact layer in this bank is already noise
        /// (thuds, scuffs, dust), so breath added texture that was
        /// indistinguishable from them while never lining up with anything on
        /// screen. The grunts stay: those are a voiced vocal-fold model and they
        /// fire ON an impact, so they read as a fighter rather than as air.
        ///
        /// A private const rather than deleting the code or clearing the field,
        /// which is the same trick `Systems_SoftBodyJiggle.ENABLE_JIGGLE` uses and
        /// for the same reason: `breathInterval` is PUBLIC and therefore serialized
        /// into every scene that holds this component, so a code default cannot
        /// turn it off on its own — the stale scene values would win. Gating the
        /// playback makes all of them inert.
        private const bool ENABLE_BREATHING = false;

        [Tooltip("Seconds between breaths while a round is live. INERT — breathing is off, see ENABLE_BREATHING.")]
        public float breathInterval = 4.5f;
        [Tooltip("Distance between wrestlers that counts as locked up.")]
        public float lockUpDistance = 0.62f;
        public float lockUpSeconds = 1.1f;

        [Header("Crowd")]
        [Range(0f, 1f)] public float murmurBase = 0.13f;
        [Tooltip("Bed volume at full excitement. Raised 0.34 -> 0.6 on 2026-08-26 when the on-screen 'CROWD ROARING FOR' meter was removed: the crowd's backing is now something you HEAR, not read.")]
        [Range(0f, 1f)] public float murmurPeak = 0.6f;
        [Tooltip("How much full crowd support (Systems_CrowdMomentum.Support01) counts toward excitement. 1 = a fully backed underdog is as loud as a fighter on the rim.")]
        [Range(0f, 1f)] public float supportExcitement = 0.9f;
        [Tooltip("Fraction of the ring half-width past which a fighter counts as being walked to the edge.")]
        [Range(0.5f, 1f)] public float edgeFraction = 0.72f;

        [Header("Space")]
        [Tooltip("Arena half-width mapped to full stereo pan.")]
        public float panWidth = 3.4f;
        [Tooltip("Reverb wetness. This is a big wooden hall, not a studio.")]
        public float reverbLevel = -1200f;

        private const string AUDIO_PATH = "Audio/";
        private const int VOICE_COUNT = 10;
        private const int CROWD_VOICE_COUNT = 3;

        // Slow-mo filtering. 22 kHz is effectively open; 900 Hz is underwater.
        private const float LPF_OPEN = 22000f;
        private const float LPF_SLOWMO = 900f;

        // Impact layers, three variants each.
        private AudioClip[] _thudLow, _thudMid, _thudDust, _scuffs, _taikos, _grunts, _breaths;
        /// The looping crowd bed is OFF: the player asked for no background sound.
        /// A const rather than a serialized flag, so the 42-odd stale serialized
        /// values scattered through the scenes cannot switch it back on behind our
        /// backs — the same trick `Systems_SoftBodyJiggle.ENABLE_JIGGLE` uses.
        /// Every `_murmur*` call site below already null-guards, so this alone is
        /// sufficient: nothing loads, nothing allocates, nothing plays.
        private const bool ENABLE_AMBIENT_BED = false;

        private AudioClip _strain, _murmur, _gong, _ooh, _gasp, _cheer, _applause, _salt;

        private AudioSource[] _voices;
        private AudioSource[] _crowdVoices;
        private AudioSource _murmurSource;
        private AudioLowPassFilter _sfxLowPass;
        private AudioLowPassFilter _crowdLowPass;
        private int _nextVoice;
        private int _nextCrowdVoice;
        private int _thudVariant;

        private Systems_GameMatchManager _manager;
        private Agent_Biped _wrestlerA, _wrestlerB;

        private float _nextThudTime;
        private float _nextGruntTime;
        private float _nextBreathTime;
        private float _nextStrainTime;
        private float _nextOohTime;
        private float _nextGaspTime;
        private float _lockUpSince = -1f;
        private float _murmurPitch = 1f;
        private float _tension;
        private float _anticipation;
        private Systems_CrowdMomentum _momentum;
        private bool _momentumLookedUp;
        private bool _roarFired;
        private float _duckUntil;
        private float _duckAmount;
        private readonly Dictionary<Agent_BipedBody, float> _nextScuffTime = new Dictionary<Agent_BipedBody, float>();

        private void Awake()
        {
            LoadClips();
            BuildBuses();
        }

        private void LoadClips()
        {
            _thudLow = LoadSet("SFX_ThudLow", 3);
            _thudMid = LoadSet("SFX_ThudMid", 3);
            _thudDust = LoadSet("SFX_ThudDust", 3);
            _scuffs = LoadSet("SFX_Scuff", 3);
            _taikos = LoadSet("SFX_Taiko", 3);
            _grunts = LoadSet("SFX_Grunt", 4);
            _breaths = LoadSet("SFX_Breath", 2);

            // Pre-layered fallbacks, so a project that has not run
            // PoSumo/Generate Audio yet still makes the old noises rather than none.
            if (_thudLow.Length == 0)
            {
                _thudLow = LoadSet("SFX_Thud", 3);
            }

            _strain = Resources.Load<AudioClip>(AUDIO_PATH + "SFX_Strain");
            _murmur = ENABLE_AMBIENT_BED ? Resources.Load<AudioClip>(AUDIO_PATH + "SFX_CrowdMurmur") : null;
            _gong = Resources.Load<AudioClip>(AUDIO_PATH + "SFX_Gong");
            _ooh = Resources.Load<AudioClip>(AUDIO_PATH + "SFX_CrowdOoh");
            _gasp = Resources.Load<AudioClip>(AUDIO_PATH + "SFX_CrowdGasp");
            _cheer = Resources.Load<AudioClip>(AUDIO_PATH + "SFX_CrowdCheer");
            _applause = Resources.Load<AudioClip>(AUDIO_PATH + "SFX_CrowdApplause");
            _salt = Resources.Load<AudioClip>(AUDIO_PATH + "SFX_Salt");
        }

        /// Loads Name A..(A+count-1), skipping any that are missing.
        private static AudioClip[] LoadSet(string prefix, int count)
        {
            var found = new List<AudioClip>(count);
            for (int countIndex = 0; countIndex < count; countIndex++)
            {
                AudioClip clip = Resources.Load<AudioClip>(AUDIO_PATH + prefix + (char)('A' + countIndex));
                if (clip != null)
                {
                    found.Add(clip);
                }
            }
            return found.ToArray();
        }

        private void BuildBuses()
        {
            // SFX bus: everything the bodies do. The filters live on this
            // GameObject and therefore apply to all its sources at once, which is
            // the whole reason the voices are grouped rather than scattered.
            var sfxBus = new GameObject("Bus_SFX");
            sfxBus.transform.SetParent(transform, false);
            _voices = new AudioSource[VOICE_COUNT];
            for (int voiceIndex = 0; voiceIndex < VOICE_COUNT; voiceIndex++)
            {
                _voices[voiceIndex] = NewSource(sfxBus);
            }
            _sfxLowPass = sfxBus.AddComponent<AudioLowPassFilter>();
            _sfxLowPass.cutoffFrequency = LPF_OPEN;
            AddReverb(sfxBus);

            // Crowd bus: kept separate so it can duck under the gong and get its
            // own, gentler slow-mo treatment.
            var crowdBus = new GameObject("Bus_Crowd");
            crowdBus.transform.SetParent(transform, false);
            _crowdVoices = new AudioSource[CROWD_VOICE_COUNT];
            for (int crowdVoiceIndex = 0; crowdVoiceIndex < CROWD_VOICE_COUNT; crowdVoiceIndex++)
            {
                _crowdVoices[crowdVoiceIndex] = NewSource(crowdBus);
            }
            if (ENABLE_AMBIENT_BED)
            {
                _murmurSource = NewSource(crowdBus);
                _murmurSource.loop = true;
            }
            _crowdLowPass = crowdBus.AddComponent<AudioLowPassFilter>();
            _crowdLowPass.cutoffFrequency = LPF_OPEN;
            // The crowd was the one bus with NO reverb, which is backwards: a hall
            // full of people is the most reverberant thing in the building, and a
            // bone-dry cheer sits in front of the wrestlers instead of around them.
            // Wetter, longer and darker than the SFX send — a body hitting clay is
            // a near-field transient, a crowd is the room itself.
            AddReverb(crowdBus, room: -200f, decay: 2.6f, level: reverbLevel + 500f);
        }

        private static AudioSource NewSource(GameObject host)
        {
            var source = host.AddComponent<AudioSource>();
            source.playOnAwake = false;
            // 2D with MANUAL panning. True 3D falloff in an orthographic 2D game
            // fights the follow camera — the listener's distance to the action
            // changes with zoom, so volumes pump. Panning by world x gives the
            // directional cue without the pumping.
            source.spatialBlend = 0f;
            return source;
        }

        /// Arena send. `room`, `decay` and `level` default to the near-field
        /// settings the SFX bus has always used; the crowd bus passes wetter,
        /// longer values so the two buses sit at different depths in the room.
        ///
        /// The filter itself is built by `Systems_AudioMix.AddArenaReverb`, which
        /// is now the single definition of this room — `Systems_FighterVoice` had
        /// no send at all and needed the identical one, and two hand-copied reverb
        /// blocks would drift.
        private void AddReverb(GameObject host, float room = -400f, float decay = 1.4f, float level = float.NaN)
        {
            // NaN rather than an overload: the caller's default has to be the
            // serialized reverbLevel, which is not a compile-time constant.
            Systems_AudioMix.AddArenaReverb(
                host, room, decay, float.IsNaN(level) ? reverbLevel : level);
        }

        private void Start()
        {
            _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            if (_manager != null)
            {
                _manager.RoundStarted += OnRoundStarted;
                _manager.RoundEnded += OnRoundEnded;
                _manager.MatchEnded += OnMatchEnded;
                _manager.MatchReset += OnMatchReset;
                _wrestlerA = _manager.wrestlerA;
                _wrestlerB = _manager.wrestlerB;
            }

            if (_murmur != null)
            {
                _murmurSource.clip = _murmur;
                _murmurSource.volume = murmurBase * masterVolume * Systems_AudioMix.CrowdLevel;
                _murmurSource.Play();
            }

            // No opening taiko here. It used to be fired from Start because a
            // countdown-only opening raised no RoundStarted — but the walk-in
            // path always did raise it, so every match with enableWalkIn on
            // opened with two taiko hits. The manager now raises RoundStarted for
            // the countdown-only opening too, so OnRoundStarted is the single
            // source of the hit on both paths.
            _nextBreathTime = Time.time + breathInterval;
        }

        private void OnEnable() { Sensor_Impact.AnyImpact += OnImpact; }

        private void OnDisable()
        {
            Sensor_Impact.AnyImpact -= OnImpact;
            if (_manager != null)
            {
                _manager.RoundStarted -= OnRoundStarted;
                _manager.RoundEnded -= OnRoundEnded;
                _manager.MatchEnded -= OnMatchEnded;
                _manager.MatchReset -= OnMatchReset;
            }
        }

        // ------------------------------------------------------------- events

        private void OnRoundStarted()
        {
            PlayTaiko();
            _anticipation = 0f;
            _nextBreathTime = Time.time + breathInterval;
        }

        private void OnRoundEnded(Agent_Biped winner, Agent_Biped loser)
        {
            // Applause between rounds, and the bed drops into anticipation for the
            // next one.
            //
            // Ducked under, the same way OnMatchEnded ducks under the gong and
            // OnKnockout ducks under the hit. The finish was the only one of the
            // four big audio moments that did NOT duck, so the applause came up
            // ON TOP of a bed still running at full level and the two smeared into
            // one wash — audible as "the crowd noise never changes" rather than as
            // a missing effect. Shorter and shallower than the gong's: a round end
            // is punctuation, not the end of the bout.
            Duck(0.4f, 0.5f);
            PlayCrowd(_applause, 0.5f, 1f);
            _anticipation = 1f;

            // Ring-out streak chant (2026-08-26): consecutive ring-outs by the
            // same fighter in one match and the crowd takes up a chant — three
            // cheer beats on a timer, on top of the applause. Any other outcome, or
            // the other fighter scoring, breaks the run.
            bool ringOut = winner != null && _manager != null
                && _manager.LastOutcome == Systems_GameMatchManager.RoundOutcome.RingOut;
            if (ringOut && winner == _streakFighter) _streakCount++;
            else { _streakFighter = ringOut ? winner : null; _streakCount = ringOut ? 1 : 0; }
            if (_streakCount >= CHANT_STREAK)
            {
                _chantBeatsLeft = CHANT_BEATS;
                _nextChantTime = Time.unscaledTime + 0.6f;   // let the applause land first
                Systems_Log.Info($"[CROWD] chant for {winner.behaviorName} — {_streakCount} ring-outs running");
            }
        }

        // 2, not 3: a bracket bout is first-to-2, so a three-round run could never
        // happen there. Two ring-outs running IS the clinch, and that is the moment.
        private const int CHANT_STREAK = 2;
        private const int CHANT_BEATS = 3;
        private const float CHANT_BEAT_SECONDS = 0.45f;
        private Agent_Biped _streakFighter;
        private int _streakCount;
        private int _chantBeatsLeft;
        private float _nextChantTime;

        /// One cheer per beat, on the unscaled clock — no coroutine, per the rules.
        private void TickChant()
        {
            if (_chantBeatsLeft <= 0 || Time.unscaledTime < _nextChantTime) return;
            _chantBeatsLeft--;
            _nextChantTime = Time.unscaledTime + CHANT_BEAT_SECONDS;
            PlayCrowd(_cheer, 0.9f, 1.04f);
            if (_chantBeatsLeft == 0) PlayCrowd(_applause, 0.6f, 1f);
        }

        private void OnMatchEnded(Agent_Biped winner)
        {
            Play(_gong, 1f, 1f, 0f);
            // The gong owns this moment: duck the crowd under it, then let the
            // roar come up behind.
            Duck(0.55f, 0.7f);
            PlayCrowd(_cheer, 0.95f, 1f);
        }

        private void OnMatchReset()
        {
            _anticipation = 0f;
            _tension = 0f;
            _streakFighter = null;
            _streakCount = 0;
            _chantBeatsLeft = 0;
            PlayCrowd(_applause, 0.4f, 1.05f);
        }

        private void PlayTaiko()
        {
            if (_taikos.Length == 0)
            {
                return;
            }
            Play(_taikos[Random.Range(0, _taikos.Length)], 0.8f, Random.Range(0.97f, 1.03f), 0f);
        }

        /// Ceremonial salt, called by the presentation layer at a round opening.
        public void PlaySalt(float worldX)
        {
            Play(_salt, 0.5f, Random.Range(0.95f, 1.08f), Pan(worldX));
        }

        /// Pull every non-SFX bus down for a moment — used so the gong is not
        /// competing with three thousand people.
        public void Duck(float amount, float seconds)
        {
            _duckAmount = Mathf.Max(_duckAmount, Mathf.Clamp01(amount));
            _duckUntil = Mathf.Max(_duckUntil, Time.unscaledTime + seconds);
        }

        // ------------------------------------------------------------ impacts

        private static bool IsStatic(Collision2D c) =>
            c.rigidbody == null || c.rigidbody.bodyType == RigidbodyType2D.Static;

        private void OnImpact(Sensor_Impact sensor, Collision2D c)
        {
            float speed = c.relativeVelocity.magnitude;
            float now = Time.unscaledTime;
            float pan = Pan(sensor.transform.position.x);

            if (IsStatic(c))
            {
                if (sensor.isFoot)
                {
                    if (speed < scuffMinSpeed || speed > scuffMaxSpeed) return;
                    float next;
                    if (_nextScuffTime.TryGetValue(sensor.owner, out next) && now < next) return;
                    _nextScuffTime[sensor.owner] = now + 0.14f;
                    PlayScuff(Mathf.Lerp(0.04f, 0.15f, speed / scuffMaxSpeed), pan);
                    return;
                }
                // Body part slamming the ground: dusty and dull, no flesh slap.
                if (speed > 2f && now >= _nextThudTime)
                {
                    _nextThudTime = now + 0.06f;
                    PlayLayeredThud(speed, 0.75f, pan, slapScale: 0.4f, dustScale: 1.4f);
                    MaybeGrunt(sensor.owner, speed * 0.8f, pan);
                }
                return;
            }

            // Dynamic contact: only wrestler-vs-wrestler hits are interesting,
            // and both sides report, so a short global cooldown dedupes them.
            var otherBody = c.collider.GetComponentInParent<Agent_BipedBody>();
            if (otherBody == null || otherBody == sensor.owner) return;
            if (speed < thudMinSpeed || now < _nextThudTime) return;
            _nextThudTime = now + 0.06f;

            PlayLayeredThud(speed, 1f, pan, slapScale: 1f, dustScale: 0.7f);
            MaybeGrunt(sensor.owner, speed, pan);

            // Tension spikes on contact and bleeds off in Update.
            _tension = Mathf.Clamp01(_tension + Mathf.InverseLerp(thudMinSpeed, thudFullSpeed, speed) * 0.35f);

            if (speed > thudFullSpeed * 0.92f && now >= _nextGaspTime)
            {
                _nextGaspTime = now + 3.5f;
                PlayCrowd(_gasp, 0.55f, Random.Range(0.96f, 1.06f));
            }
        }

        /// The three-layer impact. `t` decides both the gains and the relative
        /// balance: a light contact is nearly all slap and dust with no body under
        /// it, a heavy one is dominated by mass.
        private void PlayLayeredThud(float speed, float pitchScale, float pan, float slapScale, float dustScale)
        {
            float t = Mathf.Clamp01((speed - thudMinSpeed) / Mathf.Max(0.01f, thudFullSpeed - thudMinSpeed));
            _thudVariant = (_thudVariant + 1) % 3;
            float pitch = pitchScale * Random.Range(0.93f, 1.07f);

            // Body mass: only really present on a hard hit. This is the layer that
            // makes a slam sound heavy.
            PlayFrom(_thudLow, _thudVariant, Mathf.Lerp(0.05f, 1f, t * t), pitch, pan);
            // Flesh slap: audible across the whole range, loudest in the middle.
            PlayFrom(_thudMid, _thudVariant, Mathf.Lerp(0.2f, 0.85f, t) * slapScale, pitch * 1.02f, pan);
            // Clay dust: the tail. Barely scaled, so even a light contact has one.
            PlayFrom(_thudDust, _thudVariant, Mathf.Lerp(0.15f, 0.5f, t) * dustScale,
                     Random.Range(0.9f, 1.15f), pan);
        }

        private void PlayScuff(float volume, float pan)
        {
            PlayFrom(_scuffs, Random.Range(0, Mathf.Max(1, _scuffs.Length)), volume,
                     Random.Range(0.85f, 1.15f), pan);
        }

        // ------------------------------------------------------------- voices

        private void MaybeGrunt(Agent_BipedBody body, float speed, float pan)
        {
            if (_grunts.Length == 0 || speed < gruntThreshold || Time.unscaledTime < _nextGruntTime)
            {
                return;
            }
            _nextGruntTime = Time.unscaledTime + Random.Range(0.28f, 0.6f);
            float strength = Mathf.InverseLerp(gruntThreshold, thudFullSpeed, speed);
            PlayFrom(_grunts, Random.Range(0, _grunts.Length),
                     Mathf.Lerp(0.40f, 0.95f, strength),
                     // A harder hit knocks the pitch up: that is involuntary.
                     Random.Range(0.92f, 1.06f) + strength * 0.12f,
                     pan);
        }

        private void UpdateVoices()
        {
            if (_manager == null || !_manager.RoundActive)
            {
                return;
            }
            float now = Time.time;

            // Breathing, alternating between the two fighters. OFF — see
            // ENABLE_BREATHING above for why the layer went rather than its rate.
            if (ENABLE_BREATHING && _breaths.Length > 0 && now >= _nextBreathTime)
            {
                bool useA = Random.value < 0.5f;
                Agent_Biped fighter = useA ? _wrestlerA : _wrestlerB;
                _nextBreathTime = now + breathInterval * Random.Range(0.7f, 1.3f);
                if (fighter != null)
                {
                    PlayFrom(_breaths, Random.Range(0, _breaths.Length),
                             0.12f, Random.Range(0.9f, 1.1f), Pan(fighter.TorsoX));
                }
            }

            // Locked up chest to chest: the held, straining push. Nothing else in
            // the mix tells you the fighters are working while barely moving.
            if (_strain != null && _wrestlerA != null && _wrestlerB != null)
            {
                float gap = Mathf.Abs(_wrestlerA.TorsoX - _wrestlerB.TorsoX);
                if (gap < lockUpDistance)
                {
                    if (_lockUpSince < 0f)
                    {
                        _lockUpSince = now;
                    }
                    else if (now - _lockUpSince > lockUpSeconds && now >= _nextStrainTime)
                    {
                        _nextStrainTime = now + Random.Range(1.6f, 2.8f);
                        float midX = (_wrestlerA.TorsoX + _wrestlerB.TorsoX) * 0.5f;
                        Play(_strain, 0.4f, Random.Range(0.92f, 1.08f), Pan(midX));
                    }
                }
                else
                {
                    _lockUpSince = -1f;
                }
            }
        }

        // -------------------------------------------------------------- crowd

        private void UpdateCrowd()
        {
            float dt = Time.unscaledDeltaTime;
            // Tension decays steadily; impacts push it back up in OnImpact.
            _tension = Mathf.Max(0f, _tension - dt * 0.28f);
            _anticipation = Mathf.Max(0f, _anticipation - dt * 0.5f);

            float edge = EdgeTension();
            float support = CrowdSupport();
            float excitement = Mathf.Clamp01(Mathf.Max(_tension, edge, support * supportExcitement));

            // The crowd hitting full support used to print "CROWD ROARING FOR X"
            // in the dock. It is now a roar: one cheer as support peaks, re-armed
            // once it drops back, so a see-saw bout gets one per swing rather than
            // a cheer every frame it sits at 1.
            if (support >= 0.99f && !_roarFired)
            {
                _roarFired = true;
                PlayCrowd(_cheer, 0.85f, Random.Range(0.97f, 1.03f));
            }
            else if (support < 0.6f)
            {
                _roarFired = false;
            }

            if (_murmurSource != null && _murmurSource.clip != null)
            {
                // The bed rises with excitement and DROPS between rounds — a crowd
                // going quiet before a bout is as expressive as one roaring.
                float target = Mathf.Lerp(murmurBase, murmurPeak, excitement) * (1f - 0.55f * _anticipation);
                // The mix level is folded into the per-frame lerp target rather
                // than applied once at Play, so a player moving the crowd slider
                // mid-bout hears it immediately without this needing to subscribe
                // to Systems_AudioMix.Changed.
                _murmurSource.volume = Mathf.Lerp(
                    _murmurSource.volume,
                    target * masterVolume * Systems_AudioMix.CrowdLevel * DuckFactor(),
                    dt * 3f);
                // Barely-there pitch lift: a crowd getting louder also gets higher.
                // Tracked in a field rather than read back off the source, because
                // UpdateFilters also scales the pitch for slow motion — reading and
                // rewriting the source would compound the two every frame.
                _murmurPitch = Mathf.Lerp(_murmurPitch, 1f + excitement * 0.05f, dt * 2f);
            }

            // "Ooh" when a fighter is genuinely being walked to the edge.
            if (_ooh != null && edge > 0.75f && Time.unscaledTime >= _nextOohTime)
            {
                _nextOohTime = Time.unscaledTime + 2.6f;
                PlayCrowd(_ooh, Mathf.Lerp(0.3f, 0.7f, edge), Random.Range(0.95f, 1.05f));
            }
        }

        /// `Systems_CrowdMomentum.Support01`, or 0 when that companion is off.
        /// Looked up once and late: both are spawned in the same Start and the
        /// order is undefined, so an Awake-time lookup would find nothing.
        private float CrowdSupport()
        {
            if (!_momentumLookedUp)
            {
                _momentumLookedUp = true;
                _momentum = FindAnyObjectByType<Systems_CrowdMomentum>();
            }
            return _momentum != null ? _momentum.Support01 : 0f;
        }

        /// 0 when both fighters are mid-ring, approaching 1 as either nears the
        /// edge — the single best proxy this game has for "something is about to
        /// happen".
        private float EdgeTension()
        {
            if (_manager == null || _wrestlerA == null || _wrestlerB == null)
            {
                return 0f;
            }
            float half = _manager.ringHalfWidth;
            if (half <= 0.01f)
            {
                return 0f;
            }
            float centerX = _manager.transform.position.x;
            float a = Mathf.Abs(_wrestlerA.TorsoX - centerX) / half;
            float b = Mathf.Abs(_wrestlerB.TorsoX - centerX) / half;
            float worst = Mathf.Max(a, b);
            return Mathf.Clamp01(Mathf.InverseLerp(edgeFraction, 1f, worst));
        }

        // --------------------------------------------------------------- mix

        private void Update()
        {
            UpdateVoices();
            UpdateCrowd();
            TickChant();
            UpdateFilters();
        }

        /// Slow motion drops the low-pass and the pitch. This is the cheapest and
        /// most effective trick available: Systems_MatchPresentation already owns
        /// Time.timeScale, so all this has to do is follow it.
        private void UpdateFilters()
        {
            float scale = Mathf.Clamp01(Time.timeScale);
            float dt = Time.unscaledDeltaTime;

            // Only the bottom of the range matters — anything above ~0.8 is
            // effectively normal speed and should sound untouched.
            float slow = 1f - Mathf.Clamp01(Mathf.InverseLerp(0.2f, 0.85f, scale));

            if (_sfxLowPass != null)
            {
                float target = Mathf.Lerp(LPF_OPEN, LPF_SLOWMO, slow);
                _sfxLowPass.cutoffFrequency =
                    Mathf.Lerp(_sfxLowPass.cutoffFrequency, target, 1f - Mathf.Exp(-12f * dt));
            }
            if (_crowdLowPass != null)
            {
                // The crowd is already distant; muffle it less or it disappears.
                float target = Mathf.Lerp(LPF_OPEN, LPF_SLOWMO * 2.2f, slow);
                _crowdLowPass.cutoffFrequency =
                    Mathf.Lerp(_crowdLowPass.cutoffFrequency, target, 1f - Mathf.Exp(-10f * dt));
            }
            if (_murmurSource != null)
            {
                _murmurSource.pitch = _murmurPitch * Mathf.Lerp(1f, 0.82f, slow);
            }
        }

        private float DuckFactor()
        {
            if (Time.unscaledTime >= _duckUntil)
            {
                _duckAmount = 0f;
                return 1f;
            }
            return 1f - _duckAmount;
        }

        // ------------------------------------------------------------ playing

        /// World x to stereo pan. Relative to the arena centre rather than the
        /// camera, so a hit on the left of the ring stays on the left even as the
        /// follow camera tracks across it.
        private float Pan(float worldX)
        {
            float centerX = _manager != null ? _manager.transform.position.x : 0f;
            return Mathf.Clamp((worldX - centerX) / Mathf.Max(0.01f, panWidth), -1f, 1f) * 0.75f;
        }

        private void PlayFrom(AudioClip[] set, int index, float volume, float pitch, float pan)
        {
            if (set == null || set.Length == 0)
            {
                return;
            }
            Play(set[index % set.Length], volume, pitch, pan);
        }

        private void Play(AudioClip clip, float volume, float pitch, float pan)
        {
            if (clip == null) return;
            var src = _voices[_nextVoice];
            _nextVoice = (_nextVoice + 1) % VOICE_COUNT;
            src.pitch = pitch;
            src.panStereo = pan;
            src.PlayOneShot(clip, volume * masterVolume * Systems_AudioMix.SfxLevel);
        }

        private void PlayCrowd(AudioClip clip, float volume, float pitch)
        {
            if (clip == null) return;
            var src = _crowdVoices[_nextCrowdVoice];
            _nextCrowdVoice = (_nextCrowdVoice + 1) % CROWD_VOICE_COUNT;
            src.pitch = pitch;
            src.panStereo = 0f;   // the crowd is all around, not on one side
            src.PlayOneShot(clip, volume * masterVolume * Systems_AudioMix.CrowdLevel);
        }
    }
}
