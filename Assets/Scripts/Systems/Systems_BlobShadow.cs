using UnityEngine;

namespace PoSumo
{
    /// Contact occlusion under a wrestler: a broad soft pool under the torso plus
    /// a tight dark patch under each foot.
    ///
    /// **This is the ONLY grounding cue the game has, and that is not a temporary
    /// state.** The comment here used to say the key light "now casts real 2D
    /// shadows" and that this was merely the ambient darkening beneath them —
    /// that has been false since cast shadows were switched off. They are off for
    /// a geometric reason that no amount of tuning fixes: at gameplay framing the
    /// only clay in shot is the dohyo's front face seen edge-on, so there is no
    /// horizontal surface for a projected shadow to land on. Enabling them costs
    /// a shadow render texture per frame, on an Android target, and renders
    /// nothing. So this class is not a stand-in for a shadow; it is the effect.
    ///
    /// The important behaviour is contact hardening: a patch is small, dark and
    /// sharp where a foot is actually pressing on clay, and spreads out and fades
    /// as the body lifts away. That single cue is what tells the eye a fighter is
    /// planted or airborne. Since 2026-09-05 it is driven by measured foot LOAD as
    /// well as height — see `footLoadWeight`.
    ///
    /// Spawned by Agent_BipedBody at build time.
    public sealed class Systems_BlobShadow : MonoBehaviour
    {
        public Rigidbody2D target;                 // pelvis rigidbody
        [Tooltip("Optional. When set, each foot also gets its own contact patch.")]
        public Agent_BipedBody body;
        [Tooltip("Fallback dohyo half-extent, used only when no Systems_SumoArena is present.")]
        public float platformHalfWidth = 2.75f;    // dohyo top extent from arena center
        public float platformY = 0f;               // dohyo top surface
        public float floorY = -0.6f;               // crowd-floor surface (fall-off landings)
        public float arenaCenterX = 0f;
        public float baseWidth = 0.9f;
        public float torsoStandHeight = 0.97f;     // torso height above the ground when standing

        [Tooltip("Opacity of the broad torso pool. Deliberately low now that real shadows do the shape work.")]
        [Range(0f, 1f)] public float torsoOpacity = 0.22f;
        [Tooltip("Opacity of a foot patch at hard contact.")]
        [Range(0f, 1f)] public float footOpacity = 0.4f;
        [Tooltip("Height (m) at which a foot patch has fully faded out.")]
        public float footFadeHeight = 0.45f;

        [Tooltip("How much of the foot patch is driven by measured contact LOAD rather than by height alone. 0 reproduces the height-only behaviour exactly.\n\nHeight says a foot is NEAR the clay; load says it is PRESSING ON it. They differ in the moment that matters most — a fighter braced against a shove is barely moving, at a constant height, and driving through his feet hard enough to shift 70 kg. Reading only height renders that identically to standing around.")]
        [Range(0f, 1f)] public float footLoadWeight = 0.65f;
        [Tooltip("Foot load (as reported by Agent_BipedBody.FootLoadNear/Far) treated as fully planted. Load is normalised against body weight there, so 1 is roughly a fighter's whole mass through one foot.")]
        public float footLoadForFull = 1f;

        private SpriteRenderer _renderer;
        private SpriteRenderer[] _footRenderers;
        private Rigidbody2D[] _feet;
        private Systems_SumoArena _arena;
        private static Sprite _softDisc;

        private static Sprite SoftDisc()
        {
            if (_softDisc != null) return _softDisc;
            const int S = 128;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            float r = S / 2f - 1f;
            for (int rowIndex = 0; rowIndex < S; rowIndex++)
            {
                for (int columnIndex = 0; columnIndex < S; columnIndex++)
                {
                    float d = Vector2.Distance(new Vector2(columnIndex + 0.5f, rowIndex + 0.5f), new Vector2(S / 2f, S / 2f));
                    float a = Mathf.Clamp01(1f - d / r);
                    tex.SetPixel(columnIndex, rowIndex, new Color(1f, 1f, 1f, a * a));
                }
            }
            tex.Apply();
            _softDisc = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S,
                                      0, SpriteMeshType.FullRect);
            return _softDisc;
        }

        private void Awake()
        {
            _renderer = gameObject.AddComponent<SpriteRenderer>();
            _renderer.sprite = SoftDisc();
            _renderer.sortingOrder = -1; // above the clay, under every body part
        }

        private void Start()
        {
            // The mat geometry is read from the arena, not from the fields below.
            // Agent_BipedBody builds this shadow and sets only target/baseWidth/
            // body, so platformHalfWidth kept its 2.75 default — the pre-realism
            // ring — while the mat is 4.0. Past x = +/-2.75 every patch dropped to
            // floorY and drew 0.6 m BELOW the clay, and since walkInStartGapHalf
            // is 3 that was both fighters for the whole ceremonial opening.
            _arena = FindAnyObjectByType<Systems_SumoArena>();
            if (_arena != null)
            {
                Vector3 arenaPosition = _arena.transform.position;
                arenaCenterX = arenaPosition.x;
                platformY = arenaPosition.y;
                floorY = platformY - _arena.platformDrop;
            }

            if (body == null)
            {
                return;
            }
            _feet = new[] { body.FootNear, body.FootFar };
            _footRenderers = new SpriteRenderer[_feet.Length];
            for (int footIndex = 0; footIndex < _feet.Length; footIndex++)
            {
                var go = new GameObject($"FootContact{footIndex}");
                go.transform.SetParent(transform.parent, false);
                var renderer = go.AddComponent<SpriteRenderer>();
                renderer.sprite = SoftDisc();
                // Above the torso pool so a planted foot still reads against it.
                renderer.sortingOrder = -1;
                _footRenderers[footIndex] = renderer;
            }
        }

        /// Read live, so the patches follow the mat as the walk-in widens it and
        /// the engage contracts it back to the fighting ring.
        private float GroundAt(float x)
        {
            float half = _arena != null ? _arena.CurrentHalfWidth : platformHalfWidth;
            return Mathf.Abs(x - arenaCenterX) <= half ? platformY : floorY;
        }

        private void LateUpdate()
        {
            if (target == null) return;

            Vector2 tp = target.position;
            float groundY = GroundAt(tp.x);

            // 1 when the feet are planted, fading to 0 as the body lifts away.
            float lift = Mathf.Clamp(tp.y - torsoStandHeight - groundY, 0f, 1.5f);
            float presence = 1f - lift / 1.5f;

            transform.position = new Vector3(tp.x, groundY + 0.02f, 0f);
            // Contact hardening, torso scale: tight when planted, spreading as it
            // rises — the opposite of the old behaviour, which shrank on lift and
            // read as the body being sucked into the floor.
            float w = baseWidth * (1f + 0.55f * lift);
            transform.localScale = new Vector3(w, w * 0.2f, 1f);
            _renderer.color = new Color(0f, 0f, 0f, torsoOpacity * presence * presence);

            UpdateFeet();
        }

        private void UpdateFeet()
        {
            if (_footRenderers == null)
            {
                return;
            }
            for (int footIndex = 0; footIndex < _feet.Length; footIndex++)
            {
                Rigidbody2D foot = _feet[footIndex];
                SpriteRenderer renderer = _footRenderers[footIndex];
                if (foot == null || renderer == null)
                {
                    continue;
                }
                Vector2 position = foot.position;
                float ground = GroundAt(position.x);
                float height = Mathf.Clamp(position.y - ground, 0f, footFadeHeight);
                float contact = 1f - height / footFadeHeight;

                // Blend in MEASURED load. Agent_BipedBody samples FootLoadNear /
                // FootLoadFar every physics step for the contact observations, so
                // this is a free read of a number the body already computes — no
                // second query, no extra collision callback.
                //
                // Load is gated by height rather than replacing it, so a foot that
                // is off the clay cannot draw a patch no matter what its load
                // reads. That direction matters: a stale or spiking load on an
                // airborne foot would paint a shadow under nothing, which is far
                // more visible than a slightly soft patch under a planted one.
                if (body != null && footLoadWeight > 0f)
                {
                    float load = Mathf.Clamp01(
                        (footIndex == 0 ? body.FootLoadNear : body.FootLoadFar)
                        / Mathf.Max(0.01f, footLoadForFull));
                    contact *= Mathf.Lerp(1f, load, footLoadWeight);
                }

                renderer.transform.position = new Vector3(position.x, ground + 0.015f, 0f);
                // The real contact-hardening cue: a sharp 0.26 m patch on the clay
                // that balloons to 0.6 m and vanishes by the top of a step.
                float width = Mathf.Lerp(0.6f, 0.26f, contact);
                renderer.transform.localScale = new Vector3(width, width * 0.28f, 1f);
                renderer.color = new Color(0f, 0f, 0f, footOpacity * contact * contact);
            }
        }
    }
}
