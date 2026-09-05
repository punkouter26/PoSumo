using UnityEngine;

namespace PoSumo
{
    /// Writes the "how this bout has gone" terms into one wrestler's
    /// PoSumo/BodyLit material: `_Sweat` (a wet specular band down the lit
    /// centreline), `_Dirt` (clay staining that climbs from the feet up), `_Wet`
    /// (sweat beads joined into a sheet) and `_Flash` (the white pulse on the
    /// frame a limb is struck). It also writes `_Detail` and `_BackLight` once in
    /// Start — those are a look rather than a state.
    ///
    /// The flash lives here rather than in Systems_ImpactFx, which is the other
    /// companion that watches `Sensor_Impact.AnyImpact`, for an architectural
    /// reason: a companion must not reach across into another companion, and the
    /// per-fighter material is owned here. Both subscribe to the same static
    /// event independently, which is the sanctioned shape.
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

        [Header("Wetness")]
        [Tooltip("Sweat level at which beads join up into a sheet of water. Below this _Wet stays 0 — a lightly working fighter is shiny, not soaked.")]
        [Range(0f, 1f)] public float wetOnsetSweat = 0.45f;
        [Range(0f, 1f)] public float maxWet = 0.8f;

        [Header("Hit flash")]
        [Tooltip("Contact speed (m/s) that produces a full-strength flash. Measured strike speeds in this project run 3.9-5.3 m/s and knockbacks reach 8.8, so this sits inside the distribution rather than above it — the mistake Systems_StrikeImpulse's curve made once already.")]
        public float flashFullSpeed = 6f;
        [Tooltip("Contacts below this do not flash at all, or a fighter standing still would strobe from foot contacts.")]
        public float flashMinSpeed = 2.5f;
        [Range(0f, 1f)] public float maxFlash = 0.7f;
        [Tooltip("Flash decay per second. A hit flash is one or two frames of impression, not a glow.")]
        public float flashDecay = 4.5f;

        [Header("Static shading")]
        [Tooltip("Procedural muscle banding along each limb's long axis. Written once in Start - it is a look, not a state.\n\nDEFAULT 0, i.e. OFF, after being measured twice on live close-ups. The term works exactly as designed; the PRIMITIVES defeat it. Every part is a plain ellipse or box sprite with no anatomical UV layout, and the trunk is four separate spine segments each carrying its own copy of the wave - so any amplitude high enough to read as form also reads as horizontal stripes across the belly. Tried at 0.35 (caterpillar segmentation), then at 0.22 with the frequency bug fixed (still banded). Raise it only once the bodies carry real authored art; the shader side is correct and costs nothing while this is 0.")]
        [Range(0f, 1f)] public float detail = 0f;
        [Tooltip("Muscle bellies along each limb. The shader folds the wave with abs(), so the visible count is TWICE this - 1.6 gives about three bellies per limb. The first pass used 7, i.e. fourteen bands, and read as segmentation.")]
        [Range(0.5f, 8f)] public float detailScale = 1.6f;
        [Tooltip("Broad warm silhouette wrap that separates two clinched bodies. Written once in Start.")]
        [Range(0f, 2f)] public float backLight = 0.35f;

        // Writing a float into a material every frame is a CPU-side property-block
        // update and a shader constant upload; below this delta nobody can see the
        // difference, so it is not worth paying for.
        private const float WRITE_EPSILON = 0.002f;

        private static readonly int SweatId = Shader.PropertyToID("_Sweat");
        private static readonly int DirtId = Shader.PropertyToID("_Dirt");
        private static readonly int WetId = Shader.PropertyToID("_Wet");
        private static readonly int FlashId = Shader.PropertyToID("_Flash");
        private static readonly int DetailId = Shader.PropertyToID("_Detail");
        private static readonly int DetailScaleId = Shader.PropertyToID("_DetailScale");
        private static readonly int BackLightId = Shader.PropertyToID("_BackLight");

        private Material _material;
        private Systems_GameMatchManager _manager;
        private bool _hasSweat;
        private bool _hasDirt;
        private bool _hasWet;
        private bool _hasFlash;

        private float _sweat;
        private float _dirt;
        private float _wet;
        private float _flash;
        private float _writtenSweat = -1f;
        private float _writtenDirt = -1f;
        private float _writtenWet = -1f;
        private float _writtenFlash = -1f;

        private void Start()
        {
            // Only write sweat and clay into a material that carries the shaded
            // treatment (normal map + rim/wrap/sweat terms); on an unshaded tube a
            // sweat highlight reads as a stripe. Always true since the flat-body
            // path was removed, but the gate stays so a future flat look cannot
            // half-enable this behind its back.
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
            _hasWet = _material.HasProperty(WetId);
            _hasFlash = _material.HasProperty(FlashId);
            if (!_hasSweat && !_hasDirt && !_hasWet && !_hasFlash)
            {
                enabled = false;
                return;
            }

            // Muscle detail and back light are a LOOK, not a state — they never
            // change during a bout, so they are written once here rather than
            // re-uploaded every frame alongside the four animated terms.
            if (_material.HasProperty(DetailId))
            {
                _material.SetFloat(DetailId, detail);
            }
            if (_material.HasProperty(DetailScaleId))
            {
                _material.SetFloat(DetailScaleId, detailScale);
            }
            if (_material.HasProperty(BackLightId))
            {
                _material.SetFloat(BackLightId, backLight);
            }

            _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            SubscribeMatchReset();
        }

        // The manager is only known after Start's lookup, so the first OnEnable
        // cannot subscribe; Start does, and any later re-enable finds _manager set
        // and subscribes here. The flag keeps a Start-after-OnEnable pair from
        // subscribing twice.
        private bool _resetSubscribed;

        private void OnEnable()
        {
            Sensor_Impact.AnyImpact += OnImpact;
            SubscribeMatchReset();
        }

        private void OnDisable()
        {
            Sensor_Impact.AnyImpact -= OnImpact;
            if (_manager != null && _resetSubscribed)
            {
                _manager.MatchReset -= OnMatchReset;
                _resetSubscribed = false;
            }
        }

        private void SubscribeMatchReset()
        {
            if (_manager == null || _resetSubscribed) return;
            _manager.MatchReset += OnMatchReset;
            _resetSubscribed = true;
        }

        /// A fresh match is a fresh wrestler: dry, clean of the last bout's clay,
        /// and not mid-flash from the blow that ended the previous round.
        private void OnMatchReset()
        {
            _sweat = 0f;
            _dirt = 0f;
            _wet = 0f;
            _flash = 0f;
        }

        /// Two separate consequences of one contact, and they deliberately do NOT
        /// share a gate:
        ///
        /// - clay transfers on contact with the ARENA only — a shoulder to the
        ///   chest does not make either fighter dirtier;
        /// - the hit flash fires on ANY contact hard enough, and a strike from the
        ///   opponent is precisely the case it exists for. Gating it behind the
        ///   clay checks (as the single early-return here used to) would have made
        ///   the flash fire on everything except being hit.
        private void OnImpact(Sensor_Impact sensor, Collision2D collision)
        {
            if (sensor == null || sensor.owner != body)
            {
                return;
            }

            float speed = collision.relativeVelocity.magnitude;

            if (_hasFlash && speed > flashMinSpeed)
            {
                float strength = Mathf.Clamp01((speed - flashMinSpeed)
                                               / Mathf.Max(0.01f, flashFullSpeed - flashMinSpeed));
                // Max, not add: two limbs landing on the same frame is one blow as
                // far as the eye is concerned, and summing them blows out to white.
                _flash = Mathf.Max(_flash, strength * maxFlash);
            }

            if (_dirt >= maxDirt || speed < dirtMinSpeed)
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

            // Wetness is a consequence of sweat, not an independent state: beads
            // join into a sheet only once a fighter is genuinely working. It
            // therefore inherits sweat's asymmetric rise and fall for free.
            _wet = Mathf.Clamp01(Mathf.InverseLerp(wetOnsetSweat, maxSweat, _sweat)) * maxWet;

            if (_flash > 0f)
            {
                _flash = Mathf.MoveTowards(_flash, 0f, flashDecay * Time.deltaTime);
            }

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
            if (_hasWet && Mathf.Abs(_wet - _writtenWet) > WRITE_EPSILON)
            {
                _material.SetFloat(WetId, _wet);
                _writtenWet = _wet;
            }
            if (_hasFlash && Mathf.Abs(_flash - _writtenFlash) > WRITE_EPSILON)
            {
                _material.SetFloat(FlashId, _flash);
                _writtenFlash = _flash;
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
