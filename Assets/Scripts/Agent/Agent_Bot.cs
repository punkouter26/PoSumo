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

        // Proportional gain on normalised angle error. High enough to actually
        // reach a pose against gravity and passive joint resistance, low enough
        // that the motor is not permanently railed — a railed motor is both the
        // most expensive thing a joint can do (fatigue integrates the delivered
        // torque) and the least controllable.
        private const float GAIN = 6f;

        // Ranges in metres along the mat.
        private const float ENGAGE_RANGE = 1.30f;   // inside this, stop walking and drive
        private const float LUNGE_RANGE = 0.75f;    // inside this, commit to the shove
        private const float EDGE_ALARM = 0.85f;     // this close to our own edge, dig in

        // Gait: a phase oscillator, not a planner. Good enough to close distance.
        private const float STEP_HZ = 1.15f;
        private const float STEP_SWING_DEG = 26f;
        private const float STEP_LIFT_DEG = 38f;

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

            if (driving)
            {
                Drive(body, actions, forward, lunging || cornered);
            }
            else
            {
                Advance(body, actions, forward, ctx.Time);
            }
        }

        /// Closing distance. Alternating legs off a phase oscillator, torso held
        /// slightly forward so the walk carries momentum into the tachiai.
        private void Advance(Agent_BipedBody body, ActionSegment<float> actions, float forward, float time)
        {
            float phase = time * STEP_HZ * Mathf.PI * 2f;
            float swing = Mathf.Sin(phase) * STEP_SWING_DEG * forward;
            float lift = Mathf.Max(0f, Mathf.Sin(phase)) * STEP_LIFT_DEG;
            float liftOther = Mathf.Max(0f, -Mathf.Sin(phase)) * STEP_LIFT_DEG;

            // Hip flexion is NEGATIVE, so a forward swing subtracts.
            Track(body, actions, HIP_NEAR, -swing + CROUCH_HIP_DEG * 0.5f);
            Track(body, actions, HIP_FAR, swing + CROUCH_HIP_DEG * 0.5f);
            Track(body, actions, KNEE_NEAR, lift + 10f);
            Track(body, actions, KNEE_FAR, liftOther + 10f);
            Track(body, actions, ANKLE_NEAR, 0f);
            Track(body, actions, ANKLE_FAR, 0f);

            float lean = LEAN_DEG * 0.45f * forward * LEAN_SIGN;
            Track(body, actions, SPINE_1, lean);
            Track(body, actions, SPINE_2, lean);
            Track(body, actions, SPINE_3, lean);

            // Arms carried low and slightly forward — the sumo approach, and it
            // keeps them out of the way of the legs.
            Track(body, actions, SHOULDER_NEAR, ARM_REACH_DEG * 0.35f * forward * ARM_SIGN);
            Track(body, actions, SHOULDER_FAR, ARM_REACH_DEG * 0.35f * forward * ARM_SIGN);
            Track(body, actions, ELBOW_NEAR, ELBOW_BENT_DEG);
            Track(body, actions, ELBOW_FAR, ELBOW_BENT_DEG);
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
