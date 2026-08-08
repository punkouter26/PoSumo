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

        /// Torso height ABOVE THE MAT over which the walk-tall term ramps: nothing
        /// below CROUCH, saturated at TALL.
        ///
        /// CALIBRATED AGAINST THE MEASURED GAIT, and that is the whole point of
        /// these two numbers. They were first set to 0.65/0.95, copied from the sumo
        /// school's HIPS_HIGH_Y / HIPS_SPAN on the reasoning that both schools should
        /// measure posture against one ruler. That was wrong, and it silently
        /// disabled the term: the walking gait actually operates at 0.46-0.80 m, so
        /// TallFactor was CLAMPED AT ZERO across most of its own working range. The
        /// reward could not tell 0.60 from 0.55 — no gradient, so no amount of
        /// multiplier strength could move the gait, which is exactly what two runs
        /// then measured. A shaping term that saturates outside the range the policy
        /// lives in is not a weak term, it is an absent one.
        ///
        /// 0.45 sits just under the lowest crawl observed and 1.00 just under the
        /// 1.06 standing pose, so the ramp spans the real distribution end to end.
        /// Re-measure these if the body's segment lengths ever change.
        ///
        /// Measured RELATIVE TO ctx.ArenaGroundY, not in world space. The walk lane
        /// sits 60 m below the arena in every training scene, so an absolute-Y
        /// version of this term would read about -60 and saturate dead for every
        /// walker. (The sumo school's HipsLowFactor IS absolute-Y — that is safe only
        /// because it is unreachable from Mode.Walk.)
        private const float WALK_TALL_Y = 1.00f, WALK_CROUCH_Y = 0.45f;

        private float _wForward = 0.004f, _wStanceFloor = 0.15f, _wBend = 0.0006f;
        private float _wUpright = 0.001f, _wCadence = 0.002f, _wEnergy = 0.0003f;
        private float _wStall = 0.0008f;
        /// Defaults to 0 so an untouched character sheet trains exactly the gait it
        /// always did — the shaping change is carried by the ASSETS, not by this.
        private float _wHeight;

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
            _wHeight = character.walkHeightReward;
        }

        /// How tall this walker is carrying itself, 0..1 — 1 at standing height,
        /// 0 in a crouch.
        ///
        /// This is the term that separates WALKING from SHUFFLING, and the reason
        /// `walkUprightReward` could never do it alone: `ctx.Upright` is the chest's
        /// ORIENTATION (dot of chest-up with world-up), so a fighter folded into a
        /// deep crouch with a vertical chest scores a perfect 1.0 and collects the
        /// full upright reward while creeping along at knee height. Orientation says
        /// which way the torso points; only height says how far off the mat it is.
        private static float TallFactor(in Reward_Context ctx)
        {
            float y = Reward_Context.San(ctx.TorsoPosition.y) - ctx.ArenaGroundY;
            return Mathf.Clamp01((y - WALK_CROUCH_Y) / (WALK_TALL_Y - WALK_CROUCH_Y));
        }

        /// Summed shaping for one step.
        ///
        /// The proven walk-school structure: falling ENDS the episode (in the
        /// caller), so no on-the-ground reward exploit can exist. Cadence shaping
        /// keeps the gait stepping. The coefficients come from the character sheet,
        /// so a fighter's gait can carry the same personality as its fight style —
        /// a driver rewards forward speed, a dancer rewards cadence.
        ///
        /// THE GATE IS ON HEIGHT, NOT KNEE BEND (2026-08-08), and that swap is the
        /// whole anti-crawl mechanism. It used to read
        ///
        ///     walkGate = stanceFloor + (1 - stanceFloor) * ctx.KneeBend
        ///
        /// which multiplied the DOMINANT forward-speed term by how deeply the knees
        /// were folded. At a floor of 0.15 a fighter with straight legs kept 15% of
        /// its forward reward and one in a deep crouch kept all of it, so crawling
        /// paid up to 6.7x. The gait was correct play against that reward.
        ///
        /// Two weaker fixes were measured and rejected before this one, and both are
        /// worth knowing about because each looked sufficient on paper:
        ///
        ///   Tall01 (cold retrain) raised the floor to 0.6, zeroed walkBendReward
        ///     and added a 0.003 additive height term. It COLLAPSED the gait — the
        ///     fighter ended up at 0.33 m and would not travel at all. Zeroing the
        ///     bend reward removed the crouch the gait was balancing on before
        ///     anything replaced it, and 0.003 over a 1500-step episode is +4.5
        ///     against a +3 graduation bonus that ENDS the episode, so not finishing
        ///     outpaid finishing.
        ///   Tall02 (warm start) kept the knee-bend gate at a 0.85 floor and an
        ///     additive 0.0015 height term. It restored walking and the fight
        ///     improved (ELO +500), but the gait height was UNCHANGED at 0.56-0.74
        ///     against the shipped brain's 0.55-0.76. An additive term worth ~10% of
        ///     per-step income cannot overturn a posture baked into an 8.4M trunk.
        ///
        /// The lesson both runs teach: the crouch was produced by a MULTIPLICATIVE
        /// gate on the largest term, so only a multiplicative gate can undo it. The
        /// additive height reward is kept, small, because it still gives a gradient
        /// toward standing at zero forward speed — but it is no longer the mechanism.
        public float Evaluate(Agent_BipedBody body, Reward_StepCadence cadence,
                              in Reward_Context ctx)
        {
            float total = 0f;
            float walkGate = _wStanceFloor + (1f - _wStanceFloor) * TallFactor(in ctx);
            float vx = Reward_Context.San(ctx.TorsoVelocity.x);

            total += vx * ctx.FacingSign * _wForward * walkGate;
            total += ctx.KneeBend * _wBend;
            total += ctx.Upright * _wUpright;
            // Appended next to the other posture term rather than at the end. This
            // does perturb the accumulation order of a set of small floats summed at
            // 50 Hz, which the extraction of these providers went out of its way to
            // preserve — acceptable here only because every brain has to be retrained
            // for this change anyway, so there is no old policy whose arithmetic we
            // are trying to reproduce bit-for-bit.
            total += TallFactor(in ctx) * _wHeight;
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
