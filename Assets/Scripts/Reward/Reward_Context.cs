using UnityEngine;

namespace PoSumo
{
    /// One physics step of everything a reward provider is allowed to see.
    ///
    /// Providers read this and the `Agent_BipedBody` they are handed; they never
    /// touch the `Agent` itself. That is the whole point of the split — a provider
    /// cannot call AddReward, SetReward or EndEpisode, so it is structurally
    /// incapable of the bug class where a shaping term quietly ends an episode.
    /// Shaping returns a number; episode control stays in `Agent_Biped`.
    ///
    /// Readonly struct, passed by `in`: this is constructed once per agent per
    /// physics step, so 10 bipeds at 50 Hz would otherwise be 500 heap allocations
    /// a second in the hottest path in the project.
    public readonly struct Reward_Context
    {
        /// Facing sign (+1 / -1). Every directional term is mirrored through this so
        /// one policy works facing either way.
        public readonly float FacingSign;
        /// World Y of the mat surface — what "planted" is measured against.
        public readonly float ArenaGroundY;

        public readonly Vector2 TorsoPosition;
        public readonly Vector2 TorsoVelocity;
        /// Torso Y from the previous step, for the rise-off-the-mat term.
        public readonly float LastTorsoY;

        /// Chest uprightness, 0..1.
        public readonly float Upright;
        /// Mean knee flexion, 0..1. Computed once by the agent because both schools
        /// gate on it.
        public readonly float KneeBend;

        /// Mean |action|, mean action², and mean |Δaction| over the 13 motors.
        public readonly float Energy;
        public readonly float Effort;
        public readonly float Jerk;

        /// Momentum delivered into the opponent since the last step, uncapped.
        public readonly float PendingImpact;

        /// True while a non-foot part is touching the mat.
        public readonly bool IsDown;

        public readonly bool HasOpponent;
        public readonly float OpponentX;

        public Reward_Context(float facingSign, float arenaGroundY, Vector2 torsoPosition,
                              Vector2 torsoVelocity, float lastTorsoY, float upright,
                              float kneeBend, float energy, float effort, float jerk,
                              float pendingImpact, bool isDown, bool hasOpponent,
                              float opponentX)
        {
            FacingSign = facingSign;
            ArenaGroundY = arenaGroundY;
            TorsoPosition = torsoPosition;
            TorsoVelocity = torsoVelocity;
            LastTorsoY = lastTorsoY;
            Upright = upright;
            KneeBend = kneeBend;
            Energy = energy;
            Effort = effort;
            Jerk = jerk;
            PendingImpact = pendingImpact;
            IsDown = isDown;
            HasOpponent = hasOpponent;
            OpponentX = opponentX;
        }

        /// NaN/Inf guard. Duplicated from `Agent_Biped.San` rather than shared,
        /// because every value that reaches a reward must pass one and a provider
        /// must not have to reach back into the agent to find it.
        public static float San(float v) => float.IsFinite(v) ? v : 0f;
    }
}
