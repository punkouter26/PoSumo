using UnityEngine;

namespace PoSumo
{
    /// Walk-school objective and penalty provider: the locomotion shaping used by
    /// the walk lane in every training scene and by the ceremonial walk-in.
    ///
    /// SHAPING ONLY, same contract as `Reward_SumoObjective`. The two walk terminals
    /// — fall (−1) and graduation (+3) — deliberately stay hardcoded in
    /// `Agent_Biped`: shaping is per-character, but the terminals fix the reward
    /// scale so different characters' walk runs stay comparable to each other on
    /// TensorBoard. Moving them here would make that scale a character setting.
    public sealed class Reward_WalkObjective
    {
        /// Below this |m/s| the fighter is a statue and is fined for it.
        private const float STALL_SPEED = 0.15f;

        private float _wForward = 0.004f, _wStanceFloor = 0.15f, _wBend = 0.0006f;
        private float _wUpright = 0.001f, _wCadence = 0.002f, _wEnergy = 0.0003f;
        private float _wStall = 0.0008f;

        public void Configure(Agent_CharacterDefinition character)
        {
            if (character == null) return;
            _wForward = character.walkForwardReward;
            _wStanceFloor = character.walkStanceFloor;
            _wBend = character.walkBendReward;
            _wUpright = character.walkUprightReward;
            _wCadence = character.walkCadenceReward;
            _wEnergy = character.walkEnergyPenalty;
            _wStall = character.walkStallPenalty;
        }

        /// Summed shaping for one step, in the original term order.
        ///
        /// The proven walk-school structure: falling ENDS the episode (in the
        /// caller), so no on-the-ground reward exploit can exist. Stance and cadence
        /// shaping keep the gait crouched and stepping. The coefficients come from
        /// the character sheet, so a fighter's gait can carry the same personality as
        /// its fight style — a driver rewards forward speed, a dancer rewards
        /// cadence.
        public float Evaluate(Agent_BipedBody body, Reward_StepCadence cadence,
                              in Reward_Context ctx)
        {
            float total = 0f;
            float walkGate = _wStanceFloor + (1f - _wStanceFloor) * ctx.KneeBend;
            float vx = Reward_Context.San(ctx.TorsoVelocity.x);

            total += vx * ctx.FacingSign * _wForward * walkGate;
            total += ctx.KneeBend * _wBend;
            total += ctx.Upright * _wUpright;
            total += cadence.Evaluate(body, ctx.ArenaGroundY, _wCadence);
            total += -ctx.Energy * _wEnergy;
            if (Mathf.Abs(vx) < STALL_SPEED)
            {
                total += -_wStall;   // no statue farming
            }

            return total;
        }
    }
}
