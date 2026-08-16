using Unity.MLAgents;
using UnityEngine;

namespace PoSumo
{
    /// Training referee for the sumo env. Mirrors the deployed game's rules:
    /// falling down is NOT an instant loss — a wrestler loses by dropping off the
    /// dohyo (a foot below footOffMatY, or the torso below fallY as a backstop),
    /// or by lying down past downOutSeconds. Each round can randomize the
    /// starting platform width and surface friction so policies train across the
    /// whole endgame space. Timeout is a draw.
    ///
    /// Every number here that also exists in Assets/Settings/GameTuning.asset
    /// must equal the value in that asset. This component does NOT read it —
    /// each training scene carries its own serialized copy — so the code default
    /// is what a NEW scene inherits, and it is the thing that goes stale
    /// silently. It had: ring 5.5 vs a shipped 3.5, timeout 30 vs 20, spawn gap
    /// 1.2 vs 2.5. Corrected 2026-08-15.
    ///
    /// The one shipped rule not reproduced here is the head-KO count; see the
    /// note beside downOutSeconds for why it cannot be.
    public sealed class Systems_SumoMatchManager : MonoBehaviour
    {
        public Agent_Biped wrestlerA;
        public Agent_Biped wrestlerB;
        public Systems_SumoArena arena;
        // Matches Systems_GameMatchManager's ring. These two referees are kept in
        // step on the rules a policy has to learn, and ring width is one of them:
        // it is fed to the agent as a normalised edge-distance observation, so
        // training on 2.75 and fighting on 5.5 hands the brain an input
        // distribution it never saw.
        //
        // 3.5 is the SHIPPED value in Assets/Settings/GameTuning.asset. This
        // default said 5.5 long after the four SCN_TRAIN_* scenes had been moved
        // to 3.5, so the code and the scenes disagreed by a whole 2 m of mat and
        // only the scenes were ever right. Anyone adding a fifth training scene
        // inherited the wrong arena silently. Keep this equal to GameTuning.
        public float ringHalfWidth = 3.5f;
        // 20 s, matching GameTuning.roundTimeoutSeconds. The code default here
        // was 30 while every training scene serialised 20 — same drift as above.
        public float roundTimeoutSeconds = 20f;
        [Tooltip("Legacy rule: any non-foot ground contact loses. Off for game parity.")]
        public bool knockdownLoses = false;
        public float fallY = -0.2f;
        [Tooltip("Game parity: a foot dropping below this height loses the bout (stepping out). Must match Systems_GameMatchManager.footOffMatY.")]
        public float footOffMatY = -0.06f;

        [Header("Game parity rules")]
        [Tooltip("Seconds a fighter may lie down before the round is awarded to the opponent. 0 disables it. Must match GameTuning.downOutSeconds.\n\nPorted from Systems_GameMatchManager on 2026-08-15. It had been game-only, so no brain had ever learned that staying down is fatal — and measured play had 57% of rounds decided by exactly this rule. A policy trained without it is optimising for a referee it will never meet.")]
        // 0 since 2026-08-16, to follow GameTuning.downOutSeconds back to 0. The
        // rule was removed from the game because the SHRINKING MAT below now does
        // its job better: a fighter who stays down is squeezed off the edge and
        // loses a ring-out, which is how sumo ends, instead of being retired by a
        // count that reads on screen as the game giving up. Measured play after the
        // change was 4 rounds, 4 ring-outs, no stalls and no draws.
        //
        // Do NOT raise this without also removing the shrink, and do not remove the
        // shrink without restoring this. Either alone brings back the stall the
        // count was written for: two motionless bodies on a mat that never closes.
        public float downOutSeconds = 0f;
        [Tooltip("Width in metres of the slick band at each rim. Must match GameTuning.tawaraBandWidth. Applied to the arena in Start, so a stale serialized value on Systems_SumoArena is overwritten at runtime exactly like ringHalfWidth is.")]
        public float tawaraBandWidth = 1.2f;
        [Tooltip("Friction inside the tawara band. Must match GameTuning.tawaraFriction.")]
        public float tawaraFriction = 0.18f;

        // NOT PORTED: knockoutsToLoseMatch. The head-KO rule reads
        // Systems_BodyDamage.Knockout, and Systems_BodyDamage is a presentation
        // companion that only Systems_GameMatchManager spawns — a training env
        // has no damage model at all, so there is no event to listen for. Adding
        // one is not a referee change: it would put dismemberment and its mass
        // changes into the training physics, which invalidates every brain for a
        // rule that fires once or twice a match. Left game-only, deliberately.

        [Header("Domain randomization")]
        public bool randomizeRounds = true;
        // Widened to span the new game ring. The upper bound used to be 2.75, the
        // old fighting half-width, so every training round happened on a mat no
        // wider than that — and the game ring is now 5.5. Edge distance reaches the
        // policy normalised by ringHalfWidth, so a brain that only ever saw the
        // narrow end reads the wide ring as a feature range it was never trained
        // on. The upper bound must therefore track ringHalfWidth — it said 5.5
        // while the scenes said 3.5, so this default randomised across half a
        // metre of mat per side that the game does not have.
        public Vector2 startHalfRange = new Vector2(1.7f, 3.5f);
        public Vector2 frictionRange = new Vector2(0.5f, 1.1f);

        [Header("Curriculum dials (overridden by trainer environment_parameters)")]
        [Tooltip("Half the spawn gap between wrestlers. 2.5 is the stand-off the four training scenes serialize and the value the 3.5 ring was tuned around; this default said 1.2, from the old 5.5-metre arena.")]
        public float spawnGapHalf = 2.5f;
        [Tooltip("Random mid-round perturbation shove impulse (N·s). Lessons start heavy (45) and fade to 0.")]
        public float shoveImpulse = 0f;
        [Tooltip("0 = always full stable platform, 1 = full width/friction randomization.")]
        public float platformDifficulty = 1f;

        [Tooltip("Seconds into the round before the mat starts closing in. 0 disables the shrink. Must match GameTuning.shrinkStartSeconds.\n\nDeliberately late: the opening exchange should be fought on a full mat, and starting the squeeze at t=0 would make every round a scramble for the middle.")]
        public float shrinkStartSeconds = 8f;
        [Tooltip("Half-width the mat closes to by the bell, at the default ring size. Must match GameTuning.shrinkToHalfWidth.\n\nScaled by the round's randomized start width, so a domain-randomized small ring shrinks by the same PROPORTION rather than to the same absolute number — which would mean no shrink at all on an already-narrow round.")]
        public float shrinkToHalfWidth = 1.8f;

        private float _elapsed;
        private float _downA, _downB;
        private float _startHalf;
        private float _appliedHalf;
        private float _nextShoveTime;
        private Agent_BipedBody _bodyA, _bodyB;

        private void Start()
        {
            if (wrestlerA == null || wrestlerB == null)
            {
                var agents = FindObjectsByType<Agent_Biped>();
                foreach (var a in agents)
                {
                    if (a.teamId == 0) wrestlerA = a; else wrestlerB = a;
                }
            }
            if (arena == null) arena = FindAnyObjectByType<Systems_SumoArena>();
            // Before ResetRound below, which reaches SetPlatformHalfWidth ->
            // EnsureTawaraBands: the band is rebuilt from these two numbers, so
            // writing them afterwards would leave the first round on whatever the
            // scene happened to serialize (0.7 on both sumo arenas, against the
            // game's 1.2). The slick rim is what turns "almost out" into "out",
            // so a brain trained without it has never felt the edge give way.
            if (arena != null)
            {
                arena.tawaraBandWidth = tawaraBandWidth;
                arena.tawaraFriction = tawaraFriction;
            }
            // Strikes launch in training too, so a policy meets the same physics it
            // will fight under. Sensor_Impact.AnyImpact is a STATIC event, so one
            // instance serves every body in the scene — and a training scene holds
            // several referees, hence the scene-wide check rather than one each.
            if (FindAnyObjectByType<Systems_StrikeImpulse>() == null)
            {
                new GameObject("StrikeImpulse").AddComponent<Systems_StrikeImpulse>();
            }

            wrestlerA.opponent = wrestlerB;
            wrestlerB.opponent = wrestlerA;
            wrestlerA.ringHalfWidth = ringHalfWidth;
            wrestlerB.ringHalfWidth = ringHalfWidth;
            // Must match Systems_GameMatchManager: the sumo stance shaping measures
            // foot height against this plane, and training is where that reward is
            // actually earned. Left unset it would read every foot as planted.
            wrestlerA.arenaGroundY = transform.position.y;
            wrestlerB.arenaGroundY = transform.position.y;
            wrestlerA.arenaCenterX = transform.position.x;
            wrestlerB.arenaCenterX = transform.position.x;
            _bodyA = wrestlerA.GetComponent<Agent_BipedBody>();
            _bodyB = wrestlerB.GetComponent<Agent_BipedBody>();
            _startHalf = ringHalfWidth;
            ResetRound();
        }

        private void FixedUpdate()
        {
            if (wrestlerA == null || wrestlerB == null) return;
            _elapsed += Time.fixedDeltaTime;

            // Curriculum perturbation shoves: random balance attacks keep the
            // recovery skill exercised in context; lessons fade them out.
            if (shoveImpulse > 0.5f && _elapsed >= _nextShoveTime)
            {
                var body = Random.value < 0.5f ? _bodyA : _bodyB;
                if (body != null)
                {
                    var dir = new Vector2(Random.Range(-1f, 1f), Random.Range(-0.1f, 0.35f)).normalized;
                    body.Chest.AddForce(dir * shoveImpulse, ForceMode2D.Impulse);
                }
                _nextShoveTime = _elapsed + Random.Range(3f, 7f);
            }

            // Down-out, mirroring Systems_GameMatchManager. IsDown is any
            // non-foot ground contact, so this is a lying-down timer, not a
            // stumble timer — it only runs while the fighter is actually on the
            // mat and it resets the instant a knee or an elbow comes off it.
            if (downOutSeconds > 0f)
            {
                _downA = wrestlerA.IsDown ? _downA + Time.fixedDeltaTime : 0f;
                _downB = wrestlerB.IsDown ? _downB + Time.fixedDeltaTime : 0f;
            }
            bool aDownOut = downOutSeconds > 0f && _downA >= downOutSeconds;
            bool bDownOut = downOutSeconds > 0f && _downB >= downOutSeconds;

            TickShrinkingRing();

            bool aOut = Loses(wrestlerA) || aDownOut;
            bool bOut = Loses(wrestlerB) || bDownOut;

            if (aOut || bOut)
            {
                if (aOut && !bOut) EndRound(winner: wrestlerB, loser: wrestlerA);
                else if (bOut && !aOut) EndRound(winner: wrestlerA, loser: wrestlerB);
                // Both down past the count: the one who went down FIRST has the
                // larger timer and loses. Same tiebreak the game referee uses —
                // without it a mutual pile-up is a draw and neither policy learns
                // that getting up sooner wins.
                else if (aDownOut && bDownOut && !Mathf.Approximately(_downA, _downB))
                {
                    bool aLoses = _downA > _downB;
                    EndRound(winner: aLoses ? wrestlerB : wrestlerA,
                             loser: aLoses ? wrestlerA : wrestlerB);
                }
                else Draw(); // genuinely simultaneous — call it a draw
            }
            else if (_elapsed >= roundTimeoutSeconds)
            {
                Draw();
            }
        }

        /// Closes the mat in as the round clock runs down, mirroring
        /// Systems_GameMatchManager.TickShrinkingRing so a policy trains against the
        /// arena it will actually fight on.
        ///
        /// Nothing here detects anything: Loses() is purely vertical (a foot below
        /// the mat surface) and SetPlatformHalfWidth moves the real platform
        /// collider, so withdrawing the floor produces an ordinary ring-out through
        /// the path that already exists. That is why this is a dozen lines and not
        /// a new losing condition.
        ///
        /// The one difference from the game referee is the proportional target: a
        /// training round randomizes its start width, so shrinking to a FIXED 1.8
        /// would be a no-op on any round that already started near it.
        private void TickShrinkingRing()
        {
            if (arena == null || shrinkStartSeconds <= 0f) return;

            float span = roundTimeoutSeconds - shrinkStartSeconds;
            float target = _startHalf;
            if (span > 0f && _elapsed > shrinkStartSeconds)
            {
                float endHalf = ringHalfWidth > 0f
                    ? shrinkToHalfWidth * (_startHalf / ringHalfWidth)
                    : shrinkToHalfWidth;
                float progress = Mathf.Clamp01((_elapsed - shrinkStartSeconds) / span);
                target = Mathf.Lerp(_startHalf, endHalf, progress);
            }

            // 1 cm of hysteresis: below that the contraction is invisible and only
            // costs a collider rebuild, every physics step, on every arena in a
            // scene that holds several.
            if (Mathf.Abs(target - _appliedHalf) < 0.01f) return;
            _appliedHalf = target;
            arena.SetPlatformHalfWidth(target);
            // The slick rim travels with the edge, or the bales stay out at the
            // original radius and the contracted mat has full grip up to a cliff.
            arena.EnsureTawaraBands(target);
        }

        private bool Loses(Agent_Biped w)
        {
            // Stepping out, matching the deployed game exactly: a foot below the
            // mat surface has gone over the edge. Training previously used only
            // the torso test, so policies never learned that a stray foot is
            // fatal — the rules had silently diverged.
            Agent_BipedBody body = w == wrestlerA ? _bodyA : _bodyB;
            if (body != null)
            {
                float limit = transform.position.y + footOffMatY;
                if (body.FootNear != null && body.FootNear.position.y < limit) return true;
                if (body.FootFar != null && body.FootFar.position.y < limit) return true;
            }
            if (w.Torso.position.y < fallY) return true;   // backstop: thrown clear
            if (knockdownLoses && w.IsDown) return true;   // legacy instant-loss rule
            return false;
        }

        private void EndRound(Agent_Biped winner, Agent_Biped loser)
        {
            winner.AddReward(1f);
            loser.AddReward(-1f);
            winner.EndEpisode();
            loser.EndEpisode();
            ResetRound();
        }

        private void Draw()
        {
            // Interrupted (not terminal) so value bootstrapping stays correct.
            wrestlerA.EpisodeInterrupted();
            wrestlerB.EpisodeInterrupted();
            ResetRound();
        }

        private void ResetRound()
        {
            _elapsed = 0f;
            _downA = _downB = 0f;
            _nextShoveTime = Random.Range(2f, 5f);

            // Curriculum lessons override the dials each round.
            if (Academy.IsInitialized)
            {
                var ep = Academy.Instance.EnvironmentParameters;
                spawnGapHalf = ep.GetWithDefault("spawn_gap_half", spawnGapHalf);
                shoveImpulse = ep.GetWithDefault("shove_impulse", shoveImpulse);
                platformDifficulty = ep.GetWithDefault("platform_difficulty", platformDifficulty);
            }

            float cx = transform.position.x;
            if (wrestlerA != null)
                wrestlerA.transform.position = new Vector3(cx - spawnGapHalf, 0f, 0f);
            if (wrestlerB != null)
                wrestlerB.transform.position = new Vector3(cx + spawnGapHalf, 0f, 0f);

            float d = Mathf.Clamp01(platformDifficulty);
            if (randomizeRounds && d > 0f)
            {
                float minStart = Mathf.Lerp(ringHalfWidth, startHalfRange.x, d);
                _startHalf = Random.Range(minStart, startHalfRange.y);
                float frLo = Mathf.Lerp(0.9f, frictionRange.x, d);
                float frHi = Mathf.Lerp(0.9f, frictionRange.y, d);
                if (arena != null) arena.SetSurfaceFriction(Random.Range(frLo, frHi));
            }
            else
            {
                _startHalf = ringHalfWidth;
                if (arena != null) arena.SetSurfaceFriction(0.9f);
            }
            if (arena != null) arena.SetPlatformHalfWidth(_startHalf);
            // Reset AFTER the width is applied, so the first TickShrinkingRing of
            // the new round compares against the width the round actually opens on.
            // Left stale, a round following a fully-shrunk one sees its target as
            // already applied and the mat never re-opens.
            _appliedHalf = _startHalf;
        }
    }
}
