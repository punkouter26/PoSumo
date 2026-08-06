using UnityEngine;

namespace PoSumo
{
    /// All arena particles, on four pooled ParticleSystems: clay dust, sweat
    /// spray, the ceremonial salt throw, and a slow ambient haze over the dohyo.
    ///
    /// This used to instantiate one GameObject with its own SpriteRenderer PER
    /// PARTICLE and Destroy it a third of a second later — up to 18 allocations
    /// and 18 destroys per shove, in the middle of the fight, which is exactly the
    /// churn .claude/rules/performance.md exists to prevent. The public Burst()
    /// signature is unchanged so Systems_ImpactFx and every other caller keeps
    /// working; only the machinery behind it is different.
    ///
    /// Self-instantiating: the first call to any of the static entry points builds
    /// the systems. Particles run on SCALED time on purpose — dust hanging in the
    /// air during a slow-mo finish is most of why the slow-mo reads as impact.
    public sealed class Systems_DustPuff : MonoBehaviour
    {
        private static Systems_DustPuff _instance;
        private static bool _quitting;

        private ParticleSystem _dust;
        private ParticleSystem _sweat;
        private ParticleSystem _salt;
        private ParticleSystem _haze;
        private ParticleSystem _blood;
        private static Texture2D _puffTexture;

        /// Enter Play Mode domain reload is disabled in this project, so a static
        /// instance survives into the next Play session pointing at a destroyed
        /// object. Same treatment as Systems_TournamentState.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ClearOnPlaySessionStart()
        {
            _instance = null;
            _quitting = false;
        }

        private static Systems_DustPuff Instance
        {
            get
            {
                if (_quitting || !Application.isPlaying)
                {
                    return null;
                }
                if (_instance == null)
                {
                    var go = new GameObject("ArenaParticles");
                    _instance = go.AddComponent<Systems_DustPuff>();
                }
                return _instance;
            }
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;

            _dust = BuildDust();
            _sweat = BuildSweat();
            _salt = BuildSalt();
            _haze = BuildHaze();
            _blood = BuildBlood();
        }

        private void OnApplicationQuit() => _quitting = true;

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }

        // ---------------------------------------------------------------- API

        /// Clay dust at an impact point. Count is the old parameter and still
        /// means "roughly how hard was this".
        public static void Burst(Vector3 position, int count = 14)
        {
            Systems_DustPuff instance = Instance;
            if (instance == null || instance._dust == null)
            {
                return;
            }
            instance._dust.Emit(MakeParams(position, 0.22f), count);
        }

        /// Sweat flung off a body that just took a hit. Brighter, faster and much
        /// shorter-lived than dust, so the two read as different materials.
        public static void SweatSpray(Vector3 position, Vector2 direction, int count = 6)
        {
            Systems_DustPuff instance = Instance;
            if (instance == null || instance._sweat == null)
            {
                return;
            }
            var emitParams = MakeParams(position, 0.06f);
            Vector2 unit = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector2.up;
            emitParams.velocity = new Vector3(unit.x, unit.y + 0.6f, 0f) * Random.Range(1.4f, 2.6f);
            emitParams.applyShapeToPosition = true;
            instance._sweat.Emit(emitParams, count);
        }

        /// The shio-maki: a handful of purifying salt thrown up over the ring
        /// before a bout. Pure ceremony, and the single most recognisable thing
        /// sumo does.
        public static void SaltThrow(Vector3 position, float facingSign, int count = 40)
        {
            Systems_DustPuff instance = Instance;
            if (instance == null || instance._salt == null)
            {
                return;
            }
            for (int countIndex = 0; countIndex < count; countIndex++)
            {
                var emitParams = MakeParams(position, 0.05f);
                emitParams.velocity = new Vector3(
                    facingSign * Random.Range(0.4f, 2.1f),
                    Random.Range(2.6f, 4.4f),
                    0f);
                emitParams.applyShapeToPosition = true;
                instance._salt.Emit(emitParams, 1);
            }
        }

        /// Blood from a head KO. Heavier and darker than sweat, thrown along the
        /// impact direction, and it falls fast — Systems_BodyDamage separately
        /// paints the lasting stains, because a particle cannot leave a mark.
        ///
        /// `speedScale` is what separates a spatter from a JET. An open artery is
        /// pressurised and the droplets leave in a tight, fast column; a spatter is
        /// slow and wide. At 1 the cone is the original 0.7-radian spatter at
        /// 1.5-4.5 m/s. Above 1 the droplets go faster AND the cone narrows in
        /// proportion, so a severed limb and a neck stump throw a readable arc
        /// across the mat instead of dribbling onto their own feet.
        public static void BloodSpray(Vector3 position, Vector2 direction, int count = 18,
                                      float speedScale = 1f)
        {
            Systems_DustPuff instance = Instance;
            if (instance == null || instance._blood == null)
            {
                return;
            }
            Vector2 unit = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector2.up;
            float speed = Mathf.Max(0.1f, speedScale);
            // Tighter cone the harder it is pumping, floored so even a big jet keeps
            // some spread and does not read as a single rigid line.
            float cone = Mathf.Max(0.18f, 0.7f / speed);
            for (int countIndex = 0; countIndex < count; countIndex++)
            {
                var emitParams = MakeParams(position, 0.05f);
                // Cone around the impact direction, biased upward so it arcs.
                float spread = Random.Range(-cone, cone);
                Vector2 dir = new Vector2(
                    unit.x + spread * unit.y,
                    unit.y - spread * unit.x + 0.5f).normalized;
                emitParams.velocity = dir * Random.Range(1.5f, 4.5f) * speed;
                emitParams.applyShapeToPosition = true;
                instance._blood.Emit(emitParams, 1);
            }
        }

        /// Position and size the standing haze to match the arena.
        public static void ConfigureHaze(Vector3 center, float halfWidth)
        {
            Systems_DustPuff instance = Instance;
            if (instance == null || instance._haze == null)
            {
                return;
            }
            instance._haze.transform.position = center;
            ParticleSystem.ShapeModule shape = instance._haze.shape;
            shape.scale = new Vector3(halfWidth * 2.4f, 1.6f, 1f);
        }

        private static ParticleSystem.EmitParams MakeParams(Vector3 position, float spread)
        {
            return new ParticleSystem.EmitParams
            {
                position = position + new Vector3(
                    Random.Range(-spread, spread),
                    Random.Range(0f, spread),
                    0f),
                applyShapeToPosition = true,
            };
        }

        // ------------------------------------------------------------- Build

        /// A soft radial puff. One 32x32 texture shared by all four systems, so
        /// they batch into a single draw call.
        private static Texture2D PuffTexture()
        {
            if (_puffTexture != null)
            {
                return _puffTexture;
            }
            const int S = 32;
            _puffTexture = new Texture2D(S, S, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "PoSumo_Puff",
            };
            var center = new Vector2(S * 0.5f, S * 0.5f);
            float radius = S * 0.5f;
            for (int rowIndex = 0; rowIndex < S; rowIndex++)
            {
                for (int columnIndex = 0; columnIndex < S; columnIndex++)
                {
                    float d = Vector2.Distance(new Vector2(columnIndex + 0.5f, rowIndex + 0.5f), center) / radius;
                    float a = Mathf.Clamp01(1f - d);
                    _puffTexture.SetPixel(columnIndex, rowIndex, new Color(1f, 1f, 1f, a * a));
                }
            }
            _puffTexture.Apply();
            return _puffTexture;
        }

        /// Sprites/Default, deliberately NOT "Universal Render Pipeline/Particles/
        /// Unlit". The URP particle shader defaults to an OPAQUE surface type, so
        /// every particle rendered as a hard cream rectangle with its soft alpha
        /// ignored — the dust looked like falling cardboard. Switching that shader
        /// to transparent at runtime means poking _Surface, _Blend, the render
        /// queue and three keywords and hoping they stay stable across URP
        /// versions. Sprites/Default is alpha-blended by definition, always
        /// present, and respects the per-particle vertex colour.
        private static Material ParticleMaterial()
        {
            var material = new Material(Shader.Find("Sprites/Default")) { name = "PoSumo_Particle" };
            material.mainTexture = PuffTexture();
            return material;
        }

        private ParticleSystem CreateSystem(string name, int maxParticles, int sortingOrder)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var system = go.AddComponent<ParticleSystem>();

            ParticleSystem.MainModule main = system.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = maxParticles;
            main.playOnAwake = false;
            main.loop = true;

            // Emission is driven by explicit Emit() calls, not a rate.
            ParticleSystem.EmissionModule emission = system.emission;
            emission.enabled = false;

            ParticleSystem.ShapeModule shape = system.shape;
            shape.enabled = false;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = ParticleMaterial();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sortingOrder = sortingOrder;
            renderer.alignment = ParticleSystemRenderSpace.View;

            system.Play();
            return system;
        }

        private ParticleSystem BuildDust()
        {
            ParticleSystem system = CreateSystem("Dust", 220, 5);
            ParticleSystem.MainModule main = system.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.65f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.16f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.6f, 2.0f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.gravityModifier = 0.28f;
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.82f, 0.74f, 0.58f, 0.34f),
                new Color(0.72f, 0.63f, 0.48f, 0.2f));

            // Dust expands and thins as it disperses — the readable difference
            // between a puff of clay and a spark.
            ParticleSystem.SizeOverLifetimeModule size = system.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, Curve(1f, 2.4f));

            ParticleSystem.ColorOverLifetimeModule colour = system.colorOverLifetime;
            colour.enabled = true;
            colour.color = new ParticleSystem.MinMaxGradient(FadeOut(0.15f));

            return system;
        }

        private ParticleSystem BuildSweat()
        {
            ParticleSystem system = CreateSystem("Sweat", 120, 6);
            ParticleSystem.MainModule main = system.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.014f, 0.032f);
            main.startSpeed = 0f;    // velocity comes from EmitParams
            main.gravityModifier = 1.6f;
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.95f, 0.97f, 1f, 0.75f),
                new Color(0.85f, 0.92f, 1f, 0.5f));

            ParticleSystem.ColorOverLifetimeModule colour = system.colorOverLifetime;
            colour.enabled = true;
            colour.color = new ParticleSystem.MinMaxGradient(FadeOut(0.5f));
            return system;
        }

        private ParticleSystem BuildSalt()
        {
            ParticleSystem system = CreateSystem("Salt", 160, 6);
            ParticleSystem.MainModule main = system.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.9f, 1.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.012f, 0.028f);
            main.startSpeed = 0f;
            main.gravityModifier = 1f;
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.99f, 0.95f, 0.95f),
                new Color(0.94f, 0.94f, 0.9f, 0.7f));

            ParticleSystem.ColorOverLifetimeModule colour = system.colorOverLifetime;
            colour.enabled = true;
            colour.color = new ParticleSystem.MinMaxGradient(FadeOut(0.65f));
            return system;
        }

        private ParticleSystem BuildBlood()
        {
            // 200 was sized for impact spatter, then 800 for a decapitation pouring
            // from two wounds at ~12 droplets per 45 ms each (~530/s, ~480 alive at
            // a 0.5-1.3 s lifetime). The neck geyser is now 30 per puff every 30 ms
            // from the stump plus the head's own wound, which is ~1,900/s and ~1,700
            // alive at the peak — and a severed limb can be bleeding underneath it.
            // A budget that is too small does not error, it silently drops emits and
            // the showpiece finish reads as a dribble, so this is sized for the
            // worst case rather than the common one.
            ParticleSystem system = CreateSystem("Blood", 5200, 7);
            ParticleSystem.MainModule main = system.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 1.3f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.055f);
            main.startSpeed = 0f;          // velocity comes from EmitParams
            main.gravityModifier = 2.2f;   // heavy: blood drops, it does not drift
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.55f, 0.03f, 0.03f, 1f),
                new Color(0.32f, 0.01f, 0.02f, 1f));

            // Almost no fade: droplets stay solid and simply fall out of frame,
            // which reads as liquid. Fading them out reads as smoke.
            ParticleSystem.ColorOverLifetimeModule colour = system.colorOverLifetime;
            colour.enabled = true;
            colour.color = new ParticleSystem.MinMaxGradient(FadeOut(0.8f));

            // Droplets now hit the mat instead of falling through it. lifetimeLoss
            // 1 kills the particle on contact, so each one lands exactly once and
            // hands its position to Systems_RingBlood, which stamps the lasting
            // stain — a particle cannot leave a mark by itself.
            ParticleSystem.CollisionModule collision = system.collision;
            collision.enabled = true;
            collision.type = ParticleSystemCollisionType.World;
            collision.mode = ParticleSystemCollisionMode.Collision2D;
            collision.sendCollisionMessages = true;
            collision.lifetimeLoss = 1f;
            collision.bounce = 0f;
            collision.dampen = 1f;
            system.gameObject.AddComponent<Systems_RingBlood>();
            return system;
        }

        private ParticleSystem BuildHaze()
        {
            ParticleSystem system = CreateSystem("Haze", 60, 4);
            ParticleSystem.MainModule main = system.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(4f, 8f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.3f, 0.75f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.02f, 0.12f);
            main.gravityModifier = -0.008f;   // motes drift upward through the key light
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.93f, 0.8f, 0.05f),
                new Color(1f, 0.88f, 0.72f, 0.025f));

            // The one system that emits continuously — a standing haze the key
            // light can pick out, rather than an event.
            ParticleSystem.EmissionModule emission = system.emission;
            emission.enabled = true;
            emission.rateOverTime = 7f;

            ParticleSystem.ShapeModule shape = system.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(7f, 1.6f, 1f);

            ParticleSystem.ColorOverLifetimeModule colour = system.colorOverLifetime;
            colour.enabled = true;
            colour.color = new ParticleSystem.MinMaxGradient(FadeInOut());
            return system;
        }

        private static AnimationCurve Curve(float from, float to) =>
            AnimationCurve.EaseInOut(0f, from, 1f, to);

        /// Full alpha until `hold`, then linearly out.
        private static Gradient FadeOut(float hold)
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, Mathf.Clamp01(hold)),
                    new GradientAlphaKey(0f, 1f),
                });
            return gradient;
        }

        private static Gradient FadeInOut()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.35f),
                    new GradientAlphaKey(0f, 1f),
                });
            return gradient;
        }
    }
}
