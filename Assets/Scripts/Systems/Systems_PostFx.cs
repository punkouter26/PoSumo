using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace PoSumo
{
    /// Drives the event post-processing that the arena volume declares but, since
    /// the old Systems_PostFx was removed, nothing animated: a KO punch
    /// (chromatic aberration spike + vignette close + a desaturation dip) and a
    /// slow-motion look (desaturation + a gentle extra vignette for as long as
    /// `Time.timeScale` is down).
    ///
    /// `Systems_ArenaLighting.BuildPostProcessing` parks `ChromaticAberration` and
    /// `LensDistortion` at zero and says "nothing drives these" — this is the
    /// something again, rebuilt on the same profile so the profile layout never
    /// changes at runtime. Everything is written through the profile's component
    /// overrides, so there is no renderer feature, no extra blit and no second
    /// camera: on the 2D renderer these are passes the post stack already runs.
    ///
    /// Subscribes to the `Systems_BodyDamage` STATICS only, never to another
    /// companion, and reads `Time.timeScale` for the slow-mo state rather than
    /// reaching into Systems_MatchPresentation — which is the single owner of
    /// that value and is never touched from here.
    ///
    /// Spawned by Systems_GameMatchManager behind `enablePostFx`, after
    /// Systems_ArenaLighting in the same Start pass so `Instance.PostProfile`
    /// exists when this component's Start runs.
    public sealed class Systems_PostFx : MonoBehaviour
    {
        [Tooltip("timeScale below which the scene counts as being in a slow-motion finish.")]
        public float slowMoThreshold = 0.95f;
        [Tooltip("Saturation offset while slow motion is running. Negative = drained, the 'expensive moment' look.")]
        public float slowMoDesaturation = -26f;
        [Tooltip("Extra vignette while slow motion is running.")]
        [Range(0f, 0.6f)] public float slowMoVignette = 0.14f;
        [Tooltip("Seconds the KO punch takes to decay to nothing, on the unscaled clock so a slow-mo finish does not stretch it.")]
        public float punchDecaySeconds = 0.85f;
        [Tooltip("Punch strength for a head KO. Dismemberment lands proportionally lower.")]
        [Range(0f, 1f)] public float koPunchStrength = 1f;
        [Tooltip("Chromatic aberration at the peak of a full-strength punch.")]
        [Range(0f, 1f)] public float punchAberration = 0.65f;
        [Tooltip("Extra vignette at the peak of a full-strength punch.")]
        [Range(0f, 0.6f)] public float punchVignette = 0.32f;
        [Tooltip("Saturation dip at the peak of a full-strength punch.")]
        public float punchDesaturation = -18f;

        // Below this delta an override is not re-written. Same logic as
        // Systems_BodySurface's WRITE_EPSILON: a volume override write is cheap but
        // not free, and nothing can see a 0.001 change in aberration.
        private const float WRITE_EPSILON = 0.004f;

        private VolumeProfile _profile;
        private Vignette _vignette;
        private ColorAdjustments _colour;
        private ChromaticAberration _aberration;

        private float _baseVignette;
        private float _baseSaturation;
        private bool _hasVignette;
        private bool _hasColour;
        private bool _hasAberration;

        private float _punch;
        private float _writtenAberration = -1f;
        private float _writtenVignette = -1f;
        private float _writtenSaturation = -1f;
        private bool _restored;

        private void Start()
        {
            Systems_ArenaLighting lighting = Systems_ArenaLighting.Instance;
            if (lighting == null || lighting.PostProfile == null)
            {
                // Lighting (and with it the post stack) is off; there is nothing to
                // drive. Silent: enableLighting off is a deliberate configuration.
                enabled = false;
                return;
            }

            _profile = lighting.PostProfile;
            _hasVignette = _profile.TryGet(out _vignette);
            _hasColour = _profile.TryGet(out _colour);
            _hasAberration = _profile.TryGet(out _aberration);

            // Baselines are whatever the lighting rig authored, so the driver only
            // ever ADDS on top and a retune of enablePost numbers upstream keeps
            // holding without this file needing to know them.
            _baseVignette = _hasVignette ? _vignette.intensity.value : 0f;
            _baseSaturation = _hasColour ? _colour.saturation.value : 0f;
        }

        private void OnEnable()
        {
            // Statics: the sanctioned cross-companion surface. A missed
            // unsubscribe keeps a destroyed component's handler alive into the
            // next bout — the exact leak the architecture rules warn about.
            Systems_BodyDamage.Knockout += OnKnockout;
            Systems_BodyDamage.Dismembered += OnDismembered;
        }

        private void OnDisable()
        {
            Systems_BodyDamage.Knockout -= OnKnockout;
            Systems_BodyDamage.Dismembered -= OnDismembered;
            RestoreBaselines();
        }

        private void OnKnockout(Agent_BipedBody body, Vector3 point)
        {
            _punch = koPunchStrength;
        }

        private void OnDismembered(Agent_BipedBody body, Systems_BodyDamage.Region region, Vector3 point)
        {
            _punch = Mathf.Max(_punch, koPunchStrength * 0.7f);
        }

        private void Update()
        {
            if (_profile == null)
            {
                return;
            }

            // Unscaled: both looks exist BECAUSE time slowed down, so measuring
            // their decay on scaled time would freeze them mid-punch.
            float dt = Time.unscaledDeltaTime;
            if (_punch > 0f)
            {
                _punch = Mathf.Max(0f, _punch - dt / Mathf.Max(0.01f, punchDecaySeconds));
            }

            bool slowMo = Time.timeScale < slowMoThreshold;
            float punch = _punch * _punch; // squared: fast attack, quick settle

            float aberration = (slowMo ? 0.18f : 0f) + punch * punchAberration;
            float vignette = _baseVignette
                             + (slowMo ? slowMoVignette : 0f)
                             + punch * punchVignette;
            float saturation = _baseSaturation
                               + (slowMo ? slowMoDesaturation : 0f)
                               + punch * punchDesaturation;

            if (_hasAberration && Mathf.Abs(aberration - _writtenAberration) > WRITE_EPSILON)
            {
                _aberration.intensity.Override(Mathf.Clamp01(aberration));
                _writtenAberration = aberration;
            }
            if (_hasVignette && Mathf.Abs(vignette - _writtenVignette) > WRITE_EPSILON)
            {
                _vignette.intensity.Override(Mathf.Clamp01(vignette));
                _writtenVignette = vignette;
            }
            if (_hasColour && Mathf.Abs(saturation - _writtenSaturation) > WRITE_EPSILON)
            {
                _colour.saturation.Override(saturation);
                _writtenSaturation = saturation;
            }
            _restored = false;
        }

        /// On disable (scene teardown, flag flipped) put the profile back exactly
        /// as the lighting rig authored it. The profile is runtime-built and dies
        /// with the scene anyway, but this component can also be toggled mid-match
        /// and must not leave a -26 saturation behind when it goes.
        private void RestoreBaselines()
        {
            if (_restored || _profile == null)
            {
                return;
            }
            if (_hasAberration)
            {
                _aberration.intensity.Override(0f);
            }
            if (_hasVignette)
            {
                _vignette.intensity.Override(_baseVignette);
            }
            if (_hasColour)
            {
                _colour.saturation.Override(_baseSaturation);
            }
            _writtenAberration = -1f;
            _writtenVignette = -1f;
            _writtenSaturation = -1f;
            _restored = true;
        }
    }
}
