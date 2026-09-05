using UnityEngine;

namespace PoSumo
{
    /// Expanding shock rings on the heaviest moments — a head KO, a limb coming
    /// off, and body-on-body contact hard enough to count as a slam.
    ///
    /// This is the one thing the VFX inventory genuinely lacked. Systems_DustPuff
    /// already builds six particle systems (dust, sweat, salt, blood, spark,
    /// haze) and the ring-out burst, the salt throw and the sweat spray are all
    /// long since wired up — but every one of them is PARTICLES, so the biggest
    /// moments in the game read as "more of the same small stuff" rather than as
    /// a different kind of event. A single expanding ring is the cheapest way to
    /// say "that one was different".
    ///
    /// **It subscribes to statics, never to another companion.** Both signals it
    /// needs — `Sensor_Impact.AnyImpact` and `Systems_BodyDamage.Knockout` /
    /// `Dismembered` — are static events, which is the sanctioned way for two
    /// companions to react to the same thing without either reaching into the
    /// other. Systems_ImpactFx watches the same impact event independently.
    ///
    /// Pooled, because the alternative is instantiating a GameObject inside a
    /// collision callback. `RING_POOL` rings can be live at once; a new ring past
    /// that recycles the oldest, which is correct for this effect — if six rings
    /// are already on screen nobody is going to miss the seventh.
    ///
    /// Spawned at runtime by Systems_GameMatchManager behind `enableShockwave`.
    public sealed class Systems_ShockwaveFx : MonoBehaviour
    {
        [Tooltip("Body-on-body relative speed (m/s) below which no ring is drawn. Set well above Systems_ImpactFx.minSpeed (2.2) — the ring is for slams, and one on every shove would be worse than none.")]
        public float minSpeed = 6.5f;
        [Tooltip("Relative speed treated as a maximum-strength slam.")]
        public float maxSpeed = 11f;
        [Tooltip("Seconds a ring takes to expand and fade.")]
        public float ringSeconds = 0.42f;
        [Tooltip("World radius of a full-strength ring, in metres. The mat is 3.5 m half-width, so a ring much past 2 m covers the whole arena and stops reading as local to the hit.")]
        public float maxRadius = 1.9f;
        [Tooltip("Minimum gap between rings. Longer than the dust cooldown on purpose: rings are punctuation.")]
        public float cooldown = 0.35f;

        /// Rings that can be alive simultaneously.
        private const int RING_POOL = 6;

        /// Sorting order. Above every body part (the head is 4) and below the
        /// atmosphere's foreground haze at 20, so a ring draws over the fighters
        /// that produced it but does not punch through the arena's front layer.
        private const int SORTING_ORDER = 12;

        private static readonly int ProgressId = Shader.PropertyToID("_Progress");
        private static readonly int StrengthId = Shader.PropertyToID("_Strength");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private sealed class Ring
        {
            public Transform transform;
            public MeshRenderer renderer;
            public MaterialPropertyBlock properties;
            public float elapsed;
            public float strength;
            public float radius;
            public bool active;
        }

        private Ring[] _rings;
        private int _nextRing;
        private float _nextAllowed;
        private Material _material;
        private Mesh _quad;

        private void Awake()
        {
            Shader shader = Shader.Find("PoSumo/Shockwave");
            if (shader == null)
            {
                // Stripped from the build, or the .shader failed to import. A
                // missing decoration is not worth a hard failure, but it IS worth
                // a warning — LogWarning is never stripped, so this is visible in
                // a shipped build the way a silent `enabled = false` would not be.
                Debug.LogWarning("[SHOCKWAVE] PoSumo/Shockwave not found — shock rings disabled.");
                enabled = false;
                return;
            }

            _material = new Material(shader) { name = "Shockwave (runtime)" };
            _quad = BuildQuad();
            _rings = new Ring[RING_POOL];
            for (int ringIndex = 0; ringIndex < RING_POOL; ringIndex++)
            {
                _rings[ringIndex] = BuildRing(ringIndex);
            }
        }

        private void OnEnable()
        {
            Sensor_Impact.AnyImpact += OnImpact;
            Systems_BodyDamage.Knockout += OnKnockout;
            Systems_BodyDamage.Dismembered += OnDismembered;
        }

        private void OnDisable()
        {
            // All three are STATIC events. A missed unsubscribe here keeps a dead
            // scene's rings alive across the load into the next bout, which in a
            // bracket means every match accumulates another set of listeners.
            Sensor_Impact.AnyImpact -= OnImpact;
            Systems_BodyDamage.Knockout -= OnKnockout;
            Systems_BodyDamage.Dismembered -= OnDismembered;
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
            // A unit quad centred on the origin. Built by hand rather than taken
            // from a PrimitiveType, because creating a primitive spawns a
            // GameObject with a collider that then has to be torn down.
            var mesh = new Mesh { name = "ShockwaveQuad" };
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

        private Ring BuildRing(int index)
        {
            var host = new GameObject("Ring_" + index);
            host.transform.SetParent(transform, false);

            var filter = host.AddComponent<MeshFilter>();
            filter.sharedMesh = _quad;

            var meshRenderer = host.AddComponent<MeshRenderer>();
            // sharedMaterial, never .material: touching .material clones it per
            // ring and breaks batching, and the per-ring values ride in a
            // MaterialPropertyBlock precisely so they do not need a clone.
            meshRenderer.sharedMaterial = _material;
            meshRenderer.sortingOrder = SORTING_ORDER;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.enabled = false;

            return new Ring
            {
                transform = host.transform,
                renderer = meshRenderer,
                properties = new MaterialPropertyBlock(),
                active = false,
            };
        }

        /// Fire a ring. `strength` is 0..1 and scales radius, brightness and the
        /// ring's thickness together.
        public void Emit(Vector3 position, float strength, Color tint)
        {
            if (_rings == null)
            {
                return;
            }

            Ring ring = _rings[_nextRing];
            _nextRing = (_nextRing + 1) % RING_POOL;

            ring.elapsed = 0f;
            ring.strength = Mathf.Clamp01(strength);
            ring.radius = Mathf.Lerp(maxRadius * 0.45f, maxRadius, ring.strength);
            ring.active = true;
            ring.transform.position = new Vector3(position.x, position.y, 0f);
            ring.transform.localScale = Vector3.one * (ring.radius * 2f);
            ring.renderer.enabled = true;

            ring.properties.SetColor(ColorId, tint);
            ring.properties.SetFloat(ProgressId, 0f);
            ring.properties.SetFloat(StrengthId, Mathf.Lerp(0.6f, 2.2f, ring.strength));
            ring.renderer.SetPropertyBlock(ring.properties);
        }

        private void Update()
        {
            if (_rings == null)
            {
                return;
            }

            // Unscaled: a shock ring is punctuation on the moment of the hit, and
            // Systems_MatchPresentation drops Time.timeScale to ~0.25 for exactly
            // the finishes that produce the biggest rings. On scaled time the ring
            // would crawl outward for nearly two seconds and read as a bubble.
            float dt = Time.unscaledDeltaTime;

            for (int ringIndex = 0; ringIndex < _rings.Length; ringIndex++)
            {
                Ring ring = _rings[ringIndex];
                if (!ring.active)
                {
                    continue;
                }

                ring.elapsed += dt;
                float progress = ring.elapsed / Mathf.Max(0.01f, ringSeconds);
                if (progress >= 1f)
                {
                    ring.active = false;
                    ring.renderer.enabled = false;
                    continue;
                }

                ring.properties.SetFloat(ProgressId, progress);
                ring.properties.SetFloat(StrengthId, Mathf.Lerp(0.6f, 2.2f, ring.strength));
                ring.renderer.SetPropertyBlock(ring.properties);
            }
        }

        private void OnImpact(Sensor_Impact reporter, Collision2D collision)
        {
            if (reporter == null || reporter.owner == null || Time.time < _nextAllowed)
            {
                return;
            }
            // Body-on-body only. A fighter landing on the clay is a dust event,
            // not a shock event.
            if (collision.collider.GetComponentInParent<Agent_BipedBody>() == null)
            {
                return;
            }

            float speed = collision.relativeVelocity.magnitude;
            if (speed < minSpeed)
            {
                return;
            }

            _nextAllowed = Time.time + cooldown;
            float strength = Mathf.Clamp01((speed - minSpeed) / Mathf.Max(0.01f, maxSpeed - minSpeed));
            Emit(collision.GetContact(0).point, strength, new Color(1f, 0.88f, 0.7f, 1f));
        }

        /// A head KO always draws a full-strength ring and ignores the cooldown —
        /// it is the single biggest moment the game has.
        private void OnKnockout(Agent_BipedBody body, Vector3 point)
        {
            Emit(point, 1f, new Color(1f, 0.72f, 0.62f, 1f));
        }

        private void OnDismembered(Agent_BipedBody body, Systems_BodyDamage.Region region, Vector3 point)
        {
            Emit(point, 0.85f, new Color(1f, 0.5f, 0.45f, 1f));
        }
    }
}
