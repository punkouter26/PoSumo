using UnityEngine;

namespace PoSumo
{
    /// Hit smears: a one-quad radial STREAK burst at a contact point, aimed along
    /// the impact velocity, gone in ~0.14 s.
    ///
    /// The gap between the two effects that already watch `Sensor_Impact.AnyImpact`:
    /// Systems_ImpactFx's particle flash is many small dots and Systems_ShockwaveFx's
    /// ring is an omnidirectional punctuation reserved for slams. Neither says
    /// WHICH WAY a blow was travelling — and `Systems_StrikeImpulse` delivers real
    /// momentum, so direction is information the game already simulates and the
    /// eye never sees. A streak quad is the cheapest directed shape there is.
    ///
    /// The speed gate (4.5) sits deliberately between ImpactFx's flash gate
    /// (~2.5) and ShockwaveFx's slam gate (6.5): a smear appears on solid strikes,
    /// a ring only when somebody gets launched, so the three effects tier the
    /// same physical event by weight instead of all firing together.
    ///
    /// Pooled like the shockwave — never instantiate inside a collision callback
    /// — and subscribed to the same STATIC event, never to another companion.
    /// Spawned by Systems_GameMatchManager behind `enableHitSmear`, after
    /// Systems_ImpactFx so the two share the event in spawn order.
    public sealed class Systems_HitSmear : MonoBehaviour
    {
        [Tooltip("Relative speed (m/s) below which no smear is drawn. Above ImpactFx's flash gate, below ShockwaveFx's slam gate — this is the middle tier.")]
        public float minSpeed = 4.5f;
        [Tooltip("Relative speed treated as a maximum-strength blow.")]
        public float maxSpeed = 9f;
        [Tooltip("Seconds a smear lives, on the unscaled clock.")]
        public float lifeSeconds = 0.14f;
        [Tooltip("World size of a full-strength smear, in metres.")]
        public float maxSize = 1.15f;
        [Tooltip("Minimum gap between smears. Longer than the impact flash's cooldown on purpose — smears are punctuation.")]
        public float cooldown = 0.18f;

        /// Smears that can be alive at once; past this the oldest recycles.
        private const int SMEAR_POOL = 8;

        /// Above every body part (head is 4) and below the shockwave rings at 12,
        /// so a slam draws ring OVER smear.
        private const int SORTING_ORDER = 11;

        private static readonly int ProgressId = Shader.PropertyToID("_Progress");
        private static readonly int StrengthId = Shader.PropertyToID("_Strength");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private sealed class Smear
        {
            public Transform transform;
            public MeshRenderer renderer;
            public MaterialPropertyBlock properties;
            public float elapsed;
            public float strength;
            public bool active;
        }

        private Smear[] _smears;
        private int _nextSmear;
        private float _nextAllowed = -1f;
        private Material _material;
        private Mesh _quad;

        private void Awake()
        {
            Shader shader = Shader.Find("PoSumo/HitSmear");
            if (shader == null)
            {
                // Stripped or not imported. LogWarning is never stripped, so this
                // stays visible in a shipped build, the way a silent
                // `enabled = false` would not.
                Debug.LogWarning("[HITSMEAR] PoSumo/HitSmear not found — hit smears disabled.");
                enabled = false;
                return;
            }

            _material = new Material(shader) { name = "HitSmear (runtime)" };
            _quad = BuildQuad();
            _smears = new Smear[SMEAR_POOL];
            for (int smearIndex = 0; smearIndex < SMEAR_POOL; smearIndex++)
            {
                _smears[smearIndex] = BuildSmear(smearIndex);
            }
        }

        private void OnEnable()
        {
            Sensor_Impact.AnyImpact += OnImpact;
        }

        private void OnDisable()
        {
            // Static event: a missed unsubscribe survives the scene load into the
            // next bout and accumulates one listener per match played.
            Sensor_Impact.AnyImpact -= OnImpact;
        }

        private void OnDestroy()
        {
            if (_material != null)
            {
                Destroy(_material);
            }
            if (_quad != null)
            {
                Destroy(_quad);
            }
        }

        private Mesh BuildQuad()
        {
            // Unit quad by hand — creating a primitive would spawn a collider that
            // then has to be torn down (the same reason the shockwave does this).
            var mesh = new Mesh { name = "HitSmearQuad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f),
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
            };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private Smear BuildSmear(int index)
        {
            var host = new GameObject("Smear_" + index);
            host.transform.SetParent(transform, false);

            var filter = host.AddComponent<MeshFilter>();
            filter.sharedMesh = _quad;

            var meshRenderer = host.AddComponent<MeshRenderer>();
            // sharedMaterial + MPB, never .material — cloning per smear is exactly
            // the batching break the performance rules forbid.
            meshRenderer.sharedMaterial = _material;
            meshRenderer.sortingOrder = SORTING_ORDER;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.enabled = false;

            return new Smear
            {
                transform = host.transform,
                renderer = meshRenderer,
                properties = new MaterialPropertyBlock(),
                active = false,
            };
        }

        private void OnImpact(Sensor_Impact reporter, Collision2D collision)
        {
            if (reporter == null || reporter.owner == null || Time.unscaledTime < _nextAllowed)
            {
                return;
            }
            // Body-on-body only, matching the shockwave's rule: hitting the clay is
            // a dust event and has its own language.
            if (collision.collider.GetComponentInParent<Agent_BipedBody>() == null)
            {
                return;
            }

            float speed = collision.relativeVelocity.magnitude;
            if (speed < minSpeed)
            {
                return;
            }

            _nextAllowed = Time.unscaledTime + cooldown;
            float strength = Mathf.Clamp01((speed - minSpeed) / Mathf.Max(0.01f, maxSpeed - minSpeed));

            Vector2 dir = collision.relativeVelocity;
            float angle = dir.sqrMagnitude > 0.0001f ? Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg : 0f;
            Emit(collision.GetContact(0).point, angle, strength);
        }

        private void Emit(Vector3 position, float angleDegrees, float strength)
        {
            if (_smears == null)
            {
                return;
            }

            Smear smear = _smears[_nextSmear];
            _nextSmear = (_nextSmear + 1) % SMEAR_POOL;

            smear.elapsed = 0f;
            smear.strength = Mathf.Clamp01(strength);
            smear.active = true;
            smear.transform.position = new Vector3(position.x, position.y, 0f);
            // Aim the streak axis along the blow. A little jitter so two identical
            // strikes never render as the same shape.
            smear.transform.rotation = Quaternion.Euler(0f, 0f, angleDegrees + Random.Range(-14f, 14f));
            smear.transform.localScale = Vector3.one * (maxSize * Mathf.Lerp(0.6f, 1f, smear.strength));
            smear.renderer.enabled = true;

            smear.properties.SetColor(ColorId, new Color(1f, 0.9f, 0.72f, 1f));
            smear.properties.SetFloat(ProgressId, 0f);
            smear.properties.SetFloat(StrengthId, Mathf.Lerp(0.7f, 1.6f, smear.strength));
            smear.renderer.SetPropertyBlock(smear.properties);
        }

        private void Update()
        {
            if (_smears == null)
            {
                return;
            }

            // Unscaled: the biggest blows happen inside a slow-mo finish, and a
            // smear crawling at quarter speed for half a second reads as a stuck
            // texture. Punctuation must land in realtime.
            float dt = Time.unscaledDeltaTime;

            for (int smearIndex = 0; smearIndex < _smears.Length; smearIndex++)
            {
                Smear smear = _smears[smearIndex];
                if (!smear.active)
                {
                    continue;
                }

                smear.elapsed += dt;
                float progress = smear.elapsed / Mathf.Max(0.01f, lifeSeconds);
                if (progress >= 1f)
                {
                    smear.active = false;
                    smear.renderer.enabled = false;
                    continue;
                }

                smear.properties.SetFloat(ProgressId, progress);
                smear.renderer.SetPropertyBlock(smear.properties);
            }
        }
    }
}
