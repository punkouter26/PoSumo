using Unity.MLAgents.Actuators;
using UnityEngine;

namespace PoSumo
{
    /// Inputs the BOT needs, gathered once per decision by the agent.
    ///
    /// A `readonly struct` passed by `in` for the same reason `Reward_Context` is:
    /// one per agent per decision, and a class would be hundreds of heap
    /// allocations a second in the hottest path in the project.
    public readonly struct Agent_BotContext
    {
        public readonly float FacingSign;
        public readonly float ArenaCenterX;
        public readonly float RingHalfWidth;
        public readonly bool HasOpponent;
        public readonly float OpponentX;
        public readonly float Time;

        public Agent_BotContext(
            float facingSign, float arenaCenterX, float ringHalfWidth,
            bool hasOpponent, float opponentX, float time)
        {
            FacingSign = facingSign;
            ArenaCenterX = arenaCenterX;
            RingHalfWidth = ringHalfWidth;
            HasOpponent = hasOpponent;
            OpponentX = opponentX;
            Time = time;
        }
    }

    /// The BOT: a sumo fighter driven by hand-written rules instead of a trained policy.
    ///
    /// WHY THIS CAN EXIST AT ALL. An "action" here is not a torque, it is a target
    /// motor SPEED: `Agent_BipedBody.ApplyMotor` does
    /// `motorSpeed = clamp(action,-1,1) * maxSpeed * facingSign`. A proportional
    /// controller on joint ANGLE therefore lands directly on the action space —
    /// error in, velocity command out — with no torque model to reproduce.
    ///
    /// Both sides of that mapping are already FACING-LOCAL, which is what makes a
    /// scripted fighter tractable at all:
    ///   * `JointAngleNorm(j)` returns `jointAngle * facingSign / 180`, and
    ///   * `ApplyMotor` multiplies the command by `facingSign` on the way out.
    /// So one set of targets drives a fighter facing either direction, exactly as
    /// one policy does. Never re-apply facingSign in here — it is already applied
    /// twice and a third would un-mirror the body.
    ///
    /// SIGN CONVENTIONS ARE MEASURED IN THIS PROJECT, NOT GUESSED, and they have
    /// bitten it hard before (see CLAUDE.md: every asymmetric joint was inverted
    /// for the life of the project). `HingeJoint2D.jointAngle` is the NEGATIVE of
    /// the child segment's geometric rotation relative to its parent. The ranges
    /// that follow from that, taken from `Agent_BipedBody.JOINT_DEFS`:
    ///
    ///     hip      -120 .. 30    flexion NEGATIVE (thigh forward)
    ///     knee        0 .. 150   flexion POSITIVE (heel toward buttock)
    ///     ankle     -35 .. 35    symmetric
    ///     spine x3  -20 .. 20    symmetric, 20 deg each = 60 total
    ///     shoulder -120 .. 120   symmetric
    ///     elbow    -150 .. 0     flexion NEGATIVE
    ///
    /// The two signs that could NOT be read off a range — which way the spine and
    /// shoulder lean toward the opponent — are isolated in LEAN_SIGN and
    /// ARM_SIGN below so they can be flipped from one place after watching a bout,
    /// rather than by hunting through the posture tables.
    public sealed class Agent_Bot
    {
        // Joint indices. These mirror JOINT_DEFS order exactly; if that table is
        // reordered these must move with it or the bot drives the wrong limbs.
        private const int HIP_NEAR = 0, KNEE_NEAR = 1, ANKLE_NEAR = 2;
        private const int HIP_FAR = 3, KNEE_FAR = 4, ANKLE_FAR = 5;
        private const int SPINE_1 = 6, SPINE_2 = 7, SPINE_3 = 8;
        private const int SHOULDER_NEAR = 9, ELBOW_NEAR = 10;
        private const int SHOULDER_FAR = 11, ELBOW_FAR = 12;

        /// +1 means "positive spine jointAngle leans the chest toward the opponent".
        /// Flip this single constant if the bot is measured leaning away.
        private const float LEAN_SIGN = 1f;

        /// +1 means "positive shoulder jointAngle swings the arm toward the opponent".
        private const float ARM_SIGN = 1f;

        // Proportional gain on NORMALISED angle error (error is degrees/180), so
        // the command saturates at an error of 180/GAIN degrees.
        //
        // This was 6, which saturates past 30 deg — and a saturated command asks
        // the joint for its FULL motor speed, 400-500 deg/s on the legs. Thirteen
        // joints slamming at full rate is destabilising by itself: the body is
        // thrown around by its own corrections faster than any balance law can
        // answer. At 2.5 the command stays proportional out to ~72 deg, which is
        // wider than any target this controller asks for, so the servo behaves like
        // a spring instead of a switch.
        private const float GAIN = 2.5f;

        /// Uprightness below which the BOT stops trying to walk and just tries to
        /// stand. Stepping while already toppling is how the previous version threw
        /// itself down: the swing leg leaves the ground exactly when the body can
        /// least afford to lose half its support.
        private const float STAND_UPRIGHT_THRESHOLD = 2f;

        // Ranges in metres along the mat.
        private const float ENGAGE_RANGE = 1.30f;   // inside this, stop walking and drive
        private const float LUNGE_RANGE = 0.75f;    // inside this, commit to the shove
        private const float EDGE_ALARM = 0.85f;     // this close to our own edge, dig in

        // ── Gait: SIMBICON, not a phase oscillator ───────────────────────────
        //
        // The first version of this drove the legs from a blind sine wave. It could
        // not work and did not: an open-loop gait has no way to notice it is
        // falling, so any disturbance integrates and the fighter goes down at the
        // tachiai every single time (measured: a full match, 3-0, both bodies flat,
        // every round decided by downOutSeconds).
        //
        // SIMBICON (Yin, Loken & van de Panne 2007) fixes exactly that with one
        // term. The swing hip is not driven to a fixed angle but to
        //
        //     theta = theta0 + CD * d + CV * v
        //
        // where d is the horizontal distance from the centre of mass to the stance
        // foot and v is the centre-of-mass velocity, both in the facing-local
        // frame. Falling forward raises d and v, which swings the free leg further
        // forward, which plants the next foot ahead of the fall and catches it.
        // That feedback is the whole difference between a walker and a faller, and
        // it is why a hand-written 2D biped is tractable at all.
        //
        // Gains are in degrees per metre and degrees per metre/second because the
        // joint targets below are in degrees, unlike the paper's radians.
        private const float SWING_DURATION = 0.30f;
        private const float CD_DEG_PER_M = 115f;
        private const float CV_DEG_PER_MPS = 42f;
        private const float SWING_HIP_BASE_DEG = -32f;  // negative = flexed = thigh forward
        private const float SWING_KNEE_DEG = 68f;       // lift the foot clear of the mat
        private const float STANCE_KNEE_DEG = 16f;      // near-extension carries the weight
        private const float SWING_HIP_MIN_DEG = -95f;   // stay inside the hip's -120..30
        private const float SWING_HIP_MAX_DEG = 18f;

        /// The stance hip EXTENDS. This is where the propulsion comes from and
        /// getting it wrong is what made the first SIMBICON pass walk backwards.
        ///
        /// MEASURED, not assumed (probe: gravity off, agent disabled, drive hip 0 to
        /// each rail, read the thigh via pelvis.InverseTransformPoint):
        ///
        ///     facing-local jointAngle +31 deg -> thigh x = -0.42  (BEHIND the hip)
        ///     facing-local jointAngle -121 deg -> thigh x = +0.70  (FORWARD)
        ///
        /// So negative is flexion/forward — which the swing target above already had
        /// right — and POSITIVE is extension. The old stance target of -6 deg held
        /// the support thigh FORWARD for the whole step, so the planted foot sat
        /// ahead of the body and pushed it backwards: both fighters drifted outward
        /// and off the far edge of the dohyo, which is exactly what was measured.
        /// Rotating the stance thigh backwards against a planted foot is what
        /// carries the pelvis forward over it.
        private const float STANCE_HIP_DEG = 20f;

        /// Left at zero deliberately. The ankle's polarity has NOT been measured,
        /// and an unmeasured sign in the propulsion path is exactly the confounder
        /// that made the last diagnosis take a probe to resolve. Worth revisiting
        /// once the gait itself is stable.
        private const float STANCE_ANKLE_PUSH_DEG = 0f;

        // ── Trunk balance ────────────────────────────────────────────────────
        //
        // The missing half of SIMBICON. The paper holds the trunk upright with a
        // virtual torque and lets the stance hip take the reaction; a constant
        // stance angle cannot regulate trunk pitch at all, so the body pitches over
        // however good the swing-leg feedback is. That was measured: with the swing
        // feedback working and the stance leg finally extending, both fighters still
        // collapsed in about a second.
        //
        // Lean is read as dot(chest.up, facingForward), which needs no probe: it is
        // 0 upright and positive when the chest has pitched toward the opponent.
        //
        // The spine correction sign IS measured (gravity off, agent disabled, drive
        // joints 6/7/8 to +1, read the chest through pelvis.InverseTransformPoint):
        //     spine action +1 -> chestLocalX_facing = +0.81, i.e. chest FORWARD.
        // So countering a forward pitch means a NEGATIVE spine target.
        //
        // Note the same probe returned a world-space pitch of 148 deg, which is the
        // counter-rotation artefact CLAUDE.md warns about — with gravity off the
        // whole body rotates to conserve angular momentum. The parent-local read is
        // the trustworthy one, and is why the probe is written that way.
        private const float TRUNK_SPINE_KP = 46f;   // degrees of spine per unit lean
        private const float TRUNK_SPINE_KD = 5.5f;  // per rad/s of chest rotation
        private const float TRUNK_HIP_KP = 38f;     // degrees of extra stance extension per unit lean
        private const float SPINE_LIMIT_DEG = 18f;  // of the 20 the joint allows

        // Standing balance. Stiffer than the walking correction because there is no
        // swing leg to place — the ankles and hips are the entire strategy.
        // Measured down from 90/12, which did not merely overshoot — it inverted the
        // fighter. A lean of 1.0 commanded a 90 deg target swing on hips and spine
        // at once; the trunk flipped through up = -0.37 -> +0.45 -> -0.49 in under a
        // second while the body was still at full height. The controller was doing
        // the falling over, not gravity.
        private const float STAND_KP = 22f;
        private const float STAND_KD = 4f;
        private const float STAND_HIP_DEG = -4f;

        /// Nearly straight, and that is load-bearing rather than cosmetic. At 22 deg
        /// the trunk stayed upright (0.92) but the whole body SANK — pelvis 1.06 ->
        /// 0.26 — because a bent knee has to hold the body's weight on a long moment
        /// arm and the joint only has 250 N-m, less once the Hill force-velocity
        /// term and fatigue take their cut. A near-straight leg carries 69.6 kg on
        /// its own geometry and asks the motor for almost nothing.
        ///
        /// It costs the sumo crouch, which matters for driving — that is why Drive()
        /// keeps its own deeper CROUCH_KNEE_DEG. Standing up is the prerequisite;
        /// crouching is only useful once the body can hold itself at all.
        private const float STAND_KNEE_DEG = 7f;
        /// Zero until the ankle's polarity is MEASURED. It was 0.45, feeding an
        /// unmeasured sign straight into the balance loop — if it is backwards the
        /// ankle drives the fall it is meant to arrest, and there is no way to tell
        /// that apart from "gains too high" while both are in play at once.
        private const float ANKLE_SHARE = 0f;

        /// How far the feet are split fore/aft when standing. Widens the support
        /// polygon the centre of mass has to stay inside.
        private const float STANCE_SPLIT_DEG = 20f;

        // Postures, in degrees, in the jointAngle convention documented above.
        private const float CROUCH_KNEE_DEG = 55f;   // bent: low centre of mass
        private const float CROUCH_HIP_DEG = -35f;   // negative = flexed = thigh forward
        private const float DRIVE_KNEE_DEG = 12f;    // near-extension: the push itself
        private const float DRIVE_HIP_DEG = -8f;
        private const float LEAN_DEG = 18f;          // per spine joint, of 20 available
        private const float ARM_REACH_DEG = 62f;
        private const float ELBOW_BENT_DEG = -55f;
        private const float ELBOW_PUSH_DEG = -12f;   // straightens into the shove
        private const float ANKLE_BRACE_DEG = 14f;

        private float _lungeUntil;

        // Gait state. Per-agent, because Agent_Biped owns one Agent_Bot each.
        private bool _swingLegIsNear = true;
        private float _swingTimer;
        private float _lastTime;

        /// Writes 13 continuous actions. Never touches rewards or episode state —
        /// it has no reference to the Agent, which is deliberate and mirrors the
        /// reward providers: a decision-maker that structurally cannot score itself.
        public void Decide(Agent_BipedBody body, in Agent_BotContext ctx, ActionSegment<float> actions)
        {
            for (int index = 0; index < actions.Length; index++)
            {
                actions[index] = 0f;
            }
            if (body == null || body.Torso == null)
            {
                return;
            }

            // Everything below is in the facing-local frame, so "forward" is always
            // +x regardless of which way this fighter is turned.
            float torsoX = body.Torso.position.x;
            float upright = Mathf.Clamp01(Vector2.Dot(body.Chest.transform.up, Vector2.up));
            float toOpponent = ctx.HasOpponent ? (ctx.OpponentX - torsoX) * ctx.FacingSign : ENGAGE_RANGE * 2f;
            float gap = Mathf.Abs(toOpponent);
            float forward = toOpponent >= 0f ? 1f : -1f;

            // Distance from OUR position to the edge we would be pushed out over —
            // the one behind us, which is the one that loses the round.
            float offCentre = (torsoX - ctx.ArenaCenterX) * ctx.FacingSign;
            float edgeBehind = ctx.RingHalfWidth + offCentre;
            bool cornered = edgeBehind < EDGE_ALARM;

            if (upright < 0.35f || body.IsLimp)
            {
                Recover(body, actions);
                return;
            }

            // A cornered fighter drives regardless of range: giving ground to walk
            // is how a round is lost on position when the clock runs out.
            bool driving = gap <= ENGAGE_RANGE || cornered;
            if (gap <= LUNGE_RANGE)
            {
                _lungeUntil = ctx.Time + 0.45f;
            }
            bool lunging = ctx.Time < _lungeUntil;

            // Balance before locomotion. Both feet stay planted until the trunk is
            // actually upright — a swing leg lifts half the support away, which is
            // the last thing a toppling body needs.
            if (upright < STAND_UPRIGHT_THRESHOLD)
            {
                Stand(body, actions);
                return;
            }

            if (driving)
            {
                Drive(body, actions, forward, lunging || cornered);
            }
            else
            {
                Advance(body, actions, forward, ctx.Time);
            }
        }

        /// Closing distance, SIMBICON-style: one leg swings on a timer while the
        /// other carries the weight, and the swing target is corrected every
        /// decision by how far the body has already fallen.
        private void Advance(Agent_BipedBody body, ActionSegment<float> actions, float forward, float time)
        {
            // Advance the swing timer on wall-clock delta rather than a fixed step:
            // Heuristic runs once per DecisionRequester period (3 physics steps for
            // every shipped fighter), so assuming a step here would run the gait at
            // a third of its intended cadence.
            float dt = _lastTime > 0f ? Mathf.Clamp(time - _lastTime, 0f, 0.2f) : 0f;
            _lastTime = time;
            _swingTimer += dt;

            bool nearIsSwing = _swingLegIsNear;
            bool swingFootDown = nearIsSwing ? body.FootDownNear : body.FootDownFar;

            // Swap legs when the swing foot lands, or when the timer expires and it
            // has not — the timeout is what stops a leg hanging forever when the
            // foot never makes contact (over an edge, or mid-fall).
            if ((_swingTimer > SWING_DURATION * 0.55f && swingFootDown) || _swingTimer > SWING_DURATION)
            {
                _swingLegIsNear = !_swingLegIsNear;
                _swingTimer = 0f;
                nearIsSwing = _swingLegIsNear;
            }

            Rigidbody2D stanceFoot = nearIsSwing ? body.FootFar : body.FootNear;
            Vector2 com = CentreOfMass(body, out Vector2 comVelocity);

            // THE feedback term. Both are facing-local, so "forward" is +.
            float d = stanceFoot != null ? (com.x - stanceFoot.position.x) * body.facingSign : 0f;
            float v = comVelocity.x * body.facingSign;
            float swingHip = Mathf.Clamp(
                SWING_HIP_BASE_DEG - (CD_DEG_PER_M * d + CV_DEG_PER_MPS * v) * forward,
                SWING_HIP_MIN_DEG, SWING_HIP_MAX_DEG);

            int swingHipJoint = nearIsSwing ? HIP_NEAR : HIP_FAR;
            int swingKneeJoint = nearIsSwing ? KNEE_NEAR : KNEE_FAR;
            int swingAnkleJoint = nearIsSwing ? ANKLE_NEAR : ANKLE_FAR;
            int stanceHipJoint = nearIsSwing ? HIP_FAR : HIP_NEAR;
            int stanceKneeJoint = nearIsSwing ? KNEE_FAR : KNEE_NEAR;
            int stanceAnkleJoint = nearIsSwing ? ANKLE_FAR : ANKLE_NEAR;

            // The swing knee flexes to clear the mat, then extends through the back
            // half of the swing so the foot lands on a straightening leg.
            float swingProgress = Mathf.Clamp01(_swingTimer / SWING_DURATION);
            float swingKnee = Mathf.Lerp(SWING_KNEE_DEG, 20f, swingProgress);

            Track(body, actions, swingHipJoint, swingHip);
            Track(body, actions, swingKneeJoint, swingKnee);
            Track(body, actions, swingAnkleJoint, 0f);

            // Trunk balance. `lean` is 0 upright, positive pitched toward the
            // opponent; `leanRate` damps it so the correction does not oscillate.
            float lean = TrunkLean(body);
            float leanRate = body.Chest != null ? body.Chest.angularVelocity * Mathf.Deg2Rad * body.facingSign : 0f;

            // Falling forward => extend the stance hip harder, which drives the
            // pelvis forward and puts the support back underneath the mass.
            Track(body, actions, stanceHipJoint, STANCE_HIP_DEG + TRUNK_HIP_KP * lean);
            Track(body, actions, stanceKneeJoint, STANCE_KNEE_DEG);
            Track(body, actions, stanceAnkleJoint, STANCE_ANKLE_PUSH_DEG * forward);

            // Spine rights the chest. NEGATIVE opposes a forward pitch, because the
            // probe showed positive spine angle carries the chest forward. Split
            // across the three joints since each only has 20 deg to give.
            float spine = Mathf.Clamp(
                -(TRUNK_SPINE_KP * lean + TRUNK_SPINE_KD * leanRate) / 3f,
                -SPINE_LIMIT_DEG, SPINE_LIMIT_DEG);
            Track(body, actions, SPINE_1, spine);
            Track(body, actions, SPINE_2, spine);
            Track(body, actions, SPINE_3, spine);

            // Arms low and slightly forward — the sumo approach, and it keeps them
            // out of the legs.
            Track(body, actions, SHOULDER_NEAR, ARM_REACH_DEG * 0.35f * forward * ARM_SIGN);
            Track(body, actions, SHOULDER_FAR, ARM_REACH_DEG * 0.35f * forward * ARM_SIGN);
            Track(body, actions, ELBOW_NEAR, ELBOW_BENT_DEG);
            Track(body, actions, ELBOW_FAR, ELBOW_BENT_DEG);
        }

        /// Two-footed balance. No swing leg, no stepping — both feet planted, knees
        /// softly bent, and the whole balance budget spent on holding the trunk over
        /// the feet.
        ///
        /// This is the first milestone and the honest test of the balance law: if a
        /// biped cannot stand still it certainly cannot walk, and every earlier
        /// version went straight to stepping and so never separated the two
        /// failures.
        ///
        /// Ankles are driven here (unlike the gait, which leaves them at zero
        /// because their polarity is unmeasured) using the SAME sign convention the
        /// hip probe established for the leg chain: a positive facing-local angle
        /// rotates the child backwards. Leaning forward therefore wants a positive
        /// ankle to push the shin back upright over the foot.
        private void Stand(Agent_BipedBody body, ActionSegment<float> actions)
        {
            float lean = TrunkLean(body);
            float leanRate = body.Chest != null ? body.Chest.angularVelocity * Mathf.Deg2Rad * body.facingSign : 0f;
            float correction = STAND_KP * lean + STAND_KD * leanRate;

            // Hips extend against a forward lean, pushing the pelvis back under the
            // chest; knees stay softly bent so the legs can absorb rather than
            // transmit, which is also the sumo stance.
            // STAGGERED, not feet-together. In the sagittal plane a biped with both
            // feet in the same place has a support base of one foot length, ~0.27 m,
            // and the centre of mass only has to cross that to be unrecoverable.
            // Splitting the legs — one forward, one back — roughly doubles it, and
            // it is the sumo stance for exactly this reason. Hip flexion is negative
            // (measured), so the forward leg subtracts and the trailing leg adds.
            Track(body, actions, HIP_NEAR, STAND_HIP_DEG - STANCE_SPLIT_DEG + correction);
            Track(body, actions, HIP_FAR, STAND_HIP_DEG + STANCE_SPLIT_DEG + correction);
            Track(body, actions, KNEE_NEAR, STAND_KNEE_DEG);
            Track(body, actions, KNEE_FAR, STAND_KNEE_DEG);
            Track(body, actions, ANKLE_NEAR, Mathf.Clamp(correction * ANKLE_SHARE, -30f, 30f));
            Track(body, actions, ANKLE_FAR, Mathf.Clamp(correction * ANKLE_SHARE, -30f, 30f));

            float spine = Mathf.Clamp(-correction / 3f, -SPINE_LIMIT_DEG, SPINE_LIMIT_DEG);
            Track(body, actions, SPINE_1, spine);
            Track(body, actions, SPINE_2, spine);
            Track(body, actions, SPINE_3, spine);

            // Arms out for balance, the way anyone teetering does it.
            Track(body, actions, SHOULDER_NEAR, 25f * ARM_SIGN);
            Track(body, actions, SHOULDER_FAR, 25f * ARM_SIGN);
            Track(body, actions, ELBOW_NEAR, -20f);
            Track(body, actions, ELBOW_FAR, -20f);
        }

        /// Trunk pitch: 0 upright, positive when the chest has pitched toward the
        /// opponent, negative when it has fallen back. Deliberately a dot product
        /// against the facing-forward axis rather than a SignedAngle — it needs no
        /// measured sign convention, has no wraparound at +/-180, and saturates
        /// gracefully instead of flipping when the fighter goes past horizontal.
        private static float TrunkLean(Agent_BipedBody body)
        {
            if (body.Chest == null)
            {
                return 0f;
            }
            Vector2 facingForward = new Vector2(body.facingSign, 0f);
            return Vector2.Dot(body.Chest.transform.up, facingForward);
        }

        /// Mass-weighted centre of mass and its velocity over every surviving part.
        /// Walks `Parts` rather than using the pelvis as a proxy, because the pelvis
        /// is only 12% of the body and a fighter can be mid-fall with the pelvis
        /// still over its feet. Skips nulls: limbs detach in this game.
        private static Vector2 CentreOfMass(Agent_BipedBody body, out Vector2 velocity)
        {
            Vector2 weighted = Vector2.zero;
            Vector2 weightedVelocity = Vector2.zero;
            float total = 0f;
            Rigidbody2D[] parts = body.Parts;
            for (int partIndex = 0; partIndex < parts.Length; partIndex++)
            {
                Rigidbody2D part = parts[partIndex];
                if (part == null)
                {
                    continue;
                }
                float mass = part.mass;
                weighted += part.position * mass;
                weightedVelocity += part.linearVelocity * mass;
                total += mass;
            }
            if (total <= 0f)
            {
                velocity = Vector2.zero;
                return body.Torso != null ? body.Torso.position : Vector2.zero;
            }
            velocity = weightedVelocity / total;
            return weighted / total;
        }

        /// In contact. Crouch to get under the opponent, then extend the legs to
        /// convert that into forward drive while the arms push.
        ///
        /// The crouch/extend split is the whole idea: pushing from straight legs
        /// just transmits the shove into the mat, and a measured sustained push
        /// here is only 71-500 N against a friction wall of ~376 N, so the drive
        /// has to come from leg extension rather than from leaning alone.
        private void Drive(Agent_BipedBody body, ActionSegment<float> actions, float forward, bool committing)
        {
            float kneeTarget = committing ? DRIVE_KNEE_DEG : CROUCH_KNEE_DEG;
            float hipTarget = committing ? DRIVE_HIP_DEG : CROUCH_HIP_DEG;

            Track(body, actions, HIP_NEAR, hipTarget);
            Track(body, actions, HIP_FAR, hipTarget);
            Track(body, actions, KNEE_NEAR, kneeTarget);
            Track(body, actions, KNEE_FAR, kneeTarget);

            // Ankles brace against the mat so the leg drive pushes the OPPONENT
            // rather than sliding our own feet backwards.
            Track(body, actions, ANKLE_NEAR, ANKLE_BRACE_DEG * forward);
            Track(body, actions, ANKLE_FAR, ANKLE_BRACE_DEG * forward);

            float lean = LEAN_DEG * forward * LEAN_SIGN;
            Track(body, actions, SPINE_1, lean);
            Track(body, actions, SPINE_2, lean);
            Track(body, actions, SPINE_3, lean);

            Track(body, actions, SHOULDER_NEAR, ARM_REACH_DEG * forward * ARM_SIGN);
            Track(body, actions, SHOULDER_FAR, ARM_REACH_DEG * forward * ARM_SIGN);
            float elbow = committing ? ELBOW_PUSH_DEG : ELBOW_BENT_DEG;
            Track(body, actions, ELBOW_NEAR, elbow);
            Track(body, actions, ELBOW_FAR, elbow);
        }

        /// Knocked over. Tuck the legs under the body and push the arms down; the
        /// referee's `downOutSeconds` forfeits the round after 3 s on the mat, so
        /// this is a real rule and not just presentation.
        private void Recover(Agent_BipedBody body, ActionSegment<float> actions)
        {
            Track(body, actions, KNEE_NEAR, 95f);
            Track(body, actions, KNEE_FAR, 95f);
            Track(body, actions, HIP_NEAR, -80f);
            Track(body, actions, HIP_FAR, -80f);
            Track(body, actions, ANKLE_NEAR, 0f);
            Track(body, actions, ANKLE_FAR, 0f);
            Track(body, actions, SPINE_1, 0f);
            Track(body, actions, SPINE_2, 0f);
            Track(body, actions, SPINE_3, 0f);
            // Arms down and straight: the only thing available to press against.
            Track(body, actions, SHOULDER_NEAR, -100f * ARM_SIGN);
            Track(body, actions, SHOULDER_FAR, -100f * ARM_SIGN);
            Track(body, actions, ELBOW_NEAR, 0f);
            Track(body, actions, ELBOW_FAR, 0f);
        }

        /// Proportional controller from a target angle in DEGREES to a motor-speed
        /// action. `JointAngleNorm` is already `jointAngle * facingSign / 180`, so
        /// the error is computed in the same normalised, facing-local units the
        /// action space uses.
        private static void Track(Agent_BipedBody body, ActionSegment<float> actions, int joint, float targetDegrees)
        {
            if (joint >= actions.Length)
            {
                return;
            }
            float error = (targetDegrees / 180f) - body.JointAngleNorm(joint);
            actions[joint] = Mathf.Clamp(error * GAIN, -1f, 1f);
        }
    }
}
