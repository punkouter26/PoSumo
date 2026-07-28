using System.Collections.Generic;
using UnityEngine;

namespace PoSumo
{
    /// Visible damage: darkening bruise decals stamped where a fighter actually
    /// got hit, and a bloody head KO.
    ///
    /// A decal is a small soft dark sprite parented to the part that took the hit,
    /// so it travels and rotates with that limb for the rest of the tournament.
    /// Opacity scales with impact force, and repeated hits to the same area stack
    /// into a genuinely dark patch.
    ///
    /// The KO is PRESENTATION ONLY — it does not end the round. Every shipped brain
    /// was trained by Systems_SumoMatchManager, which has no head rule at all, so
    /// making a head hit a losing condition here would decide matches on a skill no
    /// policy was ever taught (and would re-open the game-vs-training referee
    /// divergence this project has been bitten by before). Instead the fighter goes
    /// limp and bleeds, which in practice means it gets shoved out moments later —
    /// dramatic, and still won by the rule the brains actually know.
    ///
    /// Damage persists across the whole tournament via a static store keyed by
    /// behaviour name; it survives the scene load between bracket matches the same
    /// way Systems_TournamentState does.
    ///
    /// Spawned at runtime by Systems_GameMatchManager, one per fighter.
    public sealed class Systems_BodyDamage : MonoBehaviour
    {
        public Agent_BipedBody body;

        [Header("Bruises")]
        [Tooltip("Impact speed (m/s) below which a contact leaves no mark.")]
        public float minSpeed = 3f;
        [Tooltip("Impact speed treated as a maximum-strength blow.")]
        public float maxSpeed = 9f;
        [Tooltip("Darkness a single maximum-strength hit adds.")]
        [Range(0f, 1f)] public float maxOpacityPerHit = 0.6f;
        [Tooltip("Darkness of the WEAKEST qualifying hit. Must be above zero: most real contacts land just over minSpeed, so a straight strength*max mapping made almost every genuine bruise invisible.")]
        [Range(0f, 1f)] public float minOpacityPerHit = 0.2f;
        [Tooltip("World size of a maximum-strength bruise, in metres.")]
        public float maxSize = 0.16f;
        [Tooltip("Radius in metres around the head centre that head decals are kept within. Loosened now that the SpriteMask hard-clips to the face silhouette: the clamp only has to keep the blob's CENTRE on the head, and the mask trims whatever hangs over the jaw or the hairline. A tighter value bunched every mark in the middle of the face.")]
        public float headDecalRadius = 0.18f;
        [Tooltip("Hard cap on decals per fighter. Oldest are recycled past this.")]
        public int maxDecals = 40;
        [Tooltip("Minimum gap between marks so one scrum does not paint a fighter black.")]
        public float cooldown = 0.18f;

        [Header("Head KO")]
        [Tooltip("Impact speed against the HEAD that triggers the KO. Deliberately high — this should be a moment, not a regular event.")]
        public float koSpeed = 7.5f;
        [Tooltip("Seconds the KO'd fighter stays limp. It will usually be pushed out during this.")]
        public float koLimpSeconds = 2.5f;
        [Tooltip("Speed (m/s) the KO'd body is driven backwards by the blow that landed it. Applied as a uniform velocity change across all 14 parts, so the ragdoll travels as one piece instead of the struck head whipping away from the torso.")]
        public float koKnockbackSpeed = 4.5f;
        [Tooltip("Share of the knockback aimed upward. Some lift gets the body off the clay so it is carried toward the edge rather than skidding on friction; all lift would launch it straight up.")]
        [Range(0f, 1f)] public float koKnockbackLift = 0.35f;
        [Tooltip("Minimum gap between blood spots thrown onto this fighter by the other's spray. A KO throws 26 droplets at once, so without a gap one burst would spend the whole decal budget in a single frame.")]
        public float splashCooldown = 0.04f;

        /// Fired when a fighter is knocked out by a head blow. Presentation only —
        /// no referee listens to this.
        public static event System.Action<Agent_BipedBody, Vector3> Knockout;

        private struct Mark
        {
            public int partIndex;      // -1 = head
            public Vector2 localPoint; // in that renderer's local space
            public float strength;     // 0..1
            public bool blood;
        }

        // Tournament-persistent damage, keyed by behaviour name. Enter Play Mode
        // domain reload is disabled in this project, so this needs the same
        // SubsystemRegistration clear as Systems_TournamentState or a new session
        // starts with the previous one's bruises.
        private static readonly Dictionary<string, List<Mark>> Store =
            new Dictionary<string, List<Mark>>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ClearOnPlaySessionStart() => Store.Clear();

        /// Wipe all accumulated damage. Called when a tournament is reset so a new
        /// bracket starts on clean bodies.
        public static void ClearAll() => Store.Clear();

        /// Live damage components, keyed by the body they mark. Systems_RingBlood
        /// needs to go from "a droplet hit this collider" to "paint that fighter",
        /// and the component is spawned under the match manager rather than under
        /// the fighter, so GetComponentInParent cannot find it.
        private static readonly Dictionary<Agent_BipedBody, Systems_BodyDamage> Active =
            new Dictionary<Agent_BipedBody, Systems_BodyDamage>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ClearRegistryOnPlaySessionStart() => Active.Clear();

        public static Systems_BodyDamage For(Agent_BipedBody target)
        {
            if (target == null) return null;
            Active.TryGetValue(target, out Systems_BodyDamage found);
            return found;
        }

        /// Paint a blood spot where a flying droplet actually struck this fighter.
        ///
        /// Called by Systems_RingBlood when a KO spray lands on a BODY instead of
        /// on the clay. The mark goes onto the part that was hit and is stored in
        /// that part's local space, so it rides the limb from then on — which is
        /// the whole difference between blood on a shoulder and a red dot hanging
        /// in the air where the shoulder used to be.
        public void SplashAt(Vector3 worldPoint, Rigidbody2D hitPart)
        {
            if (body == null || _marks == null || Time.time < _nextSplashTime)
            {
                return;
            }
            _nextSplashTime = Time.time + splashCooldown;
            // Droplet spots are small: a full-strength mark is the KO wound
            // itself, not the spatter thrown off it.
            AddMark(worldPoint, SplashPartIndex(worldPoint, hitPart),
                    Random.Range(0.1f, 0.35f), blood: true);
        }

        /// Which part a droplet landed on. The head is a compound collider on the
        /// CHEST rigidbody, so a rigidbody lookup alone would paint a face hit
        /// onto the torso — proximity to the head anchor is the only thing that
        /// separates them, exactly as OnImpact has to compare against HeadCollider.
        private int SplashPartIndex(Vector3 worldPoint, Rigidbody2D hitPart)
        {
            if (body.HeadDecalAnchor != null &&
                (worldPoint - body.HeadDecalAnchor.position).sqrMagnitude < 0.0625f)   // 0.25 m
            {
                return -1;
            }
            if (hitPart != null && body.Parts != null)
            {
                for (int i = 0; i < body.Parts.Length; i++)
                {
                    if (body.Parts[i] == hitPart) return i;
                }
            }
            return -1;
        }

        private static Sprite _bruiseSprite;

        private readonly List<GameObject> _decals = new List<GameObject>();
        private List<Mark> _marks;
        private float _nextMarkTime;
        private float _nextSplashTime;
        private float _koUntil;
        private bool _knockedOut;

        private void Start()
        {
            if (body == null)
            {
                body = GetComponentInParent<Agent_BipedBody>();
            }
            var agent = body != null ? body.GetComponent<Agent_Biped>() : null;
            if (body == null || agent == null || string.IsNullOrEmpty(agent.behaviorName))
            {
                enabled = false;
                return;
            }

            // NOTE: keyed by behaviour name, so the two bracket slots seeded with
            // the SAME character share one damage record. Per-slot tracking would
            // need a stable entrant id the arena scene does not currently carry.
            if (!Store.TryGetValue(agent.behaviorName, out _marks))
            {
                _marks = new List<Mark>();
                Store[agent.behaviorName] = _marks;
            }
            Active[body] = this;

            // Replay everything this fighter has taken so far this tournament.
            for (int i = 0; i < _marks.Count; i++)
            {
                Paint(_marks[i]);
            }
        }

        private void OnEnable() => Sensor_Impact.AnyImpact += OnImpact;

        private void OnDisable()
        {
            // Static event: a missed unsubscribe keeps a dead scene's fighter alive.
            Sensor_Impact.AnyImpact -= OnImpact;
            if (body != null && Active.TryGetValue(body, out Systems_BodyDamage registered)
                && registered == this)
            {
                Active.Remove(body);
            }
        }

        private void Update()
        {
            if (_knockedOut && Time.time >= _koUntil)
            {
                _knockedOut = false;
                // Hand the body back to its brain. If the round already ended, the
                // referee's own reset takes over and this is harmless.
                if (body != null)
                {
                    body.RestoreMotors();
                }
                var agent = body != null ? body.GetComponent<Agent_Biped>() : null;
                if (agent != null)
                {
                    agent.actionsEnabled = true;
                }
            }
        }

        private void OnImpact(Sensor_Impact sensor, Collision2D collision)
        {
            if (body == null || sensor == null || sensor.owner != body)
            {
                return;   // only damage THIS fighter
            }
            // Struck by the opponent, not by the clay.
            var other = collision.collider.GetComponentInParent<Agent_BipedBody>();
            if (other == null || other == body)
            {
                return;
            }

            float speed = collision.relativeVelocity.magnitude;
            if (speed < minSpeed || collision.contactCount == 0)
            {
                return;
            }

            ContactPoint2D contact = collision.GetContact(0);
            // The head is a compound collider on the CHEST rigidbody, so a head hit
            // arrives here as a Chest collision — the only way to tell is to compare
            // the struck collider against the head's own.
            bool headHit = body.HeadCollider != null && contact.otherCollider == body.HeadCollider;

            if (headHit && speed >= koSpeed && !_knockedOut)
            {
                Knockout?.Invoke(body, contact.point);
                DoKnockout(contact.point, collision.relativeVelocity);
                return;   // the KO paints its own, much heavier marks
            }

            if (Time.time < _nextMarkTime)
            {
                return;
            }
            _nextMarkTime = Time.time + cooldown;

            float strength = Mathf.Clamp01((speed - minSpeed) / Mathf.Max(0.01f, maxSpeed - minSpeed));
            AddMark(contact.point, headHit ? -1 : PartIndexOf(sensor.transform), strength, blood: false);
        }

        private void DoKnockout(Vector3 point, Vector2 impactVelocity)
        {
            _knockedOut = true;
            _koUntil = Time.time + koLimpSeconds;

            // Limp, not dead: the fighter flops and will almost certainly be pushed
            // out, but the referee decides the round exactly as it always did.
            var agent = body.GetComponent<Agent_Biped>();
            if (agent != null)
            {
                agent.actionsEnabled = false;
            }
            body.GoLimp();
            ApplyKnockback(impactVelocity);

            Systems_DustPuff.BloodSpray(point, -impactVelocity, 26);

            // Stains: a heavy dark mark on the head plus spatter around it. These
            // are permanent for the tournament — the particles fall away, the
            // staining is what makes a KO still visible three matches later.
            AddMark(point, -1, 1f, blood: true);
            for (int i = 0; i < 4; i++)
            {
                Vector3 offset = point + (Vector3)(Random.insideUnitCircle * 0.12f);
                AddMark(offset, -1, Random.Range(0.4f, 0.8f), blood: true);
            }
        }

        /// Drives the whole limp body backwards along the line of the blow.
        ///
        /// Without this a knockout only removes the fighter's motors, so a clean
        /// head shot dropped him more or less where he stood and the moment read
        /// as a stumble. Sending him travelling sells the hit and — since he is
        /// limp and cannot recover for koLimpSeconds — often carries him toward
        /// the edge, which is what actually decides the round.
        ///
        /// The impulse is scaled by each part's own mass, so every part gains the
        /// SAME velocity and the ragdoll translates as a unit. A flat impulse
        /// would move the 0.9 kg foot five times faster than the 13 kg chest and
        /// tear the body into a starfish.
        private void ApplyKnockback(Vector2 impactVelocity)
        {
            if (body.Parts == null || koKnockbackSpeed <= 0f)
            {
                return;
            }

            // Collision2D.relativeVelocity points against the blow, so its
            // negation is the direction the blow was travelling — the way the
            // head was driven. Only its sign is used: the magnitude is whatever
            // the scrum happened to produce, and a KO should always land with the
            // same weight. Facing is the fallback for a dead-on vertical hit,
            // since a fighter always faces its opponent.
            float blowX = -impactVelocity.x;
            float backwards = Mathf.Abs(blowX) > 0.01f ? Mathf.Sign(blowX) : -body.facingSign;
            Vector2 direction = new Vector2(backwards, koKnockbackLift).normalized;

            for (int partIndex = 0; partIndex < body.Parts.Length; partIndex++)
            {
                Rigidbody2D part = body.Parts[partIndex];
                if (part == null)
                {
                    continue;
                }
                part.AddForce(direction * (koKnockbackSpeed * part.mass), ForceMode2D.Impulse);
            }
        }

        /// PART_DEFS index of the transform that reported the hit, or -1.
        private int PartIndexOf(Transform partTransform)
        {
            if (body.Parts == null)
            {
                return -1;
            }
            for (int i = 0; i < body.Parts.Length; i++)
            {
                if (body.Parts[i] != null && body.Parts[i].transform == partTransform)
                {
                    return i;
                }
            }
            return -1;
        }

        private void AddMark(Vector3 worldPoint, int partIndex, float strength, bool blood)
        {
            SpriteRenderer host = RendererFor(partIndex);
            if (host == null)
            {
                return;
            }
            var mark = new Mark
            {
                partIndex = partIndex,
                // Stored in the host's LOCAL space so it survives the fighter being
                // respawned somewhere else next round, and CLAMPED to the drawn
                // sprite so a mark can never hang in the air beside the art.
                //
                // This matters most on the head: its hitbox is a 0.5 m circle while
                // the face photo draws only ~0.39 m wide, so a genuine head contact
                // routinely lands outside the visible face and the blood appeared to
                // float in front of it. Clamping paints onto the texture instead of
                // at the raw physics point.
                localPoint = LocalPointFor(partIndex, host, worldPoint),
                strength = Mathf.Clamp01(strength),
                blood = blood,
            };
            _marks.Add(mark);
            if (_marks.Count > maxDecals)
            {
                _marks.RemoveAt(0);
            }
            Paint(mark);
        }

        /// Convert a world hit into the space the decal will be parented in.
        ///
        /// Head marks go on Agent_BipedBody.HeadDecalAnchor, whose world scale is 1,
        /// so its local units are METRES and the clamp is a plain radius. Limb and
        /// torso marks go on the part's art transform, whose units are the sprite's,
        /// so those clamp against the sprite rect instead.
        private Vector2 LocalPointFor(int partIndex, SpriteRenderer host, Vector3 worldPoint)
        {
            Transform anchor = AnchorFor(partIndex, host);
            Vector2 local = anchor.InverseTransformPoint(worldPoint);
            if (partIndex < 0)
            {
                // Keep it on the face: the head hitbox is a good deal wider than the
                // drawn photo, so raw contacts land beside it.
                return Vector2.ClampMagnitude(local, headDecalRadius);
            }
            return ClampToSprite(host, local);
        }

        /// Where a decal for this part gets parented.
        private Transform AnchorFor(int partIndex, SpriteRenderer host)
        {
            if (partIndex < 0 && body.HeadDecalAnchor != null)
            {
                return body.HeadDecalAnchor;
            }
            return host.transform;
        }

        /// Pull a local-space point inside the host sprite's own rect.
        ///
        /// `inset` keeps the blob's CENTRE away from the very edge so the decal sits
        /// on the art rather than half-hanging off it. Sprite bounds are in the
        /// sprite's own units before the transform's scale, which is exactly the
        /// space localPosition is expressed in, so no scale conversion is needed.
        private static Vector2 ClampToSprite(SpriteRenderer host, Vector2 local)
        {
            if (host.sprite == null)
            {
                return local;
            }
            const float INSET = 0.72f;
            Vector3 extents = host.sprite.bounds.extents;
            return new Vector2(
                Mathf.Clamp(local.x, -extents.x * INSET, extents.x * INSET),
                Mathf.Clamp(local.y, -extents.y * INSET, extents.y * INSET));
        }

        private SpriteRenderer RendererFor(int partIndex)
        {
            if (partIndex < 0)
            {
                return body.HeadRenderer;
            }
            if (body.ArtRenderers == null || partIndex >= body.ArtRenderers.Length)
            {
                return null;
            }
            return body.ArtRenderers[partIndex];
        }

        private void Paint(Mark mark)
        {
            SpriteRenderer host = RendererFor(mark.partIndex);
            if (host == null)
            {
                return;
            }

            // Recycle rather than grow without bound — a long tournament would
            // otherwise leave hundreds of renderers on a body.
            while (_decals.Count >= maxDecals && _decals.Count > 0)
            {
                GameObject oldest = _decals[0];
                _decals.RemoveAt(0);
                if (oldest != null)
                {
                    Destroy(oldest);
                }
            }

            Transform anchor = AnchorFor(mark.partIndex, host);
            var go = new GameObject(mark.blood ? "Blood" : "Bruise");
            go.transform.SetParent(anchor, false);
            go.transform.localPosition = new Vector3(mark.localPoint.x, mark.localPoint.y, 0f);
            go.transform.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));

            // Body parts carry extreme non-uniform scale (a shin is 0.11 x 0.38), so
            // a uniform local scale would smear the decal into a streak. Divide the
            // wanted world size back out through the host's own scale to keep it round.
            float size = Mathf.Lerp(maxSize * 0.45f, maxSize, mark.strength);
            Vector3 lossy = anchor.lossyScale;
            go.transform.localScale = new Vector3(
                Mathf.Approximately(lossy.x, 0f) ? size : size / Mathf.Abs(lossy.x),
                Mathf.Approximately(lossy.y, 0f) ? size : size / Mathf.Abs(lossy.y),
                1f);

            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = BruiseSprite();
            renderer.sharedMaterial = host.sharedMaterial;
            renderer.sortingOrder = host.sortingOrder + 1;

            // Head decals are clipped to the face's own alpha silhouette. The
            // face photo is a cut-out with transparent margins and its hitbox is
            // wider than the drawing, so an unclipped blob landing near the edge
            // rendered over empty space and read as blood floating beside the
            // head. Clamping the position helped but could not fix it: the head
            // is not a circle, and the clamp radius that keeps a blob inside the
            // jaw also keeps it off the cheekbones.
            if (mark.partIndex < 0 && body.HeadMask != null)
            {
                renderer.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;
            }
            renderer.color = mark.blood
                // Blood: dark red, nearly opaque, and it stays.
                ? new Color(0.42f, 0.02f, 0.03f, Mathf.Lerp(0.5f, 0.92f, mark.strength))
                // Bruise: pure darkening, no hue of its own, so it works on every
                // fighter colour and on the photographic faces alike.
                : new Color(0.05f, 0.02f, 0.05f,
                            Mathf.Lerp(minOpacityPerHit, maxOpacityPerHit, mark.strength));

            _decals.Add(go);
        }

        /// Soft radial blob with a slightly bitten edge — a clean circle reads as a
        /// sticker rather than a mark under the skin.
        private static Sprite BruiseSprite()
        {
            if (_bruiseSprite != null)
            {
                return _bruiseSprite;
            }
            const int S = 64;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "PoSumo_Bruise",
            };
            var centre = new Vector2(S * 0.5f, S * 0.5f);
            float radius = S * 0.5f;
            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    Vector2 d = p - centre;
                    float angle = Mathf.Atan2(d.y, d.x);
                    // Wobble the radius so the outline is irregular.
                    float wobble = 1f + 0.16f * Mathf.Sin(angle * 3f) + 0.1f * Mathf.Sin(angle * 5f + 1.3f);
                    float dist = d.magnitude / (radius * wobble);
                    float a = Mathf.Clamp01(1f - dist);
                    // Squared falloff: dense core, soft edge.
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
                }
            }
            tex.Apply();
            _bruiseSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S,
                                          0, SpriteMeshType.FullRect);
            return _bruiseSprite;
        }
    }
}
