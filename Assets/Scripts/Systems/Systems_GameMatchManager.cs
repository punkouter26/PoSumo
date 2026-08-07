using UnityEngine;
using UnityEngine.UIElements;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace PoSumo
{
    /// Scored exhibition referee with a round state machine:
    ///   Fighting -> RoundEnded (loser visibly falls, motors cut, banner shows)
    ///            -> Grace (poses reset, brief settle, no scoring)
    ///            -> Fighting
    /// A round loss (pushed out, fallen off, or sustained throw-down) scores a
    /// point for the opponent; first to pointsToWin takes the match. Match end
    /// dims the arena and waits for the REMATCH button, a pointer press or Space
    /// — except in a bracket bout, where the result is already final (see
    /// MarkBracketBout). UI Toolkit only.
    public sealed class Systems_GameMatchManager : MonoBehaviour
    {
        private enum Phase { Fighting, RoundEnded, Grace, WalkIn, MatchOver }

        public Agent_Biped wrestlerA;           // teamId 0
        public Agent_Biped wrestlerB;           // teamId 1
        public string nameA = "MATT";
        public string nameB = "STANDARD";
        public Color colorA = new Color(0.85f, 0.25f, 0.2f);
        public Color colorB = new Color(0.2f, 0.5f, 0.3f);
        public int pointsToWin = 3;
        // Doubled from 2.75. This is the physical platform half-width AND the
        // edge-distance observation every brain reads, so the four shipped
        // policies are now fighting a ring twice the size they trained on —
        // expect slower ring-outs and more wandering until they get a corrective
        // pass. Copied from GameTuning in Start; the arena scenes serialize their
        // own copy, which is why the asset is the one that matters.
        public float ringHalfWidth = 5.5f;
        public float fallY = -1.5f;
        // MUST match Systems_SumoMatchManager.knockdownLoses, which is false. This
        // defaulted true while the training referee defaulted false: every shipped
        // arena scene serializes 0 so nothing was live, but a NEW arena scene would
        // have silently given the game a losing condition no brain was trained
        // against. That divergence has bitten this project before.
        public bool knockdownLoses = false;
        [Tooltip("Head knockouts one fighter can suffer before losing the match outright — boxing's three-knockdown rule. 0 disables it. Copied from GameTuning in Start.")]
        public int knockoutsToLoseMatch = 3;
        [Tooltip("Realtime seconds from the deciding knockout to the result card, so the KO slow-motion plays out first. Copied from GameTuning in Start.")]
        public float knockoutAnnounceSeconds = 2.2f;
        public float roundTimeoutSeconds = 30f;
        public float betweenRoundsPause = 2.5f;
        public float graceSeconds = 0.4f;
        public float downGraceSeconds = 0.2f;
        [Tooltip("Seconds a fighter may lie down before the round goes to the opponent. GAME-ONLY — the training referee has no equivalent. 0 disables it.")]
        public float downOutSeconds = 3f;
        [Tooltip("Losing all four limbs to one blow ends the round immediately. GAME-ONLY, like downOutSeconds above. Turning it off does NOT let a gibbed fighter keep going — downOutSeconds still retires it about 3 s later, since it can never satisfy the get-up condition. Copied from GameTuning in Start.")]
        public bool gibLosesRound = true;
        /// Shrinking ring. Defaults mirror Systems_GameTuning; the ASSET wins.
        public float shrinkStartSeconds = 8f;
        public float shrinkToHalfWidth = 1.8f;
        /// Half-width the mat is currently at, so the contraction is only pushed to
        /// the arena when it actually changes — SetPlatformHalfWidth rebuilds
        /// collider and sprite scales, and calling it every physics step with an
        /// unchanged value is pure waste at 50 Hz.
        private float _appliedHalfWidth = -1f;
        public PanelSettings panelSettings;
        public Systems_GameTuning tuning;
        // Companion spawn toggles and knockdownLoses now live on GameTuning.asset
        // (copied in Start), so all three arena scenes share one set instead of
        // each carrying its own serialized copy. Only mirrorPitchScale stays here
        // because it is a per-match presentation detail, not a global rule.
        [Tooltip("Pitch applied to the SECOND fighter's voice when both wrestlers are the same character, so a mirror match has two distinguishable voices.")]
        public float mirrorPitchScale = 0.85f;
        [Tooltip("Persist results to the career record (W/L, head-to-head, Elo, titles). Turn off for throwaway test matches such as MatchTestHarness runs.")]
        public bool recordCareerStats = true;

        // Resolved from `tuning` in Start; the defaults here are the fallback when
        // no tuning asset is assigned.
        private bool enablePresentation = true, enableAudio = true, enableFaceMood = true;
        private bool enableVoice = true, enableLighting = true, enableImpactFx = true;
        private bool enableAtmosphere = true, enableMusic = true;
        private bool enableBodyDamage = true;
        [Tooltip("Round-opening countdown length; physics and brains are held until it finishes.")]
        public int countdownSeconds = 3;
        [Tooltip("Camera ortho when the countdown punches in on a fighter's head.")]
        public float countdownHeadOrtho = 0.85f;
        [Tooltip("Half the gap between the fighters' neutral stand-off positions during the countdown.")]
        public float neutralGapHalf = 0.9f;

        [Header("Walk-in intro")]
        [Tooltip("Rounds open with both fighters walking in on their locomotion brain, then the mat contracts and the countdown runs.")]
        public bool enableWalkIn = true;
        [Tooltip("Platform half-width during the walk-in. The mat contracts back to ringHalfWidth before the fight, so the FIGHTING ring is unchanged and the fight brains never see a size they were not trained on.")]
        public float walkInHalfWidth = 4f;
        [Tooltip("Half the gap the fighters start from. They walk from here all the way to contact, so this is the full approach distance each fighter covers minus half a body.")]
        public float walkInStartGapHalf = 3f;
        [Tooltip("Surface gap (m) between the two fighters' colliders at which they count as having touched — 0 is literal contact. Measured body-to-body, NOT torso-to-torso: limb pose moves torso separation at contact between roughly 0.9 m (arms down) and 1.8 m (arms extended), so a torso-distance threshold fires at the wrong moment in one pose or the other.")]
        public float walkInTouchGap = 0.05f;
        [Tooltip("Hard cap on the walk-in. Backstop only: covers walkInStartGapHalf metres each at the measured ~1.4 m/s, plus margin for a stumble. On timeout the fighters are parked at the stand-off and the round opens the old way.")]
        public float walkInTimeout = 12f;
        [Tooltip("Seconds over which the fight brains ramp from openingActionScale back to full power once the round opens. Stops the round starting with an all-out lunge from a dead stop.")]
        public float minSettleSeconds = 1f;
        [Tooltip("Motor authority the fight brains get on the first frame of a round, ramped to 1 over minSettleSeconds. Low = the fighters visibly settle before committing.")]
        [Range(0.05f, 1f)] public float openingActionScale = 0.3f;
        [Tooltip("Settle a timed-out round on position instead of calling it a draw. Without this nothing breaks a stalemate, so cagey matchups draw forever.")]
        public bool timeoutDecidesOnPosition = true;
        [Tooltip("Centre-distance difference (m) below which a timeout is still a genuine draw.")]
        public float timeoutDeadHeat = 0.15f;
        [Tooltip("Seconds the loser flops as a limp ragdoll before the winner is announced.")]
        public float limpBeforeAnnounce = 1.2f;
        [Tooltip("A foot dropping below this height (mat top is y=0) counts as stepping off the mat — an instant loss, sumo's stepping-out rule.")]
        public float footOffMatY = -0.06f;

        private int _scoreA, _scoreB;
        private int _koA, _koB;                 // head knockouts SUFFERED, this match
        private float _elapsed, _phaseLeft, _downA, _downB;
        // Set by OnGibbed, cleared at every round reset alongside _downA/_downB.
        // Needed because a gib and the ring-out check RACE: the gib takes the body
        // apart inside a collision callback, and the loose parts are off the mat by
        // the time this component's FixedUpdate evaluates OutOfRing. Whichever runs
        // first would otherwise decide the round, and a gib was being scored
        // "DOUBLE OUT — DRAW" instead of a win for the fighter still standing.
        private bool _gibbedA, _gibbedB;
        private int _lastShownSeconds = -1;
        private float _countdownLeft;
        private int _lastCountdownDigit = -1;
        private Phase _phase = Phase.Fighting;
        private float _walkInLeft;
        /// Smallest surface gap reached so far this walk-in, and how long is left
        /// before "no progress" gives up on it. See the stall check in FixedUpdate.
        private float _walkInBestGap;
        private float _walkInStallLeft;
        /// Seconds of no meaningful closing before the walk-in is abandoned.
        /// Comfortably longer than a stumble-and-recover, far shorter than the 12 s
        /// hard cap that produced twelve seconds of dead air in measured play.
        private const float WALKIN_STALL_SECONDS = 2.5f;
        /// Metres of closing that count as progress. Below this it is jitter — the
        /// fighters sway on the spot even when they are going nowhere.
        private const float WALKIN_PROGRESS_EPSILON = 0.05f;
        private float _savedGroundWidth;
        private float _braceStarted = -999f;
        private float _releaseTime = -1f;
        private bool _walkBrainHolding;
        /// The long-mat walk-in is a MATCH ceremony, not a round one: it plays
        /// once when the match opens and later rounds go straight to the
        /// countdown from the stand-off. Cleared by ResetMatch so a rematch — and
        /// every fresh bracket match, which loads the scene again — gets its own.
        private bool _walkInPlayed;
        // Cached for WalkInTouched: GetComponentsInChildren allocates, and the touch
        // test runs every physics step of the approach.
        private Collider2D[] _walkInColsA, _walkInColsB;
        private Systems_SumoArena _arena;
        private Systems_CameraFollow _camFollow;

        /// The walk-in only runs if BOTH fighters actually have a locomotion brain;
        /// Agent_Biped.BeginWalkIn is a silent no-op without one, which would strand
        /// them at the far ends until the timeout.
        /// One brain per fighter now does both jobs, so "is a walk policy available"
        /// collapses to "is a brain available at all". Kept as a named check because
        /// the walk-in still has to be skipped for a fighter with no model rather
        /// than handing the ceremony to a body with no policy driving it.
        private bool WalkBrainsReady()
        {
            return wrestlerA != null && wrestlerB != null
                && wrestlerA.character != null && wrestlerB.character != null
                && wrestlerA.character.inferenceModel != null
                && wrestlerB.character.inferenceModel != null;
        }
        // Cached in Start: the foot check runs every FixedUpdate, so no
        // GetComponent in the hot path.
        private Agent_BipedBody _bodyA, _bodyB;

        // Read-only stats for HUDs.
        /// 1-based round number within the current match, for the HUD.
        public int RoundNumber => _scoreA + _scoreB + 1;

        /// Rounds needed to take this match — differs between exhibition and a
        /// bracket bout, so the HUD must read it rather than assume.
        public int PointsToWin => pointsToWin;

        public int ScoreA => _scoreA;
        public int ScoreB => _scoreB;
        /// Head knockouts each fighter has SUFFERED this match.
        public int KnockoutsA => _koA;
        public int KnockoutsB => _koB;
        /// True when the last match ended on the three-knockdown rule rather than
        /// on rounds. Read by Systems_TournamentReporter, whose return-to-bracket
        /// delay has to outlast whichever announce delay the result card used.
        public bool EndedByKnockout { get; private set; }
        public int MatchWinsA { get; private set; }
        public int MatchWinsB { get; private set; }
        public float RoundElapsed => _elapsed;
        public float LongestRound { get; private set; }
        /// How many rounds were settled by the timeout tiebreak rather than a
        /// ring-out. Zero over a long session means the tiebreak is unreachable.
        public int TimeoutDecisions { get; private set; }

        /// How a round finished. Exists so every exit from a round is NAMED and
        /// logged, not just the one that happened to have a Debug.Log in it.
        ///
        /// Before this the timeout branch was the only instrumented path, so a
        /// played tournament could report "10 rounds decided on the clock" and say
        /// nothing whatsoever about how the other rounds ended — the ring-out and
        /// down-out branches were silent. That makes the single most important
        /// question about this game (is anyone actually being pushed out of the
        /// ring?) unanswerable from a log, which is how the ring stayed mistuned.
        public enum RoundOutcome
        {
            RingOut,          // a foot left the mat — the real sumo win
            DownOut,          // lay down past downOutSeconds (game-only rule)
            Knockdown,        // knockdownLoses is on and the grace expired
            DoubleOut,        // both finished in the same physics step
            TimeoutDecision,  // clock expired, settled on position
            TimeoutDraw,      // clock expired, too close to call
            Gibbed,           // lost all four limbs to one blow (game-only rule)
        }

        /// Per-outcome tally for the whole session, indexed by RoundOutcome.
        /// Static so it survives the scene load between bracket matches — each bout
        /// builds a fresh manager, and a per-manager counter would reset every time
        /// and could only ever describe one match.
        private static readonly int[] _outcomeTally =
            new int[System.Enum.GetValues(typeof(RoundOutcome)).Length];

        /// Rounds counted this session, across every match.
        private static int _roundsLogged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOutcomeTally()
        {
            System.Array.Clear(_outcomeTally, 0, _outcomeTally.Length);
            _roundsLogged = 0;
        }

        /// One line summarising every round outcome seen this session.
        /// `MatchTestHarness` and a tournament run both end by printing this.
        public static string OutcomeSummary()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("ROUND OUTCOMES: ").Append(_roundsLogged).Append(" rounds");
            var names = (RoundOutcome[])System.Enum.GetValues(typeof(RoundOutcome));
            for (int index = 0; index < names.Length; index++)
            {
                if (_outcomeTally[index] == 0) continue;
                sb.Append("  ").Append(names[index]).Append('=').Append(_outcomeTally[index]);
                sb.Append(" (").Append(Mathf.RoundToInt(100f * _outcomeTally[index] / Mathf.Max(1, _roundsLogged)));
                sb.Append("%)");
            }
            // Decapitation rate rides along because it is tuned against the same
            // runs and was the other thing a played tournament could not report.
            sb.Append("  decapitations=").Append(Systems_BodyDamage.DecapitationCount);
            sb.Append(" limbsLost=").Append(Systems_BodyDamage.LimbLossCount);
            sb.Append(" matStains=").Append(Systems_RingBlood.StainCount);
            return sb.ToString();
        }
        /// True from the instant the round phase opens — INCLUDING the countdown,
        /// while both fighters are still frozen on their marks. Reactive systems
        /// (crowd, faces, voices) want this, because they should be live for the
        /// build-up too.
        public bool RoundActive => _phase == Phase.Fighting;

        /// True only once the countdown has released the fighters and they are
        /// actually wrestling.
        ///
        /// Anything that AVERAGES over the round must use this, not RoundActive.
        /// The stats table sampled on RoundActive and so folded ~3 seconds of two
        /// motionless bodies into every average each round: work rate and balance
        /// were quietly diluted, and TERRITORY — which is a share of the round
        /// between the two fighters and must therefore total 100 — was reading
        /// 26/51 and 32/48, because during the freeze the pair sit symmetric
        /// about the centre line and the sample counts for neither of them.
        public bool ScoringLive => _phase == Phase.Fighting && _countdownLeft <= 0f;

        /// True while a round is actually being contested. Unlike ScoringLive this
        /// includes the countdown, because what it answers is "may something hand
        /// a body back to its brain" — Systems_BodyDamage's expiring knockout.
        public bool RoundLive => _phase == Phase.Fighting;

        /// True while the pause card is up. Read by Systems_MatchPresentation,
        /// whose slow-motion timer is REALTIME and so keeps running behind the
        /// pause — it must not write Time.timeScale back to 1 there.
        public bool IsPaused => _paused;

        /// Fired when a round is decided: (roundWinner, roundLoser) — both null on a draw.
        public event System.Action<Agent_Biped, Agent_Biped> RoundEnded;
        /// Fired when scoring resumes for a fresh round.
        public event System.Action RoundStarted;
        /// Fired once when the match is decided, with the match winner.
        public event System.Action<Agent_Biped> MatchEnded;
        /// Fired when a rematch resets the scores (HUD aggregates restart here).
        public event System.Action MatchReset;
        private Systems_HudRoot _hud;
        private Label _scoreDigits, _banner, _clock, _countdown;
        private Label _resultTitle, _resultScore;
        private VisualElement _pauseCard, _resultCard;
        private VisualElement _scoreBug;   // hidden while the result card is up
        private Button _rematchButton;     // hidden for a bracket bout — see MarkBracketBout
        private bool _paused;
        private bool _bracketBout;
        // Announce reveals still in flight, so a rematch can drop them — see
        // CancelPendingAnnounce.
        private IVisualElementScheduledItem _resultCardReveal, _scoreBugHide, _bannerReveal;
        private bool _openingRoundPending;

        private void Start()
        {
            if (tuning != null)
            {
                // Bracket matches are shorter than exhibitions. Decided HERE
                // rather than by the tournament reporter: Start order between
                // components is undefined, and this Start was overwriting the
                // reporter's value, silently running brackets as best-of-5.
                pointsToWin = Systems_TournamentState.Active
                    ? tuning.tournamentPointsToWin
                    : tuning.pointsToWin;
                roundTimeoutSeconds = tuning.roundTimeoutSeconds;
                betweenRoundsPause = tuning.betweenRoundsPause;
                graceSeconds = tuning.graceSeconds;
                downGraceSeconds = tuning.downGraceSeconds;
                downOutSeconds = tuning.downOutSeconds;
                gibLosesRound = tuning.gibLosesRound;
                knockdownLoses = tuning.knockdownLoses;
                knockoutsToLoseMatch = tuning.knockoutsToLoseMatch;
                shrinkStartSeconds = tuning.shrinkStartSeconds;
                shrinkToHalfWidth = tuning.shrinkToHalfWidth;
                knockoutAnnounceSeconds = tuning.knockoutAnnounceSeconds;
                ringHalfWidth = tuning.ringHalfWidth;
                neutralGapHalf = tuning.neutralGapHalf;
                enableWalkIn = tuning.enableWalkIn;
                walkInHalfWidth = tuning.walkInHalfWidth;
                walkInStartGapHalf = tuning.walkInStartGapHalf;
                walkInTouchGap = tuning.walkInTouchGap;
                walkInTimeout = tuning.walkInTimeout;
                enablePresentation = tuning.enablePresentation;
                enableAudio = tuning.enableAudio;
                enableFaceMood = tuning.enableFaceMood;
                enableVoice = tuning.enableVoice;
                enableLighting = tuning.enableLighting;
                enableImpactFx = tuning.enableImpactFx;
                enableAtmosphere = tuning.enableAtmosphere;
                enableMusic = tuning.enableMusic;
                enableBodyDamage = tuning.enableBodyDamage;
            }

            // NOT ANDed with Systems_ArenaLighting.LightingEffects, and that is
            // deliberate. With the effects off that component still builds the one
            // flat global light the arena is lit by, so skipping it would leave
            // every LIT sprite in the scene with no light at all — solid black.
            // The effects switch shapes what the rig builds; this decides whether
            // there is a rig, and the answer has to stay yes.

            ResolveWrestlers();
            _bodyA = wrestlerA.GetComponent<Agent_BipedBody>();
            _bodyB = wrestlerB.GetComponent<Agent_BipedBody>();
            wrestlerA.opponent = wrestlerB;
            wrestlerB.opponent = wrestlerA;
            wrestlerA.ringHalfWidth = ringHalfWidth;
            wrestlerB.ringHalfWidth = ringHalfWidth;
            wrestlerA.arenaCenterX = transform.position.x;
            wrestlerB.arenaCenterX = transform.position.x;
            wrestlerA.arenaGroundY = transform.position.y;
            wrestlerB.arenaGroundY = transform.position.y;

            // Size the dohyo to the configured ring at startup. The platform is
            // resized once more per round — wide for the walk-in, back to
            // ringHalfWidth before the fight — but it never moves DURING a round,
            // so the fighting ring is always the size the brains trained on.
            // Baked scenes ship a 5.5 m-wide platform, so without this the
            // physical edge would ignore ringHalfWidth entirely.
            _arena = FindAnyObjectByType<Systems_SumoArena>();
            if (_arena != null && tuning != null)
            {
                // Arena physics is tuned centrally, like everything else that the
                // three arena scenes each used to serialize their own copy of.
                _arena.tawaraBandWidth = tuning.tawaraBandWidth;
                _arena.tawaraFriction = tuning.tawaraFriction;
                _arena.SetSurfaceFriction(tuning.surfaceFriction);
                _arena.SetPlatformHalfWidth(tuning.ringHalfWidth);
                _arena.EnsureTawaraBands(tuning.ringHalfWidth);
            }
            if (_arena != null)
            {
                // SetPlatformHalfWidth clamps to groundWidth * 0.5, so a ring
                // wider than the arena's configured span would be silently capped
                // and the fight would run on a mat narrower than every observation
                // told the brains it was. Make the span fit the ring first.
                _arena.groundWidth = Mathf.Max(_arena.groundWidth, ringHalfWidth * 2f);
                _arena.SetPlatformHalfWidth(ringHalfWidth);
            }

            _camFollow = FindAnyObjectByType<Systems_CameraFollow>();
            BuildUi();
            UpdateScoreboard(false);
            SpawnCompanionSystems();

            if (enableWalkIn && WalkBrainsReady())
            {
                _phase = Phase.WalkIn;
                BeginWalkInPhase();
            }
            else
            {
                HoldUpright();
                StartCountdown();
                // Round 1 of a countdown-only opening raises RoundStarted like
                // any other round. It did not, so the opening round got no salt
                // throw and Systems_MusicDirector never went live for it — and
                // Systems_MatchAudio compensated by firing its own taiko from
                // Start, which then double-hit on the walk-in path that DOES
                // raise the event. Deferred rather than raised here: the
                // companions subscribe in their own Start, which runs after this
                // one finished creating them, so an inline raise reaches nobody.
                _openingRoundPending = true;
            }
        }

        /// Identity is adopted in Awake, not Start: the fight HUD reads nameA/
        /// colorA from here during its own Start, and Start-order between two
        /// components is undefined — resolving late showed a stale name on the
        /// stat panel while the scoreboard was already correct.
        private void Awake()
        {
            ResolveWrestlers();
            AdoptCharacterIdentity();
        }

        // Systems_BodyDamage.Knockout is a static event: a missed unsubscribe
        // keeps a finished scene's referee counting knockouts in the next one.
        private void OnEnable()
        {
            Systems_BodyDamage.Knockout += OnKnockout;
            Systems_BodyDamage.Gibbed += OnGibbed;
        }

        private void OnDisable()
        {
            Systems_BodyDamage.Knockout -= OnKnockout;
            Systems_BodyDamage.Gibbed -= OnGibbed;
        }

        private void ResolveWrestlers()
        {
            if (wrestlerA != null && wrestlerB != null) return;
            foreach (var a in FindObjectsByType<Agent_Biped>())
            {
                if (a.teamId == 0) wrestlerA = a; else wrestlerB = a;
            }
        }

        /// Scoreboard name and colour follow whichever characters are actually
        /// fighting, so swapping the roster needs no further scene edits.
        private void AdoptCharacterIdentity()
        {
            if (wrestlerA.character != null)
            {
                nameA = wrestlerA.character.behaviorName.ToUpperInvariant();
                colorA = wrestlerA.character.teamColor;
            }
            if (wrestlerB.character != null)
            {
                nameB = wrestlerB.character.behaviorName.ToUpperInvariant();
                colorB = wrestlerB.character.teamColor;
            }
        }

        /// Round opening: both fighters hold a neutral standing pose at the
        /// stand-off gap, physics off, until the countdown releases them.

        /// Opens the round: widen the mat, stand both fighters at the far ends and
        /// hand them to their locomotion brains to walk to the stand-off marks.
        ///
        /// The widening is temporary and purely for the intro — EndWalkInPhase
        /// contracts the platform back to ringHalfWidth before anyone fights, so
        /// the fighting ring is exactly the size the fight brains trained on.
        private void BeginWalkInPhase()
        {
            _walkInPlayed = true;
            // Pull the camera out for the ceremony. At normal framing the start
            // marks sit outside the frame, so the fighters appeared from
            // off-screen already halfway through their walk — the approach is the
            // point, so it has to be visible from the first stride. Held a beat
            // past the timeout; the arrival path clears it early.
            if (_camFollow != null)
            {
                _camFollow.PullBackWide(walkInTimeout + 1f);
            }

            if (_arena != null)
            {
                // SetPlatformHalfWidth clamps to groundWidth * 0.5, so widening the
                // mat past its configured span needs groundWidth raised first or the
                // request is silently capped and the fighters spawn off the edge.
                _savedGroundWidth = _arena.groundWidth;
                _arena.groundWidth = Mathf.Max(_savedGroundWidth, walkInHalfWidth * 2f + 0.8f);
                _arena.SetPlatformHalfWidth(walkInHalfWidth);
            }
            float centre = transform.position.x;

            // Order matters. Swap the brain FIRST, then EndEpisode so
            // OnEpisodeBegin resets pose, contact sensors and action history under
            // the policy that is about to drive, and only then place the body.
            // Handing the walk task a body still carrying the fight task's
            // leftover state is what made the fighters collapse on the first stride.
            // Both aim at the CENTRE, not at their own stand-off mark, so they walk
            // into each other rather than halting a stand-off apart. The walk task
            // never stops on its own — with episode control suppressed it strides
            // straight through its target — so contact, not arrival, is what ends
            // the approach. See WalkInTouched.
            wrestlerA.BeginWalkIn(centre);
            wrestlerB.BeginWalkIn(centre);
            wrestlerA.EndEpisode();
            wrestlerB.EndEpisode();

            PoseForWalkIn(wrestlerA, -walkInStartGapHalf);
            PoseForWalkIn(wrestlerB, +walkInStartGapHalf);
            wrestlerA.actionScale = 1f;
            wrestlerB.actionScale = 1f;
            // Cached after PoseForWalkIn, so the ragdolls are built and their
            // colliders exist. Allocating here keeps the per-step touch test clean.
            _walkInColsA = wrestlerA.GetComponentsInChildren<Collider2D>();
            _walkInColsB = wrestlerB.GetComponentsInChildren<Collider2D>();
            _walkInLeft = walkInTimeout;
            // Seeded from the ACTUAL opening gap rather than infinity, so the first
            // step is measured against where they really started; and the stall
            // clock starts full so a slow first stride is never mistaken for a stall.
            _walkInBestGap = WalkInSurfaceGap();
            _walkInStallLeft = WALKIN_STALL_SECONDS;
        }

        /// Places a fighter at the far end, upright and simulating, ready to walk.
        /// Unlike PoseNeutral this leaves physics and motors ON — the walk task
        /// needs a live body to drive.
        private void PoseForWalkIn(Agent_Biped w, float offsetX)
        {
            if (w == null) return;
            var p = w.transform.position;
            w.transform.position = new Vector3(transform.position.x + offsetX, p.y, p.z);
            var body = w.GetComponent<Agent_BipedBody>();
            if (body != null)
            {
                body.ResetPose();
                SetSimulated(body, true);
            }
            w.actionsEnabled = true;
        }

        /// True once the two fighters' bodies actually meet.
        ///
        /// Measured as the surface gap between A's rightmost collider edge and B's
        /// leftmost, because torso separation is not a usable proxy: measured on a
        /// live pair, one fighter's collider span in x runs to ~2.0 m with the limbs
        /// extended and ~0.5 m with them down, which moves torso-separation-at-contact
        /// between roughly 1.8 m and 0.9 m. A fixed torso threshold therefore either
        /// fires while they are still a stride apart or never fires at all — the
        /// latter is what made the approach run to its timeout every time.
        ///
        /// Colliders are cached at BeginWalkInPhase; this runs every physics step of
        /// the walk-in and must not allocate.
        private bool WalkInTouched() => WalkInSurfaceGap() <= walkInTouchGap;

        /// Signed gap in metres between A's rightmost collider edge and B's
        /// leftmost. Negative means they already overlap. PositiveInfinity when the
        /// colliders could not be resolved at all.
        private float WalkInSurfaceGap()
        {
            if (_walkInColsA == null || _walkInColsB == null) return float.PositiveInfinity;

            float aMax = float.NegativeInfinity;
            for (int colliderIndex = 0; colliderIndex < _walkInColsA.Length; colliderIndex++)
            {
                Collider2D c = _walkInColsA[colliderIndex];
                if (c == null) continue;
                float x = c.bounds.max.x;
                if (x > aMax) aMax = x;
            }

            float bMin = float.PositiveInfinity;
            for (int colliderIndex = 0; colliderIndex < _walkInColsB.Length; colliderIndex++)
            {
                Collider2D c = _walkInColsB[colliderIndex];
                if (c == null) continue;
                float x = c.bounds.min.x;
                if (x < bMin) bMin = x;
            }

            if (float.IsInfinity(aMax) || float.IsInfinity(bMin)) return float.PositiveInfinity;
            return bMin - aMax;
        }

        /// Hands both bodies to the fight brains at the moment of contact and opens
        /// the round from exactly where they met.
        ///
        /// Deliberately does NOT reposition: HoldUpright would teleport the pair
        /// back to the stand-off marks, undoing the approach the walk-in just
        /// played. They are already upright, simulating and balanced, so the fight
        /// brains inherit a settled body — the same handoff BeginSimulation makes
        /// at FIGHT!, just triggered by contact instead of a countdown.
        private void EngageFromWalkIn()
        {
            Debug.Log($"WALK-IN RESULT: contact after {(walkInTimeout - _walkInLeft):F2}s — fight brains engaged");
            EndWalkInPhase();      // both bodies back on their fight brain
            ContractMat();
            if (_camFollow != null) _camFollow.ClearShots();

            _braceStarted = Time.time;
            _phase = Phase.Fighting;
            _elapsed = 0f;
            _downA = _downB = 0f;
            _gibbedA = _gibbedB = false;
            _countdownLeft = 0f;   // contact IS the start; no freeze, no reposition
            BeginSimulation();
            FlashFight();
            RoundStarted?.Invoke();
        }

        /// "FIGHT!" without the preceding count — the fighters have already engaged,
        /// so the banner marks the handoff rather than counting down to it.
        private void FlashFight()
        {
            if (_hud == null || _countdown == null) return;
            _countdown.style.fontSize = Systems_UiKit.FONT_HERO;
            _countdown.text = "FIGHT!";
            _hud.ShowCentre(_countdown);
            _countdown.schedule.Execute(HideCountdown).StartingIn(600);
        }

        /// Hands both fighters back to their fight brains and shrinks the mat to
        /// the real ring before the countdown starts.
        /// Shrinks the intro mat back to the real fighting ring.
        private void ContractMat()
        {
            if (_arena == null) return;
            _arena.SetPlatformHalfWidth(ringHalfWidth);
            _arena.EnsureTawaraBands(ringHalfWidth);
            // Every round opens on a FULL mat. Without this reset the shrinking
            // ring would be cumulative: round 2 would start at whatever width
            // round 1 finished on, and by round 3 the fighters would be spawning
            // on a strip narrower than their own stance.
            _appliedHalfWidth = ringHalfWidth;
            if (_savedGroundWidth > 0f) _arena.groundWidth = _savedGroundWidth;
        }

        /// Hands both bodies from the locomotion brain back to the fight brain.
        private void EndWalkInPhase()
        {
            wrestlerA.EndWalkIn();
            wrestlerB.EndWalkIn();
            _walkBrainHolding = false;
        }

        /// Parks both fighters at the stand-off with physics OFF.
        ///
        /// Note this genuinely freezes them rather than "bracing" them with zeroed
        /// motors: a ragdoll whose joints are all braked is a stiff mannequin, and
        /// a stiff mannequin still topples like an inverted pendulum. Staying
        /// upright needs active balance, which only a policy provides — that is
        /// what the walk-brain hold below is for.
        private void HoldUpright()
        {
            PoseNeutral(wrestlerA, -neutralGapHalf);
            PoseNeutral(wrestlerB, +neutralGapHalf);
        }

        private void PoseNeutral(Agent_Biped w, float offsetX)
        {
            if (w == null) return;
            var p = w.transform.position;
            w.transform.position = new Vector3(transform.position.x + offsetX, p.y, p.z);
            var body = w.GetComponent<Agent_BipedBody>();
            if (body == null) return;
            body.ResetPose();
            SetSimulated(body, false);
            w.actionsEnabled = false;
        }


        /// Countdown hit zero: physics back on, fight brains live.
        private void BeginSimulation()
        {
            // When the walk task held them through the countdown they are already
            // simulating and balanced; all that changes here is which policy owns
            // the body. Unfreeze still runs for the no-walk-brain fallback path.
            if (_walkBrainHolding)
            {
                EndWalkInPhase();
            }
            Unfreeze(wrestlerA);
            Unfreeze(wrestlerB);

            // Ease the fight brains in. Released at full power from a dead stop they
            // fire a maximum-effort lunge on the first frame and throw themselves
            // over — measured: both released perfectly upright (1.00) and one was
            // face-down within half a second.
            _releaseTime = Time.time;
            wrestlerA.actionScale = openingActionScale;
            wrestlerB.actionScale = openingActionScale;
        }

        /// Ramps motor authority back to full over minSettleSeconds after the round
        /// opens, so the fighters take their own weight before they can commit.
        private void TickActionRamp()
        {
            if (_releaseTime < 0f) return;
            float t = (Time.time - _releaseTime) / Mathf.Max(0.01f, minSettleSeconds);
            if (t >= 1f)
            {
                wrestlerA.actionScale = 1f;
                wrestlerB.actionScale = 1f;
                _releaseTime = -1f;
                return;
            }
            float s = Mathf.Lerp(openingActionScale, 1f, t);
            wrestlerA.actionScale = s;
            wrestlerB.actionScale = s;
        }

        private static void Unfreeze(Agent_Biped w)
        {
            SetSimulated(w.GetComponent<Agent_BipedBody>(), true);
            w.actionsEnabled = true;
        }

        /// Toggle physics across every rigidbody of a body, optionally killing its
        /// motion first. The walk-in pose, the neutral pose, Unfreeze and
        /// FreezeFighter each walked body.Parts themselves; this is the one loop
        /// they were all copies of.
        private static void SetSimulated(Agent_BipedBody body, bool simulated, bool zeroVelocity = false)
        {
            if (body == null || body.Parts == null) return;
            for (int partIndex = 0; partIndex < body.Parts.Length; partIndex++)
            {
                Rigidbody2D part = body.Parts[partIndex];
                if (part == null) continue;
                if (zeroVelocity)
                {
                    part.linearVelocity = Vector2.zero;
                    part.angularVelocity = 0f;
                }
                part.simulated = simulated;
            }
        }

        /// Presentation, audio and face-mood are runtime-spawned children so
        /// scenes stay manager-only and older scenes pick them up automatically.
        private void SpawnCompanionSystems()
        {
            SpawnCompanion<Systems_MatchPresentation>(enablePresentation, "Presentation");
            SpawnCompanion<Systems_MatchAudio>(enableAudio, "MatchAudio");
            // One mood driver per fighter that actually has face art.
            if (enableFaceMood && FindAnyObjectByType<Systems_FaceMood>() == null)
            {
                SpawnFaceMood(wrestlerA);
                SpawnFaceMood(wrestlerB);
            }
            if (enableBodyDamage && FindAnyObjectByType<Systems_BodyDamage>() == null)
            {
                SpawnBodyDamage(wrestlerA);
                SpawnBodyDamage(wrestlerB);
            }
            // Sweat and clay are terms in the BodyLit shader, so they ride the
            // lighting flag rather than earning a GameTuning bool of their own —
            // with the lighting rig off there is no shaded surface to wet or stain,
            // and Systems_BodySurface disables itself on the flat-shading path.
            if (enableLighting && FindAnyObjectByType<Systems_BodySurface>() == null)
            {
                SpawnBodySurface(wrestlerA);
                SpawnBodySurface(wrestlerB);
            }
            if (enableVoice && FindAnyObjectByType<Systems_FighterVoice>() == null)
            {
                SpawnVoice(wrestlerA, 1f);
                // The bracket seeds every fighter twice, so both wrestlers can be
                // the same character. Drop the second one's pitch so a Matt-vs-Matt
                // bout does not sound like one man arguing with himself.
                bool mirrorMatch = wrestlerA != null && wrestlerB != null
                                   && wrestlerA.behaviorName == wrestlerB.behaviorName;
                SpawnVoice(wrestlerB, mirrorMatch ? mirrorPitchScale : 1f);
            }
            SpawnCompanion<Systems_ImpactFx>(enableImpactFx, "ImpactFx");
            SpawnCompanion<Systems_ArenaLighting>(enableLighting, "ArenaLighting");
            SpawnCompanion<Systems_CareerRecorder>(recordCareerStats, "CareerRecorder");
            SpawnCompanion<Systems_ArenaAtmosphere>(enableAtmosphere, "Atmosphere");
            SpawnCompanion<Systems_MusicDirector>(enableMusic, "Music");
        }

        /// Spawns one companion as a child of the manager, if its GameTuning flag is
        /// on and the scene does not already have one. The per-fighter companions
        /// (face mood, voice, body damage) keep their own helpers because they need
        /// arguments; everything else is this one shape, and was six copies of it.
        private void SpawnCompanion<T>(bool enabled, string objectName) where T : Component
        {
            if (!enabled || FindAnyObjectByType<T>() != null) return;
            var go = new GameObject(objectName);
            go.transform.SetParent(transform, false);
            go.AddComponent<T>();
        }

        private void SpawnBodyDamage(Agent_Biped fighter)
        {
            if (fighter == null) return;
            var body = fighter.GetComponent<Agent_BipedBody>();
            if (body == null) return;
            var go = new GameObject($"Damage_{fighter.behaviorName}");
            go.transform.SetParent(transform, false);
            go.AddComponent<Systems_BodyDamage>().body = body;
        }

        /// One sweat/clay driver per fighter. Bound to the body instance rather
        /// than the behaviour name because a mirror bout has two wrestlers with the
        /// same name and each needs its own material written.
        private void SpawnBodySurface(Agent_Biped fighter)
        {
            if (fighter == null) return;
            var body = fighter.GetComponent<Agent_BipedBody>();
            if (body == null) return;
            var go = new GameObject($"Surface_{fighter.behaviorName}");
            go.transform.SetParent(transform, false);
            go.AddComponent<Systems_BodySurface>().body = body;
        }

        /// One voice per fighter. Systems_FighterVoice disables itself when that
        /// fighter has no recorded clips in Resources, so this is safe to call for
        /// everyone — today only Matt has a voice.
        private void SpawnVoice(Agent_Biped fighter, float pitchScale)
        {
            if (fighter == null) return;
            var go = new GameObject($"Voice_{fighter.behaviorName}");
            go.transform.SetParent(transform, false);
            var voice = go.AddComponent<Systems_FighterVoice>();
            // The instance, not just the name: in a mirror bout the name matches
            // both wrestlers and every voice bound to wrestlerA.
            voice.fighter = fighter;
            voice.fighterBehaviorName = fighter.behaviorName;
            voice.pitchScale = pitchScale;
        }

        private void SpawnFaceMood(Agent_Biped fighter)
        {
            if (fighter == null) return;
            var body = fighter.GetComponent<Agent_BipedBody>();
            if (body == null || body.character == null || body.character.headSprite == null) return;
            var go = new GameObject($"FaceMood_{fighter.behaviorName}");
            go.transform.SetParent(transform, false);
            var mood = go.AddComponent<Systems_FaceMood>();
            // See SpawnVoice: the instance disambiguates a mirror bout.
            mood.fighter = fighter;
            mood.fighterBehaviorName = fighter.behaviorName;
        }

        private static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        private void BuildUi()
        {
            _hud = Systems_HudRoot.Ensure(transform, panelSettings);
            BuildTopBar();
            BuildCallouts();
            BuildResultCard();
            BuildPauseUi();
        }

        /// The top bar: pause on the left, the scorebug and round clock in the
        /// middle. Systems_FightHud adds its STATS toggle to the right slot.
        ///
        /// These were four independently positioned floats before — the score at
        /// `bottom:480`, a pixel offset measured against a 1280-tall panel that
        /// lands at a different height on every other aspect ratio and put the
        /// score over the arena rather than clear of it; the clock stacked under
        /// it; the pause chip at `top:10 left:10`; the stats chip at
        /// `top:10 right:10` from a different file and a different UIDocument.
        private void BuildTopBar()
        {
            // An on-screen pause affordance, in the slot that was being reserved
            // for it anyway. Removing it left the hardware back key as the ONLY
            // way to pause on Android with nothing on screen advertising that,
            // while the slot it vacated went on costing 104pt of hardcoded width
            // for nothing. The escapeKey binding in Update stays — this is a
            // second door to the same room, not a replacement for it.
            _hud.TopBarLeft.Add(Systems_UiKit.PauseButton(TogglePause));

            // Which bout of the bracket this is. The bracket screen announces
            // "MATCH 3 of 7 — KIM v NICK", then the arena showed two names and a
            // score and nothing else, so a quarterfinal and the final were
            // indistinguishable — during autoplay you could watch the tournament
            // be decided without knowing it. Exhibition matches have no bracket
            // position, so they get no label.
            if (Systems_TournamentState.Active)
            {
                int match = Systems_TournamentState.CurrentMatch;
                Label round = Systems_UiKit.Text(
                    $"{Systems_TournamentState.RoundName(match)} · MATCH {match + 1}/{Systems_TournamentState.MATCH_COUNT}",
                    Systems_UiKit.FONT_MICRO, Systems_UiKit.Gold, true);
                round.style.unityTextAlign = TextAnchor.MiddleCenter;
                round.style.marginBottom = Systems_UiKit.SPACE_1;
                round.style.textShadow = Systems_UiKit.Outline;
                _hud.TopBarCentre.Add(round);
            }

            _scoreBug = Systems_UiKit.Card(Systems_UiKit.Panel).NoPick();
            _scoreBug.style.flexDirection = FlexDirection.Row;
            _scoreBug.style.alignItems = Align.Center;
            _scoreBug.Pad(Systems_UiKit.SPACE_4, Systems_UiKit.SPACE_1);

            _scoreBug.Add(NamePlate(nameA, colorA));

            _scoreDigits = Systems_UiKit.Text("0 : 0", Systems_UiKit.FONT_TITLE, Systems_UiKit.TextHi, true);
            _scoreDigits.style.marginLeft = Systems_UiKit.SPACE_3;
            _scoreDigits.style.marginRight = Systems_UiKit.SPACE_3;
            _scoreDigits.style.unityTextAlign = TextAnchor.MiddleCenter;
            _scoreBug.Add(_scoreDigits);

            _scoreBug.Add(NamePlate(nameB, colorB));
            _hud.TopBarCentre.Add(_scoreBug);

            _clock = Systems_UiKit.Text("", Systems_UiKit.FONT_LEAD, Systems_UiKit.TextMid, true);
            _clock.style.marginTop = Systems_UiKit.SPACE_1;
            _clock.style.unityTextAlign = TextAnchor.MiddleCenter;
            _clock.style.textShadow = Systems_UiKit.Outline;
            _hud.TopBarCentre.Add(_clock);
        }

        private static Label NamePlate(string fighterName, Color teamColor)
        {
            Label plate = Systems_UiKit.Text(fighterName, Systems_UiKit.FONT_BODY, teamColor, true);
            plate.style.unityTextAlign = TextAnchor.MiddleCenter;
            return plate;
        }

        /// Round banner and countdown digit. Both are centred in the stage band
        /// by the layout instead of at hand-tuned percentages (49% and 36%), and
        /// the HUD root shows only one member of this layer at a time, so the
        /// 112pt digit can no longer land on top of a banner.
        private void BuildCallouts()
        {
            _banner = Systems_UiKit.Text("", Systems_UiKit.FONT_HERO, Systems_UiKit.Gold, true);
            _banner.style.unityTextAlign = TextAnchor.MiddleCenter;
            _banner.style.textShadow = Systems_UiKit.Outline;
            _banner.style.whiteSpace = WhiteSpace.Normal;
            _banner.NoPick();
            _hud.AddCentre(_banner);

            _countdown = Systems_UiKit.Text("", Systems_UiKit.FONT_MEGA, Systems_UiKit.Gold, true);
            _countdown.style.unityTextAlign = TextAnchor.MiddleCenter;
            _countdown.style.textShadow = Systems_UiKit.Outline;
            // A 112pt digit dead centre of the screen is the single largest thing
            // in the HUD; it has no business hit-testing.
            _countdown.NoPick();
            _hud.AddCentre(_countdown);
        }

        /// Match-over: one centred results card owns the moment.
        private void BuildResultCard()
        {
            _resultCard = Systems_UiKit.Card(Systems_UiKit.Ink, Systems_UiKit.RADIUS_LG);
            _resultCard.style.width = Length.Percent(100);
            _resultCard.style.maxWidth = 520;
            _resultCard.Pad(Systems_UiKit.SPACE_5, Systems_UiKit.SPACE_5);
            _resultCard.style.borderTopWidth = 5;
            // Coloured here as well as on match end. The border width was set at
            // build time but its colour only when a match was decided, so any
            // earlier display drew a black bar across the top of the card.
            _resultCard.style.borderTopColor = Systems_UiKit.Gold;

            _resultTitle = Systems_UiKit.Text("", Systems_UiKit.FONT_HERO, Systems_UiKit.Gold, true);
            _resultTitle.style.unityTextAlign = TextAnchor.MiddleCenter;
            _resultTitle.style.whiteSpace = WhiteSpace.Normal;
            _resultCard.Add(_resultTitle);

            _resultScore = Systems_UiKit.Text("", Systems_UiKit.FONT_LEAD, Systems_UiKit.TextMid);
            _resultScore.style.unityTextAlign = TextAnchor.MiddleCenter;
            _resultScore.style.marginTop = Systems_UiKit.SPACE_1;
            _resultCard.Add(_resultScore);

            // Big touch-friendly rematch button (mobile) — Space and any pointer
            // press still work too. Kept in a field because a bracket bout hides
            // it at reveal time; see MarkBracketBout.
            _rematchButton = Systems_UiKit.PrimaryButton("REMATCH", ResetMatch);
            _rematchButton.style.marginTop = Systems_UiKit.SPACE_5;
            _resultCard.Add(_rematchButton);

            _hud.AddModal(_resultCard);
        }

        /// Pause / quit. Before this the only exit from a match was playing it to
        /// the end — on Android the hardware back button did nothing at all.
        private void BuildPauseUi()
        {
            _pauseCard = Systems_UiKit.Card(Systems_UiKit.Ink, Systems_UiKit.RADIUS_LG);
            _pauseCard.style.width = Length.Percent(100);
            _pauseCard.style.maxWidth = 520;
            _pauseCard.Pad(Systems_UiKit.SPACE_5, Systems_UiKit.SPACE_5);

            Label title = Systems_UiKit.Text("PAUSED", Systems_UiKit.FONT_TITLE, Systems_UiKit.Gold, true);
            title.style.unityTextAlign = TextAnchor.MiddleCenter;
            _pauseCard.Add(title);

            Button resume = Systems_UiKit.PrimaryButton("RESUME", TogglePause);
            resume.style.marginTop = Systems_UiKit.SPACE_5;
            _pauseCard.Add(resume);

            Button quit = Systems_UiKit.GhostButton("QUIT MATCH", QuitToBracket);
            quit.style.marginTop = Systems_UiKit.SPACE_3;
            _pauseCard.Add(quit);

            _hud.AddModal(_pauseCard);
        }

        private void TogglePause()
        {
            if (_phase == Phase.MatchOver) return;   // result card owns that moment
            _paused = !_paused;
            if (_paused) _hud.ShowModal(_pauseCard); else _hud.HideModal();
            // Restore to 1 rather than to the pre-pause value: presentation slow-mo
            // ends on a realtime timer that keeps running while paused, so the saved
            // value would be stale.
            Time.timeScale = _paused ? 0f : 1f;
        }

        private void QuitToBracket()
        {
            Time.timeScale = 1f;
            // Abandoning a bracket match leaves the tournament stopped rather than
            // half-played, so the bracket screen offers a clean restart instead of
            // auto-launching the match that was just walked out of.
            if (Systems_TournamentState.Active)
            {
                Systems_TournamentState.Stop();
            }
            UnityEngine.SceneManagement.SceneManager.LoadScene("SCN_TOURNAMENT");
        }

        /// The names are static for the life of the match and are drawn as their
        /// own coloured labels in the scorebug, so only the digits are rewritten
        /// here — no per-round rich-text string to rebuild.
        private void UpdateScoreboard(bool flash)
        {
            if (_scoreDigits == null) return;
            _scoreDigits.text = $"{_scoreA} : {_scoreB}";
            if (flash)
            {
                // An eased settle rather than "jump to HERO, snap back on a 400ms
                // timer". The old pair of instant steps read as a rendering fault,
                // and if the scheduled item was ever dropped — a scene load
                // landing inside that window — the digits stayed oversized for the
                // rest of the match with nothing to put them back.
                _scoreDigits.PopFontSize(Systems_UiKit.FONT_HERO, Systems_UiKit.FONT_TITLE, 320);
            }
        }

        private void StartCountdown()
        {
            _countdownLeft = countdownSeconds;
            _lastCountdownDigit = -1;
        }

        /// 3-2-1 over the walk-in: the camera punches in on one fighter's head
        /// per digit (A on the first, B on the second), then releases back to
        /// the wide two-shot on the final digit so the engage reads clearly.
        private void TickCountdown()
        {
            _countdownLeft -= Time.fixedDeltaTime;
            if (_countdownLeft <= 0f)
            {
                // A word needs a smaller size than a single digit does: "FIGHT!"
                // at the digit's 112pt runs past the edge of a 720pt-wide panel.
                _countdown.style.fontSize = Systems_UiKit.FONT_HERO;
                _countdown.text = "FIGHT!";
                _countdown.schedule.Execute(HideCountdown).StartingIn(600);
                return;
            }

            int digit = Mathf.CeilToInt(_countdownLeft);
            if (digit == _lastCountdownDigit) return;
            _lastCountdownDigit = digit;
            _countdown.text = digit.ToString();
            _hud.ShowCentre(_countdown);
            // Each digit lands oversized and settles onto FONT_MEGA. PopFontSize
            // writes the final size itself, so this replaces the plain assignment
            // rather than adding to it. The "FIGHT!" branch above still sets
            // FONT_HERO directly and must: a word at 112pt runs off a 720pt panel,
            // and it fires ~1s after the last digit, well clear of this animation.
            _countdown.PopFontSize(Systems_UiKit.FONT_MEGA + 28, Systems_UiKit.FONT_MEGA, 260);

            if (digit == 1) return; // let the last punch-in expire — wide for the engage
            var fighter = ((countdownSeconds - digit) & 1) == 0 ? wrestlerA : wrestlerB;
            PunchOnHead(fighter);
        }

        private void PunchOnHead(Agent_Biped fighter)
        {
            if (_camFollow == null || fighter == null) return;
            _camFollow.PunchIn(Systems_CameraFollow.FocusPoint(fighter), countdownHeadOrtho, 1.05f);
        }

        private void HideCountdown()
        {
            _hud.HideCentre(_countdown);
        }

        private void UpdateClock()
        {
            int remaining = Mathf.Max(0, Mathf.CeilToInt(roundTimeoutSeconds - _elapsed));
            if (remaining != _lastShownSeconds)
            {
                _lastShownSeconds = remaining;
                _clock.text = remaining.ToString();
                _clock.style.color = remaining <= 5 ? Systems_UiKit.Bad : Systems_UiKit.TextMid;
            }
        }

        private void Update()
        {
            if (_openingRoundPending)
            {
                _openingRoundPending = false;
                RoundStarted?.Invoke();
            }
            if (_phase == Phase.MatchOver && !_bracketBout && ContinuePressed()) { ResetMatch(); return; }
            // Android maps its hardware back button onto escapeKey, so this is the
            // system-back handler as well as the desktop shortcut. Update still runs
            // at timeScale 0, so it can also un-pause.
            if (BackPressed()) TogglePause();
        }

        private bool BackPressed()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
#else
            return false;
#endif
        }

        /// Dismiss the result card and rematch. Never consulted for a bracket bout
        /// — see MarkBracketBout.
        ///
        /// This was space-only. On the actual target platform there is no keyboard,
        /// so Keyboard.current is null and the check could never return true — an
        /// Android player reaching the result card was stuck on it with no way out.
        /// Any pointer press (touch or mouse) now works, with space kept for desktop.
        private bool ContinuePressed()
        {
#if ENABLE_INPUT_SYSTEM
            if (Pointer.current != null && Pointer.current.press.wasPressedThisFrame)
            {
                return true;
            }
            return Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;
#else
            // No legacy-Input fallback: the project is Input System package only
            // (ProjectSettings activeInputHandler: 1), so this branch never
            // compiles, and UnityEngine.Input would throw at runtime under that
            // setting anyway. The REMATCH button still dismisses the card.
            return false;
#endif
        }

        private void FixedUpdate()
        {
            if (wrestlerA == null || wrestlerB == null) return;
            // A physics step can land before Start() has built the UI and set the
            // opening phase. Scoring a round in that window dereferences UI that
            // does not exist yet, so nothing runs until the match is actually set up.
            if (_clock == null) return;

            switch (_phase)
            {
                case Phase.MatchOver:
                    return;

                case Phase.RoundEnded:
                    // Motors are cut; the loser is mid-fall for the audience.
                    _phaseLeft -= Time.fixedDeltaTime;
                    if (_phaseLeft <= 0f)
                    {
                        wrestlerA.EndEpisode();   // resets poses via OnEpisodeBegin
                        wrestlerB.EndEpisode();
                        HoldUpright();
                        _downA = _downB = 0f;
                        _gibbedA = _gibbedB = false;
                        _hud.HideCentre(_banner);
                        _phase = Phase.Grace;
                        _phaseLeft = graceSeconds;
                    }
                    return;

                case Phase.Grace:
                    // Fighters are frozen in the stand-off; brief beat before
                    // the walk-in (or, with it disabled, the countdown).
                    _phaseLeft -= Time.fixedDeltaTime;
                    if (_phaseLeft <= 0f)
                    {
                        // Only the round that opens the match walks in.
                        if (enableWalkIn && !_walkInPlayed && WalkBrainsReady())
                        {
                            _phase = Phase.WalkIn;
                            BeginWalkInPhase();
                            return;
                        }
                        _phase = Phase.Fighting;
                        _elapsed = 0f;
                        _downA = _downB = 0f;
                        _gibbedA = _gibbedB = false;
                        StartCountdown();
                        RoundStarted?.Invoke();
                    }
                    return;

                case Phase.WalkIn:
                    // Locomotion brains walk both fighters in towards each other. No
                    // scoring, no clock — Agent_Biped.suppressEpisodeControl keeps
                    // the walk policy from ending episodes while it owns the body.
                    _walkInLeft -= Time.fixedDeltaTime;

                    // Normal path: they meet in the middle and the fight brains take
                    // over on the spot.
                    if (WalkInTouched())
                    {
                        EngageFromWalkIn();
                        return;
                    }

                    // STALL DETECTION, ahead of the hard timeout. A walk-in that is
                    // going to work closes the gap steadily — measured successes ran
                    // 2.3-4.9 s. A failure does not creep, it simply stops: the one
                    // observed failure sat at surfaceGap 1.17 and burned the entire
                    // 12 s cap doing nothing, which is twelve seconds of dead air
                    // before a bout. Waiting out a fixed cap punishes the player for
                    // the policy's bad day; watching for progress ends it as soon as
                    // it is clearly not coming.
                    float gap = WalkInSurfaceGap();
                    if (gap < _walkInBestGap - WALKIN_PROGRESS_EPSILON)
                    {
                        _walkInBestGap = gap;
                        _walkInStallLeft = WALKIN_STALL_SECONDS;
                    }
                    else
                    {
                        _walkInStallLeft -= Time.fixedDeltaTime;
                    }

                    // Backstop only — a fighter stumbled or never got walking. Fall
                    // back to the old opening so a bad walk-in cannot hang the match.
                    if (_walkInLeft <= 0f || _walkInStallLeft <= 0f)
                    {
                        bool stalled = _walkInStallLeft <= 0f && _walkInLeft > 0f;
                        float spent = walkInTimeout - Mathf.Max(0f, _walkInLeft);
                        Debug.LogWarning($"WALK-IN RESULT: {(stalled ? "STALLED" : "timed out")} after " +
                                         $"{spent:F1}s with no contact — " +
                                         $"surfaceGap={gap:F2} (need <= {walkInTouchGap:F2}), " +
                                         $"bestGap={_walkInBestGap:F2}, " +
                                         $"cols A={(_walkInColsA == null ? -1 : _walkInColsA.Length)} " +
                                         $"B={(_walkInColsB == null ? -1 : _walkInColsB.Length)}, " +
                                         $"Ax={wrestlerA.TorsoX:F2} Bx={wrestlerB.TorsoX:F2} " +
                                         $"startGapHalf={walkInStartGapHalf:F1} — " +
                                         "parking at the stand-off and opening with the countdown instead");
                        // They never made contact. Put them on the stand-off marks
                        // and open the round the pre-walk-in way, countdown and all,
                        // so a failed approach degrades to a normal round start
                        // instead of stranding the match in this phase.
                        HoldUpright();
                        EndWalkInPhase();
                        ContractMat();
                        // Drop the wide ceremony shot so the camera settles onto
                        // the pair before the countdown punches in on their heads.
                        if (_camFollow != null) _camFollow.ClearShots();
                        _braceStarted = Time.time;
                        _phase = Phase.Fighting;
                        _elapsed = 0f;
                        _downA = _downB = 0f;
                        _gibbedA = _gibbedB = false;
                        StartCountdown();
                        RoundStarted?.Invoke();
                    }
                    return;
            }

            // Phase.Fighting — the round clock and scoring hold until the
            // countdown releases the frozen fighters.
            if (_countdownLeft > 0f)
            {
                TickCountdown();
                if (_countdownLeft > 0f) return;
                // Both fighters have been standing braced through the countdown.
                // Hold the release until they have had at least minSettleSeconds
                // of settled, upright physics, so a shortened countdown can never
                // hand the fight brain an unsettled body that flops immediately.
                BeginSimulation();
            }
            TickActionRamp();

            _elapsed += Time.fixedDeltaTime;
            UpdateClock();

            // Accumulated whenever ANY rule reads them — the instant knockdown rule
            // (off in the shipping config, kept in step with the training referee)
            // or the game-only down-out below.
            if (knockdownLoses || downOutSeconds > 0f)
            {
                _downA = wrestlerA.IsDown ? _downA + Time.fixedDeltaTime : 0f;
                _downB = wrestlerB.IsDown ? _downB + Time.fixedDeltaTime : 0f;
            }

            TickShrinkingRing();

            // Anyone off the mat goes limp the instant it happens, so the ragdoll
            // flop plays out before the result is announced.
            bool aOffMat = OutOfRing(wrestlerA, _bodyA);
            bool bOffMat = OutOfRing(wrestlerB, _bodyB);
            if (aOffMat) GoLimp(wrestlerA);
            if (bOffMat) GoLimp(wrestlerB);

            // Down-out is GAME-ONLY and deliberately diverges from
            // Systems_SumoMatchManager. Every other losing condition is mirrored
            // there; this one is a spectacle rule in the same category as
            // knockoutsToLoseMatch. Without it a dismembered fighter can never
            // satisfy IsDown again and the round always runs the clock out.
            bool aDownOut = downOutSeconds > 0f && _downA >= downOutSeconds;
            bool bDownOut = downOutSeconds > 0f && _downB >= downOutSeconds;

            bool aOut = aOffMat || (knockdownLoses && _downA >= downGraceSeconds) || aDownOut;
            bool bOut = bOffMat || (knockdownLoses && _downB >= downGraceSeconds) || bDownOut;

            // A GIB OUTRANKS EVERY OTHER LOSING CONDITION, and is checked before
            // them. A fighter taken apart has parts scattered across and off the
            // mat, so OutOfRing reports it out — and if a flying part gibs the
            // opponent too, BOTH read as out and the round was being called a draw.
            // Exactly one fighter gibbed is not a draw: the one still in one piece
            // won it. Both gibbed genuinely is, and falls through below.
            if (_gibbedA != _gibbedB)
            {
                EndRound(_gibbedA ? wrestlerB : wrestlerA, RoundOutcome.Gibbed,
                         null, "TORN APART");
                return;
            }

            if (aOut && bOut)
            {
                // Both finished in the same step. The one that went down FIRST
                // stayed down longer, so award it against them rather than calling
                // a draw that neither earned.
                if (aDownOut && bDownOut && !Mathf.Approximately(_downA, _downB))
                {
                    EndRound(_downA > _downB ? wrestlerB : wrestlerA, RoundOutcome.DownOut,
                             null, "COULD NOT CONTINUE");
                }
                else
                {
                    EndRound(null, RoundOutcome.DoubleOut, "DOUBLE OUT — DRAW");
                }
            }
            // The outcome is picked apart rather than collapsed into one label,
            // because "a foot left the mat" and "lay there until the clock said so"
            // are the two ends of whether this game works and were indistinguishable
            // in the log before.
            else if (aOut) EndRound(wrestlerB, OutcomeFor(aOffMat, aDownOut), null,
                                    aDownOut ? "COULD NOT CONTINUE" : null);
            else if (bOut) EndRound(wrestlerA, OutcomeFor(bOffMat, bDownOut), null,
                                    bDownOut ? "COULD NOT CONTINUE" : null);
            else if (_elapsed >= roundTimeoutSeconds) DecideOnTimeout();
        }

        /// Closes the mat in as the round clock runs down, so a stalemate finishes
        /// the way sumo finishes instead of being handed to a judge.
        ///
        /// Nothing here detects anything: `OutOfRing` is purely vertical (a foot
        /// below the mat surface), and `SetPlatformHalfWidth` moves the real
        /// platform collider, so withdrawing the floor produces an ordinary
        /// RingOut through the existing path. That is why this is a dozen lines
        /// rather than a new rule.
        ///
        /// Deliberately starts LATE (shrinkStartSeconds, 8 of 20). The opening
        /// exchange should be fought on a full mat; the squeeze is what breaks a
        /// grapple that has gone nowhere, and starting it at t=0 would just make
        /// every round a scramble for the middle.
        private void TickShrinkingRing()
        {
            if (_arena == null || shrinkStartSeconds <= 0f) return;

            float span = roundTimeoutSeconds - shrinkStartSeconds;
            float target = ringHalfWidth;
            if (span > 0f && _elapsed > shrinkStartSeconds)
            {
                float progress = Mathf.Clamp01((_elapsed - shrinkStartSeconds) / span);
                target = Mathf.Lerp(ringHalfWidth, shrinkToHalfWidth, progress);
            }

            // 1 cm of hysteresis: below that the contraction is invisible and only
            // costs a collider rebuild.
            if (Mathf.Abs(target - _appliedHalfWidth) < 0.01f) return;
            _appliedHalfWidth = target;
            _arena.SetPlatformHalfWidth(target);
            // The slick rim has to travel with the edge, or the bales stay out at
            // the original radius and the contracted mat has full grip right up to
            // a cliff — which is the one thing the tawara exists to prevent.
            _arena.EnsureTawaraBands(target);
        }

        /// Ring-out takes precedence: a fighter can satisfy both in the same step
        /// (lying down AND over the edge) and the edge is the sumo rule.
        private static RoundOutcome OutcomeFor(bool offMat, bool downOut)
        {
            if (offMat) return RoundOutcome.RingOut;
            return downOut ? RoundOutcome.DownOut : RoundOutcome.Knockdown;
        }

        /// The shrinking ring used to force stalemates to resolve; with it gone,
        /// a timeout is settled the way a real bout goes to the judges — whoever
        /// held nearer the centre pushed the other toward the edge and takes the
        /// round. Only a near dead heat is still a draw.
        private void DecideOnTimeout()
        {
            if (!timeoutDecidesOnPosition)
            {
                EndRound(null, RoundOutcome.TimeoutDraw, "TIME — DRAW");
                return;
            }
            float centerX = transform.position.x;
            float distanceA = Mathf.Abs(wrestlerA.TorsoX - centerX);
            float distanceB = Mathf.Abs(wrestlerB.TorsoX - centerX);
            if (Mathf.Abs(distanceA - distanceB) < timeoutDeadHeat)
            {
                EndRound(null, RoundOutcome.TimeoutDraw, "TIME — DRAW");
                return;
            }
            bool aWins = distanceA < distanceB;
            Agent_Biped winner = aWins ? wrestlerA : wrestlerB;
            string label = WrapName(aWins ? nameA : nameB, aWins ? colorA : colorB);
            TimeoutDecisions++;
            // WINNER's distance first, then the loser's. It used to print distanceA
            // then distanceB unconditionally, so every round wrestlerB won read as
            // "<B> held the centre (1.53m vs 0.45m)" — the winner apparently FARTHER
            // from the middle. The rule above was always right; the sentence was
            // not, and six of ten lines in a played tournament looked like a
            // game-breaking inversion that did not exist.
            Debug.Log($"[MATCH] timeout decision #{TimeoutDecisions}: " +
                      $"{(aWins ? nameA : nameB)} held the centre at " +
                      $"{(aWins ? distanceA : distanceB):F2}m vs " +
                      $"{(aWins ? distanceB : distanceA):F2}m");
            EndRound(winner, RoundOutcome.TimeoutDecision, null, $"TIME — {label} DECISION");
        }

        /// Cut a fighter's motors so it flops lifelessly off the edge instead of
        /// holding a rigid pose on the way down.
        private static void GoLimp(Agent_Biped fighter)
        {
            fighter.actionsEnabled = false;
            var body = fighter.GetComponent<Agent_BipedBody>();
            if (body != null) body.GoLimp();
        }

        /// Sumo's stepping-out rule: the moment either foot drops off the edge of
        /// the mat the bout is lost, without waiting for the body to tumble clear.
        /// A foot cannot get below the mat surface while still standing on it, so
        /// dipping under footOffMatY means it has gone over the edge.
        ///
        /// The torso/fallY test is kept as a backstop for a body that leaves the
        /// mat without a foot leading — thrown clear, or landing head-first.
        private bool OutOfRing(Agent_Biped w, Agent_BipedBody body)
        {
            if (body != null)
            {
                float matTop = transform.position.y;
                float limit = matTop + footOffMatY;
                // Only feet still joined to the body count. A leg torn off by
                // Systems_BodyDamage is debris — it gets shoved around and will
                // very likely slide off the edge, and losing the round because
                // your severed leg fell off the mat is the opposite of what the
                // blow that took it off earned.
                if (body.FootNear != null && body.FootNearAttached
                    && body.FootNear.position.y < limit) return true;
                if (body.FootFar != null && body.FootFarAttached
                    && body.FootFar.position.y < limit) return true;
            }
            return w.Torso.position.y < fallY;
        }

        private void EndRound(Agent_Biped roundWinner, RoundOutcome outcome,
                              string drawText, string winText = null)
        {
            LongestRound = Mathf.Max(LongestRound, _elapsed);

            // Every exit from a round passes through here, so this is the one place
            // that can honestly account for all of them. Distances from the centre
            // are included on purpose: a RingOut at 3.9m and a TimeoutDecision at
            // 0.2m tell you the ring is doing its job, whereas a run of timeouts
            // with both fighters parked near the middle says they never engaged.
            _roundsLogged++;
            _outcomeTally[(int)outcome]++;
            float centre = transform.position.x;
            string winnerName = roundWinner == wrestlerA ? nameA
                              : roundWinner == wrestlerB ? nameB : "—";
            Debug.Log($"[ROUND] {_roundsLogged} {outcome} winner={winnerName} " +
                      $"t={_elapsed:F1}s score={_scoreA}-{_scoreB} " +
                      $"aX={wrestlerA.TorsoX - centre:F2} bX={wrestlerB.TorsoX - centre:F2} " +
                      $"ko={_koA}-{_koB}");

            if (roundWinner == wrestlerA) _scoreA++;
            else if (roundWinner == wrestlerB) _scoreB++;
            UpdateScoreboard(roundWinner != null);

            // Clay dust where the loser went down / out.
            var loser = roundWinner == wrestlerA ? wrestlerB : roundWinner == wrestlerB ? wrestlerA : null;
            if (loser != null)
                Systems_DustPuff.Burst(loser.Torso.position);

            bool matchOver = _scoreA >= pointsToWin || _scoreB >= pointsToWin;
            if (matchOver)
            {
                // Match decided: the loser goes fully limp and stays down, while
                // the winner keeps its brain running so it carries on moving
                // instead of freezing mid-pose over the body.
                Agent_Biped matchWinner = _scoreA > _scoreB ? wrestlerA : wrestlerB;
                Agent_Biped matchLoser = matchWinner == wrestlerA ? wrestlerB : wrestlerA;
                GoLimp(matchLoser);
                matchWinner.actionsEnabled = true;
            }
            else
            {
                // Between rounds both stop driving; whoever left the mat is
                // already fully limp from GoLimp in FixedUpdate, and the survivor
                // simply holds its stance until the next round resets them.
                wrestlerA.actionsEnabled = false;
                wrestlerB.actionsEnabled = false;
            }

            // A ragdoll flop deserves a beat before the result is declared.
            bool limpFlop = IsLimp(wrestlerA) || IsLimp(wrestlerB);
            long announceDelayMs = limpFlop ? (long)(limpBeforeAnnounce * 1000f) : 0L;

            _clock.text = "";
            _lastShownSeconds = -1;
            _countdownLeft = 0f;
            HideCountdown();

            RoundEnded?.Invoke(roundWinner, loser);

            if (matchOver)
            {
                _phase = Phase.MatchOver;
                bool aWon = _scoreA > _scoreB;
                if (aWon) MatchWinsA++; else MatchWinsB++;
                // Cumulative across the whole session, not just this bout — a
                // single match is far too small a sample to say anything about
                // whether ring-outs happen, and the bracket builds a fresh manager
                // per bout so only a static tally can span them.
                Debug.Log("[MATCH] " + OutcomeSummary());
                _hud.HideCentre(_banner);
                _resultTitle.text = $"{WrapName(aWon ? nameA : nameB, aWon ? colorA : colorB)} WINS";
                _resultScore.text = $"{Mathf.Max(_scoreA, _scoreB)} — {Mathf.Min(_scoreA, _scoreB)}";
                _resultCard.style.borderTopColor = aWon ? colorA : colorB;
                // One call now raises the card AND its backdrop; they were two
                // separately scheduled reveals that had to be kept in step. It
                // also freezes both fighters on the same frame.
                ShowResultCardAfter(announceDelayMs);
                // The running scorebug would otherwise sit above the result card
                // competing with it; the card already states the score.
                _scoreBugHide = HideAfter(_scoreBug, announceDelayMs);
                MatchEnded?.Invoke(aWon ? wrestlerA : wrestlerB);
                return;
            }

            _banner.text = roundWinner != null
                ? (winText ?? $"{WrapName(roundWinner == wrestlerA ? nameA : nameB, roundWinner == wrestlerA ? colorA : colorB)} SCORES!")
                : drawText;
            ShowCentreAfter(_banner, announceDelayMs);

            _phase = Phase.RoundEnded;
            // Extend the pause by the announce delay so the result stays on
            // screen just as long as it did before.
            _phaseLeft = betweenRoundsPause + announceDelayMs / 1000f;
        }

        /// Boxing's three-knockdown rule, layered on top of the sumo rules.
        ///
        /// A head knockout was otherwise pure spectacle: Systems_BodyDamage cuts
        /// the fighter's motors and Systems_MatchPresentation slows time, but the
        /// round was still only won by pushing the limp body out, so a clean head
        /// shot could cost the man who landed it nothing at all. Counting them
        /// gives the KO a consequence — take `knockoutsToLoseMatch` in one match
        /// and it ends there, wherever the bodies happen to be standing.
        ///
        /// Deliberately NOT mirrored into Systems_SumoMatchManager. The two
        /// referees are kept in step on the losing conditions a policy has to
        /// learn; this is not one of them. It can only end a MATCH, never a
        /// training episode, so no brain can meet a rule it never trained against.
        /// A fighter lost all four limbs to one blow — the round is over immediately.
        ///
        /// This is a GAME-ONLY rule with no equivalent in Systems_SumoMatchManager,
        /// like downOutSeconds and knockoutsToLoseMatch. No brain has trained against
        /// it, which is fine: it is spectacle layered on the sumo rules, and the
        /// victim could not have kept fighting anyway.
        ///
        /// Without it the existing down-out rule would still end the round about 3 s
        /// later — a limbless fighter can never satisfy the get-up condition — so
        /// this only removes the wait, and the fallback stays correct if gibLosesRound
        /// is ever turned off.
        private void OnGibbed(Agent_BipedBody victim, Vector3 point)
        {
            if (victim == null) return;

            Agent_Biped loser = victim == _bodyA ? wrestlerA
                              : victim == _bodyB ? wrestlerB : null;
            if (loser == null) return;

            // Recorded FIRST and unconditionally, before any early return. This flag
            // is what lets the FixedUpdate evaluation resolve the race described on
            // _gibbedA — if this callback loses it, the evaluation still knows a gib
            // happened and awards the round correctly instead of calling a draw.
            if (loser == wrestlerA) _gibbedA = true; else _gibbedB = true;

            if (!gibLosesRound) return;
            // Only Fighting can be ended. The blow that gibs often lands in the same
            // physics step as a ring-out, and a second EndRound during RoundEnded or
            // Grace would score the round twice.
            if (_hud == null || _phase != Phase.Fighting) return;

            Debug.Log($"[MATCH] gib on {(loser == wrestlerA ? nameA : nameB)} — round over");
            EndRound(loser == wrestlerA ? wrestlerB : wrestlerA, RoundOutcome.Gibbed,
                     null, "TORN APART");
        }

        private void OnKnockout(Agent_BipedBody victim, Vector3 point)
        {
            if (victim == null || knockoutsToLoseMatch <= 0) return;
            // _hud null means Start has not built the UI yet; MatchOver means the
            // result card already owns the screen.
            if (_hud == null || _phase == Phase.MatchOver) return;

            Agent_Biped loser;
            int suffered;
            if (victim == _bodyA) { loser = wrestlerA; suffered = ++_koA; }
            else if (victim == _bodyB) { loser = wrestlerB; suffered = ++_koB; }
            else return;

            Debug.Log($"[MATCH] knockout {suffered}/{knockoutsToLoseMatch} on " +
                      $"{(loser == wrestlerA ? nameA : nameB)}");
            if (suffered < knockoutsToLoseMatch) return;

            EndMatchByKnockout(loser);
        }

        /// Stops the match on the deciding knockout. The KO'd fighter loses no
        /// matter what the round score says — that is the whole point of the rule.
        private void EndMatchByKnockout(Agent_Biped loser)
        {
            Agent_Biped winner = loser == wrestlerA ? wrestlerB : wrestlerA;
            bool aWon = winner == wrestlerA;

            _phase = Phase.MatchOver;
            EndedByKnockout = true;
            if (aWon) MatchWinsA++; else MatchWinsB++;
            LongestRound = Mathf.Max(LongestRound, _elapsed);

            // The loser is already limp from Systems_BodyDamage; keep it that way
            // and let the winner carry on moving over the body.
            GoLimp(loser);
            winner.actionsEnabled = true;

            _clock.text = "";
            _lastShownSeconds = -1;
            _countdownLeft = 0f;
            HideCountdown();
            _hud.HideCentre(_banner);

            _resultTitle.text = $"{WrapName(aWon ? nameA : nameB, aWon ? colorA : colorB)} WINS BY KO";
            _resultScore.text = $"{(aWon ? _koB : _koA)} knockouts · rounds {_scoreA}–{_scoreB}";
            _resultCard.style.borderTopColor = aWon ? colorA : colorB;

            // The UI Toolkit scheduler runs on realtime, as does the presentation's
            // slow-motion timer, so this delay genuinely outlasts the KO replay
            // instead of being stretched along with it.
            long delayMs = (long)(Mathf.Max(0f, knockoutAnnounceSeconds) * 1000f);
            ShowResultCardAfter(delayMs);
            _scoreBugHide = HideAfter(_scoreBug, delayMs);

            // No RoundEnded is fired: no round was won. Raising it would hand the
            // career recorder a round result that never happened.
            MatchEnded?.Invoke(winner);
        }

        private static bool IsLimp(Agent_Biped fighter)
        {
            var body = fighter.GetComponent<Agent_BipedBody>();
            return body != null && body.IsLimp;
        }

        private static IVisualElementScheduledItem HideAfter(VisualElement element, long delayMs)
        {
            if (element == null) return null;
            if (delayMs <= 0L) { element.style.display = DisplayStyle.None; return null; }
            return element.schedule.Execute(() => element.style.display = DisplayStyle.None)
                          .StartingIn(delayMs);
        }

        /// Drops every announce still in flight.
        ///
        /// The result card, the scorebug hide and the round banner are all
        /// scheduled ~1.2 s out so the ragdoll lands first. Nothing used to
        /// cancel them, so starting the next match inside that window — a tap on
        /// the MatchOver phase, or MatchTestHarness chaining — let RevealResultCard
        /// fire into the NEW match: it froze both fighters (actions off, physics
        /// off) and raised the previous match's card as a modal. The walk-in then
        /// never reached contact and burned its full timeout with two motionless
        /// bodies, which is measured play the harness tally then reported as real.
        private void CancelPendingAnnounce()
        {
            if (_resultCardReveal != null) { _resultCardReveal.Pause(); _resultCardReveal = null; }
            if (_scoreBugHide != null) { _scoreBugHide.Pause(); _scoreBugHide = null; }
            if (_bannerReveal != null) { _bannerReveal.Pause(); _bannerReveal = null; }
        }

        /// Reveal the result card, and stop the match dead at the same moment.
        ///
        /// The winner is deliberately left driving through the finish so he
        /// carries on moving over the body instead of freezing mid-pose. Once the
        /// card is up that reads as the game still running behind a dialog, so
        /// everything halts on the same frame the card appears — not when the
        /// match was decided, because the ragdoll flop in between is the point.
        private void RevealResultCard()
        {
            FreezeFighter(wrestlerA);
            FreezeFighter(wrestlerB);
            // Applied here rather than at build time: the reporter is spawned by
            // Systems_MatchRoster and its Start is not ordered against this
            // component's, so the flag is only reliably set by the time a match
            // has actually been decided.
            if (_rematchButton != null)
            {
                _rematchButton.style.display = _bracketBout ? DisplayStyle.None : DisplayStyle.Flex;
            }
            _hud.ShowModal(_resultCard);
        }

        private void ShowResultCardAfter(long delayMs)
        {
            if (delayMs <= 0L)
            {
                RevealResultCard();
                return;
            }
            _resultCardReveal = _resultCard.schedule.Execute(RevealResultCard).StartingIn(delayMs);
        }

        /// Kills a fighter's motion outright: motors off, velocities zeroed and
        /// physics stopped, so the pose under the result card holds still.
        /// ResetMatch's EndEpisode -> HoldUpright -> round opening puts them back
        /// to simulating, so nothing has to undo this explicitly.
        private static void FreezeFighter(Agent_Biped fighter)
        {
            if (fighter == null) return;
            fighter.actionsEnabled = false;
            SetSimulated(fighter.GetComponent<Agent_BipedBody>(), false, zeroVelocity: true);
        }

        /// Raise a callout drawn over the arena now, or after a delay so the ragdoll
        /// lands first. Routing through the HUD root means the mutual exclusion with
        /// any other overlay comes along automatically.
        private void ShowCentreAfter(VisualElement element, long delayMs)
        {
            if (delayMs <= 0L)
            {
                _hud.ShowCentre(element);
                return;
            }
            _bannerReveal = element.schedule.Execute(() => _hud.ShowCentre(element)).StartingIn(delayMs);
        }

        private static string WrapName(string n, Color c) => $"<color=#{Hex(c)}>{n}</color>";

        /// Called by Systems_TournamentReporter, which exists only for a bracket
        /// bout. A bracket result is reported to Systems_TournamentState the
        /// instant the match is decided and the return to SCN_TOURNAMENT is
        /// already scheduled, so a rematch cannot change the outcome — it just
        /// restarts the fight for a second and then cuts to the bracket anyway.
        /// Both the REMATCH button and tap-to-continue are suppressed.
        ///
        /// Not gated on Systems_TournamentState.Active: ReportWinner clears that
        /// flag on the final, and it is cleared before the result card is even
        /// revealed, so the final's card would have offered a rematch.
        public void MarkBracketBout() => _bracketBout = true;

        /// Public so MatchTestHarness can chain matches unattended without the
        /// REMATCH button. It used to reach this through private reflection, which
        /// meant renaming the method turned the harness into a silent no-op after
        /// one match — and the harness tally is how fighters are judged here.
        public void ResetMatch()
        {
            _scoreA = _scoreB = 0;
            _koA = _koB = 0;
            EndedByKnockout = false;
            _walkInPlayed = false;   // a rematch is a new match, so it walks in again
            // Before HideModal, or a reveal scheduled by the match just finished
            // fires into this one and freezes it behind a stale card.
            CancelPendingAnnounce();
            MatchReset?.Invoke();
            UpdateScoreboard(false);
            _hud.HideModal();            // clears the result card and the backdrop
            _hud.HideCentre(_banner);
            if (_scoreBug != null) _scoreBug.style.display = DisplayStyle.Flex;
            wrestlerA.EndEpisode();
            wrestlerB.EndEpisode();
            HoldUpright();
            _downA = _downB = 0f;
            _gibbedA = _gibbedB = false;
            _phase = Phase.Grace;
            _phaseLeft = graceSeconds;
        }
    }
}
