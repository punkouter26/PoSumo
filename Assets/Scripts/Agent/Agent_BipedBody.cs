using System.Collections.Generic;
using UnityEngine;

namespace PoSumo
{
    /// Builds a 14-part 2D ragdoll biped from primitive colliders at runtime.
    /// v02 body: 4-segment articulated torso (pelvis, lower back, upper back,
    /// chest) linked by 3 motorized spine joints, plus legs (hip/knee/ankle)
    /// and arms (shoulder/elbow) — 13 motors total.
    /// Right-facing geometry is defined once; facingSign = -1 mirrors it.
    /// All colliders of the same biped ignore each other (limbs pass through
    /// their own body) but still collide with the ground and the opponent.
    public sealed class Agent_BipedBody : MonoBehaviour
    {
        [Tooltip("Character sheet; when set, overrides teamColor/headSprite/body scales at Awake.")]
        public Agent_CharacterDefinition character;

        public int facingSign = 1;
        /// One base colour per fighter, taken from the character sheet in Awake, and
        /// used by EVERY body part. The head is the only exception — it carries that
        /// fighter's face art instead. The per-part `tint` in PART_DEFS only shades
        /// this one colour for near/far depth; it never introduces a second hue, so
        /// a wrestler always reads as a single identifiable colour.
        public Color teamColor = new Color(0.85f, 0.25f, 0.2f);
        private bool _faceArtFacesLeft;

        [Header("Build (1 = Matt's lightweight baseline)")]
        [Tooltip("Multiplies every part's mass. Heavyweight sumo ~2.")]
        public float massScale = 1f;
        [Tooltip("Widens the four torso segments (sumo belly). Heavyweight ~1.3.")]
        public float widthScale = 1f;
        [Tooltip("Multiplies joint motor torque caps to carry the extra mass.")]
        public float torqueScale = 1f;

        [Tooltip("Optional face texture for the head (drawn right-facing; auto-flipped when facingSign is -1). Falls back to a plain circle.")]
        public Sprite headSprite;

        [Tooltip("Diameter of the head hitbox in metres, identical for every character regardless of face art. PHYSICS: changing this changes collision geometry and invalidates trained brains — 0.5 is what the previous art-derived collider measured, so it is the value that changes the dynamics least.")]
        public float headDiameter = 0.5f;

        // Dressing. Visual only: no extra rigidbodies, no extra colliders, no extra
        // joints, and nothing that touches a mass or a contact — so it cannot
        // change the dynamics a policy was trained against.
        //
        // The mawashi belt, the sagari cords and the chonmage topknot were built
        // and then REMOVED: at gameplay zoom the verlet cords and the knot read as
        // loose floating beads beside the body rather than as cloth and hair. Real
        // ShadowCaster2D shadows were removed for the same reason — see the note in
        // Systems_ArenaLighting. Both are in git history if they get revisited.
        [Header("Dressing (visual only — cannot invalidate a trained brain)")]
        [Tooltip("Velocity-driven wobble on the torso art. Heavier fighters wobble more.")]
        public bool enableJiggle = true;

        [HideInInspector] public Rigidbody2D Torso;   // pelvis (root mass, ring-out tracking)
        [HideInInspector] public Rigidbody2D Chest;   // top segment (posture/lean sensing)
        [HideInInspector] public SpriteRenderer HeadRenderer; // face sprite (mood swaps)
        /// Alpha silhouette of the current face, kept in step with HeadRenderer.
        /// Systems_BodyDamage clips head decals against it.
        [HideInInspector] public SpriteMask HeadMask;
        [HideInInspector] public HingeJoint2D[] Joints;   // 10 motors
        [HideInInspector] public Rigidbody2D[] Parts;
        [HideInInspector] public List<Collider2D> AllColliders = new List<Collider2D>();

        /// This wrestler's own PoSumo/BodyLit material. Per-fighter rather than
        /// shared so Systems_BodySurface can write sweat and clay into it without
        /// the opponent getting sweaty too.
        public Material BodyMaterial { get; private set; }

        /// Every limb/torso art renderer, in PART_DEFS order. Head excluded — it
        /// carries face art and is swapped by Systems_FaceMood.
        public SpriteRenderer[] ArtRenderers { get; private set; }

        /// The head's own collider. The head is a compound child collider on the
        /// CHEST rigidbody and has no Sensor_Impact of its own, so a hit to the head
        /// arrives as a Chest collision — identifying it means comparing against
        /// this. Exposed for Systems_BodyDamage's head-KO check.
        public CircleCollider2D HeadCollider { get; private set; }

        /// Unit-scale transform riding on the head, immune to face-art rescaling.
        /// Parent head decals here, never to HeadRenderer.transform — local units
        /// are metres.
        public Transform HeadDecalAnchor { get; private set; }

        // Spawn clearance above contact surfaces: enough to avoid frame-0
        // interpenetration, small enough that the feet are effectively already
        // planted when physics starts (a taller drop costs balance).
        private const float SPAWN_CLEARANCE = 0.01f;
        private const float MAX_ANGULAR_VELOCITY = 1080f; // deg/s clamp against physics blow-up
        // Art is drawn at EXACTLY collider size. An earlier version oversized it to
        // hide the seams between segments, but that made the drawing lie about the
        // physics: a limb appeared to touch the opponent while the colliders were
        // still apart. Accuracy wins — the drawn edge is the edge that collides.
        private const float ART_OVERLAP = 1f;

        private float[] _maxSpeed;   // deg/s per joint
        private float[] _maxTorque;
        private Vector3[] _initialLocalPos;
        private Quaternion[] _initialLocalRot;
        private Rigidbody2D[] _thighs;
        private float _headCellW, _headCellH; // chest local scale the head must compensate for

        private static Sprite _boxSprite, _torsoSprite, _circleSprite, _squareSprite;
        private static PhysicsMaterial2D _footMat, _bodyMat;

        /// Passive joint resistance, as a fraction of each joint's own motor
        /// budget. HingeJoint2D has no spring — that is a 3D-joint feature — so it
        /// is applied as an explicit restoring torque each physics step.
        ///
        /// Deliberately weak. This is ligament and antagonist muscle tone, not a
        /// second actuator: it biases the body toward its neutral pose and bleeds
        /// oscillation, without giving the policy something it has to overpower to
        /// move. Before this, every joint was either motor-driven or completely
        /// free, which is why unheld limbs swung like a pendulum.
        private const float PASSIVE_STIFFNESS_FRAC = 0.06f;   // of max torque at 90 deg off neutral
        private const float PASSIVE_DAMPING_FRAC = 0.10f;     // of max torque at 400 deg/s

        // NOT YET IMPLEMENTED: passive neck.
        //
        // The head is a compound collider on the Chest rigidbody with its 6 kg
        // folded into Chest's 13, so it cannot bob or whip on impact. Making it a
        // real segment needs its own Rigidbody2D and an unpowered hinge — powered
        // is not an option, because Agent_Biped builds observations by looping over
        // ActionCount, so a 14th driven joint would change the action space AND
        // the 44-obs vector, invalidating every brain's input and output layer.
        //
        // The reason it is not done here: the head would have to leave PART_DEFS'
        // parallel arrays, which index ArtRenderers (head is deliberately excluded
        // from those, it carries face art) and back every loop that limps, freezes
        // and resets Parts[]. A neck wired in halfway leaves the head still
        // simulating after the match-end freeze. It needs its own pass.

        private struct PartDef
        {
            public string name; public float w, h, mass, x, y;
            public bool circle, isFoot; public int sorting; public float tint;
            public PartDef(string n, float w_, float h_, float m, float x_, float y_,
                           int sort, float tint_, bool circ = false, bool foot = false)
            { name = n; w = w_; h = h_; mass = m; x = x_; y = y_; sorting = sort; tint = tint_; circle = circ; isFoot = foot; }
        }

        private struct JointDef
        {
            public int child, parent; public float ax, ay, min, max, torque, speed;
            public JointDef(int c, int p, float ax_, float ay_, float mn, float mx, float t, float s)
            { child = c; parent = p; ax = ax_; ay = ay_; min = mn; max = mx; torque = t; speed = s; }
        }

        // Right-facing layout. Heights: ankle 0.10, knee 0.533, hip 0.964,
        // spine joints 1.130 / 1.259 / 1.388, shoulder 1.471, elbow 1.144.
        // 69.6 kg — see the TotalMass property, which is the number to trust.
        //
        // MASSES track Winter's anthropometric fractions and are unchanged:
        // thigh 10.1% (vs 10.0), shin 5.0% (4.65), foot 1.44% (1.45), torso
        // chain 54.6% (57.8); the arms run ~20-28% heavy on purpose.
        //
        // LENGTHS did not, and were re-derived against Winter for a 1.76 m body.
        // Every limb had been 8-18% short — shank 0.38 against 0.246H = 0.433,
        // foot 0.22 against 0.152H = 0.268, upper arm 0.30 against 0.186H = 0.327
        // — while the trunk ran long at 0.55 against 0.288H = 0.507. Short shanks
        // and short feet mean a short stride and a small base of support, which
        // works directly against the sumo stance the shaping now asks for.
        // The legs grew 0.084 m and the trunk gave back 0.043, so standing height
        // rises only ~2%. The whole chain below is derived from those lengths:
        // change a segment and the joint anchors above it must move with it.
        private static readonly PartDef[] PART_DEFS =
        {
            new PartDef("Pelvis",    0.32f,  0.18f,  11f,  0f,    1.054f,  0, 1f),
            new PartDef("ThighNear", 0.14f,  0.431f,  7f,  0f,    0.749f,  2, 0.95f),
            new PartDef("ShinNear",  0.11f,  0.433f,  3.5f, 0f,   0.317f,  2, 0.95f),
            new PartDef("FootNear",  0.268f, 0.08f,   1f,  0.06f, 0.04f,   2, 0.95f, false, true),
            new PartDef("ThighFar",  0.14f,  0.431f,  7f,  0f,    0.749f, -2, 0.78f),
            new PartDef("ShinFar",   0.11f,  0.433f,  3.5f, 0f,   0.317f, -2, 0.78f),
            new PartDef("FootFar",   0.268f, 0.08f,   1f,  0.06f, 0.04f,  -2, 0.78f, false, true),
            new PartDef("LowerBack", 0.30f,  0.14f,   7f,  0f,    1.195f,  0, 0.97f),
            new PartDef("UpperBack", 0.31f,  0.14f,   7f,  0f,    1.324f,  0, 0.99f),
            new PartDef("Chest",     0.34f,  0.18f,  13f,  0f,    1.471f,  0, 1f),
            new PartDef("UArmNear",  0.10f,  0.327f,  2.5f, 0f,   1.308f,  3, 0.9f),
            new PartDef("FArmNear",  0.09f,  0.28f,   1.8f, 0f,   1.004f,  3, 0.9f),
            new PartDef("UArmFar",   0.10f,  0.327f,  2.5f, 0f,   1.308f, -3, 0.76f),
            new PartDef("FArmFar",   0.09f,  0.28f,   1.8f, 0f,   1.004f, -3, 0.76f),
        };

        // child, parent, anchor(x,y in root space), min, max (deg), torque, speed
        //
        // RANGES. The ankle, the three spine joints and the shoulders were SYMMETRIC and bent
        // backwards far past human range — a fighter could hyperextend his spine
        // 75 degrees and throw an arm 160 degrees behind him, which is the single
        // largest "that is not a body" contributor after motor saturation. They are
        // now clamped to roughly human TOTAL range:
        //   ankle    +/-45 -> +/-25   (human ROM ~70 deg: 20 dorsi + 50 plantar)
        //   spine    +/-25 -> +/-20   each, so +/-60 over three (human ~120 total)
        //   shoulder +/-160 -> +/-120 (human ~240 total)
        //
        // SIGN CONVENTION — measured, do not re-derive by eye. The three asymmetric
        // joints (hip, knee, elbow) were inverted for the whole life of the project.
        // HingeJoint2D.jointAngle here is the NEGATIVE of the geometric rotation of
        // the child segment relative to its parent: measuring
        //     Vector2.SignedAngle(parent.transform.up, child.transform.up) * facingSign
        // against jointAngle gives a ratio of exactly -1.00 on every joint tested.
        // So a range written as if it were geometric bends the limb the wrong way.
        // The old values gave a bird leg — knee (-150..0) swung the shin FORWARD,
        // elbow (0..150) swung the forearm BACKWARD, and hip (-30..120) gave 120
        // degrees of extension with only 30 of flexion, so the fighter could neither
        // crouch nor drive off a loaded leg. Negated and swapped:
        //   hip   (-30..120) -> (-120..30)  120 flexion / 30 extension
        //   knee  (-150..0)  -> (0..150)    150 flexion, hard stop at straight
        //   elbow (0..150)   -> (-150..0)   150 flexion, hard stop at straight
        // The symmetric joints are unaffected by the sign and keep their values.
        // Agent_Biped.KneeBendFactor() reads knee flexion as POSITIVE jointAngle and
        // must be flipped with these if they ever move again.
        //
        // TORQUE. Legs were already realistic (hip 300, knee 250, ankle 120 N-m
        // against human peaks of ~250-300 / ~250-300 / ~150-200). The upper body
        // was 2-4x human and is brought back: the three spine joints ran 400 N-m
        // EACH against a human trunk-extensor peak of ~300-500 N-m in total, and
        // that surplus is what let the torso catapult the whole body around.
        //   spine    400 -> 180 each (540 total, still generous for a sumo)
        //   shoulder 150 -> 80  (human ~50-100)
        //   elbow    100 -> 60  (human ~50-70)
        private static readonly JointDef[] JOINT_DEFS =
        {
            new JointDef(1, 0,  0f, 0.964f,-120f,  30f, 300f, 400f), // hip near
            new JointDef(2, 1,  0f, 0.533f,   0f, 150f, 250f, 500f), // knee near
            new JointDef(3, 2,  0f, 0.10f,  -25f,  25f, 120f, 400f), // ankle near
            new JointDef(4, 0,  0f, 0.964f,-120f,  30f, 300f, 400f), // hip far
            new JointDef(5, 4,  0f, 0.533f,   0f, 150f, 250f, 500f), // knee far
            new JointDef(6, 5,  0f, 0.10f,  -25f,  25f, 120f, 400f), // ankle far
            new JointDef(7, 0,  0f, 1.130f, -20f,  20f, 180f, 250f), // spine 1 (pelvis->lower back)
            new JointDef(8, 7,  0f, 1.259f, -20f,  20f, 180f, 250f), // spine 2 (lower->upper back)
            new JointDef(9, 8,  0f, 1.388f, -20f,  20f, 180f, 250f), // spine 3 (upper back->chest)
            new JointDef(10, 9, 0f, 1.471f,-120f, 120f,  80f, 500f), // shoulder near (on chest)
            new JointDef(11,10, 0f, 1.144f,-150f,   0f,  60f, 500f), // elbow near
            new JointDef(12, 9, 0f, 1.471f,-120f, 120f,  80f, 500f), // shoulder far (on chest)
            new JointDef(13,12, 0f, 1.144f,-150f,   0f,  60f, 500f), // elbow far
        };

        private void Awake()
        {
            if (character != null)
            {
                teamColor = character.teamColor;
                headSprite = character.headSprite;
                massScale = character.massScale;
                widthScale = character.widthScale;
                torqueScale = character.torqueScale;
                _faceArtFacesLeft = character.faceArtFacesLeft;
            }
            Build();
        }

        /// Limb sprite: a rounded capsule, not the old 4x4 white square. Parts are
        /// scaled by their transform, so the corner rounding is stretched into an
        /// ellipse per part — which is exactly what a limb end should look like.
        /// Edges are antialiased in the alpha so the silhouette stays smooth at the
        /// large scale factors the body uses.
        /// Limb sprite. Radius 0.5 makes it a true ellipse in local space, which is
        /// exactly the shape a CapsuleCollider2D of size (1,1) takes under the same
        /// non-uniform part scale — so the drawn limb and the colliding limb are the
        /// same shape, and a kick connects where it looks like it connects.
        public static Sprite BoxSprite() => RoundedSprite(ref _boxSprite, 0.5f);

        /// Soft-cornered rectangle. NOT used for body parts any more — see
        /// SquareSprite — but kept for dressing (the mawashi band) that has no
        /// collider and therefore nothing to be accurate to.
        public static Sprite TorsoSprite() => RoundedSprite(ref _torsoSprite, 0.14f);

        /// Exact rectangle for every part that collides with a BoxCollider2D:
        /// the four torso segments and the two feet.
        ///
        /// These were drawn with TorsoSprite's 0.14 corner radius, which insets
        /// the DRAWN edge from the box that actually collides — so a chest-to-chest
        /// shove connected slightly before the art touched, and a foot looked
        /// rounder than the flat sole standing on the clay. The rule this body
        /// already follows for limbs (the drawn edge IS the colliding edge) now
        /// holds for the trunk too.
        ///
        /// Four texels of solid white: the sprite quad's own edge is the silhouette,
        /// so it matches the collider exactly at any non-uniform scale.
        public static Sprite SquareSprite()
        {
            if (_squareSprite == null)
            {
                const int S = 4;
                var tex = new Texture2D(S, S, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                for (int rowIndex = 0; rowIndex < S; rowIndex++)
                {
                    for (int columnIndex = 0; columnIndex < S; columnIndex++)
                    {
                        tex.SetPixel(columnIndex, rowIndex, Color.white);
                    }
                }
                tex.Apply();
                _squareSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S,
                                              0, SpriteMeshType.FullRect);
            }
            return _squareSprite;
        }

        private static Sprite RoundedSprite(ref Sprite cache, float radiusFraction)
        {
            if (cache == null)
            {
                const int S = 128;
                float RADIUS = S * radiusFraction;
                var tex = new Texture2D(S, S, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                float half = S * 0.5f;
                float inner = half - RADIUS;
                for (int rowIndex = 0; rowIndex < S; rowIndex++)
                {
                    for (int columnIndex = 0; columnIndex < S; columnIndex++)
                    {
                        // Distance to the rounded-rect boundary, in pixels.
                        float dx = Mathf.Max(Mathf.Abs(columnIndex + 0.5f - half) - inner, 0f);
                        float dy = Mathf.Max(Mathf.Abs(rowIndex + 0.5f - half) - inner, 0f);
                        float distance = Mathf.Sqrt(dx * dx + dy * dy);
                        float alpha = Mathf.Clamp01((RADIUS - distance) / 1.5f);
                        tex.SetPixel(columnIndex, rowIndex, new Color(1f, 1f, 1f, alpha));
                    }
                }
                tex.Apply();
                cache = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S,
                                      0, SpriteMeshType.FullRect);
            }
            return cache;
        }

        public static Sprite CircleSprite()
        {
            if (_circleSprite == null)
            {
                const int S = 64;
                var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
                float r = S / 2f - 1f;
                for (int rowIndex = 0; rowIndex < S; rowIndex++)
                    for (int columnIndex = 0; columnIndex < S; columnIndex++)
                    {
                        float d = Vector2.Distance(new Vector2(columnIndex + 0.5f, rowIndex + 0.5f), new Vector2(S / 2f, S / 2f));
                        tex.SetPixel(columnIndex, rowIndex, d <= r ? Color.white : Color.clear);
                    }
                tex.Apply();
                _circleSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S);
            }
            return _circleSprite;
        }

        private void Build()
        {
            if (_footMat == null)
            {
                _footMat = new PhysicsMaterial2D("Foot") { friction = 0.9f, bounciness = 0f };
                _bodyMat = new PhysicsMaterial2D("Body") { friction = 0.4f, bounciness = 0f };
            }

            int n = PART_DEFS.Length;
            Parts = new Rigidbody2D[n];
            ArtRenderers = new SpriteRenderer[n];
            BodyMaterial = Systems_ArenaLighting.CreateBodyMaterial($"PoSumo_Body_{name}");
            var thighs = new List<Rigidbody2D>();

            bool IsTorsoPart(string name) =>
                name == "Pelvis" || name == "LowerBack" || name == "UpperBack" || name == "Chest";

            for (int index = 0; index < n; index++)
            {
                var d = PART_DEFS[index];
                var go = new GameObject(d.name);
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3(d.x * facingSign, d.y + SPAWN_CLEARANCE, 0);

                float w = d.w * (IsTorsoPart(d.name) ? widthScale : 1f);
                go.transform.localScale = new Vector3(w, d.h, 1f);

                // The art lives on a child scaled slightly larger than the collider,
                // so neighbouring parts overlap and the body reads as one creature.
                // Drawn at exact collider size, the 4-segment spine and the leg
                // chain show a seam at every joint. Physics is untouched: only this
                // child is oversized.
                var art = new GameObject("Art");
                art.transform.SetParent(go.transform, false);
                art.transform.localScale = new Vector3(ART_OVERLAP, ART_OVERLAP, 1f);

                // Limbs are capsule-shaped; the trunk and the feet stay boxy so their
                // flat faces (chest-to-chest shoving, sole-on-clay) collide flat.
                bool boxy = IsTorsoPart(d.name) || d.isFoot;

                var sr = art.AddComponent<SpriteRenderer>();
                // Drawn shape == colliding shape, part for part. Boxy parts get an
                // exact rectangle (BoxCollider2D); limbs get the ellipse that a
                // unit CapsuleCollider2D becomes under the part's own scale. Only
                // the head is exempt: it carries face art on a CircleCollider2D.
                sr.sprite = d.circle ? CircleSprite()
                          : boxy ? SquareSprite()
                          : BoxSprite();
                sr.color = new Color(teamColor.r * d.tint, teamColor.g * d.tint, teamColor.b * d.tint, 1f);
                sr.sortingOrder = d.sorting;
                // This fighter's body material so the light rig shapes the limbs and
                // the sweat/clay terms apply; the per-part tint stays on the
                // renderer, which costs no extra material.
                sr.sharedMaterial = BodyMaterial;
                ArtRenderers[index] = sr;


                var rb = go.AddComponent<Rigidbody2D>();
                rb.mass = d.mass * massScale;
                // Soft tissue. Real limbs are not frictionless linkages: muscle,
                // fat and skin dissipate energy continuously, which is most of why
                // a living body does not ring like a pendulum. At the old 0.05 the
                // ragdoll had effectively no passive damping and every limb whipped
                // and oscillated, which read as a puppet wearing motors.
                rb.linearDamping = 0.25f;
                rb.angularDamping = 0.8f;
                rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;

                // Collider shape follows the drawn shape so contacts happen exactly
                // where the art says they should. A capsule limb inside a box
                // collider left a visible gap at the rounded tip — the leg would
                // "kick" the opponent through empty space.
                Collider2D col;
                if (boxy)
                {
                    col = go.AddComponent<BoxCollider2D>();
                }
                else
                {
                    var capsule = go.AddComponent<CapsuleCollider2D>();
                    capsule.size = Vector2.one;                     // scaled to the part by the transform
                    capsule.direction = CapsuleDirection2D.Vertical;
                    col = capsule;
                }
                col.sharedMaterial = d.isFoot ? _footMat : _bodyMat;
                AllColliders.Add(col);

                Parts[index] = rb;
                if (d.name.StartsWith("Thigh")) thighs.Add(rb);

                // Soft-tissue lag on the trunk. Lives on the Art child, which
                // carries no collider, so this is pure rendering: the physics body
                // it reads from is never written back to.
                if (enableJiggle && IsTorsoPart(d.name))
                {
                    var jiggle = art.AddComponent<Systems_SoftBodyJiggle>();
                    jiggle.source = rb;
                    // Bulk wobbles: a heavyweight at massScale 1.45 moves nearly
                    // twice as much meat as the baseline build.
                    jiggle.amplitude = 0.055f * widthScale * Mathf.Sqrt(massScale);
                }

                if (index == 0) Torso = rb; // pelvis: root mass, ring-out tracking

                if (d.name == "Chest")
                {
                    Chest = rb;
                    // Head: compound child collider on the chest body (6 kg folded
                    // into the chest's 13 kg). Chest is scaled; compensate so the
                    // head stays round and sits at the neck (world y ~1.64).
                    var head = new GameObject("Head");
                    head.transform.SetParent(go.transform, false);
                    head.transform.localPosition = new Vector3(0f, 0.25f / d.h, 0f);
                    var hsr = head.AddComponent<SpriteRenderer>();
                    hsr.sortingOrder = 1;
                    hsr.sharedMaterial = BodyMaterial;
                    float parentW = d.w * widthScale;
                    HeadRenderer = hsr;

                    // Alpha silhouette of the face, used to clip damage decals to
                    // the drawn head. It lives on the ART object so it inherits the
                    // same aspect-fit scale and flip the photo gets — a mask that
                    // does not line up with the picture is worse than no mask.
                    // alphaCutoff is low so soft hair edges still count as head.
                    var mask = head.AddComponent<SpriteMask>();
                    mask.alphaCutoff = 0.1f;
                    HeadMask = mask;
                    _headCellW = parentW;
                    _headCellH = d.h;
                    if (headSprite != null)
                    {
                        SetHeadSprite(headSprite);
                    }
                    else
                    {
                        hsr.sprite = CircleSprite();
                        hsr.color = new Color(0.95f, 0.8f, 0.65f);
                        head.transform.localScale = new Vector3(0.312f / parentW, 0.312f / d.h, 1f);
                    }
                    // Head hitbox on its OWN child, deliberately NOT the art
                    // transform.
                    //
                    // The collider used to sit on the head art object — the same
                    // object SetHeadSprite rescales to aspect-fit each character's
                    // face photo. So the hitbox inherited the face image's aspect
                    // ratio: measured live it came out 0.519 m across while the
                    // drawn face was only 0.412 m wide, and, worse, a character
                    // whose PNG had a different aspect got a DIFFERENT-SIZED head
                    // to hit. That is a competitive difference between fighters
                    // that nothing in the Inspector shows you.
                    //
                    // This child is scaled to cancel the chest's own scale, so its
                    // world scale is uniform and the circle is a true circle of
                    // headDiameter metres for every character, whatever art they
                    // carry. Swapping a face can no longer change the physics.
                    // Anchor for anything that must STICK to the head without being
                    // rescaled by the face art: damage decals, blood stains.
                    //
                    // SetHeadSprite rescales the head art object to aspect-fit each
                    // character's photo, and Systems_FaceMood swaps that photo
                    // constantly during a match. Anything parented to the art
                    // therefore gets re-scaled mid-fight — which is why blood stains
                    // computed a compensating scale at paint time and then collapsed
                    // to nothing the next time Matt changed expression.
                    //
                    // Scaled to cancel the chest's own scale, so its world scale is
                    // exactly 1: child local units are metres, and nothing that
                    // happens to the face can touch it.
                    var headDecals = new GameObject("HeadDecals");
                    headDecals.transform.SetParent(go.transform, false);
                    headDecals.transform.localPosition = head.transform.localPosition;
                    headDecals.transform.localScale = new Vector3(1f / parentW, 1f / d.h, 1f);
                    HeadDecalAnchor = headDecals.transform;

                    var headHit = new GameObject("HeadHitbox");
                    headHit.transform.SetParent(go.transform, false);
                    headHit.transform.localPosition = head.transform.localPosition;
                    headHit.transform.localScale =
                        new Vector3(headDiameter / parentW, headDiameter / d.h, 1f);
                    var hc = headHit.AddComponent<CircleCollider2D>();
                    hc.radius = 0.5f;                 // unit circle -> headDiameter world
                    hc.sharedMaterial = _bodyMat;
                    AllColliders.Add(hc);
                    HeadCollider = hc;
                }
                // (Chest rb mass already includes the head via the def table.)

                // Ground-contact reporter on every non-foot part.
                if (!d.isFoot) go.AddComponent<Sensor_BodyPartContact>();

                // Impact reporter (audio/FX) on every part, feet included.
                var impact = go.AddComponent<Sensor_Impact>();
                impact.owner = this;
                impact.isFoot = d.isFoot;
            }
            _thighs = thighs.ToArray();

            // Joints
            Joints = new HingeJoint2D[JOINT_DEFS.Length];
            _maxSpeed = new float[JOINT_DEFS.Length];
            _maxTorque = new float[JOINT_DEFS.Length];
            for (int jointDefIndex = 0; jointDefIndex < JOINT_DEFS.Length; jointDefIndex++)
            {
                var d = JOINT_DEFS[jointDefIndex];
                var child = Parts[d.child];
                var parent = Parts[d.parent];
                var joint = child.gameObject.AddComponent<HingeJoint2D>();
                joint.connectedBody = parent;
                joint.autoConfigureConnectedAnchor = false;
                Vector2 worldAnchor = transform.TransformPoint(
                    new Vector3(d.ax * facingSign, d.ay + SPAWN_CLEARANCE, 0));
                joint.anchor = child.transform.InverseTransformPoint(worldAnchor);
                joint.connectedAnchor = parent.transform.InverseTransformPoint(worldAnchor);
                joint.useLimits = true;
                var lim = new JointAngleLimits2D
                {
                    min = facingSign > 0 ? d.min : -d.max,
                    max = facingSign > 0 ? d.max : -d.min
                };
                joint.limits = lim;
                joint.useMotor = true;
                var m = joint.motor; m.maxMotorTorque = d.torque * torqueScale; m.motorSpeed = 0; joint.motor = m;

                Joints[jointDefIndex] = joint;
                _maxSpeed[jointDefIndex] = d.speed;
                _maxTorque[jointDefIndex] = d.torque * torqueScale;
            }

            // Self-pass-through: ignore every collider pair within this biped.
            for (int a = 0; a < AllColliders.Count; a++)
                for (int b = a + 1; b < AllColliders.Count; b++)
                    Physics2D.IgnoreCollision(AllColliders[a], AllColliders[b], true);

            // Record spawn pose for resets.
            _initialLocalPos = new Vector3[n];
            _initialLocalRot = new Quaternion[n];
            for (int index = 0; index < n; index++)
            {
                _initialLocalPos[index] = Parts[index].transform.localPosition;
                _initialLocalRot[index] = Parts[index].transform.localRotation;
            }

            // Soft contact shadow so the body reads as grounded.
            var shadow = new GameObject("BlobShadow");
            shadow.transform.SetParent(transform, false);
            var blob = shadow.AddComponent<Systems_BlobShadow>();
            blob.target = Torso;
            blob.baseWidth = 0.9f * widthScale;
            blob.body = this;
        }


        public void ResetPose()
        {
            RestoreMotors();   // a limp body from last round must drive again
            for (int partIndex = 0; partIndex < Parts.Length; partIndex++)
            {
                Parts[partIndex].transform.localPosition = _initialLocalPos[partIndex];
                Parts[partIndex].transform.localRotation = _initialLocalRot[partIndex];
                Parts[partIndex].linearVelocity = Vector2.zero;
                Parts[partIndex].angularVelocity = 0f;
            }
            // Tiny random kick on the thighs so left/right symmetry breaks.
            foreach (var t in _thighs)
                t.angularVelocity = Random.Range(-12f, 12f);
            Physics2D.SyncTransforms();
        }

        /// True ragdoll: switch every joint motor OFF so the body goes limp and
        /// flops under gravity alone. Note that merely zeroing the action leaves
        /// the motors at full torque holding a zero-velocity target, which reads
        /// as rigid rather than lifeless — so this disables them outright.
        public void GoLimp()
        {
            if (IsLimp) return;
            IsLimp = true;
            for (int jointIndex = 0; jointIndex < Joints.Length; jointIndex++)
            {
                HingeJoint2D joint = Joints[jointIndex];
                if (joint == null) continue;
                var motor = joint.motor;
                motor.motorSpeed = 0f;
                motor.maxMotorTorque = 0f;
                joint.motor = motor;
                joint.useMotor = false;
            }
        }

        /// Re-arm the motors after a limp. ApplyMotor writes torque back per
        /// joint, but useMotor stays false until it is turned on again here.
        public void RestoreMotors()
        {
            if (!IsLimp) return;
            IsLimp = false;
            for (int jointIndex = 0; jointIndex < Joints.Length; jointIndex++)
            {
                HingeJoint2D joint = Joints[jointIndex];
                if (joint == null) continue;
                joint.useMotor = true;
                var motor = joint.motor;
                motor.maxMotorTorque = _maxTorque[jointIndex];
                joint.motor = motor;
            }
        }

        /// True while the body is a limp ragdoll (motors disabled).
        public bool IsLimp { get; private set; }

        /// True once this joint has been torn off by Systems_BodyDamage. The joint
        /// component is destroyed, so every read of it must be guarded.
        ///
        /// Detachment is a GAME-LAYER effect only: Systems_BodyDamage is spawned by
        /// Systems_GameMatchManager, and training runs under Systems_SumoMatchManager,
        /// which never creates one. No policy has ever seen a missing limb, and none
        /// needs re-training because of this.
        public bool IsDetached(int jointIndex) =>
            _detached != null && jointIndex >= 0 && jointIndex < _detached.Length && _detached[jointIndex];

        private bool[] _detached;

        /// Is this part still connected to the pelvis through unbroken joints?
        ///
        /// Walks the JOINT_DEFS parent chain rather than testing one joint, because
        /// severing a hip orphans the shin and the foot too, not just the thigh.
        ///
        /// The referee needs this. Systems_GameMatchManager.OutOfRing loses the round
        /// when FootNear or FootFar drops below the mat surface — sound while the foot
        /// is load-bearing, but a SEVERED leg is debris that gets shoved around and
        /// will very likely slide off the edge. Without this test, tearing off a
        /// fighter's leg would hand the round away the instant that leg fell off the
        /// platform, which is the opposite of what the blow earned.
        public bool IsPartAttached(int partIndex)
        {
            if (_detached == null) return true;
            int current = partIndex;
            // Bounded: the chain can visit each joint at most once.
            for (int guard = 0; current != 0 && guard <= JOINT_DEFS.Length; guard++)
            {
                int j = JointIndexForChild(current);
                if (j < 0) return true;             // nothing feeds this part
                if (_detached[j]) return false;     // severed somewhere above
                current = JOINT_DEFS[j].parent;
            }
            return true;
        }

        /// The joint that attaches this part to its parent, or -1 for the pelvis
        /// (the root, which hangs from nothing). Public so callers can name a limb
        /// by its ROOT PART rather than memorising a joint index — the JOINT_DEFS
        /// order has been reordered before and hardcoded indices went stale silently.
        public static int JointIndexForChild(int childPart)
        {
            for (int jointDefIndex = 0; jointDefIndex < JOINT_DEFS.Length; jointDefIndex++)
            {
                if (JOINT_DEFS[jointDefIndex].child == childPart) return jointDefIndex;
            }
            return -1;
        }

        /// Feet still carrying the fighter. PART_DEFS indices 3 and 6.
        public bool FootNearAttached => IsPartAttached(3);
        public bool FootFarAttached => IsPartAttached(6);

        /// Tear a limb off at the given joint. The severed parts keep their
        /// rigidbodies and colliders, so the limb falls to the clay and is shoved
        /// around like any other debris.
        ///
        /// Returns false if the joint was already gone, so callers can avoid
        /// re-firing presentation for a limb that is already off.
        /// Head mass folded into Chest's 13 kg by PART_DEFS. Pulled back out when
        /// the head comes off so the remaining torso is not still carrying it.
        private const float HEAD_MASS = 6f;

        public bool HeadDetached { get; private set; }

        /// Takes the head off.
        ///
        /// The head is NOT a separate body: it is a compound CircleCollider2D and a
        /// sprite parented to Chest, with its 6 kg folded into Chest's 13. Giving it
        /// a real rigidbody and neck hinge up front would redistribute mass on every
        /// fighter and invalidate all eight trained brains — CLAUDE.md is explicit
        /// that changing mass does that.
        ///
        /// So the split happens only at the moment of decapitation: an intact
        /// fighter has byte-identical dynamics to before, and the change lands on a
        /// body that has already lost the round. Returns the freed head, or null if
        /// it is already off.
        public Rigidbody2D DetachHead()
        {
            if (HeadDetached || Chest == null || HeadRenderer == null) return null;
            HeadDetached = true;

            Transform art = HeadRenderer.transform;
            var free = new GameObject("HeadDetached");
            free.transform.position = art.position;
            free.transform.rotation = art.rotation;
            free.transform.localScale = art.lossyScale;

            var sr = free.AddComponent<SpriteRenderer>();
            sr.sprite = HeadRenderer.sprite;
            sr.color = HeadRenderer.color;
            sr.sharedMaterial = HeadRenderer.sharedMaterial;
            sr.sortingOrder = HeadRenderer.sortingOrder;
            sr.flipX = HeadRenderer.flipX;

            var rb = free.AddComponent<Rigidbody2D>();
            rb.mass = HEAD_MASS * massScale;
            rb.linearDamping = 0.25f;
            rb.angularDamping = 0.8f;
            // Carry the chest's motion plus an upward kick, so it visibly pops off
            // rather than dropping straight down out of frame.
            rb.linearVelocity = Chest.linearVelocity + new Vector2(Random.Range(-1.5f, 1.5f), Random.Range(2f, 4.5f));
            rb.angularVelocity = Random.Range(-540f, 540f);

            var col = free.AddComponent<CircleCollider2D>();
            col.radius = 0.5f * headDiameter / Mathf.Max(0.0001f, art.lossyScale.x);
            if (HeadCollider != null) col.sharedMaterial = HeadCollider.sharedMaterial;

            // The stump: kill the hitbox, hide the attached art, and hand the mass
            // back so the torso does not keep carrying a head it no longer has.
            if (HeadCollider != null) HeadCollider.enabled = false;
            HeadRenderer.enabled = false;
            if (HeadMask != null) HeadMask.enabled = false;
            Chest.mass = Mathf.Max(0.1f, Chest.mass - HEAD_MASS * massScale);

            return rb;
        }

        public bool DetachJoint(int jointIndex)
        {
            if (Joints == null || jointIndex < 0 || jointIndex >= Joints.Length) return false;
            if (_detached == null) _detached = new bool[Joints.Length];
            if (_detached[jointIndex]) return false;

            HingeJoint2D joint = Joints[jointIndex];
            if (joint == null) return false;

            _detached[jointIndex] = true;
            // Destroy rather than disable: a disabled HingeJoint2D still reports
            // jointAngle, so the observation guard below could not tell the two
            // apart, and the limb would hang in place instead of falling.
            Destroy(joint);
            return true;
        }

        /// action in [-1,1] -> motor target speed; sign mirrored with facing.
        public void ApplyMotor(int jointIndex, float action)
        {
            // A limp body ignores motor commands outright. Without this the
            // agent's own zeroed-action path keeps writing full maxMotorTorque
            // back every decision step, which silently un-limps the ragdoll.
            if (IsLimp) return;

            var joint = Joints[jointIndex];
            // Detached limbs have no joint to drive. The agent still emits 13
            // actions every step — the action space is fixed — so this is the
            // only place that absorbs the missing one.
            if (joint == null) return;
            var m = joint.motor;
            m.motorSpeed = Mathf.Clamp(action, -1f, 1f) * _maxSpeed[jointIndex] * facingSign;
            m.maxMotorTorque = _maxTorque[jointIndex];
            joint.motor = m;
        }

        /// Clamp all body-part angular velocities against physics destabilization.
        public void ClampAngularVelocities()
        {
            for (int partIndex = 0; partIndex < Parts.Length; partIndex++)
                Parts[partIndex].angularVelocity =
                    Mathf.Clamp(Parts[partIndex].angularVelocity, -MAX_ANGULAR_VELOCITY, MAX_ANGULAR_VELOCITY);
        }

        // A destroyed HingeJoint2D throws on every read, and Agent_Biped's
        // observation loop calls both of these for all 13 joints every decision.
        // Reporting a detached joint as a neutral 0 keeps the 44-observation
        // vector the right SHAPE — which is what the brain contract requires —
        // and reads as "that joint is at rest", which is the closest honest
        // description of a limb lying on the clay.
        public float JointAngleNorm(int j)
        {
            HingeJoint2D joint = Joints[j];
            return joint == null ? 0f : joint.jointAngle * facingSign / 180f;
        }

        public float JointSpeedNorm(int j)
        {
            HingeJoint2D joint = Joints[j];
            return joint == null ? 0f : joint.jointSpeed * facingSign / 600f;
        }

        /// Applies the passive restoring torque to every joint.
        ///
        /// Runs every physics step, not every decision: the policy only acts once
        /// per DecisionPeriod (3 steps here), and passive tissue does not take
        /// turns. Equal and opposite on the connected body so the pair stays an
        /// internal force and cannot push the fighter around by itself.
        private void FixedUpdate()
        {
            if (Joints == null || _maxTorque == null) return;
            for (int jointIndex = 0; jointIndex < Joints.Length; jointIndex++)
            {
                HingeJoint2D joint = Joints[jointIndex];
                if (joint == null || !joint.enabled) continue;
                Rigidbody2D child = joint.attachedRigidbody;
                if (child == null || !child.simulated) continue;

                float budget = _maxTorque[jointIndex];
                float stiffness = budget * PASSIVE_STIFFNESS_FRAC / 90f;   // N-m per degree
                float damping = budget * PASSIVE_DAMPING_FRAC / 400f;      // N-m per deg/s
                float torque = -(joint.jointAngle * stiffness + joint.jointSpeed * damping);

                child.AddTorque(torque);
                Rigidbody2D parent = joint.connectedBody;
                if (parent != null && parent.simulated)
                {
                    parent.AddTorque(-torque);
                }
            }
        }

        /// Swap the face sprite, aspect-fitting to ~0.39 m tall regardless of
        /// the source image size (mood images vary a few pixels each).
        public void SetHeadSprite(Sprite s)
        {
            if (HeadRenderer == null || s == null) return;
            HeadRenderer.sprite = s;
            HeadRenderer.color = Color.white;
            // The mask carries the same photo on the same transform, so blood
            // parented under HeadDecalAnchor is clipped to the drawn silhouette
            // instead of hanging over the transparent margin beside the face.
            // Re-assigned here because Systems_FaceMood swaps this sprite
            // constantly during a match and the mask has to follow it.
            if (HeadMask != null)
            {
                HeadMask.sprite = s;
            }
            // Rig geometry is right-facing; left-facing source art flips the
            // other way so every character looks where it is walking.
            HeadRenderer.flipX = _faceArtFacesLeft ? facingSign > 0 : facingSign < 0;
            float sy = Mathf.Max(0.0001f, s.bounds.size.y);
            const float TARGET_H = 0.39f;
            HeadRenderer.transform.localScale =
                new Vector3(TARGET_H / (sy * _headCellW), TARGET_H / (sy * _headCellH), 1f);
        }

        public Rigidbody2D FootNear => Parts[3];
        public Rigidbody2D FootFar => Parts[6];

        /// Baseline skeleton is 69.6 kg at massScale 1.
        public float TotalMass => 69.6f * massScale;
    }
}
