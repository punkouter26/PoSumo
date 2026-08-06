using UnityEngine;

namespace PoSumo
{
    /// Writes the two "how this bout has gone" terms into one wrestler's
    /// PoSumo/BodyLit material: `_Sweat` (a wet specular band down the lit
    /// centreline) and `_Dirt` (clay staining that climbs from the feet up).
    ///
    /// The shader has implemented both since the realism pass, and its header has
    /// always credited this class for driving them — but the class did not exist,
    /// so `_Dirt` was never written by anything and `_Sweat` was only ever forced
    /// to zero by the flat-shading path in Systems_ArenaLighting. Both terms cost
    /// no extra texture fetch (they read the same cylinder normal map the lighting
    /// already samples), so this is free once something bothers to set them.
    ///
    /// One instance per fighter, spawned by Systems_GameMatchManager, because
    /// `Agent_BipedBody.BodyMaterial` is per-fighter — writing a shared material
    /// would make the opponent sweat in sympathy.
    public sealed class Systems_BodySurface : MonoBehaviour
    {
        public Agent_BipedBody body;

        [Header("Sweat")]
        [Tooltip("Exertion (mean joint angular speed, rad/s) that counts as flat out.")]
        public float exertionForFull = 9f;
        [Tooltip("How fast the sheen catches up to hard work. Sweat appears over seconds, not frames.")]
        public float sweatRise = 0.22f;
        [Tooltip("How fast it fades when a wrestler stops working. Slower than the rise — nobody dries off instantly.")]
        public float sweatFall = 0.07f;
        [Range(0f, 1f)] public float maxSweat = 0.85f;

        [Header("Clay")]
        [Tooltip("Clay picked up per m/s of arena contact. Accumulates across the whole match.")]
        public float dirtPerImpactSpeed = 0.018f;
        [Tooltip("Contacts below this are footsteps, not falls, and stain far less.")]
        public float dirtMinSpeed = 1.2f;
        [Range(0f, 1f)] public float maxDirt = 0.7f;

        // Writing a float into a material every frame is a CPU-side property-block
        // update and a shader constant upload; below this delta nobody can see the
        // difference, so it is not worth paying for.
        private const float WRITE_EPSILON = 0.002f;

        private static readonly int SweatId = Shader.PropertyToID("_Sweat");
        private static readonly int DirtId = Shader.PropertyToID("_Dirt");

        private Material _material;
        private Systems_GameMatchManager _manager;
        private bool _hasSweat;
        private bool _hasDirt;

        private float _sweat;
        private float _dirt;
        private float _writtenSweat = -1f;
        private float _writtenDirt = -1f;

        private void Start()
        {
            // Flat shading zeroes the rim/wrap/sweat terms on purpose and never
            // assigns the normal map, so a sweat highlight written here would sit
            // on an unshaded tube and read as a stripe. Respect the flag rather
            // than half-enabling the look behind its back.
            //
            // BodyShadingActive also covers the master switch: with
            // LightingEffects off the body material is a stock UNLIT sprite that
            // has neither _Sweat nor _Dirt at all.
            if (!Systems_ArenaLighting.BodyShadingActive)
            {
                enabled = false;
                return;
            }
            if (body == null)
            {
                enabled = false;
                return;
            }

            _material = body.BodyMaterial;
            if (_material == null)
            {
                enabled = false;
                return;
            }

            // The material falls back to the plain lit sprite shader when
            // PoSumo/BodyLit is missing or stripped from a build, and that one has
            // neither property. Checking once beats a HasProperty call per frame.
            _hasSweat = _material.HasProperty(SweatId);
            _hasDirt = _material.HasProperty(DirtId);
            if (!_hasSweat && !_hasDirt)
            {
                enabled = false;
                return;
            }

            _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            if (_manager != null)
            {
                _manager.MatchReset += OnMatchReset;
            }
        }

        private void OnEnable() => Sensor_Impact.AnyImpact += OnImpact;

        private void OnDisable()
        {
            Sensor_Impact.AnyImpact -= OnImpact;
            if (_manager != null)
            {
                _manager.MatchReset -= OnMatchReset;
            }
        }

        /// A fresh match is a fresh wrestler: dry, and clean of the last bout's clay.
        private void OnMatchReset()
        {
            _sweat = 0f;
            _dirt = 0f;
        }

        /// Clay transfers on contact with the arena, not with the opponent — a
        /// shoulder to the chest does not make either fighter dirtier.
        private void OnImpact(Sensor_Impact sensor, Collision2D collision)
        {
            if (sensor == null || sensor.owner != body || _dirt >= maxDirt)
            {
                return;
            }
            float speed = collision.relativeVelocity.magnitude;
            if (speed < dirtMinSpeed)
            {
                return;
            }
            if (collision.collider.GetComponentInParent<Agent_BipedBody>() != null)
            {
                return;
            }
            _dirt = Mathf.Min(maxDirt, _dirt + (speed - dirtMinSpeed) * dirtPerImpactSpeed);
        }

        private void Update()
        {
            // Start disables this component on every unusable path, but a caller
            // that re-enables it later would resume Update against a half-set-up
            // instance — Start does not run twice. Cheaper to re-check than to
            // trust nobody ever toggles it.
            if (_material == null || body == null)
            {
                return;
            }

            float target = Mathf.Clamp01(Exertion() / exertionForFull) * maxSweat;
            // Asymmetric: sweat builds under load and lingers afterwards.
            float rate = target > _sweat ? sweatRise : sweatFall;
            _sweat = Mathf.MoveTowards(_sweat, target, rate * Time.deltaTime);

            if (_hasSweat && Mathf.Abs(_sweat - _writtenSweat) > WRITE_EPSILON)
            {
                _material.SetFloat(SweatId, _sweat);
                _writtenSweat = _sweat;
            }
            if (_hasDirt && Mathf.Abs(_dirt - _writtenDirt) > WRITE_EPSILON)
            {
                _material.SetFloat(DirtId, _dirt);
                _writtenDirt = _dirt;
            }
        }

        /// Mean absolute angular speed across the ragdoll. Angular rather than
        /// linear because a wrestler locked chest-to-chest is working hardest
        /// precisely when nothing is travelling anywhere.
        private float Exertion()
        {
            Rigidbody2D[] parts = body.Parts;
            if (parts == null || parts.Length == 0)
            {
                return 0f;
            }
            float total = 0f;
            int counted = 0;
            for (int partIndex = 0; partIndex < parts.Length; partIndex++)
            {
                Rigidbody2D part = parts[partIndex];
                if (part == null)
                {
                    continue;
                }
                total += Mathf.Abs(part.angularVelocity) * Mathf.Deg2Rad;
                counted++;
            }
            return counted == 0 ? 0f : total / counted;
        }
    }
}
