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

        [Header("Region damage / dismemberment")]
        [Tooltip("Summed mark strength at which a region reads fully RED on the HUD mannequin. Marks are 0..1 each and cooldown-limited, so this is roughly 'a handful of solid hits to the same limb'.")]
        public float regionRedAt = 2.5f;
        [Tooltip("Multiple of regionRedAt past which an ARM or LEG tears off. Above 1 on purpose: red must be a WARNING the player can act on, not the same instant as losing the limb.\n\nRaised from 1.6 to 8 — limbs are ~5x harder to remove. At 1.6 both fighters were routinely dismembered inside the first 15 seconds of a round, which is also what made them permanently 'down'.")]
        public float detachAtRedMultiple = 8f;
        [Tooltip("Same, for the HEAD. Deliberately far LOWER than the limb figure so decapitation is the spectacular, relatively common finish while arms and legs mostly stay on. 0.8 = a gate of 2.0 damage; at 1.2 (gate 3.0) a measured bout reached the low 2s and never crossed.")]
        public float headDetachAtRedMultiple = 0.8f;
        [Tooltip("Master switch for dismemberment. Turning this off leaves the mannequin colouring intact, which is the safe fallback if detachment ever proves too swingy.")]
        public bool allowDetach = true;

        [Header("Dismemberment bleeding")]
        [Tooltip("Droplets in the opening spray from EACH end of the break — the stump left on the body and the cut end of the severed limb.")]
        public int severBurstDroplets = 24;
        [Tooltip("Seconds each cut end keeps bleeding after the limb comes off. The droplets that land are what dirties the mat and the bodies, so this is how much blood the break contributes over time.")]
        public float bleedSeconds = 7f;
        [Tooltip("Seconds between droplet puffs while a cut end bleeds.")]
        public float bleedInterval = 0.1f;
        [Tooltip("Droplets per puff while bleeding. Small — the accumulation over bleedSeconds is what reads, not any single puff.")]
        public int bleedDroplets = 3;

        [Header("Decapitation bleeding")]
        [Tooltip("Opening burst from EACH wound the instant the head comes off — the neck stump and the cut face of the head.")]
        public int decapBurstDroplets = 40;
        [Tooltip("Seconds the two wounds pour for. Flow decays as (1-t)^2, so it is a hard pour at the start and a trickle by the end.")]
        public float decapBleedSeconds = 5f;
        [Tooltip("Droplets per puff at full flow. Decays to 1 by decapBleedSeconds.")]
        public int decapPeakDroplets = 12;
        [Tooltip("Seconds between puffs at full flow. Stretches to 4x this as the flow dies.")]
        public float decapPeakInterval = 0.045f;

        /// Coarse body regions for the HUD mannequin. Six is the useful number on a
        /// portrait phone — the body has 14 parts, but nobody can read 14 swatches.
        public enum Region { Head, Torso, ArmNear, ArmFar, LegNear, LegFar }
        public const int REGION_COUNT = 6;

        /// PART_DEFS index -> region. Index -1 is the head (a compound collider on
        /// Chest, not a part of its own).
        public static Region RegionOf(int partIndex)
        {
            switch (partIndex)
            {
                case -1: return Region.Head;
                case 1: case 2: case 3: case 14: return Region.LegNear;  // thigh, shin, foot, toe
                case 4: case 5: case 6: case 15: return Region.LegFar;
                case 10: case 11: return Region.ArmNear;          // upper arm, forearm
                case 12: case 13: return Region.ArmFar;
                default: return Region.Torso;                     // pelvis, backs, chest
            }
        }

        /// The part a region hangs from, or -1 if the region cannot be severed.
        ///
        /// Torso is the trunk — there is nothing to detach it from. The head is not
        /// here either, but for a different reason: it has no neck joint (it is a
        /// compound collider on Chest), so it does not detach by destroying a hinge.
        /// Agent_BipedBody.DetachHead splits it onto a fresh rigidbody at the moment
        /// of decapitation instead — which keeps an INTACT fighter's mass
        /// distribution, and therefore every trained brain, untouched.
        private static int RootPartOf(Region region)
        {
            switch (region)
            {
                case Region.ArmNear: return 10;   // UArmNear, hangs off the near shoulder
                case Region.ArmFar:  return 12;   // UArmFar
                case Region.LegNear: return 1;    // ThighNear, hangs off the near hip
                case Region.LegFar:  return 4;    // ThighFar
                default: return -1;
            }
        }

        // Kept in its own tournament-persistent store rather than summed from the
        // mark list, because marks are RECYCLED past maxDecals — summing them would
        // make a fighter's damage go DOWN as the oldest bruises scrolled off, and a
        // limb could un-redden. Damage only ever accumulates.
        private static readonly Dictionary<string, float[]> RegionStore =
            new Dictionary<string, float[]>();

        private float[] _regionDamage = new float[REGION_COUNT];

        /// 0..1 for the HUD: 0 = untouched (green), 1 = fully red. Can exceed 1
        /// internally before a limb tears off, so it is clamped here.
        public float RegionDamage01(Region region) =>
            Mathf.Clamp01(_regionDamage[(int)region] / Mathf.Max(0.01f, regionRedAt));

        /// UNCLAMPED region damage. RegionDamage01 saturates at 1.0, which hid how
        /// close the head actually was to its detach gate.
        public float RegionDamageRaw(Region region) => _regionDamage[(int)region];

        /// True once this region has been torn off. The head is not a jointed
        /// part, so it answers from its own flag.
        public bool RegionDetached(Region region)
        {
            if (body == null) return false;
            if (region == Region.Head) return body.HeadDetached;
            int rootPart = RootPartOf(region);
            if (rootPart < 0) return false;
            int joint = Agent_BipedBody.JointIndexForChild(rootPart);
            return joint >= 0 && body.IsDetached(joint);
        }

        /// Fired when a fighter is knocked out by a head blow. Presentation only —
        /// no referee listens to this.
        public static event System.Action<Agent_BipedBody, Vector3> Knockout;

        /// Fired when a limb is torn off. Presentation + HUD; no referee listens.
        public static event System.Action<Agent_BipedBody, Region, Vector3> Dismembered;

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
        private static void ClearOnPlaySessionStart()
        {
            Store.Clear();
            RegionStore.Clear();
        }

        /// Wipe all accumulated damage. Called when a tournament is reset so a new
        /// bracket starts on clean bodies — and on whole limbs.
        public static void ClearAll()
        {
            Store.Clear();
            RegionStore.Clear();
        }

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
                    Random.Range(0.1f, 0.35f), blood: true, countsAsDamage: false);
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
                for (int partIndex = 0; partIndex < body.Parts.Length; partIndex++)
                {
                    if (body.Parts[partIndex] == hitPart) return partIndex;
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
        private Systems_GameMatchManager _manager;

        private void Start()
        {
            _manager = FindAnyObjectByType<Systems_GameMatchManager>();
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

            // Keyed by behaviour name so a fighter carries its bruises from bout
            // to bout. A MIRROR bout breaks that: the bracket seeds every
            // character twice, so both slots resolved to one key — and
            // _regionDamage is the live dismemberment gate, not merely a record.
            // Sharing it meant the detach threshold was reached on half the
            // punishment and the limb tore off a fighter who had never been hit
            // there, on both bodies, with rules consequences via OutOfRing's
            // foot test. Mirror bouts get a per-side key; every other pairing is
            // keyed exactly as before.
            string key = DamageKey(agent);
            if (!Store.TryGetValue(key, out _marks))
            {
                _marks = new List<Mark>();
                Store[key] = _marks;
            }
            if (!RegionStore.TryGetValue(key, out _regionDamage))
            {
                _regionDamage = new float[REGION_COUNT];
                RegionStore[key] = _regionDamage;
            }
            Active[body] = this;

            // Replay everything this fighter has taken so far this tournament.
            for (int markIndex = 0; markIndex < _marks.Count; markIndex++)
            {
                Paint(_marks[markIndex]);
            }

            // A limb lost in an earlier bout is still gone. Applied SILENTLY —
            // no spray, no sound, no event — because the moment it came off
            // belonged to the match where it happened; replaying the drama on
            // every subsequent scene load would be absurd.
            ReapplyStandingDismemberment();
        }

        /// Persistent damage-record key for this fighter.
        ///
        /// Behaviour name alone in every normal bout, so nothing about the
        /// existing carry-over changes. Only a mirror bout — where the name
        /// identifies both fighters equally — is split per side, because there
        /// the alternative is one shared mutable array driving two bodies.
        private string DamageKey(Agent_Biped agent)
        {
            if (_manager == null
                || _manager.wrestlerA == null || _manager.wrestlerB == null
                || _manager.wrestlerA.behaviorName != _manager.wrestlerB.behaviorName)
            {
                return agent.behaviorName;
            }
            return agent.teamId == 0 ? agent.behaviorName + "#A" : agent.behaviorName + "#B";
        }

        /// Re-sever anything already past the threshold, without presentation.
        private void ReapplyStandingDismemberment()
        {
            if (!allowDetach || body == null) return;
            float limit = regionRedAt * detachAtRedMultiple;
            for (int r = 0; r < REGION_COUNT; r++)
            {
                if (_regionDamage[r] < limit) continue;
                int rootPart = RootPartOf((Region)r);
                if (rootPart < 0) continue;
                int joint = Agent_BipedBody.JointIndexForChild(rootPart);
                if (joint >= 0) body.DetachJoint(joint);
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
            TickBleeders();

            if (_knockedOut && Time.time >= _koUntil)
            {
                _knockedOut = false;
                // Only hand the body back while a round is actually live. The
                // referee ends a round with both fighters' actions off but their
                // physics still simulating through the announce beat, so an
                // expiring KO restored motors mid-beat and the "unconscious"
                // loser climbed back to his feet under the SCORES! banner.
                // Skipping it is safe: the next round's ResetPose calls
                // RestoreMotors anyway.
                if (_manager != null && !_manager.RoundLive)
                {
                    return;
                }
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
            // The wound itself is the damage; the spatter around it is the same
            // blow drawn wider, so only the first mark scores. Otherwise one KO
            // would add ~3.4 to the head and redline it in a single hit.
            AddMark(point, -1, 1f, blood: true);
            for (int index = 0; index < 4; index++)
            {
                Vector3 offset = point + (Vector3)(Random.insideUnitCircle * 0.12f);
                AddMark(offset, -1, Random.Range(0.4f, 0.8f), blood: true, countsAsDamage: false);
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
            for (int partIndex = 0; partIndex < body.Parts.Length; partIndex++)
            {
                if (body.Parts[partIndex] != null && body.Parts[partIndex].transform == partTransform)
                {
                    return partIndex;
                }
            }
            return -1;
        }

        /// countsAsDamage distinguishes a mark this fighter EARNED from one merely
        /// painted on it. Blood thrown off the other fighter's knockout lands here
        /// too, and being spattered by someone else's wound must not cost you a limb.
        private void AddMark(Vector3 worldPoint, int partIndex, float strength, bool blood,
                             bool countsAsDamage = true)
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

            if (countsAsDamage)
            {
                AccumulateDamage(RegionOf(partIndex), strength, worldPoint);
            }
        }

        /// Add to a region's running total and tear the limb off if it goes past
        /// the threshold.
        private void AccumulateDamage(Region region, float strength, Vector3 worldPoint)
        {
            if (_regionDamage == null) return;
            _regionDamage[(int)region] += Mathf.Clamp01(strength);

            if (!allowDetach || body == null) return;

            // The head has its own, much lower threshold: it is the showpiece
            // finish, while arms and legs are meant to survive most bouts.
            float multiple = region == Region.Head ? headDetachAtRedMultiple : detachAtRedMultiple;
            if (_regionDamage[(int)region] < regionRedAt * multiple) return;

            if (region == Region.Head)
            {
                DecapitateHead(worldPoint);
                return;
            }

            int rootPart = RootPartOf(region);
            if (rootPart < 0) return;                       // the torso cannot come off
            int joint = Agent_BipedBody.JointIndexForChild(rootPart);
            if (joint < 0) return;

            // Resolve the break geometry BEFORE detaching: DetachJoint destroys the
            // HingeJoint2D, and the joint is the only thing that knows where the two
            // parts were actually joined and which bodies they were. Afterwards
            // there is nothing left to ask.
            Rigidbody2D limbPart = null, parentPart = null;
            Vector3 breakPoint = worldPoint;
            HingeJoint2D hinge = (body.Joints != null && joint < body.Joints.Length) ? body.Joints[joint] : null;
            if (hinge != null)
            {
                limbPart = hinge.attachedRigidbody;      // the piece that comes away
                parentPart = hinge.connectedBody;        // the piece it hung from
                breakPoint = hinge.transform.TransformPoint(hinge.anchor);
            }

            if (!body.DetachJoint(joint)) return;               // false = already gone

            // Sell it: blood out of BOTH ends of the break, and both ends keep
            // bleeding for a few seconds as they move. The HUD mannequin turns that
            // region to a stump on its next repaint.
            OpenBleed(parentPart, breakPoint);
            OpenBleed(limbPart, breakPoint);
            Dismembered?.Invoke(body, region, breakPoint);
            Debug.Log($"[DAMAGE] {name} lost {region} at {_regionDamage[(int)region]:F1} damage — " +
                      $"bleeding from stump '{(parentPart == null ? "none" : parentPart.name)}' and " +
                      $"cut end '{(limbPart == null ? "none" : limbPart.name)}' at {breakPoint}");
        }

        /// Takes the head off and bleeds both the neck stump and the loose head.
        private void DecapitateHead(Vector3 worldPoint)
        {
            Rigidbody2D head = body.DetachHead();
            if (head == null) return;                       // already off

            // The neck is where the head sat, not wherever the last punch landed.
            Vector3 neck = body.Chest != null
                ? body.Chest.transform.TransformPoint(new Vector3(0f, 0.25f, 0f))
                : worldPoint;

            // Both wounds pour: the neck stump on the body, and the CUT FACE of the
            // head — the bottom of the sphere, not its centre. Stored in each part's
            // local space, so the head's jet stays pointed out of its own neck-hole
            // however it tumbles.
            // Built in WORLD units on purpose. DetachHead gives the freed head the
            // art transform's lossyScale, so TransformPoint(0, -r, 0) would scale
            // the offset and put the jet somewhere inside the skull instead of on
            // its bottom edge. Stepping down the head's own up-axis by the real
            // radius is scale-independent; OpenBleed converts to local space itself.
            float headRadius = 0.5f * body.headDiameter;
            Vector3 headCut = head.transform.position - head.transform.up * headRadius;
            Vector2 headCutDir = -head.transform.up;

            OpenBleed(body.Chest, neck, Vector2.up,
                      decapBurstDroplets, decapBleedSeconds, decapPeakDroplets, decapPeakInterval);
            OpenBleed(head, headCut, headCutDir,
                      decapBurstDroplets, decapBleedSeconds, decapPeakDroplets, decapPeakInterval);

            Dismembered?.Invoke(body, Region.Head, neck);
            Debug.Log($"[DAMAGE] {name} DECAPITATED at {_regionDamage[(int)Region.Head]:F1} damage — neck at {neck}");
        }

        /// One bleeding cut end. Stored in the PART's local space, not world space,
        /// so the spray origin rides the stump as the fighter moves and rides the
        /// severed limb as it tumbles away — a world-space origin would leave both
        /// jets hanging in the air where the limb used to be.
        private struct Bleeder
        {
            public Rigidbody2D part;
            public Vector2 localPoint;
            public Vector2 localDir;
            public float startTime;
            public float duration;
            public float nextEmit;
            public int peakDroplets;    // per puff at t=0
            public float peakInterval;  // seconds between puffs at t=0
        }

        private readonly List<Bleeder> _bleeders = new List<Bleeder>();

        /// Opens one end of a break: an immediate spray, then a timed bleed.
        ///
        /// Direction is centre-of-mass -> break point, so each end sprays AWAY from
        /// its own body. Applied to the stump and the severed limb separately, that
        /// gives the two opposed jets: the body sprays out of its socket, the loose
        /// limb sprays out of its cut.
        private void OpenBleed(Rigidbody2D part, Vector3 breakPoint)
        {
            OpenBleed(part, breakPoint, Vector2.zero, severBurstDroplets, bleedSeconds,
                      bleedDroplets, bleedInterval);
        }

        /// Opens one cut end: an immediate burst, then a bleed that DECAYS from a
        /// hard pour to a trickle over `duration`.
        ///
        /// `dirOverride` is for cuts whose outward direction is not simply
        /// centre-of-mass -> break point — a severed head's wound faces its own
        /// local down, wherever the head has tumbled to.
        private void OpenBleed(Rigidbody2D part, Vector3 breakPoint, Vector2 dirOverride,
                               int burst, float duration, int peakDroplets, float peakInterval)
        {
            if (part == null) return;

            Vector2 outward = dirOverride;
            if (outward.sqrMagnitude < 1e-6f)
            {
                outward = (Vector2)breakPoint - part.worldCenterOfMass;
                if (outward.sqrMagnitude < 1e-6f) outward = Vector2.up;
            }
            outward.Normalize();

            Systems_DustPuff.BloodSpray(breakPoint, outward, burst);

            _bleeders.Add(new Bleeder
            {
                part = part,
                localPoint = part.transform.InverseTransformPoint(breakPoint),
                localDir = part.transform.InverseTransformDirection(outward),
                startTime = Time.time,
                duration = duration,
                nextEmit = Time.time + peakInterval,
                peakDroplets = peakDroplets,
                peakInterval = peakInterval,
            });
        }

        /// Drives every open cut end. Droplets that land are stamped permanently by
        /// Systems_RingBlood — on the mat if they hit the clay, on the struck part
        /// via SplashAt if they hit a fighter — so the mess accumulates on both.
        private void TickBleeders()
        {
            for (int bleederIndex = _bleeders.Count - 1; bleederIndex >= 0; bleederIndex--)
            {
                Bleeder bleeder = _bleeders[bleederIndex];
                float age = Time.time - bleeder.startTime;
                if (bleeder.part == null || age >= bleeder.duration)
                {
                    _bleeders.RemoveAt(bleederIndex);
                    continue;
                }
                if (Time.time < bleeder.nextEmit) continue;

                // Squared falloff: it pours hard, drops off fast, then tails to a
                // trickle rather than stopping dead. Linear decay still read as a
                // steady leak for most of its life.
                float t = Mathf.Clamp01(age / Mathf.Max(0.01f, bleeder.duration));
                float flow = (1f - t) * (1f - t);

                int droplets = Mathf.Max(1, Mathf.RoundToInt(Mathf.Lerp(1f, bleeder.peakDroplets, flow)));
                // Puffs also thin out, so the very end is the odd drop, not a
                // metronome of one-droplet bursts.
                float interval = Mathf.Lerp(bleeder.peakInterval * 4f, bleeder.peakInterval, flow);

                bleeder.nextEmit = Time.time + interval;
                _bleeders[bleederIndex] = bleeder;

                Transform partTransform = bleeder.part.transform;
                Systems_DustPuff.BloodSpray(partTransform.TransformPoint(bleeder.localPoint),
                                            partTransform.TransformDirection(bleeder.localDir),
                                            droplets);
            }
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
            for (int rowIndex = 0; rowIndex < S; rowIndex++)
            {
                for (int columnIndex = 0; columnIndex < S; columnIndex++)
                {
                    var p = new Vector2(columnIndex + 0.5f, rowIndex + 0.5f);
                    Vector2 d = p - centre;
                    float angle = Mathf.Atan2(d.y, d.x);
                    // Wobble the radius so the outline is irregular.
                    float wobble = 1f + 0.16f * Mathf.Sin(angle * 3f) + 0.1f * Mathf.Sin(angle * 5f + 1.3f);
                    float dist = d.magnitude / (radius * wobble);
                    float a = Mathf.Clamp01(1f - dist);
                    // Squared falloff: dense core, soft edge.
                    tex.SetPixel(columnIndex, rowIndex, new Color(1f, 1f, 1f, a * a));
                }
            }
            tex.Apply();
            _bruiseSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S,
                                          0, SpriteMeshType.FullRect);
            return _bruiseSprite;
        }
    }
}
