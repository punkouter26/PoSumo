using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace PoSumo
{
    /// Corner lanterns: warm lights hung at the arena's edges that flicker like a
    /// live venue and FLARE with the crowd.
    ///
    /// The lighting rig was completely steady except for the key's slow drift,
    /// which is what made it read as a lighting SETUP rather than as a room. The
    /// lanterns add the other half of a live hall: small, warm, independently
    /// unstable sources whose combined flicker says "fire and paper", and whose
    /// flare when the crowd roars ties the light to the sound — the crowd system
    /// already drives a cheer and a torque boost on the same signal.
    ///
    /// Reads `Systems_CrowdMomentum.Support01` through the same cached
    /// FindAnyObjectByType the music director uses for the identical signal, so a
    /// bout with enableCrowdMomentum off simply loses the flare, not the rig.
    /// Round-end flares come off the manager's `RoundEnded` event. Both are
    /// read-only with respect to the fight.
    ///
    /// Deliberately NOT part of Systems_ArenaLighting: it is its own companion
    /// behind its own `enableLanterns` flag with its own spawn line, so the rig
    /// file stays the exposure authority and this stays a decoration. Spawned
    /// AFTER the lighting companion so it parents into an arena that already has
    /// its global fill — with the global on, a lantern that fails to spawn cannot
    /// leave anything black.
    public sealed class Systems_ArenaLanterns : MonoBehaviour
    {
        [Tooltip("Lantern height above the mat. Hung on the implied roof line, below where the key hangs.")]
        public float height = 3.1f;
        [Tooltip("Horizontal offset from the arena centre, in metres. The mat is ~3.5 m half-width, so ±4.6 puts the poles just outside the clay.")]
        public float sideOffset = 4.6f;
        [Tooltip("Warm lantern colour.")]
        public Color lanternColor = new Color(1f, 0.62f, 0.3f);
        [Tooltip("Base intensity of each lantern. Small on purpose — these punctuate the rig, they do not light the fight.")]
        public float baseIntensity = 0.5f;
        [Tooltip("Outer radius of each lantern's pool.")]
        public float outerRadius = 3.4f;
        [Tooltip("Fraction of intensity the flicker can remove at the trough. Real flames never go out entirely.")]
        [Range(0f, 0.6f)] public float flickerDepth = 0.3f;
        [Tooltip("Flicker rate multiplier. The three lanterns run incommensurate rates off this so they never synchronise into a loop.")]
        public float flickerRate = 7.3f;
        [Tooltip("Extra intensity at full crowd support — the hall brightens when the crowd roars.")]
        public float crowdFlare = 0.65f;
        [Tooltip("Extra intensity on the one-second flare fired when a round ends.")]
        public float roundEndFlare = 1.1f;

        private const int LANTERN_COUNT = 3;

        private Light2D[] _lanterns;
        private float[] _phases;
        private float[] _rates;
        private float _flarePulse;
        private Systems_CrowdMomentum _crowd;
        private bool _crowdSearched;
        private Systems_GameMatchManager _manager;

        private void Start()
        {
            _manager = GetComponentInParent<Systems_GameMatchManager>();
            if (_manager == null)
            {
                _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            }

            // The arena is the authority on where the clay is — same rule the ring
            // squeeze cue uses. Without one, hang the lanterns around the manager.
            Vector3 home = _manager != null ? _manager.transform.position : transform.position;

            _lanterns = new Light2D[LANTERN_COUNT];
            _phases = new float[LANTERN_COUNT];
            _rates = new float[LANTERN_COUNT];
            for (int lanternIndex = 0; lanternIndex < LANTERN_COUNT; lanternIndex++)
            {
                // Two poles flanking the ring, one wash on the back wall between
                // them and further back. Positions are offsets from HOME, never
                // accumulated — the same discipline as the key light's drift.
                float side = lanternIndex == 0 ? -1f : lanternIndex == 1 ? 1f : 0f;
                float depth = lanternIndex == 2 ? -2.2f : 0f;
                var go = new GameObject("Lantern_" + lanternIndex);
                go.transform.SetParent(transform, false);
                go.transform.position = home + new Vector3(side * sideOffset, height + depth * 0.4f, depth);

                Light2D light = go.AddComponent<Light2D>();
                light.lightType = Light2D.LightType.Point;
                light.color = lanternColor;
                light.intensity = baseIntensity;
                light.pointLightInnerRadius = 0.3f;
                light.pointLightOuterRadius = outerRadius;
                light.falloffIntensity = 0.7f;
                // Light2D shadows default ON when created in code, and a shadow
                // pass per lantern for shadows the edge-on dohyo can never show is
                // the exact waste the lighting file documents.
                light.shadowsEnabled = false;
                light.volumetricShadowsEnabled = false;
                // Volumetric glow on a tiny warm source reads as haze around the
                // flame at almost no cost, and the atmosphere's particles give it
                // something to scatter off.
                light.volumetricEnabled = true;
                light.volumeIntensity = 0.22f;

                _lanterns[lanternIndex] = light;
                _phases[lanternIndex] = Random.Range(0f, Mathf.PI * 2f);
                // Incommensurate rates: no sum of sines here repeats inside a bout.
                _rates[lanternIndex] = flickerRate * (1f + lanternIndex * 0.37f);
            }
        }

        private void OnEnable()
        {
            if (_manager == null)
            {
                _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            }
            if (_manager != null)
            {
                _manager.RoundEnded += OnRoundEnded;
            }
        }

        private void OnDisable()
        {
            if (_manager != null)
            {
                _manager.RoundEnded -= OnRoundEnded;
            }
        }

        private void OnRoundEnded(Agent_Biped winner, Agent_Biped loser)
        {
            // A decided round is a roar; the hall flares for about a second.
            _flarePulse = loser != null ? roundEndFlare : roundEndFlare * 0.4f;
        }

        private void Update()
        {
            if (_lanterns == null)
            {
                return;
            }

            // Realtime, never scaled: the slow-mo finish must not freeze the fire.
            float t = Time.unscaledTime;
            float dt = Time.unscaledDeltaTime;
            if (_flarePulse > 0f)
            {
                _flarePulse = Mathf.Max(0f, _flarePulse - dt * 1.4f);
            }

            float crowd = CrowdSupport01();
            float flare = crowd * crowdFlare + _flarePulse;

            // Lanterns are the one thing in the rig allowed to look unstable, so
            // this is the one place a per-frame sin() stack is the POINT.
            // Unscaled clock; intensity is a base-times-flame-times-flare product
            // so the crowd lift scales the flame rather than flattening it.
            for (int lanternIndex = 0; lanternIndex < _lanterns.Length; lanternIndex++)
            {
                Light2D light = _lanterns[lanternIndex];
                if (light == null)
                {
                    continue;
                }
                float phase = _phases[lanternIndex];
                float rate = _rates[lanternIndex];
                // Two incommensurate sines beat against each other into a flame
                // wobble that never visibly loops.
                float flame = 1f - flickerDepth * 0.5f
                              * (1f + Mathf.Sin(t * rate + phase)
                                     * 0.7f
                                     + Mathf.Sin(t * rate * 2.71f + phase * 1.9f) * 0.3f);
                light.intensity = baseIntensity * Mathf.Max(0.35f, flame) + flare;
            }
        }

        /// Cached, not looked up per frame — the same one-crowd-signal rule the
        /// music director and fighter panel follow.
        private float CrowdSupport01()
        {
            if (!_crowdSearched)
            {
                _crowdSearched = true;
                _crowd = FindAnyObjectByType<Systems_CrowdMomentum>();
            }
            return _crowd != null ? _crowd.Support01 : 0f;
        }
    }
}
