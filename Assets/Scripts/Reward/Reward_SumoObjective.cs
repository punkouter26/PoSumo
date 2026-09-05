using UnityEngine;

namespace PoSumo
{
    /// Sumo-school objective and penalty provider: everything that shapes a fighter
    /// inside a refereed bout.
    ///
    /// SHAPING ONLY. The ±1 for winning or losing a round is assigned by
    /// `Systems_SumoMatchManager`, and no terminal is decided here — this class
    /// returns a number and cannot end an episode.
    ///
    /// Every coefficient comes from the character sheet, which is what lets Nick be
    /// a light mobile fighter and Kim a planted anchor without a line of per-fighter
    /// code. The defaults below are the constants this branch used before the
    /// coefficients became per-character, so an unassigned character trains exactly
    /// what it always did.
    public sealed class Reward_SumoObjective
    {
        /// Stance saturates here, roughly a shoulder-and-a-half. Past this, wider is
        /// not better.
        private const float SUMO_STANCE_WIDTH = 0.55f;
        /// Height below which a foot counts as planted, measured from the mat.
        private const float PLANTED_HEIGHT = 0.12f;
        /// Torso height at which the hips-low term saturates, and the span over
        /// which it ramps.
        private const float HIPS_HIGH_Y = 0.95f, HIPS_SPAN = 0.3f;
        /// Uprightness above which a fighter counts as still on its feet.
        private const float ON_FEET_UPRIGHT = 0.6f;
        /// Reward per m/s of upward torso velocity while down. Not per-character:
        /// it is a nudge out of a failure state, not a style.
        private const float RISE_VELOCITY_REWARD = 0.0005f;

        private float _rUpright = 0.0005f, _rClosing = 0.0006f, _rLunge = 0.001f;
        private float _lungeThresh = 1.5f;
        private float _rImpact = 0.01f, _impactCap = 8f, _rKnee = 0.0004f, _rHips = 0.0003f;
        private float _rCadence = 0.0015f, _rRise = 0.02f, _pJerk = 0.0003f;
        private float _bendFloor = 0.3f;
        private float _pEffort = 0.0015f, _rStance = 0.0009f;
        private float _rDrive = 0f;

        public void Configure(Agent_CharacterDefinition character)
        {
            if (character == null) return;
            _rUpright = character.uprightReward;
            _rClosing = character.closingReward;
            _rLunge = character.lungeBonus;
            _lungeThresh = character.lungeThreshold;
            _rImpact = character.impactReward;
            _impactCap = character.impactCap;
            _rKnee = character.kneeBendReward;
            _rHips = character.hipsLowReward;
            _rCadence = character.cadenceReward;
            _rRise = character.riseReward;
            _pJerk = character.jerkPenalty;
            _pEffort = character.effortPenalty;
            _rStance = character.stanceReward;
            _rDrive = character.driveReward;
            _bendFloor = character.straightLegEarnFraction;
        }

        /// Summed shaping for one step. Term order is preserved exactly as it was
        /// when it lived inline in `Agent_Biped.OnActionReceived` — these are small
        /// floats accumulated at 50 Hz, and reordering them changes the arithmetic.
        public float Evaluate(Agent_BipedBody body, Reward_StepCadence cadence,
                              in Reward_Context ctx)
        {
            float total = 0f;
            // Straight legs earn only the floor fraction of the closing terms.
            float bendGate = _bendFloor + (1f - _bendFloor) * ctx.KneeBend;
            Vector2 tv = ctx.TorsoVelocity;

            total += ctx.Upright * _rUpright;

            if (ctx.HasOpponent)
            {
                float toward = Mathf.Sign(ctx.OpponentX - ctx.TorsoPosition.x);
                float closing = Reward_Context.San(tv.x) * toward;
                total += closing * _rClosing * bendGate;
                // Lunge: explosive bursts toward the opponent pay extra.
                if (closing > _lungeThresh)
                {
                    total += (closing - _lungeThresh) * _rLunge * bendGate;
                }
            }

            // Impact reward: momentum actually delivered into the opponent. The
            // caller clears the accumulator after this returns.
            float impact = Mathf.Min(ctx.PendingImpact, _impactCap);
            if (impact > 0f)
            {
                total += impact * _rImpact;
            }

            if (!ctx.IsDown && ctx.Upright > ON_FEET_UPRIGHT)
            {
                total += ctx.KneeBend * _rKnee;
                total += HipsLowFactor(ctx) * _rHips;
                total += cadence.Evaluate(body, ctx.ArenaGroundY, _rCadence);
            }
            else if (ctx.IsDown)
            {
                // Getting back up mid-round pays (falls are not losses).
                total += Mathf.Max(0f, tv.y) * RISE_VELOCITY_REWARD;
                total += (Reward_Context.San(ctx.TorsoPosition.y) - ctx.LastTorsoY) * _rRise;
            }

            // SUSTAINED DRIVE. Every other term here pays for a collision — closing
            // speed, lunge, impact momentum — so the policy learned to hit rather
            // than to push, and measured push in contact was 71-500 N against a
            // human's sustained 350-700 N. Sumo is not a striking sport: it is won by
            // holding a load through the feet and walking the opponent backwards.
            //
            // Paid only when BOTH feet are carrying weight and the fighter is actually
            // moving toward the opponent, which is the one posture that cannot be
            // farmed by flailing.
            if (ctx.HasOpponent && body.FootDownNear && body.FootDownFar)
            {
                float toward = Mathf.Sign(ctx.OpponentX - ctx.TorsoPosition.x);
                float drive = Reward_Context.San(tv.x) * toward;
                if (drive > 0f)
                {
                    float grip = Mathf.Min(body.FootLoadNear, body.FootLoadFar);
                    // LIVE AS OF THE REBUILD01 GENERATION. `driveReward` was 0 on all
                    // five character sheets for the whole life of the project, so this
                    // line — the only term in the provider that pays for sumo's actual
                    // winning mechanic — had never contributed to any brain.
                    //
                    // It stayed at 0 because enabling it would have confounded a run
                    // that was also changing the observation vector. That reasoning
                    // held while there were warm-start trunks to protect; there are
                    // none now (Training/results is absent), so the vector change is a
                    // cold retrain regardless and the two are no longer separable by
                    // waiting. It is still worth isolating: the intended sequence is
                    // Rebuild01 with drive at 0, then a warm follow-up that turns it
                    // on ALONE, so a moved ELO attributes cleanly. See the per-fighter
                    // config headers.
                    total += drive * grip * _rDrive;
                }
            }

            // Sumo base: a wide, low, two-footed stance. Nothing rewarded this before,
            // and it showed — the fighters were measured mid-bout with the chest
            // 0.87 m above the mat at a 61-degree lean, and in another sample standing
            // on one foot with the other 0.48 m in the air. That is a collapse, not a
            // stance.
            total += StanceFactor(body, ctx) * _rStance;

            total += -ctx.Jerk * _pJerk;

            // ONE torque cost, not two. There used to be an L1 term on top of this:
            //
            //     float useful = Clamp01(|tv.x| / USEFUL_SPEED);
            //     total += -ctx.Energy * _pEnergy * (1f - useful);
            //
            // charging the same quantity a second time on a different norm, and
            // gated so that it vanished whenever the fighter was moving fast. That
            // gate is the exact mechanism the quadratic term below was added to
            // undo — drive hard and the torque bill disappears — so the pair pulled
            // against each other, and tuning either one moved a net cost that was
            // the sum of both. Keeping the L1 term meant every fighter carried two
            // coefficients (`energyPenalty`, `effortPenalty`) for one effect.
            //
            // The quadratic is the one worth keeping, and it is not a coin toss:
            // an L1 cost has a constant gradient, so it shifts every action down
            // uniformly and a policy pays it off by being slightly less lazy
            // everywhere. A squared cost rises steeply toward the rails, which is
            // what actually discourages slamming a motor to full torque — and
            // slamming is what was measured, 7 to 12 of the 13 motors above |0.9|
            // with a mean magnitude of 0.75-0.91. It is also ungated, so full-power
            // flailing costs something even mid-charge.
            //
            // `energyPenalty` stays on the character sheet: Reward_WalkObjective
            // still uses it, ungated, where there is no second term to overlap with.
            total += -ctx.Effort * _pEffort;

            return total;
        }

        /// ARENA-RELATIVE, matching the planted-feet terms below.
        ///
        /// This read raw world `TorsoPosition.y` while `StanceFactor` ten lines down
        /// already subtracted `ArenaGroundY` — two height tests in one provider, in
        /// two different frames. It was not an active bug, because the sumo referees
        /// sit at y = 0 in every scene and the walk population uses the other
        /// provider entirely, so the subtraction was a no-op everywhere it ran.
        ///
        /// It is corrected anyway: `Systems_SumoMatchManager` writes `arenaGroundY`
        /// from its own transform, so the no-op holds only as long as nobody offsets
        /// a sumo arena — and the walk lane in these very scenes proves offsetting is
        /// something this project does. Silent, total (Clamp01 pins it at 1) and
        /// invisible in a log is the worst failure shape available here.
        private static float HipsLowFactor(in Reward_Context ctx) =>
            Mathf.Clamp01((HIPS_HIGH_Y - (Reward_Context.San(ctx.TorsoPosition.y) - ctx.ArenaGroundY))
                          / HIPS_SPAN);

        /// How much this looks like a sumo stance, 0..1: both feet planted, and
        /// planted APART. Multiplied by knee bend so it cannot be farmed by standing
        /// straight-legged with the feet spread.
        ///
        /// Ground contact is judged against the mat plane rather than a collision
        /// query so it costs nothing per step; a foot within ankle height of the
        /// surface is planted.
        private static float StanceFactor(Agent_BipedBody body, in Reward_Context ctx)
        {
            float nearY = Reward_Context.San(body.FootNear.position.y) - ctx.ArenaGroundY;
            float farY = Reward_Context.San(body.FootFar.position.y) - ctx.ArenaGroundY;
            float nearDown = Mathf.Clamp01(1f - nearY / PLANTED_HEIGHT);
            float farDown = Mathf.Clamp01(1f - farY / PLANTED_HEIGHT);
            // Product, not average: one foot planted is standing, not a stance.
            float grounded = nearDown * farDown;

            float spread = Mathf.Abs(Reward_Context.San(body.FootNear.position.x)
                                     - Reward_Context.San(body.FootFar.position.x));
            float wide = Mathf.Clamp01(spread / SUMO_STANCE_WIDTH);

            return grounded * wide * ctx.KneeBend;
        }
    }
}
