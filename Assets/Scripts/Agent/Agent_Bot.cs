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
    /// The one sign that could NOT be read off a range — which way the shoulder
    /// swings toward the opponent — is isolated in ARM_SIGN below so it can be
    /// flipped from one place after watching a bout, rather than by hunting
    /// through the posture tables.
    ///
    /// THE BOT ONLY STANDS. It has no gait and no drive: every decision is either
    /// `Recover` (down) or `Stand` (up). A SIMBICON walk (`Advance`), a
    /// crouch-and-shove (`Drive`), a lunge timer, a cornered-drive rule and a
    /// debounced foot-contact filter used to live in this file behind
    /// `StandUprightThreshold`, and that threshold was set to 2 against an
    /// `upright` that is `Clamp01`ed — i.e. the gait was deliberately unreachable.
    /// That was investigated as a bug on 2026-08-07 and measured not to be one, in
    /// SCN_BOT with refereeing disabled, sampling the longest unbroken stretch of
    /// `!IsDown && upright > 0.5`:
    ///
    ///     gait off         1.35 - 4.16 s     <- best
    ///     threshold 0.95   0.45 s
    ///     threshold 0.85   0.43 - 0.89 s
    ///     threshold 0.70   0.89 s
    ///     threshold 0.60   0.89 s
    ///
    /// With the gait live the BOT walked itself over in under a second: a swing
    /// leg lifts half the support away, and the gait had never been tuned against
    /// the current body. Fixing it is a control-engineering job, not a threshold.
    /// Note the stand loop itself diverges run to run (1.35-4.16 s at fixed gains),
    /// so any single measurement here is noise — take repeats.
    ///
    /// The unreachable code was DELETED on 2026-08-26 rather than kept as a
    /// half-tuned reference. It lives in git history — commit 058f91f and earlier —
    /// and anyone reviving it should start from there, not from this file.
    public sealed class Agent_Bot
    {
        // Joint indices. These mirror JOINT_DEFS order exactly; if that table is
        // reordered these must move with it or the bot drives the wrong limbs.
        private const int HIP_NEAR = 0, KNEE_NEAR = 1, ANKLE_NEAR = 2;
        private const int HIP_FAR = 3, KNEE_FAR = 4, ANKLE_FAR = 5;
        private const int SPINE_1 = 6, SPINE_2 = 7, SPINE_3 = 8;
        private const int SHOULDER_NEAR = 9, ELBOW_NEAR = 10;
        private const int SHOULDER_FAR = 11, ELBOW_FAR = 12;

        /// +1 means "positive shoulder jointAngle swings the arm toward the opponent".
        private const float ARM_SIGN = 1f;

        // Proportional gain on NORMALISED angle error (error is degrees/180), so
        // the command saturates at an error of 180/Gain degrees.
        //
        // This was 6, which saturates past 30 deg — and a saturated command asks
        // the joint for its FULL motor speed, 400-500 deg/s on the legs. Thirteen
        // joints slamming at full rate is destabilising by itself: the body is
        // thrown around by its own corrections faster than any balance law can
        // answer. At 2.5 the command stays proportional out to ~72 deg, which is
        // wider than any target this controller asks for, so the servo behaves like
        // a spring instead of a switch.
        /// NOT const: the sweep harness writes these. UpperCamel rather than
        /// UPPER_SNAKE because they are static fields, not constants.
        internal static float Gain = 2.5f;

        // Standing balance. Stiff, because there is no swing leg to place — the
        // ankles and hips are the entire strategy.
        // Measured down from 90/12, which did not merely overshoot — it inverted the
        // fighter. A lean of 1.0 commanded a 90 deg target swing on hips and spine
        // at once; the trunk flipped through up = -0.37 -> +0.45 -> -0.49 in under a
        // second while the body was still at full height. The controller was doing
        // the falling over, not gravity.
        internal static float StandKp = 22f;
        internal static float StandKd = 4f;

        /// Degrees of correction per m/s of centre-of-mass drift. Moving forward
        /// leans the body back, which is how a person standing on a train stays put.
        internal static float StandKv = 26f;

        /// Lean setpoint per metre away from the ring centre — a gentle bias that
        /// makes the fighter drift back toward the middle instead of wandering off
        /// the rim while perfectly upright, which is what was measured.
        private const float STAND_KX = 0.05f;

        /// Hard cap on that bias. A lean is the whole propulsion here, so an
        /// uncapped one is a fall rather than a walk.
        private const float LEAN_TARGET_LIMIT = 0.13f;

        /// Spine correction clamp, per joint, of the 20 deg the joint allows.
        private const float SPINE_LIMIT_DEG = 18f;

        private const float STAND_HIP_DEG = -4f;

        /// Nearly straight, and that is load-bearing rather than cosmetic. At 22 deg
        /// the trunk stayed upright (0.92) but the whole body SANK — pelvis 1.06 ->
        /// 0.26 — because a bent knee has to hold the body's weight on a long moment
        /// arm and the joint only has 250 N-m, less once the Hill force-velocity
        /// term and fatigue take their cut. A near-straight leg carries 69.6 kg on
        /// its own geometry and asks the motor for almost nothing.
        ///
        /// It costs the sumo crouch, which matters for driving. Standing up is the
        /// prerequisite; crouching is only useful once the body can hold itself at
        /// all.
        internal static float StandKneeDeg = 7f;
        /// MEASURED at last (probe: gravity off, agent disabled, drive ankle 2 to
        /// each rail, read the SHIN in the FOOT's frame — with the foot planted that
        /// is exactly "which way does the body lean"):
        ///
        ///     facing-local ankle +37 deg -> shin x = -0.78  (body leans BACK)
        ///     facing-local ankle -35 deg -> shin x = +0.49  (body leans FORWARD)
        ///
        /// So positive ankle pushes the body backwards, which is what counters a
        /// forward lean — the original +correction sign was right all along, and
        /// zeroing it removed a CORRECT term while chasing the gain problem. The
        /// ankle is what regulates centre of pressure, so this is the cheapest
        /// balance authority the body has.
        private const float ANKLE_SHARE = 0.45f;

        /// How far the feet are split fore/aft when standing. Widens the support
        /// polygon the centre of mass has to stay inside.
        internal static float StanceSplitDeg = 20f;

        private float _lastTime;

        /// Gains are authored against Matt: 69.6 kg at widthScale 1, the documented
        /// baseline. Nick is 57.2 kg at widthScale 0.82 and is NOT the same plant —
        /// a lighter body is accelerated further by the same joint torque, so one
        /// gain set cannot serve both, which is why one fighter would hold a round
        /// while the other went down in the same match.
        ///
        /// Scaled by the square root of the mass ratio rather than the ratio: the
        /// correction is a target ANGLE, and what it has to overcome is a toppling
        /// rate that goes with the pendulum's natural frequency, which is a square
        /// root — not with weight directly.
        private float _gainScale = 1f;
        private const float BASELINE_MASS_KG = 69.6f;

        /// Eased joint targets and the timestep they advance on.
        private readonly float[] _smoothTarget = new float[13];
        private bool _targetsSeeded;
        private float _dt;

        /// Degrees per second the eased target may travel. Fast enough to reach a
        /// stance in a few tenths of a second, slow enough that no joint is ever
        /// commanded to its full 400-500 deg/s motor speed by a step change.
        private const float TARGET_SLEW_DEG_PER_S = 220f;

        /// A gap longer than this means the round was frozen and the body reset, so
        /// the eased targets must be re-seeded from the new pose rather than
        /// continuing from the pose the fighter had when it fell over.
        private const float RESET_GAP_SECONDS = 0.25f;

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

            // One timestep for the whole decision, from wall clock rather than an
            // assumed step: the BOT runs at decisionPeriod 1 but that is a setting,
            // not a guarantee.
            float rawGap = _lastTime > 0f ? ctx.Time - _lastTime : 0f;
            _dt = Mathf.Clamp(rawGap, 0f, 0.1f);
            // A long gap means the round froze and the referee reset the body, so
            // the eased targets have to restart from the NEW pose. Continuing from
            // the pose the fighter had when it fell over would command a snap back
            // to it — the very transient this is here to prevent.
            if (!_targetsSeeded || rawGap > RESET_GAP_SECONDS || rawGap < 0f)
            {
                SeedTargets(body);
            }
            _lastTime = ctx.Time;
            _gainScale = Mathf.Sqrt(Mathf.Clamp(body.TotalMass / BASELINE_MASS_KG, 0.5f, 2f));

            // Everything below is in the facing-local frame, so "forward" is always
            // +x regardless of which way this fighter is turned.
            float torsoX = body.Torso.position.x;
            float upright = Mathf.Clamp01(Vector2.Dot(body.Chest.transform.up, Vector2.up));

            // Where we are relative to the ring centre, facing-local: negative while
            // still on our own side of the mat, rising toward 0 at the centre.
            float offCentre = (torsoX - ctx.ArenaCenterX) * ctx.FacingSign;

            if (upright < 0.35f || body.IsLimp)
            {
                Recover(body, actions);
                return;
            }

            Stand(body, actions, offCentre);
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
        /// Ankles are driven using the SAME sign convention the hip probe
        /// established for the leg chain: a positive facing-local angle rotates the
        /// child backwards. Leaning forward therefore wants a positive ankle to push
        /// the shin back upright over the foot.
        private void Stand(Agent_BipedBody body, ActionSegment<float> actions, float offCentre)
        {
            float lean = TrunkLean(body);
            float leanRate = body.Chest != null ? body.Chest.angularVelocity * Mathf.Deg2Rad * body.facingSign : 0f;

            // POSITION, not just posture and velocity. Nothing in this controller
            // previously regulated WHERE the fighter was — it had ArenaCenterX and
            // RingHalfWidth handed to it and used neither — so a slow bias in any
            // direction was never corrected and the fighter eventually stepped off
            // the rim while perfectly upright. That is the failure that was watched
            // live: 1.8 m of clean upright travel, and then over the edge.
            //
            // offCentre is (torso - centre) * facingSign, so it is NEGATIVE while we
            // are still on our own side of the mat and rises toward 0 at the centre.
            // Moving toward the centre is therefore moving FORWARD in the facing
            // frame, which wants a POSITIVE correction — hence the minus sign, by
            // the same measured hip convention as the drift term.
            //
            // It doubles as the sumo objective: the centre is where you want to be,
            // and the edge is what loses a round.
            // Applied as a LEAN SETPOINT, not as another term in the correction.
            // Injecting it straight into `correction` drove the hip, ankle and spine
            // all at once against the balance loop and made things strictly worse —
            // measured, both fighters down within a second where they had been
            // holding a full round. Asking the body to lean slightly toward the
            // centre instead lets the existing balance law do the work, which is
            // also how a person walks somewhere: you lean, you do not shove your
            // own hip.
            float leanTarget = Mathf.Clamp(-STAND_KX * offCentre, -LEAN_TARGET_LIMIT, LEAN_TARGET_LIMIT);

            // DRIFT. Holding the trunk vertical is not the same as holding still,
            // and the difference was measured: a fighter left the dohyo at y -0.33
            // while its uprightness was still 0.95. It did not fall over, it walked
            // itself off the edge — the controller had no term that could even see
            // horizontal motion. Damping centre-of-mass velocity gives it one.
            CentreOfMass(body, out Vector2 comVelocity);
            float drift = comVelocity.x * body.facingSign;
            // DRIFT TERM IS SUBTRACTED, and the sign follows from the measured hip
            // convention rather than intuition. Positive hip = thigh backward, so
            // with the foot planted a positive correction drives the pelvis FORWARD.
            // To arrest a forward drift the correction must therefore be NEGATIVE.
            // Adding +KV*v instead made a backward drift produce a negative
            // correction, which drove the pelvis further backward and fed itself:
            // measured runaway to -1.5 m/s in the facing-local frame, both fighters,
            // straight off the back of the dohyo.
            float correction = (StandKp * (lean - leanTarget) + StandKd * leanRate - StandKv * drift) * _gainScale;

            // Hips extend against a forward lean, pushing the pelvis back under the
            // chest; knees stay softly bent so the legs can absorb rather than
            // transmit, which is also the sumo stance.
            // STAGGERED, not feet-together. In the sagittal plane a biped with both
            // feet in the same place has a support base of one foot length, ~0.27 m,
            // and the centre of mass only has to cross that to be unrecoverable.
            // Splitting the legs — one forward, one back — roughly doubles it, and
            // it is the sumo stance for exactly this reason. Hip flexion is negative
            // (measured), so the forward leg subtracts and the trailing leg adds.
            Track(body, actions, HIP_NEAR, STAND_HIP_DEG - StanceSplitDeg + correction);
            Track(body, actions, HIP_FAR, STAND_HIP_DEG + StanceSplitDeg + correction);
            Track(body, actions, KNEE_NEAR, StandKneeDeg);
            Track(body, actions, KNEE_FAR, StandKneeDeg);
            Track(body, actions, ANKLE_NEAR, Mathf.Clamp(correction * ANKLE_SHARE, -30f, 30f));
            Track(body, actions, ANKLE_FAR, Mathf.Clamp(correction * ANKLE_SHARE, -30f, 30f));

            // Spine rights the chest. NEGATIVE opposes a forward pitch, because the
            // probe (gravity off, agent disabled, joints 6/7/8 driven to +1, chest
            // read through pelvis.InverseTransformPoint) showed positive spine angle
            // carries the chest forward. Split across the three joints since each
            // only has 20 deg to give.
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
        private void Track(Agent_BipedBody body, ActionSegment<float> actions, int joint, float targetDegrees)
        {
            if (joint >= actions.Length)
            {
                return;
            }

            // RATE-LIMITED TARGET, and this is what stops the BOT jumping at the
            // bell. The reset pose is a deep crouch — measured hip -80 deg, knee
            // +95 deg, both feet together — while the standing stance wants hip ~-4
            // and knee ~7. That is an ~88 deg knee error on frame one, which
            // saturates the command and extends BOTH legs at full motor speed
            // simultaneously. The fighter launches: measured with every part off
            // the ground (zero contacts) at torso y 0.96, then a crash to 0.19.
            //
            // Easing the target in at a bounded rate means the pose is always
            // reachable, the error stays small, and the servo never rails. It also
            // makes the controller independent of whatever pose a round starts in,
            // which is the general form of "start somewhere easy to balance from".
            _smoothTarget[joint] = Mathf.MoveTowards(
                _smoothTarget[joint], targetDegrees, TARGET_SLEW_DEG_PER_S * _dt);

            float error = (_smoothTarget[joint] / 180f) - body.JointAngleNorm(joint);
            actions[joint] = Mathf.Clamp(error * Gain, -1f, 1f);
        }

        /// Seed the eased targets from the body's ACTUAL pose, so the first decision
        /// of a round asks for no movement at all and the stance is approached from
        /// wherever the referee left the fighter.
        private void SeedTargets(Agent_BipedBody body)
        {
            for (int joint = 0; joint < _smoothTarget.Length; joint++)
            {
                _smoothTarget[joint] = body.JointAngleNorm(joint) * 180f;
            }
            _targetsSeeded = true;
        }
    }
}
