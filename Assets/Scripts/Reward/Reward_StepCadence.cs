using UnityEngine;

namespace PoSumo
{
    /// Step-cadence bonus: pays once per alternation of the single planted foot
    /// (near→far or far→near), i.e. actual stepping rather than skating.
    ///
    /// Owned by `Agent_Biped` and shared by BOTH schools rather than duplicated
    /// into each provider. The walk-in switches a fighter between Walk and Sumo
    /// mid-round, and the two branches always read one alternation history; giving
    /// each provider its own would let a fighter be paid twice for one step across
    /// that switch.
    public sealed class Reward_StepCadence
    {
        /// Height below which a foot counts as planted; the foot centre sits ~0.04
        /// when flat.
        private const float PLANTED_HEIGHT = 0.12f;
        /// A foot sliding faster than this is skating, not standing.
        private const float PLANTED_SPEED = 0.5f;
        /// Minimum seconds between paid steps — without it a foot chattering across
        /// the planted threshold banks a bonus every physics step.
        private const float MIN_STEP_INTERVAL = 0.25f;

        private int _lastSinglePlant;
        private float _lastStepTime;

        public void Reset()
        {
            _lastSinglePlant = 0;
            _lastStepTime = 0f;
        }

        /// Returns `scale` on the step that completes an alternation, otherwise 0.
        ///
        /// Heights are ARENA-RELATIVE, exactly as the stance term measures them.
        /// Against absolute world Y this test was meaningless for the walk lane,
        /// which sits at y = -60: every foot was trivially below the threshold, so
        /// plantedness collapsed to the velocity gate alone and the apex of a swing
        /// — airborne but momentarily slow — counted as a step. The same line
        /// behaved correctly in the sumo lane at y = 0, so one reward term meant two
        /// different things inside a single training scene.
        public float Evaluate(Agent_BipedBody body, float arenaGroundY, float scale)
        {
            bool nearPlanted =
                Reward_Context.San(body.FootNear.position.y) - arenaGroundY < PLANTED_HEIGHT
                && Mathf.Abs(body.FootNear.linearVelocity.x) < PLANTED_SPEED;
            bool farPlanted =
                Reward_Context.San(body.FootFar.position.y) - arenaGroundY < PLANTED_HEIGHT
                && Mathf.Abs(body.FootFar.linearVelocity.x) < PLANTED_SPEED;

            int pattern = (nearPlanted ? 1 : 0) | (farPlanted ? 2 : 0);
            float earned = 0f;
            if ((pattern == 1 && _lastSinglePlant == 2) || (pattern == 2 && _lastSinglePlant == 1))
            {
                if (Time.time - _lastStepTime > MIN_STEP_INTERVAL)
                {
                    earned = scale;
                    _lastStepTime = Time.time;
                }
            }
            if (pattern == 1 || pattern == 2)
            {
                _lastSinglePlant = pattern;
            }
            return earned;
        }
    }
}
