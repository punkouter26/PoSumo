using System.Collections.Generic;
using UnityEngine;

namespace PoSumo
{
    /// Lasting blood on the dohyo, stamped where the KO spray actually lands.
    ///
    /// Systems_BodyDamage marks the fighter; this marks the ring. A particle
    /// cannot leave a mark by itself, so the blood system is given world
    /// collision and told to send messages, and every droplet that hits the clay
    /// dies there and leaves a stain behind at the exact contact point. The
    /// pattern therefore follows the real arc of the spray — the direction of the
    /// blow decides where the ring gets dirty.
    ///
    /// Stains persist for the whole tournament, exactly like the bruises in
    /// Systems_BodyDamage: the store is static, survives the scene load between
    /// bracket matches, and is wiped by the same NEW TOURNAMENT reset. So the mat
    /// gets visibly bloodier as a bracket runs.
    ///
    /// Lives on the blood ParticleSystem's own GameObject, because that is where
    /// Unity delivers OnParticleCollision.
    [RequireComponent(typeof(ParticleSystem))]
    public sealed class Systems_RingBlood : MonoBehaviour
    {
        [Tooltip("Hard cap on stains. Oldest are recycled past this, so a long bracket cannot grow the renderer count without bound.\n\n300, up from 140: a decapitation alone throws well over a hundred droplets that land, and at 140 the pour recycled its own stains mid-flow — the mat never actually looked soaked.")]
        public int maxStains = 300;
        [Tooltip("Smallest stain, in metres.")]
        public float minSize = 0.06f;
        [Tooltip("Largest stain, in metres.")]
        public float maxSize = 0.19f;
        [Tooltip("Stains below this world Y relative to the arena are discarded — droplets that missed the dohyo and landed out in the crowd floor.")]
        public float minLocalY = -1.2f;

        /// One stain, stored in the ARENA's local space so it lands in the same
        /// place on the mat whichever arena scene the next match loads.
        private struct Stain
        {
            public float x, y, size, alpha, rotation;
        }

        private static readonly List<Stain> Store = new List<Stain>();

        /// Enter Play Mode domain reload is disabled here, so a static store
        /// survives Stop -> Play and the ring would open the next session already
        /// bloody. Same treatment as Systems_TournamentState and the bruise store.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ClearOnPlaySessionStart() => Store.Clear();

        /// Wipe the ring. Called by the tournament reset alongside
        /// Systems_BodyDamage.ClearAll so bodies and mat come clean together.
        public static void ClearAll() => Store.Clear();

        private static Sprite _stainSprite;

        private readonly List<GameObject> _rendered = new List<GameObject>();
        private readonly List<ParticleCollisionEvent> _events = new List<ParticleCollisionEvent>();
        private ParticleSystem _system;
        private Transform _arena;

        private void Start()
        {
            _system = GetComponent<ParticleSystem>();
            var arena = FindAnyObjectByType<Systems_SumoArena>();
            if (arena == null)
            {
                enabled = false;
                return;
            }
            _arena = arena.transform;

            // Replay everything spilled earlier in this tournament onto the new
            // arena, the same way Systems_BodyDamage replays a fighter's bruises.
            for (int storeIndex = 0; storeIndex < Store.Count; storeIndex++)
            {
                Paint(Store[storeIndex]);
            }
        }

        private void OnParticleCollision(GameObject other)
        {
            if (_arena == null || _system == null)
            {
                return;
            }
            int count = _system.GetCollisionEvents(other, _events);

            // A droplet that hits a FIGHTER stains the fighter, not the ring.
            //
            // These marks used to be handled here like any other collision, which
            // parented them to the arena at whatever height the limb happened to
            // be — so the spot stayed hanging in the air the moment the fighter
            // moved on. Handing them to Systems_BodyDamage instead puts them in
            // the struck part's own local space, so the blood rides the arm it
            // landed on and survives the next round's respawn like every other
            // mark that fighter carries.
            if (!other.transform.IsChildOf(_arena))
            {
                var struck = other.GetComponentInParent<Agent_BipedBody>();
                Systems_BodyDamage damage = Systems_BodyDamage.For(struck);
                if (damage == null)
                {
                    return;
                }
                var hitPart = other.GetComponentInParent<Rigidbody2D>();
                for (int countIndex = 0; countIndex < count; countIndex++)
                {
                    damage.SplashAt(_events[countIndex].intersection, hitPart);
                }
                return;
            }

            for (int countIndex = 0; countIndex < count; countIndex++)
            {
                Vector3 local = _arena.InverseTransformPoint(_events[countIndex].intersection);
                if (local.y < minLocalY)
                {
                    continue;   // missed the dohyo entirely
                }

                var stain = new Stain
                {
                    x = local.x,
                    y = local.y,
                    size = Random.Range(minSize, maxSize),
                    alpha = Random.Range(0.55f, 0.9f),
                    rotation = Random.Range(0f, 360f),
                };
                Store.Add(stain);
                if (Store.Count > maxStains)
                {
                    Store.RemoveAt(0);
                }
                Paint(stain);
            }
        }

        private void Paint(Stain stain)
        {
            while (_rendered.Count >= maxStains && _rendered.Count > 0)
            {
                GameObject oldest = _rendered[0];
                _rendered.RemoveAt(0);
                if (oldest != null)
                {
                    Destroy(oldest);
                }
            }

            var go = new GameObject("RingBlood");
            // Parented to the arena, NOT to this particle system: the stain
            // belongs to the mat and must not move or die with the emitter.
            go.transform.SetParent(_arena, false);
            go.transform.localPosition = new Vector3(stain.x, stain.y, -0.02f);
            go.transform.localRotation = Quaternion.Euler(0f, 0f, stain.rotation);
            go.transform.localScale = new Vector3(stain.size, stain.size * 0.55f, 1f);

            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = StainSprite();
            // Just above the mat surface but below the fighters, so blood reads as
            // being ON the clay rather than painted over the wrestlers' feet.
            renderer.sortingOrder = -1;
            renderer.color = new Color(0.36f, 0.02f, 0.03f, stain.alpha);
            _rendered.Add(go);
        }

        /// A splat: a soft core with a few thrown fingers, so a run of them reads
        /// as spatter rather than as a row of identical dots.
        private static Sprite StainSprite()
        {
            if (_stainSprite != null)
            {
                return _stainSprite;
            }
            const int S = 64;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "PoSumo_RingBlood",
            };
            var centre = new Vector2(S * 0.5f, S * 0.5f);
            float radius = S * 0.5f;
            for (int rowIndex = 0; rowIndex < S; rowIndex++)
            {
                for (int columnIndex = 0; columnIndex < S; columnIndex++)
                {
                    Vector2 d = new Vector2(columnIndex + 0.5f, rowIndex + 0.5f) - centre;
                    float angle = Mathf.Atan2(d.y, d.x);
                    // Irregular edge: three overlapping lobes at different rates.
                    float wobble = 1f
                                   + 0.22f * Mathf.Sin(angle * 3f)
                                   + 0.13f * Mathf.Sin(angle * 7f + 2.1f)
                                   + 0.08f * Mathf.Sin(angle * 11f + 0.7f);
                    float dist = d.magnitude / (radius * wobble);
                    float a = Mathf.Clamp01(1f - dist);
                    tex.SetPixel(columnIndex, rowIndex, new Color(1f, 1f, 1f, a * a));
                }
            }
            tex.Apply();
            _stainSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S,
                                         0, SpriteMeshType.FullRect);
            return _stainSprite;
        }
    }
}
