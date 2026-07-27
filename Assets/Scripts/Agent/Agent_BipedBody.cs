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
    public class Agent_BipedBody : MonoBehaviour
    {
        [Tooltip("Character sheet; when set, overrides teamColor/headSprite/body scales at Awake.")]
        public Agent_CharacterDefinition character;

        public int facingSign = 1;
        public Color teamColor = new Color(0.85f, 0.25f, 0.2f);
        bool _faceArtFacesLeft;

        [Header("Build (1 = Matt's lightweight baseline)")]
        [Tooltip("Multiplies every part's mass. Heavyweight sumo ~2.")]
        public float massScale = 1f;
        [Tooltip("Widens the four torso segments (sumo belly). Heavyweight ~1.3.")]
        public float widthScale = 1f;
        [Tooltip("Multiplies joint motor torque caps to carry the extra mass.")]
        public float torqueScale = 1f;

        [Tooltip("Optional face texture for the head (drawn right-facing; auto-flipped when facingSign is -1). Falls back to a plain circle.")]
        public Sprite headSprite;

        [HideInInspector] public Rigidbody2D Torso;   // pelvis (root mass, ring-out tracking)
        [HideInInspector] public Rigidbody2D Chest;   // top segment (posture/lean sensing)
        [HideInInspector] public SpriteRenderer HeadRenderer; // face sprite (mood swaps)
        [HideInInspector] public HingeJoint2D[] Joints;   // 10 motors
        [HideInInspector] public Rigidbody2D[] Parts;
        [HideInInspector] public List<Collider2D> AllColliders = new List<Collider2D>();

        // Spawn clearance above contact surfaces: enough to avoid frame-0
        // interpenetration, small enough that the feet are effectively already
        // planted when physics starts (a taller drop costs balance).
        const float SPAWN_CLEARANCE = 0.01f;
        const float MAX_ANGULAR_VELOCITY = 1080f; // deg/s clamp against physics blow-up
        // Art is drawn at EXACTLY collider size. An earlier version oversized it to
        // hide the seams between segments, but that made the drawing lie about the
        // physics: a limb appeared to touch the opponent while the colliders were
        // still apart. Accuracy wins — the drawn edge is the edge that collides.
        const float ART_OVERLAP = 1f;

        float[] _maxSpeed;   // deg/s per joint
        float[] _maxTorque;
        Vector3[] _initialLocalPos;
        Quaternion[] _initialLocalRot;
        Rigidbody2D[] _thighs;
        float _headCellW, _headCellH; // chest local scale the head must compensate for

        static Sprite _boxSprite, _torsoSprite, _circleSprite;
        static PhysicsMaterial2D _footMat, _bodyMat;

        struct PartDef
        {
            public string name; public float w, h, mass, x, y;
            public bool circle, isFoot; public int sorting; public float tint;
            public PartDef(string n, float w_, float h_, float m, float x_, float y_,
                           int sort, float tint_, bool circ = false, bool foot = false)
            { name = n; w = w_; h = h_; mass = m; x = x_; y = y_; sorting = sort; tint = tint_; circle = circ; isFoot = foot; }
        }

        struct JointDef
        {
            public int child, parent; public float ax, ay, min, max, torque, speed;
            public JointDef(int c, int p, float ax_, float ay_, float mn, float mx, float t, float s)
            { child = c; parent = p; ax = ax_; ay = ay_; min = mn; max = mx; torque = t; speed = s; }
        }

        // Right-facing layout. Heights: ankle 0.10, knee 0.48, hip 0.88,
        // spine joints 1.06 / 1.20 / 1.34, shoulder 1.43, elbow 1.13.
        // Total ~1.76 m, 69.6 kg (torso chain 38 kg incl. the 6 kg head, legs
        // 23 kg, arms 8.6 kg) — see the TotalMass property, which is the number
        // to trust. Segment masses track Winter's anthropometric fractions:
        // thigh 10.1% (vs 10.0), shin 5.0% (4.65), foot 1.44% (1.45), torso
        // chain 54.6% (57.8); the arms run ~20-28% heavy on purpose.
        static readonly PartDef[] PART_DEFS =
        {
            new PartDef("Pelvis",    0.32f, 0.18f, 11f, 0f,    0.97f,  0, 1f),
            new PartDef("ThighNear", 0.14f, 0.40f,  7f, 0f,    0.68f,  2, 0.95f),
            new PartDef("ShinNear",  0.11f, 0.38f,  3.5f, 0f,  0.29f,  2, 0.95f),
            new PartDef("FootNear",  0.22f, 0.08f,  1f, 0.05f, 0.04f,  2, 0.95f, false, true),
            new PartDef("ThighFar",  0.14f, 0.40f,  7f, 0f,    0.68f, -2, 0.55f),
            new PartDef("ShinFar",   0.11f, 0.38f,  3.5f, 0f,  0.29f, -2, 0.55f),
            new PartDef("FootFar",   0.22f, 0.08f,  1f, 0.05f, 0.04f, -2, 0.55f, false, true),
            new PartDef("LowerBack", 0.30f, 0.14f,  7f, 0f,    1.13f,  0, 0.97f),
            new PartDef("UpperBack", 0.31f, 0.14f,  7f, 0f,    1.27f,  0, 0.99f),
            new PartDef("Chest",     0.34f, 0.18f, 13f, 0f,    1.43f,  0, 1f),
            new PartDef("UArmNear",  0.10f, 0.30f,  2.5f, 0f,  1.28f,  3, 0.9f),
            new PartDef("FArmNear",  0.09f, 0.28f,  1.8f, 0f,  0.99f,  3, 0.9f),
            new PartDef("UArmFar",   0.10f, 0.30f,  2.5f, 0f,  1.28f, -3, 0.5f),
            new PartDef("FArmFar",   0.09f, 0.28f,  1.8f, 0f,  0.99f, -3, 0.5f),
        };

        // child, parent, anchor(x,y in root space), min, max (deg), torque, speed
        static readonly JointDef[] JOINT_DEFS =
        {
            new JointDef(1, 0,  0f, 0.88f,  -30f, 120f, 300f, 400f), // hip near
            new JointDef(2, 1,  0f, 0.48f, -150f,   0f, 250f, 500f), // knee near
            new JointDef(3, 2,  0f, 0.10f,  -45f,  45f, 120f, 400f), // ankle near
            new JointDef(4, 0,  0f, 0.88f,  -30f, 120f, 300f, 400f), // hip far
            new JointDef(5, 4,  0f, 0.48f, -150f,   0f, 250f, 500f), // knee far
            new JointDef(6, 5,  0f, 0.10f,  -45f,  45f, 120f, 400f), // ankle far
            new JointDef(7, 0,  0f, 1.06f,  -25f,  25f, 400f, 250f), // spine 1 (pelvis->lower back)
            new JointDef(8, 7,  0f, 1.20f,  -25f,  25f, 400f, 250f), // spine 2 (lower->upper back)
            new JointDef(9, 8,  0f, 1.34f,  -25f,  25f, 400f, 250f), // spine 3 (upper back->chest)
            new JointDef(10, 9, 0f, 1.43f, -160f, 160f, 150f, 500f), // shoulder near (on chest)
            new JointDef(11,10, 0f, 1.13f,    0f, 150f, 100f, 500f), // elbow near
            new JointDef(12, 9, 0f, 1.43f, -160f, 160f, 150f, 500f), // shoulder far (on chest)
            new JointDef(13,12, 0f, 1.13f,    0f, 150f, 100f, 500f), // elbow far
        };

        void Awake()
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

        /// Torso segments and feet: a gentle radius over a BoxCollider2D. Heavy
        /// rounding here would both break the trunk into loose ovals and inset the
        /// drawn edge from the box that actually collides — a foot has to look as
        /// flat as the sole that stands on the clay.
        public static Sprite TorsoSprite() => RoundedSprite(ref _torsoSprite, 0.14f);

        static Sprite RoundedSprite(ref Sprite cache, float radiusFraction)
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
                for (int y = 0; y < S; y++)
                {
                    for (int x = 0; x < S; x++)
                    {
                        // Distance to the rounded-rect boundary, in pixels.
                        float dx = Mathf.Max(Mathf.Abs(x + 0.5f - half) - inner, 0f);
                        float dy = Mathf.Max(Mathf.Abs(y + 0.5f - half) - inner, 0f);
                        float distance = Mathf.Sqrt(dx * dx + dy * dy);
                        float alpha = Mathf.Clamp01((RADIUS - distance) / 1.5f);
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
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
                for (int y = 0; y < S; y++)
                    for (int x = 0; x < S; x++)
                    {
                        float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(S / 2f, S / 2f));
                        tex.SetPixel(x, y, d <= r ? Color.white : Color.clear);
                    }
                tex.Apply();
                _circleSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S);
            }
            return _circleSprite;
        }

        void Build()
        {
            if (_footMat == null)
            {
                _footMat = new PhysicsMaterial2D("Foot") { friction = 0.9f, bounciness = 0f };
                _bodyMat = new PhysicsMaterial2D("Body") { friction = 0.4f, bounciness = 0f };
            }

            int n = PART_DEFS.Length;
            Parts = new Rigidbody2D[n];
            var thighs = new List<Rigidbody2D>();

            bool IsTorsoPart(string name) =>
                name == "Pelvis" || name == "LowerBack" || name == "UpperBack" || name == "Chest";

            for (int i = 0; i < n; i++)
            {
                var d = PART_DEFS[i];
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
                sr.sprite = d.circle ? CircleSprite()
                          : boxy ? TorsoSprite()
                          : BoxSprite();
                sr.color = new Color(teamColor.r * d.tint, teamColor.g * d.tint, teamColor.b * d.tint, 1f);
                sr.sortingOrder = d.sorting;
                // Shared lit material so the arena light rig shapes the limbs; the
                // per-part tint stays on the renderer, which costs no extra material.
                sr.sharedMaterial = Systems_ArenaLighting.LitSpriteMaterial();

                var rb = go.AddComponent<Rigidbody2D>();
                rb.mass = d.mass * massScale;
                rb.linearDamping = 0.05f;
                rb.angularDamping = 0.05f;
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

                Parts[i] = rb;
                if (d.name.StartsWith("Thigh")) thighs.Add(rb);

                if (i == 0) Torso = rb; // pelvis: root mass, ring-out tracking

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
                    float parentW = d.w * widthScale;
                    HeadRenderer = hsr;
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
                    var hc = head.AddComponent<CircleCollider2D>();
                    hc.sharedMaterial = _bodyMat;
                    AllColliders.Add(hc);
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
            for (int j = 0; j < JOINT_DEFS.Length; j++)
            {
                var d = JOINT_DEFS[j];
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
                Joints[j] = joint;
                _maxSpeed[j] = d.speed;
                _maxTorque[j] = d.torque * torqueScale;
            }

            // Self-pass-through: ignore every collider pair within this biped.
            for (int a = 0; a < AllColliders.Count; a++)
                for (int b = a + 1; b < AllColliders.Count; b++)
                    Physics2D.IgnoreCollision(AllColliders[a], AllColliders[b], true);

            // Record spawn pose for resets.
            _initialLocalPos = new Vector3[n];
            _initialLocalRot = new Quaternion[n];
            for (int i = 0; i < n; i++)
            {
                _initialLocalPos[i] = Parts[i].transform.localPosition;
                _initialLocalRot[i] = Parts[i].transform.localRotation;
            }

            // Soft contact shadow so the body reads as grounded.
            var shadow = new GameObject("BlobShadow");
            shadow.transform.SetParent(transform, false);
            var blob = shadow.AddComponent<Systems_BlobShadow>();
            blob.target = Torso;
            blob.baseWidth = 0.9f * widthScale;
        }

        public void ResetPose()
        {
            RestoreMotors();   // a limp body from last round must drive again
            for (int i = 0; i < Parts.Length; i++)
            {
                Parts[i].transform.localPosition = _initialLocalPos[i];
                Parts[i].transform.localRotation = _initialLocalRot[i];
                Parts[i].linearVelocity = Vector2.zero;
                Parts[i].angularVelocity = 0f;
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

        /// action in [-1,1] -> motor target speed; sign mirrored with facing.
        public void ApplyMotor(int jointIndex, float action)
        {
            // A limp body ignores motor commands outright. Without this the
            // agent's own zeroed-action path keeps writing full maxMotorTorque
            // back every decision step, which silently un-limps the ragdoll.
            if (IsLimp) return;

            var joint = Joints[jointIndex];
            var m = joint.motor;
            m.motorSpeed = Mathf.Clamp(action, -1f, 1f) * _maxSpeed[jointIndex] * facingSign;
            m.maxMotorTorque = _maxTorque[jointIndex];
            joint.motor = m;
        }

        /// Clamp all body-part angular velocities against physics destabilization.
        public void ClampAngularVelocities()
        {
            for (int i = 0; i < Parts.Length; i++)
                Parts[i].angularVelocity =
                    Mathf.Clamp(Parts[i].angularVelocity, -MAX_ANGULAR_VELOCITY, MAX_ANGULAR_VELOCITY);
        }

        public float JointAngleNorm(int j) => Joints[j].jointAngle * facingSign / 180f;

        public float JointSpeedNorm(int j) => Joints[j].jointSpeed * facingSign / 600f;

        /// Swap the face sprite, aspect-fitting to ~0.39 m tall regardless of
        /// the source image size (mood images vary a few pixels each).
        public void SetHeadSprite(Sprite s)
        {
            if (HeadRenderer == null || s == null) return;
            HeadRenderer.sprite = s;
            HeadRenderer.color = Color.white;
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
