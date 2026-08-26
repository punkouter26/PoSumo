using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace PoSumo
{
    /// Builds a 14-part 2D ragdoll biped from primitive colliders at runtime.
    /// v02 body: 4-segment articulated torso (pelvis, lower back, upper back,
    /// chest) linked by 3 motorized spine joints, plus legs (hip/knee/ankle)
    /// and arms (shoulder/elbow) — 13 motors total.
    /// Right-facing geometry is defined once; facingSign = -1 mirrors it.
    /// All colliders of the same biped ignore each other (limbs pass through
    /// their own body) but still collide with the ground and the opponent.
    /// Runs BEFORE Agent_Biped (which has no explicit order, so it sits at 0).
    ///
    /// Agent_Biped.Awake ends with base.Awake(), and ML-Agents' Agent.Awake fires
    /// OnEpisodeBegin, which calls ResetPose() on this component. Awake order
    /// between two components on the SAME GameObject is undefined in Unity, so that
    /// only worked by luck: if this component had not built yet, ResetPose walked a
    /// null _thighs and threw. It won in SCN_SUMO opened directly and LOST on the
    /// scene load a tournament match arrives through — two NullReferenceExceptions
    /// per bout, in the path the game actually ships (the build boots into the
    /// bracket). Ordering this first makes Build() finish before any reset can run.
    [DefaultExecutionOrder(-400)]
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

        /// Wins over `character.teamColor` when set, and set by nothing except
        /// `Systems_MatchRoster` on the second side of a MIRROR match (the bracket
        /// seeds five fighters into eight slots, so a fighter can and does meet
        /// itself — measured as a Nick-v-Nick FINAL on 2026-08-25).
        ///
        /// It has to be an override rather than an edit to the character sheet
        /// because both sides share the same ScriptableObject INSTANCE: writing
        /// `character.teamColor` would recolour the opponent too, and would dirty a
        /// shipped asset on disk in the Editor.
        ///
        /// NonSerialized on purpose — it is a per-bout decision, never scene data.
        [System.NonSerialized] public Color? teamColorOverride;

        private bool _faceArtFacesLeft;

        [Header("Build (1 = Matt's lightweight baseline)")]
        [Tooltip("Multiplies every part's mass. Heavyweight sumo ~2.")]
        public float massScale = 1f;
        [Tooltip("Widens the four torso segments (sumo belly). Heavyweight ~1.3.")]
        public float widthScale = 1f;
        [Tooltip("Multiplies joint motor torque caps to carry the extra mass.")]
        public float torqueScale = 1f;

        /// Runtime-only multiplier on top of the character's torqueScale. Written
        /// by Systems_MatchRoster for the BOT LADDER (EASY 0.7 / MEDIUM 1.0 / HARD
        /// 1.3) before Awake builds the joints; never serialized, never 1-not for
        /// a trained fighter.
        [System.NonSerialized] public float torqueMultiplier = 1f;

        /// Whole-body torque multiplier for transient GAME-LAYER effects; 1 is
        /// normal. Written by `Systems_CrowdMomentum` and by nothing else.
        ///
        /// NonSerialized on purpose: it is runtime state, not configuration, and a
        /// serialized copy would be exactly the kind of stale scene value this
        /// project keeps getting bitten by. `ResetPose` does NOT clear it — the
        /// crowd system owns it and restores 1 in its own OnDisable, because a
        /// round boundary is not the same thing as the crowd going quiet.
        ///
        /// The training referee has no equivalent, so no brain has ever trained
        /// against this. Keep the magnitude small; see Systems_CrowdMomentum.
        [System.NonSerialized] public float adrenaline = 1f;

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
        // loose floating beads beside the body rather than as cloth and hair.
        // They are in git history if they get revisited.
        //
        // ShadowCaster2D shadows were removed once too. They are back, with the
        // fix for what made them read badly: every part is enrolled in ONE
        // CompositeShadowCaster2D on the biped root, so the 14 heavily-overlapping
        // parts are a single caster group and stop shadowing each other. Without
        // that grouping the body interior fills with self-shadow and the wrestler
        // reads as a dark blob instead of a lit figure.
        [Header("Dressing (visual only — cannot invalidate a trained brain)")]
        /// Soft-tissue wobble on the torso art. OFF, and a const rather than the
        /// serialized bool it used to be.
        ///
        /// It is off because it is the one thing in the build that deliberately
        /// draws a body part somewhere its collider is not: the wobble slides the
        /// Art child up to 0.055 * widthScale * sqrt(massScale) metres off the
        /// rigidbody it rides, which on an 0.18 m pelvis is a third of the segment.
        /// Everything else here follows the rule that the drawn edge IS the
        /// colliding edge, and a shove that connects a visible gap early is the
        /// exact complaint that rule exists to answer.
        ///
        /// A const and not a public field because Agent_BipedBody sits on scene
        /// GameObjects: a public bool is SERIALIZED, so the four training scenes
        /// and SCN_SUMO each carry their own `enableJiggle: 1` and changing a code
        /// default would have done nothing at all. The stale scene values are now
        /// inert — the same reason Systems_TournamentBracket.ARENA_SCENE is a const.
        /// Flip this to true to get the wobble back; Systems_SoftBodyJiggle is
        /// untouched and still does its job.
        private const bool ENABLE_JIGGLE = false;

        [Tooltip("Cast 2D shadows onto the dohyo. Needs Systems_ArenaLighting.keyCastsShadows on as well — casters with no casting light do nothing, and a casting light with no casters still allocates a shadow render texture every frame. OFF because measured captures showed no visible shadow at gameplay framing; see the note in Systems_ArenaLighting.")]
        public bool castShadows = false;

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

        /// Head-only ground contact, for the head-on-the-mat losing rule.
        public Sensor_HeadContact HeadContact { get; private set; }

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
        /// Last motor speed COMMAND actually written, deg/s and facing-signed.
        /// Slew-limited toward the action's target so the command cannot invert
        /// in one step - see MOTOR_REVERSAL_SECONDS.
        private float[] _motorSpeedCmd;
        private float[] _maxTorque;
        // Derived from _maxTorque once in Build(). Both are constant for the body's
        // whole life, and FixedUpdate is the hottest path in the project — 13 joints
        // per biped, 10 bipeds in a training scene, 50 Hz — so they are not worth
        // recomputing 6,500 times a second to get the same two numbers back.
        private float[] _passiveStiffness;   // N-m per degree
        private float[] _passiveDamping;     // N-m per deg/s
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

        /// Hill's a/F0 for mixed skeletal muscle. Shapes how fast concentric torque
        /// falls off with shortening velocity; 0.25 is the textbook value.
        private const float HILL_A_F0 = 0.25f;
        /// Eccentric torque as a multiple of isometric. Real muscle resists a
        /// stretch harder than it shortens, which is what lets a braced fighter
        /// hold a shove without being driven back.
        private const float ECCENTRIC_GAIN = 1.5f;
        /// Muscle activation / deactivation time constants (seconds). Rise is
        /// faster than fall, as in vivo.
        private const float ACT_RISE_TAU = 0.05f;
        private const float ACT_FALL_TAU = 0.07f;

        /// Seconds a joint's motor command needs to travel its FULL range, i.e. to
        /// reverse from -maxSpeed to +maxSpeed.
        ///
        /// This is the other half of the activation dynamics below, and it was
        /// missing. `ApplyMotor` smoothed the TORQUE over 50-70 ms but wrote
        /// `motorSpeed` straight from the action, so the target VELOCITY could
        /// invert between two physics steps. A policy whose output alternates near
        /// the rails — which PPO's are prone to do, and which `jerkPenalty` only
        /// discourages rather than prevents — then whipped a knee across 1000 deg/s
        /// of command in 20 ms. That is the flicker: not the peak speed, but the
        /// instantaneous reversal.
        ///
        /// 0.12 s is roughly how quickly a person can reverse a loaded limb.
        private const float MOTOR_REVERSAL_SECONDS = 0.12f;

        /// Torque budget actually in force at each joint this step, after the
        /// force-velocity scaling and the activation lag. Persisted because the
        /// lag is a first-order filter over time, not a per-step function.
        private float[] _appliedTorque;

        /// PERIPHERAL FATIGUE, per joint, 0 = fresh and 1 = fully fatigued.
        ///
        /// The Hill curve above already makes fast motion weak, but it is memoryless:
        /// a joint that has been driven flat out for fifteen seconds delivers exactly
        /// as much torque as one that just started. That is the last place the body
        /// was an ideal machine rather than a muscle, and it is what let a bout be
        /// decided purely on who could hold full torque longest — which was always
        /// both of them.
        ///
        /// The model is the two-state reduction of Xia's three-compartment muscle
        /// fatigue model: a fraction of the joint's capacity moves from rested to
        /// fatigued at a rate proportional to the load being asked of it, and drains
        /// back at a rate proportional to how much of the capacity is idle. Both
        /// terms are scaled by the remaining pool, so fatigue saturates smoothly at
        /// 1 instead of running off, and recovery cannot overshoot below 0.
        private float[] _fatigue;

        /// Fatigue accumulation rate at full load, per second. The time constant is
        /// 1/rate ≈ 17 s against a 20 s round (`GameTuning.roundTimeoutSeconds`), so a
        /// fighter that holds maximum effort for a whole round arrives at the bell
        /// around 0.70 fatigued and a fighter that paces itself does not. Faster than
        /// this and the round is decided by the clock rather than by the wrestling;
        /// slower and the term may as well not exist.
        private const float FATIGUE_RATE = 0.06f;
        /// Recovery rate at zero load, per second — deliberately slower than a real
        /// unloaded muscle's, because the only genuinely unloaded moments in a bout
        /// are between rounds, and `ResetPose` already clears fatigue outright there.
        /// What this actually governs is how much a joint recovers during the parts of
        /// a bout it is not driving, which is a partial, loaded recovery.
        private const float RECOVERY_RATE = 0.10f;
        /// Fraction of the torque budget that fatigue can take away. At 1.0 a tired
        /// fighter would go completely limp, which is not what happens — a fatigued
        /// muscle loses a large share of its maximum voluntary contraction but keeps
        /// working. 0.35 leaves a fully spent joint at 65% strength.
        private const float FATIGUE_DEPTH = 0.35f;

        /// End-range passive stiffening. Real joints are slack in mid-range and get
        /// very stiff in the last stretch before the stop; a single linear spring
        /// (what this used to be) is equally stiff everywhere and lets limbs park
        /// against their limits. Cubic in the fraction of range used past the knee
        /// point, added on top of the linear term.
        private const float END_RANGE_KNEE = 0.7f;    // fraction of half-range where stiffening starts
        private const float END_RANGE_GAIN = 6f;      // multiple of the linear term at the very stop

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
            /// False for joints the policy does not drive. They still carry limits
            /// and passive resistance, but their motor is DISABLED — leaving it on
            /// would not make them free, it would make them a brake: Build() sets
            /// motorSpeed 0, so an un-driven motor actively holds the joint still at
            /// its full torque budget. Unpowered joints must come AFTER every
            /// powered one, because ApplyMotor indexes 0..ActionCount-1.
            public bool powered;
            public JointDef(int c, int p, float ax_, float ay_, float mn, float mx, float t, float s,
                            bool pow = true)
            { child = c; parent = p; ax = ax_; ay = ay_; min = mn; max = mx; torque = t; speed = s; powered = pow; }
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
            new PartDef("FootNear",  0.20f,  0.08f,   0.8f, 0.026f, 0.04f,  2, 0.95f, false, true),
            new PartDef("ThighFar",  0.14f,  0.431f,  7f,  0f,    0.749f, -2, 0.78f),
            new PartDef("ShinFar",   0.11f,  0.433f,  3.5f, 0f,   0.317f, -2, 0.78f),
            new PartDef("FootFar",   0.20f,  0.08f,   0.8f, 0.026f, 0.04f, -2, 0.78f, false, true),
            new PartDef("LowerBack", 0.30f,  0.14f,   7f,  0f,    1.195f,  0, 0.97f),
            new PartDef("UpperBack", 0.31f,  0.14f,   7f,  0f,    1.324f,  0, 0.99f),
            new PartDef("Chest",     0.34f,  0.18f,  13f,  0f,    1.471f,  0, 1f),
            new PartDef("UArmNear",  0.10f,  0.327f,  2.5f, 0f,   1.308f,  3, 0.9f),
            new PartDef("FArmNear",  0.09f,  0.28f,   1.8f, 0f,   1.004f,  3, 0.9f),
            new PartDef("UArmFar",   0.10f,  0.327f,  2.5f, 0f,   1.308f, -3, 0.76f),
            new PartDef("FArmFar",   0.09f,  0.28f,   1.8f, 0f,   1.004f, -3, 0.76f),
            // TOES (metatarsophalangeal segment). APPENDED, never inserted: Parts[3]
            // and Parts[6] are the feet by index, and Systems_BodyDamage.RegionOf
            // switches on the part index, so renumbering the existing table would
            // silently re-map damage regions and the foot accessors.
            //
            // The feet above were SHORTENED 0.268 -> 0.20 and lightened 1.0 -> 0.8 to
            // pay for these, so total foot length stays at Winter's 0.152H = 0.268 m
            // and total foot mass at 1.0 kg. The heel does not move: it sat at
            // x = 0.06 - 0.268/2 = -0.074, and 0.026 - 0.20/2 is the same -0.074.
            //
            // isFoot MUST be true. Not for the friction material (though they want
            // that too) but because Build() puts a Sensor_BodyPartContact on every
            // NON-foot part, and that sensor is what feeds NonFootGroundContacts ->
            // IsDown. A toe registered as a body part would mean every fighter is
            // permanently "down" from the moment it stands up.
            new PartDef("ToeNear",   0.068f, 0.06f,   0.2f, 0.160f, 0.03f,  2, 0.95f, false, true),
            new PartDef("ToeFar",    0.068f, 0.06f,   0.2f, 0.160f, 0.03f, -2, 0.78f, false, true),
        };

        // child, parent, anchor(x,y in root space), min, max (deg), torque, speed
        //
        // RANGES. The ankle, the three spine joints and the shoulders were SYMMETRIC and bent
        // backwards far past human range — a fighter could hyperextend his spine
        // 75 degrees and throw an arm 160 degrees behind him, which is the single
        // largest "that is not a body" contributor after motor saturation. They are
        // now clamped to roughly human TOTAL range:
        //   ankle    +/-45 -> +/-25 -> +/-35 (human TOTAL ROM ~70 deg, and +/-25 gave
        //            only 50, capping the joint that actually drives a sumo forward.
        //            Torque 120 -> 160 against a human plantarflexor peak of 150-200.)
        //            STILL SYMMETRIC, and that is deliberate restraint, not an
        //            oversight: the real ankle is asymmetric (~20 dorsi / 50 plantar),
        //            but which SIGN is plantarflexion has to be MEASURED here, not
        //            reasoned out — see the sign-convention note below, which is the
        //            bug that survived the whole life of this project. Widen-symmetric
        //            is the change that cannot be wrong. Make it asymmetric only after
        //            probing the sign the same way the hip/knee/elbow were probed.
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
            new JointDef(1, 0,  0f, 0.964f,-120f,  30f, 300f, 260f), // hip near
            new JointDef(2, 1,  0f, 0.533f,   0f, 150f, 250f, 320f), // knee near
            new JointDef(3, 2,  0f, 0.10f,  -35f,  35f, 160f, 220f), // ankle near
            new JointDef(4, 0,  0f, 0.964f,-120f,  30f, 300f, 260f), // hip far
            new JointDef(5, 4,  0f, 0.533f,   0f, 150f, 250f, 320f), // knee far
            new JointDef(6, 5,  0f, 0.10f,  -35f,  35f, 160f, 220f), // ankle far
            new JointDef(7, 0,  0f, 1.130f, -20f,  20f, 180f, 160f), // spine 1 (pelvis->lower back)
            new JointDef(8, 7,  0f, 1.259f, -20f,  20f, 180f, 160f), // spine 2 (lower->upper back)
            new JointDef(9, 8,  0f, 1.388f, -20f,  20f, 180f, 160f), // spine 3 (upper back->chest)
            new JointDef(10, 9, 0f, 1.471f,-120f, 120f,  80f, 320f), // shoulder near (on chest)
            new JointDef(11,10, 0f, 1.144f,-150f,   0f,  60f, 320f), // elbow near
            new JointDef(12, 9, 0f, 1.471f,-120f, 120f,  80f, 320f), // shoulder far (on chest)
            new JointDef(13,12, 0f, 1.144f,-150f,   0f,  60f, 320f), // elbow far
            // MTP (toe) joints — UNPOWERED, and they must stay last. ApplyMotor and
            // CollectObservations both index 0..ActionCount-1, so appending here adds
            // a joint to the body without touching the 13-action / 42-obs contract
            // that every trained brain depends on.
            //
            // Unpowered but not inert: they carry limits and the same passive
            // stiffness and damping as every other joint, which is what a real MTP
            // is during stance — a sprung hinge that stores energy as the heel rises
            // and returns it at toe-off. Before this the foot was one rigid box, so
            // push-off simply stopped at a flat plate and the gait could only shuffle.
            //
            // Anchor sits where the shortened foot now ends: -0.074 + 0.20 = 0.126.
            // Range is symmetric for the same reason the ankle is — the real MTP is
            // asymmetric (~60 extension, ~30 flexion) but the sign has to be measured
            // here, not assumed.
            new JointDef(14, 3, 0.126f, 0.04f, -35f, 35f, 40f, 400f, false), // toe near
            new JointDef(15, 6, 0.126f, 0.04f, -35f, 35f, 40f, 400f, false), // toe far
        };

        private void Awake()
        {
            if (character != null)
            {
                teamColor = character.teamColor;
                headSprite = character.headSprite;
                massScale = character.massScale;
                widthScale = character.widthScale;
                // torqueMultiplier is runtime-only (the BOT LADDER's difficulty dial,
                // written by Systems_MatchRoster before this Awake); 1 for everyone else.
                torqueScale = character.torqueScale * torqueMultiplier;
                _faceArtFacesLeft = character.faceArtFacesLeft;
            }
            // AFTER the character block, so a mirror match's second side keeps every
            // other property of the shared sheet and changes only its colour.
            if (teamColorOverride.HasValue)
            {
                teamColor = teamColorOverride.Value;
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

        /// Enrols one Art child in this wrestler's shadow caster group.
        /// ShadowCaster2D.Awake derives an empty shape path from the Renderer on
        /// the same GameObject, so no shape has to be authored here — but it means
        /// the component must be added AFTER the SpriteRenderer, which is why this
        /// is a call at the end of the part loop rather than part of the prefab-ish
        /// setup above.
        private void AddShadowCaster(GameObject art)
        {
            if (!castShadows)
            {
                return;
            }
            var caster = art.AddComponent<ShadowCaster2D>();
            // CastShadow, not CastAndSelfShadow: the body is lit by the rig and the
            // BodyLit shader, and self-shadowing on top of that only muddies the
            // limbs. The shadow this wants to sell is the one on the clay.
            caster.castingOption = ShadowCaster2D.ShadowCastingOptions.CastShadow;
            caster.selfShadows = false;
            caster.castsShadows = true;
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

            // One caster group for the whole wrestler. ShadowCasterGroup2DManager
            // walks up from each ShadowCaster2D to the nearest ShadowCasterGroup2D
            // ancestor, so putting this on the root before the parts are built is
            // what stops the 14 overlapping parts self-shadowing into a dark blob.
            // Same intent as the pairwise collision ignores: a biped is one object.
            if (castShadows)
            {
                gameObject.AddComponent<CompositeShadowCaster2D>();
            }

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

                // Shadow shape comes from this renderer's bounds: ShadowCaster2D
                // .Awake fills an empty shape path from the Renderer it sits on, so
                // the caster must go on the Art child (which has the sprite), not on
                // the physics GameObject (which has none).
                AddShadowCaster(art);


                var rb = go.AddComponent<Rigidbody2D>();
                rb.mass = d.mass * SegmentMassScale(d.name);
                // Soft tissue. Real limbs are not frictionless linkages: muscle,
                // fat and skin dissipate energy continuously, which is most of why
                // a living body does not ring like a pendulum. At the old 0.05 the
                // ragdoll had effectively no passive damping and every limb whipped
                // and oscillated, which read as a puppet wearing motors.
                rb.linearDamping = 0.25f;
                // 0.8 was ~16x Unity's default and was doing the job the actuator
                // model should do: bleeding off rotational energy everywhere to stop
                // an ideal servo catapulting the body. With the Hill curve and the
                // activation lag in ApplyMotor that brake is no longer load-bearing,
                // and at 0.8 it also flattened the follow-through a real body has.
                rb.angularDamping = 0.35f;
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
                if (ENABLE_JIGGLE && IsTorsoPart(d.name))
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
                    // UNLIT, unlike every other part. The head carries a photograph
                    // of a face; the rest of the body is flat tinted primitives that
                    // need the rig to read as a body at all. Lighting a correctly
                    // exposed photo with key + global + two rims, and then adding
                    // the BodyLit rim/wrap/sweat terms on top, clips the highlights
                    // and washes the facial detail out. See
                    // Systems_ArenaLighting.UnlitSpriteMaterial.
                    hsr.sharedMaterial = Systems_ArenaLighting.UnlitSpriteMaterial();
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

                    // The head is the one part whose silhouette a viewer actually
                    // tracks, so it casts too. It joins the same composite group as
                    // the limbs, so it does not shadow the chest it sits on.
                    AddShadowCaster(head);
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
                        // headDiameter, not the old 0.312: CircleSprite is one world
                        // unit across, so this draws the head at exactly the size of
                        // the CircleCollider2D built below. At 0.312 the plain head
                        // was drawn 38% smaller than the thing that gets hit.
                        head.transform.localScale =
                            new Vector3(headDiameter / parentW, headDiameter / d.h, 1f);
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
                    HeadContact = headHit.AddComponent<Sensor_HeadContact>();
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
            _passiveStiffness = new float[JOINT_DEFS.Length];
            _passiveDamping = new float[JOINT_DEFS.Length];
            _appliedTorque = new float[JOINT_DEFS.Length];
            _motorSpeedCmd = new float[JOINT_DEFS.Length];
            _fatigue = new float[JOINT_DEFS.Length];
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
                // An un-driven motor is a BRAKE, not a free joint: motorSpeed 0 with a
                // torque budget actively holds the joint still. Unpowered joints get
                // the motor switched off so they swing on their limits and passive
                // resistance alone.
                joint.useMotor = d.powered;
                if (d.powered)
                {
                    var m = joint.motor; m.maxMotorTorque = d.torque * torqueScale; m.motorSpeed = 0; joint.motor = m;
                }

                Joints[jointDefIndex] = joint;
                _maxSpeed[jointDefIndex] = d.speed;
                _maxTorque[jointDefIndex] = d.torque * torqueScale;
                _passiveStiffness[jointDefIndex] = _maxTorque[jointDefIndex] * PASSIVE_STIFFNESS_FRAC / 90f;
                _passiveDamping[jointDefIndex] = _maxTorque[jointDefIndex] * PASSIVE_DAMPING_FRAC / 400f;
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
            // Fresh legs every episode. Carrying fatigue across the reset would make
            // an episode's difficulty depend on how hard the PREVIOUS one was fought,
            // which is a hidden non-stationary term in the reward — the agent would be
            // scored against a body whose capability it had no way to observe at t=0.
            // In the game this is the between-rounds rest; in training it is the only
            // thing that keeps episodes comparable to each other.
            //
            // Cleared BEFORE RestoreMotors, which now scales the torque it writes back
            // by StrengthFactor and would otherwise re-arm the body at last round's
            // exhaustion.
            if (_fatigue != null)
            {
                System.Array.Clear(_fatigue, 0, _fatigue.Length);
            }
            // Same reasoning as fatigue: a slew-limited motor command is carried
            // state, so leaving last episode's value in place would start the new
            // one mid-swing and make its opening dynamics depend on how the previous
            // episode happened to end.
            if (_motorSpeedCmd != null)
            {
                System.Array.Clear(_motorSpeedCmd, 0, _motorSpeedCmd.Length);
            }
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
                // Fatigue-scaled, like every other write of maxMotorTorque. ApplyMotor
                // overwrites this on the next decision anyway, but a body restored
                // mid-round would otherwise get one free step at full strength.
                motor.maxMotorTorque = _maxTorque[jointIndex] * StrengthFactor(jointIndex);
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
            // Head mass rides the CHEST's scale, because that is the body it is
            // folded into — a compound collider on Chest, not a body of its own.
            rb.mass = HEAD_MASS * SegmentMassScale("Chest");
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
            Chest.mass = Mathf.Max(0.1f, Chest.mass - HEAD_MASS * SegmentMassScale("Chest"));

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
            // Slew-limited, not written straight through: see MOTOR_REVERSAL_SECONDS.
            // The clamp is on the COMMAND, so the joint can still be driven to full
            // speed - it just cannot get there, or reverse, instantly.
            float desiredSpeed = Mathf.Clamp(action, -1f, 1f) * _maxSpeed[jointIndex] * facingSign;
            float maxDelta = 2f * _maxSpeed[jointIndex] * Time.fixedDeltaTime / MOTOR_REVERSAL_SECONDS;
            _motorSpeedCmd[jointIndex] = Mathf.MoveTowards(_motorSpeedCmd[jointIndex], desiredSpeed, maxDelta);
            m.motorSpeed = _motorSpeedCmd[jointIndex];

            // FORCE-VELOCITY (Hill). A HingeJoint2D motor is an ideal servo: left
            // alone it delivers its FULL torque at any speed up to the target, so a
            // hip could push 300 N-m while already swinging at 400 deg/s. No muscle
            // does that — concentric force falls hyperbolically as the muscle
            // shortens faster and reaches zero at maximum shortening velocity.
            //
            // This is the physical version of what effortPenalty was reaching for
            // with a reward term: flailing at full speed now costs torque by
            // construction rather than by being fined for it afterwards.
            //
            // Hill's a/F0 = b/v0 = 0.25 is the classic value for mixed skeletal
            // muscle. Eccentric (motor fighting the joint's motion) is NOT reduced —
            // real muscle is ~1.5x stronger resisting a stretch than shortening —
            // so bracing against a shove keeps its full budget.
            float speedFrac = Mathf.Clamp01(Mathf.Abs(joint.jointSpeed) / Mathf.Max(1f, _maxSpeed[jointIndex]));
            bool concentric = joint.jointSpeed * m.motorSpeed > 0f;
            float fv = concentric ? (1f - speedFrac) / (1f + HILL_A_F0 * speedFrac * 4f) : ECCENTRIC_GAIN;
            // FATIGUE multiplies the budget rather than the command, so a tired
            // fighter still reaches for the same joint angles — it just cannot hold
            // them against as much load. Scaling motorSpeed instead would read as a
            // fighter that has decided to move slowly, which is a different thing.
            //
            // It stacks with the Hill term on purpose: force-velocity is how weak you
            // are RIGHT NOW because of how fast you are moving, fatigue is how weak
            // you are because of what you have already spent. Eccentric bracing keeps
            // its 1.5x gain but pays the same fatigue tax, so a fighter can be worn
            // down while holding a shove — which is exactly how a real bout is won.
            // ADRENALINE is a whole-body, game-layer multiplier (crowd momentum).
            // It sits here beside the Hill and fatigue terms rather than scaling
            // the ACTION, because scaling the action does nothing: ApplyMotor
            // clamps the command to [-1,1] before it reaches motorSpeed, so a
            // boost applied there is silently discarded while a cut is not.
            // Going through `target` also means it inherits the activation
            // dynamics below, so the crowd lifting a fighter ramps over ~50 ms
            // instead of stepping.
            float target = _maxTorque[jointIndex] * Mathf.Clamp(fv, 0f, ECCENTRIC_GAIN)
                           * StrengthFactor(jointIndex) * adrenaline;

            // ACTIVATION DYNAMICS. Muscle torque cannot step: it rises over ~50 ms
            // and falls over ~70 ms. At decision period 3 (60 ms) the policy could
            // otherwise invert a joint's entire torque between two decisions, which
            // is the other half of why the ragdolls snap rather than move.
            float tau = target > _appliedTorque[jointIndex] ? ACT_RISE_TAU : ACT_FALL_TAU;
            _appliedTorque[jointIndex] = Mathf.Lerp(_appliedTorque[jointIndex], target,
                                                    1f - Mathf.Exp(-Time.fixedDeltaTime / tau));

            m.maxMotorTorque = _appliedTorque[jointIndex];
            joint.motor = m;
        }

        /// Remaining strength at one joint, 1 = fresh down to 1-FATIGUE_DEPTH spent.
        /// Null-guarded because RestoreMotors can be reached from a body that has
        /// been built but never stepped.
        private float StrengthFactor(int jointIndex) =>
            _fatigue == null ? 1f : 1f - FATIGUE_DEPTH * _fatigue[jointIndex];

        /// Whole-body stamina, 1 = fresh and 0 = fully spent, averaged over the
        /// POWERED joints only. The unpowered toe hinges never load a motor, so
        /// including them would peg stamina near 2/15 of fresh forever.
        ///
        /// This is the number the agent observes and the HUD reads. It is a mean
        /// rather than a minimum because a single exhausted ankle is not the same
        /// as an exhausted fighter, and a minimum would make the signal jump around
        /// as different joints take turns being the worst.
        public float Stamina
        {
            get
            {
                if (_fatigue == null) return 1f;
                float sum = 0f;
                for (int jointIndex = 0; jointIndex < Agent_Biped.ActionCount; jointIndex++)
                {
                    sum += _fatigue[jointIndex];
                }
                return 1f - sum / Agent_Biped.ActionCount;
            }
        }

        /// Integrates one physics step of the fatigue model for every powered joint.
        ///
        /// Load is read from `GetMotorTorque`, the torque the solver ACTUALLY applied
        /// this step, not from the commanded action. That distinction is the whole
        /// point: bracing against a shove is a near-zero action holding a near-maximum
        /// torque, and any measure taken from the action vector would score that as
        /// resting. Isometric work is most of what a sumo bout is.
        private void IntegrateFatigue(float dt)
        {
            for (int jointIndex = 0; jointIndex < Agent_Biped.ActionCount; jointIndex++)
            {
                HingeJoint2D joint = Joints[jointIndex];
                // A limp body, a severed limb or a disabled joint is doing no work.
                // Falling through to the recovery branch (load 0) is correct for all
                // three — a torn-off arm should read as resting, not as straining.
                float load = 0f;
                if (!IsLimp && joint != null && joint.enabled && joint.useMotor
                    && _maxTorque[jointIndex] > 0f)
                {
                    load = Mathf.Clamp01(Mathf.Abs(joint.GetMotorTorque(dt))
                                         / _maxTorque[jointIndex]);
                }

                float f = _fatigue[jointIndex];
                float rest = 1f - f;
                // Explicit Euler is safe here because both rates are ~0.1/s against a
                // 0.02 s step — the per-step change is under 0.2% of the pool, orders
                // of magnitude inside the stability limit.
                f += (load * FATIGUE_RATE * rest - (1f - load) * RECOVERY_RATE * f) * dt;
                _fatigue[jointIndex] = Mathf.Clamp01(f);
            }
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
        /// Peak restoring torque, in N·m, applied to the pelvis when the torso is
        /// fully on its side. Sized against the hip's 300 N·m budget: meaningful
        /// help, nowhere near enough to stand the body up on its own.
        private const float POSTURAL_ASSIST_TORQUE = 55f;

        /// Damping on the same axis, N·m per rad/s. Without it the assist is a
        /// spring with no loss and the torso oscillates instead of settling.
        private const float POSTURAL_ASSIST_DAMPING = 9f;

        /// Below this uprightness the assist fades out entirely. A fighter who has
        /// genuinely been thrown should STAY thrown — otherwise the aid quietly
        /// rewrites the ring-out and knockdown rules by righting a losing body, and
        /// the referee's IsDown test stops meaning anything.
        private const float POSTURAL_ASSIST_FLOOR = 0.15f;

        /// A postural reflex the policy cannot opt out of.
        ///
        /// WHY THIS IS PHYSICS AND NOT REWARD. Six runs have now tried to buy an
        /// upright gait with shaping — tall01-tall04, gait01, and obs01 — and the
        /// arithmetic in Reward_WalkObjective explains why they could not: in
        /// Mode.Walk a fall costs about -4 (the -1 terminal, which also discards
        /// that step's shaping, plus the forgone +3 graduation) while the entire
        /// per-step advantage of standing tall over crawling is 0.0063. Break-even
        /// is an extra fall probability of 0.16% per step. Crouching is simply
        /// CORRECT PLAY under that trade, and no coefficient changes it.
        ///
        /// So this does not pay the fighter to stand. It makes standing cheaper, by
        /// supplying part of the postural tone a real trunk has and this ragdoll
        /// does not. `CLAUDE.md` nominates exactly this after gait01: "a stability
        /// aid the policy cannot opt out of (a torso-height constraint enforced in
        /// physics rather than paid for in reward)".
        ///
        /// TORQUE, NOT LIFT. An upward force would be anti-gravity — the body would
        /// read as floaty and, worse, would change how much force it takes to push
        /// one out of the ring, which is the core of the game. A restoring torque
        /// toward world-up adds no vertical energy; it only resists TIPPING, which
        /// is what a postural reflex actually does.
        ///
        /// This changes the dynamics every brain was fitted against, so it needs the
        /// usual short corrective pass at reduced learning rate — not a cold retrain.
        private void ApplyPosturalAssist()
        {
            if (Torso == null || !Torso.simulated)
            {
                return;
            }

            // +1 fully upright, 0 on its side, negative inverted.
            float upright = Torso.transform.up.y;
            if (upright <= POSTURAL_ASSIST_FLOOR)
            {
                return;
            }

            // Signed tilt from vertical, in radians, in the plane the game runs in.
            float tilt = Mathf.Atan2(Torso.transform.up.x, Torso.transform.up.y);

            // Scaled by how upright the body already is, so the aid is strongest for
            // a fighter who is nearly standing and vanishes for one who is going
            // down anyway. That keeps it a stabiliser rather than a righting reflex.
            float authority = Mathf.InverseLerp(POSTURAL_ASSIST_FLOOR, 1f, upright);

            float torque = (-tilt * POSTURAL_ASSIST_TORQUE
                            - Torso.angularVelocity * Mathf.Deg2Rad * POSTURAL_ASSIST_DAMPING)
                           * authority;
            Torso.AddTorque(torque);
        }

        private void FixedUpdate()
        {
            SampleFootLoad(FootNear, out FootDownNear, out FootLoadNear);
            SampleFootLoad(FootFar, out FootDownFar, out FootLoadFar);

            if (Joints == null || _passiveStiffness == null) return;

            // Fatigue integrates HERE, at 50 Hz, and not in ApplyMotor. ApplyMotor is
            // driven by OnActionReceived, which stops being called the moment the
            // referee cuts actions or the presentation layer takes the body — and a
            // fighter standing between rounds must still be recovering. Physics time,
            // not decision time, is what a muscle runs on.
            IntegrateFatigue(Time.fixedDeltaTime);
            ApplyPosturalAssist();

            for (int jointIndex = 0; jointIndex < Joints.Length; jointIndex++)
            {
                HingeJoint2D joint = Joints[jointIndex];
                if (joint == null || !joint.enabled) continue;
                Rigidbody2D child = joint.attachedRigidbody;
                if (child == null || !child.simulated) continue;

                float torque = -(joint.jointAngle * _passiveStiffness[jointIndex]
                                 + joint.jointSpeed * _passiveDamping[jointIndex]);

                // End-range stiffening: slack in mid-range, very stiff approaching
                // the stop. Measured against how far into ITS OWN half-range the
                // joint is, so an asymmetric joint (hip, knee, ankle, elbow) stiffens
                // at the right place on each side rather than around zero.
                var lim = joint.limits;
                float span = joint.jointAngle >= 0f ? lim.max : lim.min;
                if (!Mathf.Approximately(span, 0f))
                {
                    float used = Mathf.Clamp01(joint.jointAngle / span);
                    if (used > END_RANGE_KNEE)
                    {
                        float over = (used - END_RANGE_KNEE) / (1f - END_RANGE_KNEE);
                        torque -= Mathf.Sign(joint.jointAngle) * over * over * over
                                  * END_RANGE_GAIN * Mathf.Abs(span) * _passiveStiffness[jointIndex];
                    }
                }

                child.AddTorque(torque);
                Rigidbody2D parent = joint.connectedBody;
                if (parent != null && parent.simulated)
                {
                    parent.AddTorque(-torque);
                }
            }
        }

        /// Swap the face sprite, aspect-fitting it into the HEAD HITBOX regardless
        /// of the source image size (mood images vary a few pixels each).
        ///
        /// Fitted to headDiameter on the sprite's LONGEST side, so the drawn face
        /// is inscribed in the circle that actually collides. It used to fit 0.39 m
        /// on the height alone: a square photo was therefore drawn 22% smaller than
        /// the 0.5 m hitbox around it — every fighter's head got hit through a ring
        /// of empty space — and a wide photo overflowed the hitbox the other way.
        /// Fitting the longest side makes both cases land inside the collider.
        ///
        /// **Known and deliberate: a photo head looks SMALLER than a fallback disc,
        /// and this is not a bug to fix in code (checked 2026-08-07).** The plain
        /// circle and the BOT disc are drawn at exactly `headDiameter` in both axes,
        /// so they ARE the collider silhouette — which is what the project's
        /// "drawn edge is the colliding edge" rule asks for. A photo is instead
        /// INSCRIBED in that circle by its longest side, so Nick's 1.33:1 image
        /// draws 0.5 m wide but only 0.375 m tall and reads visibly smaller beside
        /// Standard's full disc. Both paths are individually correct against the
        /// collider; the gap is inherent to fitting a non-square image into a
        /// circle. Fitting the SHORTEST side instead would just break the rule the
        /// other way, with the drawn face overhanging the hitbox. The only real fix
        /// is square-cropped face art, which is an asset task — do not re-open this
        /// one in code.
        ///
        /// True once the head has been repainted as the BOT's solid disc. Locks out
        /// face art permanently — see ApplyBotHead.
        public bool BotHead { get; private set; }

        /// Paint the head as a solid colour disc so the BOT is obvious at a glance
        /// next to the photo-faced fighters.
        ///
        /// Reuses the plain-circle path the build already has for a character with
        /// no face art, including its scaling: CircleSprite is one world unit
        /// across, so dividing by the stored head cell draws the disc at exactly
        /// headDiameter — the same size as the CircleCollider2D. Art only; the
        /// collider is untouched, so this cannot change the dynamics or invalidate
        /// a brain.
        ///
        /// Sets BotHead, which makes SetHeadSprite a no-op from here on. That guard
        /// is the point: Systems_FaceMood swaps the face sprite constantly during a
        /// match, so without it the blue head would survive for a frame and then be
        /// overwritten by an expression the BOT is not even driving.
        public void ApplyBotHead(Color color)
        {
            BotHead = true;
            if (HeadRenderer == null) return;
            Sprite circle = CircleSprite();
            HeadRenderer.sprite = circle;
            HeadRenderer.color = color;
            HeadRenderer.flipX = false;   // a disc has no facing
            if (HeadMask != null)
            {
                // Keep the decal mask on the same silhouette, or blood and bruises
                // would clip against whatever face was there before.
                HeadMask.sprite = circle;
            }
            HeadRenderer.transform.localScale =
                new Vector3(headDiameter / Mathf.Max(0.0001f, _headCellW),
                            headDiameter / Mathf.Max(0.0001f, _headCellH), 1f);
        }

        public void SetHeadSprite(Sprite s)
        {
            // The BOT keeps its solid head against everything, including FaceMood.
            if (BotHead) return;
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
            // MEASURED 2026-08-26, so nobody re-derives it: the sprite importer
            // already TIGHT-TRIMS this art. Nick's texture is 576x433 but his
            // `textureRect` is (137, 0, 304, 409) — i.e. the transparent margin a
            // background-removed PNG carries is already gone by the time `s.bounds`
            // is read. An attempt to re-fit against the opaque pixel bounds was
            // tried here and reverted: it scans every pixel, needs read/write
            // textures, and returns ~(1,1) because the rect it scans is already the
            // trimmed one. `s.bounds` is the right thing to fit against.
            // Fit the SHORTEST side to headDiameter, not the longest.
            //
            // The hitbox is a CIRCLE of headDiameter and a face is not circular, so
            // one of the two axes must disagree with it. Fitting the LONGEST side —
            // what this did until 2026-08-26 — puts the whole head inside the
            // circle and leaves a ring of empty space on the short axis: measured on
            // Nick, a 0.382 m tall head inside a 0.500 m collider, so about 6 cm
            // above and below the drawn face still registered as a hit. That is the
            // "large space between collisions" this was reported as.
            //
            // Fitting the shortest side inverts which way the error falls: the head
            // now COVERS the circle, so contact never happens through visibly empty
            // space. The cost is the opposite error — the long axis overhangs the
            // hitbox (Nick's by about 1.35x), so two heads can appear to touch
            // slightly before they collide. That is the better error to have: a hit
            // that looks early reads as a graze, a hit through thin air reads as a
            // bug.
            //
            // The properly accurate fix is a CapsuleCollider2D on the head matching
            // the drawn ellipse, exactly as the limbs already have. It is not done
            // here because it changes the dynamics and needs a corrective training
            // pass; the four *_assist01 trunks now make that affordable if it is
            // ever wanted.
            Vector3 spriteSize = s.bounds.size;
            float shortestSide = Mathf.Max(0.0001f, Mathf.Min(spriteSize.x, spriteSize.y));
            HeadRenderer.transform.localScale =
                new Vector3(headDiameter / (shortestSide * _headCellW),
                            headDiameter / (shortestSide * _headCellH), 1f);
        }


        /// Per-foot ground contact and the normal force carried through it,
        /// normalised against body weight. Written every physics step from the
        /// feet's own contact lists.
        ///
        /// This exists because the policy is otherwise BLIND to the floor: the four
        /// "feet" slots in the observation vector are positions relative to the
        /// torso and nothing else, so a fighter cannot tell a planted foot from one
        /// in the air, nor feel how much of its weight is on each. A human has
        /// plantar pressure and Golgi tendon organs for exactly this, and driving
        /// off a surface you cannot feel is the hardest version of the problem.
        [System.NonSerialized] public float FootLoadNear, FootLoadFar;
        [System.NonSerialized] public bool FootDownNear, FootDownFar;

        private readonly ContactPoint2D[] _contactBuf = new ContactPoint2D[8];

        /// Samples one foot's contacts. Normal impulses are per-step, so dividing by
        /// fixedDeltaTime turns them back into a force before scaling by body weight.
        private void SampleFootLoad(Rigidbody2D foot, out bool down, out float load)
        {
            down = false;
            load = 0f;
            if (foot == null) return;
            int count = foot.GetContacts(_contactBuf);
            float sum = 0f;
            for (int i = 0; i < count; i++)
            {
                if (_contactBuf[i].normal.y <= 0.3f) continue;   // not a floor-ish contact
                down = true;
                sum += _contactBuf[i].normalImpulse;
            }
            if (!down) return;
            float weight = Mathf.Max(1f, TotalMass * 9.81f);
            load = Mathf.Clamp01(sum / Time.fixedDeltaTime / weight);
        }

        public Rigidbody2D FootNear => Parts[3];
        public Rigidbody2D FootFar => Parts[6];

        /// Mass multiplier for one segment, DERIVED FROM ITS SIZE.
        ///
        /// Weight follows geometry, which it did not until 2026-08-05: `widthScale`
        /// changed how wide the trunk was drawn and collided, `massScale` changed
        /// how heavy every part was, and nothing tied them together. A fighter could
        /// be built wide and light or narrow and heavy — physically incoherent in a
        /// game whose whole objective is shoving, because the shove is decided by
        /// mass while the player is looking at width. Nick was the proof: drawn at
        /// widthScale 0.82 but given massScale 0.72, he was 12% lighter than even his
        /// slim build implied, and went 0-24 lifetime.
        ///
        /// SQUARED, because widthScale is a transverse scale. The segment is a solid
        /// scaled in both horizontal axes while its LENGTH is fixed by Winter's
        /// anthropometry, so volume — and at constant density, mass — goes with the
        /// square. Using width linearly would under-weight a wide fighter by the
        /// same factor it over-weights a narrow one.
        ///
        /// Only TORSO parts take it, matching Build() (`IsTorsoPart` gates the width
        /// scaling too). That is anatomically right as well as consistent: extra
        /// bodyweight sits on the trunk, not in the shins.
        ///
        /// `massScale` survives as a pure DENSITY trim on top — bone-dense or doughy
        /// at the same size. It must be 1.0 unless that is deliberately what you
        /// want, or it silently re-introduces the decoupling this exists to remove.
        private float SegmentMassScale(string partName) =>
            massScale * (IsTorsoPart(partName) ? widthScale * widthScale : 1f);

        /// The four trunk segments — the only parts `Build` applies widthScale to,
        /// and therefore the only ones whose mass moves with it.
        ///
        /// Was a local function inside Build. Promoted to a member so the width
        /// scaling and the mass scaling read the SAME list: two copies of "which
        /// parts count as torso" is exactly how weight and size drift apart again.
        private static bool IsTorsoPart(string name) =>
            name == "Pelvis" || name == "LowerBack" || name == "UpperBack" || name == "Chest";

        /// Actual total, summed from the built bodies rather than assumed.
        ///
        /// Was `69.6f * massScale`, which stopped being true the moment mass became
        /// size-derived: the trunk is ~54.6% of the baseline, so a fighter at
        /// widthScale w now masses 69.6 * massScale * (0.546*w^2 + 0.454) and no
        /// single multiplier describes it. Reading the rigidbodies cannot drift.
        public float TotalMass
        {
            get
            {
                if (Parts == null) return 69.6f * massScale;
                float total = 0f;
                for (int partIndex = 0; partIndex < Parts.Length; partIndex++)
                {
                    if (Parts[partIndex] != null) total += Parts[partIndex].mass;
                }
                return total;
            }
        }
    }
}
