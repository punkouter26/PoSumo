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
        private const float SWING_HIP_BASE_DEG = -10f;  // negative = flexed = thigh forward
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

        /// Trunk lean the walker is asked to HOLD while advancing, as a dot product
        /// (0 = vertical, 1 = horizontal). Small on purpose: this is the entire
        /// propulsion, and too much is a fall rather than a walk.
        private const float WALK_LEAN_BIAS = 0.16f;

        /// Stance hip while walking. Near neutral — it holds the body up, it does
        /// not push. See the note at the leanError term.
        private const float STANCE_HIP_NEUTRAL_DEG = 2f;

        /// Target advance speed, m/s, in the facing-local frame. This is what turns
        /// the balance feedback into locomotion: without it the gait holds whatever
        /// velocity it happens to have, including a backward one.
        private const float WALK_SPEED_MPS = 0.9f;

        /// Nominal hip-to-sole reach of an extended leg, metres, at widthScale 1.
        /// From JOINT_DEFS: hip anchor 0.964, knee 0.533, ankle 0.10, so thigh and
        /// shin are ~0.431 and ~0.433. Scaled per fighter by widthScale.
        private const float LEG_REACH_M = 0.87f;
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

        /// DEBOUNCED foot contact. Agent_BipedBody.FootDownNear/Far are sampled in
        /// FixedUpdate from GetContacts and were measured FLICKERING between True
        /// and False on consecutive frames while the foot demonstrably had contacts
        /// — so any stance/swing decision reading them directly is reading noise,
        /// and the gait was only ever swapping legs on its timeout.
        ///
        /// Asymmetric hysteresis on purpose: believe a landing quickly (2 frames at
        /// 50 Hz = 40 ms) but a lift-off slowly (4 frames), because a spurious
        /// "airborne" reading mid-stance is far more damaging than a late one — it
        /// hands the balance loop a support leg that it thinks is not there.
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

        private int _downRunNear, _downRunFar;
        private bool _stableDownNear, _stableDownFar;
        private const int CONTACT_ON_FRAMES = 2;
        private const int CONTACT_OFF_FRAMES = 4;

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
                _swingTimer = 0f;
            }
            _lastTime = ctx.Time;
            _gainScale = Mathf.Sqrt(Mathf.Clamp(body.TotalMass / BASELINE_MASS_KG, 0.5f, 2f));

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
                Stand(body, actions, offCentre);
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
            _swingTimer += _dt;

            bool nearIsSwing = _swingLegIsNear;
            bool downNear = Debounce(body.FootDownNear, ref _downRunNear, ref _stableDownNear);
            bool downFar = Debounce(body.FootDownFar, ref _downRunFar, ref _stableDownFar);
            bool swingFootDown = nearIsSwing ? downNear : downFar;

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
            // THE MISSING TERM: a desired speed. Plain SIMBICON feedback regulates
            // balance, not velocity — it catches whatever fall is happening, so a
            // walker that starts drifting backwards is caught by a backward step and
            // then keeps walking backwards, STABLY and forever. That is exactly what
            // was measured: both fighters travelling outward at uprightness 0.94-1.00
            // until they left the dohyo, under every propulsion model tried, with
            // `forward` verified as +1 so it was never a direction-logic bug.
            //
            // Tracking (v - desired) instead of v fixes the sign of the whole gait:
            // too slow => the term goes negative => the swing foot is placed further
            // BACK => the body falls forward over it => it accelerates. Placing the
            // foot behind the centre of mass is how a biped speeds up; placing it in
            // front is a brake.
            // ── CAPTURE POINT ──────────────────────────────────────────────
            //
            // Replaces the hand-tuned hip angles. Where a foot MUST land to arrest
            // the body is not a matter of taste — for an inverted pendulum it is
            //
            //     x_capture = x_com + v * sqrt(h / g)
            //
            // measured from the centre of mass, with h the centre-of-mass height.
            // Landing there brings the body exactly to rest; landing SHORT of it
            // leaves residual forward velocity, which is how a walker accelerates,
            // so the desired speed is expressed as a deliberate shortfall rather
            // than as another gain to tune.
            //
            // The hip angle to put the foot there is then geometry, not a constant:
            // for a leg of reach L the horizontal offset dx needs asin(dx / L). That
            // is converted into this project's convention with the MEASURED sign —
            // negative jointAngle swings the thigh forward — so the whole thing has
            // exactly one place a sign could be wrong, and it is the one that was
            // probed.
            float comHeight = Mathf.Max(0.35f, com.y - (stanceFoot != null ? stanceFoot.position.y : com.y - 0.9f));
            float capture = v * Mathf.Sqrt(comHeight / 9.81f);

            // Aim SHORT of the capture point by the distance the body should still
            // be travelling at the end of the step.
            float desiredShortfall = WALK_SPEED_MPS * forward * Mathf.Sqrt(comHeight / 9.81f);
            float footOffset = capture - desiredShortfall + d;

            float legReach = Mathf.Max(0.3f, LEG_REACH_M * body.widthScale);
            float hipGeometric = Mathf.Asin(Mathf.Clamp(footOffset / legReach, -0.95f, 0.95f)) * Mathf.Rad2Deg;
            float swingHip = Mathf.Clamp(-hipGeometric, SWING_HIP_MIN_DEG, SWING_HIP_MAX_DEG);

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

            // WALKING IS STANDING WITH ONE LEG SWINGING. The previous version of
            // this REPLACED the balance controller with its own weaker one, so the
            // moment the gait engaged the fighter lost everything that was keeping
            // it upright. The stance leg and trunk now run the SAME law that holds
            // a stand — same gains, same measured ankle, same drift damping — and
            // the gait's only job is the swing leg above.
            float lean = TrunkLean(body);
            float leanRate = body.Chest != null ? body.Chest.angularVelocity * Mathf.Deg2Rad * body.facingSign : 0f;
            // LOCOMOTION COMES FROM A LEAN, NOT FROM A PUSH. Driving the stance hip
            // into extension to "push off" pitches the trunk BACKWARD about the
            // planted foot, and the balance law then steps back to catch it — which
            // is exactly the outward travel that was measured, with `forward`
            // verified as +1 for both fighters, so it was never a direction-logic
            // bug. Walking is controlled falling: hold the trunk a little way
            // toward the target, let gravity do the work, and let the swing leg
            // catch it. So the balance controller is given a non-zero setpoint
            // instead of a push.
            float leanError = lean - WALK_LEAN_BIAS * forward;
            // DRIFT TERM IS SUBTRACTED, and the sign follows from the measured hip
            // convention rather than intuition. Positive hip = thigh backward, so
            // with the foot planted a positive correction drives the pelvis FORWARD.
            // To arrest a forward drift the correction must therefore be NEGATIVE.
            // Adding +KV*v instead made a backward drift produce a negative
            // correction, which drove the pelvis further backward and fed itself:
            // measured runaway to -1.5 m/s in the facing-local frame, both fighters,
            // straight off the back of the dohyo.
            float correction = (StandKp * leanError + StandKd * leanRate - StandKv * v) * _gainScale;

            // Stance hip near neutral now: its job is to hold, not to shove.
            Track(body, actions, stanceHipJoint, STANCE_HIP_NEUTRAL_DEG + correction);
            Track(body, actions, stanceKneeJoint, StandKneeDeg);
            Track(body, actions, stanceAnkleJoint, Mathf.Clamp(correction * ANKLE_SHARE, -30f, 30f));

            // Spine rights the chest. NEGATIVE opposes a forward pitch, because the
            // probe showed positive spine angle carries the chest forward. Split
            // across the three joints since each only has 20 deg to give.
            // Same term the stand uses, for the same reason — one balance law, not
            // two that disagree at the moment the gait engages.
            float spine = Mathf.Clamp(-correction / 3f, -SPINE_LIMIT_DEG, SPINE_LIMIT_DEG);
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

        /// Hysteresis filter for one foot. Returns the debounced state.
        private static bool Debounce(bool raw, ref int run, ref bool stable)
        {
            run = raw == stable ? 0 : run + 1;
            int need = raw ? CONTACT_ON_FRAMES : CONTACT_OFF_FRAMES;
            if (run >= need)
            {
                stable = raw;
                run = 0;
            }
            return stable;
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
