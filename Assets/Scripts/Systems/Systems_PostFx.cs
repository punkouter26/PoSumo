using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace PoSumo
{
    /// Drives the arena's post-processing from what is happening in the match.
    ///
    /// Systems_ArenaLighting builds the volume once in Awake with fixed values and
    /// never touches it again, so the grade of a nothing-happening frame and the
    /// grade of a match-winning shove were identical. This animates it: a hit
    /// punches contrast, saturation, bloom and chromatic aberration for a few
    /// frames; the deciding fall drains the colour out during the slow-mo; match
    /// point closes the vignette in and breathes; a win lifts the exposure warm.
    ///
    /// Every value returns to the profile's authored baseline, so the effects
    /// compose rather than fight, and nothing drifts across a long match.
    /// Spawned at runtime by Systems_GameMatchManager.
    public sealed class Systems_PostFx : MonoBehaviour
    {
        [Header("Impact punch")]
        [Tooltip("Relative impact speed (m/s) that produces a full-strength punch.")]
        public float impactForFullPunch = 8f;
        [Tooltip("Impacts below this are ordinary contact and are ignored.")]
        public float minImpactSpeed = 2.5f;
        [Tooltip("Seconds a punch takes to decay. Short on purpose — this is a hit, not a mood.")]
        public float punchDecay = 0.26f;
        public float punchChromatic = 0.6f;
        public float punchDistortion = -0.14f;
        public float punchBloom = 0.8f;
        public float punchContrast = 9f;
        public float punchSaturation = 16f;

        [Header("Deciding fall")]
        [Tooltip("How far saturation drops while the finish plays out.")]
        public float finishDesaturation = 42f;
        public float finishVignette = 0.16f;
        public float finishBloom = 0.3f;
        public float finishAttackSeconds = 0.12f;
        public float finishReleaseSeconds = 0.9f;

        [Header("Match point")]
        public float matchPointVignette = 0.12f;
        public float matchPointPulseHz = 0.7f;

        [Header("Victory")]
        public float victoryExposure = 0.3f;
        public float victoryHoldSeconds = 2.2f;

        private Systems_GameMatchManager _manager;

        private Bloom _bloom;
        private Vignette _vignette;
        private ColorAdjustments _colour;
        private ChromaticAberration _aberration;
        private LensDistortion _distortion;

        // Authored baselines, captured once so every effect is an offset from the
        // look the profile actually shipped with.
        private float _baseBloom;
        private float _baseVignette;
        private float _baseContrast;
        private float _baseSaturation;
        private float _baseExposure;

        private float _punch;          // 0..1, decays in unscaled time
        private float _finish;         // 0..1, held for the duration of the finish
        private bool _finishActive;
        private float _victoryUntil;
        private bool _ready;

        private void Start()
        {
            _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            if (_manager != null)
            {
                _manager.RoundEnded += OnRoundEnded;
                _manager.RoundStarted += OnRoundStarted;
                _manager.MatchEnded += OnMatchEnded;
                _manager.MatchReset += OnMatchReset;
            }
            Bind();
        }

        private void OnEnable()
        {
            Sensor_Impact.AnyImpact += OnImpact;
        }

        private void OnDisable()
        {
            Sensor_Impact.AnyImpact -= OnImpact;
            if (_manager != null)
            {
                _manager.RoundEnded -= OnRoundEnded;
                _manager.RoundStarted -= OnRoundStarted;
                _manager.MatchEnded -= OnMatchEnded;
                _manager.MatchReset -= OnMatchReset;
            }
            Restore();
        }

        /// Grab the effect components from whatever volume the lighting rig built.
        /// Deferred out of Awake because Systems_ArenaLighting may build its
        /// profile in the same frame.
        private void Bind()
        {
            Systems_ArenaLighting lighting = Systems_ArenaLighting.Instance;
            VolumeProfile profile = lighting != null ? lighting.PostProfile : null;
            if (profile == null)
            {
                return;
            }

            profile.TryGet(out _bloom);
            profile.TryGet(out _vignette);
            profile.TryGet(out _colour);
            profile.TryGet(out _aberration);
            profile.TryGet(out _distortion);

            if (_bloom == null || _vignette == null || _colour == null)
            {
                return;
            }

            _baseBloom = _bloom.intensity.value;
            _baseVignette = _vignette.intensity.value;
            _baseContrast = _colour.contrast.value;
            _baseSaturation = _colour.saturation.value;
            _baseExposure = _colour.postExposure.value;
            _ready = true;
        }

        private void OnImpact(Sensor_Impact reporter, Collision2D collision)
        {
            if (!_ready || reporter == null || reporter.owner == null)
            {
                return;
            }
            // Body-on-body only, same rule Systems_ImpactFx uses — a foot landing
            // on clay every step is not a moment.
            var other = collision.collider.GetComponentInParent<Agent_BipedBody>();
            if (other == null || other == reporter.owner)
            {
                return;
            }
            float speed = collision.relativeVelocity.magnitude;
            if (speed < minImpactSpeed)
            {
                return;
            }
            float strength = Mathf.Clamp01(
                (speed - minImpactSpeed) / Mathf.Max(0.01f, impactForFullPunch - minImpactSpeed));
            // Max, not sum: a scrum of small contacts must not add up to a bigger
            // flash than the single hardest hit in it.
            _punch = Mathf.Max(_punch, strength);
        }

        private void OnRoundEnded(Agent_Biped winner, Agent_Biped loser)
        {
            if (loser != null)
            {
                _finishActive = true;   // draws get no dramatics, same as the presentation layer
            }
        }

        private void OnRoundStarted()
        {
            _finishActive = false;
        }

        private void OnMatchEnded(Agent_Biped winner)
        {
            _victoryUntil = Time.unscaledTime + victoryHoldSeconds;
        }

        private void OnMatchReset()
        {
            _finishActive = false;
            _punch = 0f;
            _victoryUntil = 0f;
        }

        private void Update()
        {
            if (!_ready)
            {
                Bind();
                if (!_ready)
                {
                    return;
                }
            }

            // Unscaled throughout: the whole point of a slow-mo finish is that the
            // grade holds while the world crawls, rather than the flash stretching
            // out into a three-second smear.
            float dt = Time.unscaledDeltaTime;

            _punch = Mathf.Max(0f, _punch - dt / Mathf.Max(0.01f, punchDecay));

            float finishTarget = _finishActive ? 1f : 0f;
            float finishRate = _finishActive
                ? dt / Mathf.Max(0.01f, finishAttackSeconds)
                : dt / Mathf.Max(0.01f, finishReleaseSeconds);
            _finish = Mathf.MoveTowards(_finish, finishTarget, finishRate);

            float victory = Time.unscaledTime < _victoryUntil ? 1f : 0f;

            // Match point: one round from losing the match, for either fighter.
            float matchPoint = 0f;
            if (_manager != null && _manager.pointsToWin > 1)
            {
                int leader = Mathf.Max(_manager.ScoreA, _manager.ScoreB);
                if (leader >= _manager.pointsToWin - 1)
                {
                    // Breathes rather than sits, so it reads as tension and not as
                    // someone having turned the brightness down.
                    matchPoint = 0.6f + 0.4f * Mathf.Sin(Time.unscaledTime * matchPointPulseHz * Mathf.PI * 2f);
                }
            }

            // Eased punch: the flash should hit hard and let go, not ramp linearly.
            float punch = _punch * _punch;

            _bloom.intensity.Override(_baseBloom + punch * punchBloom + _finish * finishBloom);
            _vignette.intensity.Override(Mathf.Clamp01(
                _baseVignette + _finish * finishVignette + matchPoint * matchPointVignette));
            _colour.contrast.Override(Mathf.Clamp(_baseContrast + punch * punchContrast + _finish * 8f, -100f, 100f));
            _colour.saturation.Override(Mathf.Clamp(
                _baseSaturation + punch * punchSaturation - _finish * finishDesaturation, -100f, 100f));
            _colour.postExposure.Override(_baseExposure + victory * victoryExposure);

            if (_aberration != null)
            {
                _aberration.intensity.Override(Mathf.Clamp01(punch * punchChromatic));
            }
            if (_distortion != null)
            {
                _distortion.intensity.Override(Mathf.Clamp(punch * punchDistortion, -1f, 1f));
            }
        }

        /// Put the profile back the way Systems_ArenaLighting authored it. The
        /// profile is a runtime ScriptableObject shared with anything else reading
        /// it, so leaving it mid-flash on teardown would leak the look.
        private void Restore()
        {
            if (!_ready)
            {
                return;
            }
            _bloom.intensity.Override(_baseBloom);
            _vignette.intensity.Override(_baseVignette);
            _colour.contrast.Override(_baseContrast);
            _colour.saturation.Override(_baseSaturation);
            _colour.postExposure.Override(_baseExposure);
            if (_aberration != null)
            {
                _aberration.intensity.Override(0f);
            }
            if (_distortion != null)
            {
                _distortion.intensity.Override(0f);
            }
        }
    }
}
