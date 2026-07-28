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
    /// dims the arena and waits for the REMATCH button, a pointer press or Space.
    /// UI Toolkit only.
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
        private bool enablePostFx = true, enableAtmosphere = true, enableMusic = true;
        private bool enableBodyDamage = true;
        private bool enableMomentumGraph = true;
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
        private int _lastShownSeconds = -1;
        private float _countdownLeft;
        private int _lastCountdownDigit = -1;
        private Phase _phase = Phase.Fighting;
        private float _walkInLeft;
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
        private bool WalkBrainsReady()
        {
            return wrestlerA != null && wrestlerB != null
                && wrestlerA.character != null && wrestlerB.character != null
                && wrestlerA.character.walkModel != null && wrestlerB.character.walkModel != null;
        }
        // Cached in Start: the foot check runs every FixedUpdate, so no
        // GetComponent in the hot path.
        private Agent_BipedBody _bodyA, _bodyB;

        // Read-only stats for HUDs.
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
        private bool _paused;

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
                knockdownLoses = tuning.knockdownLoses;
                knockoutsToLoseMatch = tuning.knockoutsToLoseMatch;
                knockoutAnnounceSeconds = tuning.knockoutAnnounceSeconds;
                ringHalfWidth = tuning.ringHalfWidth;
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
                enablePostFx = tuning.enablePostFx;
                enableAtmosphere = tuning.enableAtmosphere;
                enableMusic = tuning.enableMusic;
                enableBodyDamage = tuning.enableBodyDamage;
                enableMomentumGraph = tuning.enableMomentumGraph;
            }

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
        private void OnEnable() => Systems_BodyDamage.Knockout += OnKnockout;

        private void OnDisable() => Systems_BodyDamage.Knockout -= OnKnockout;

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
            // Handing the walk brain a body still carrying the fight brain's
            // leftover state is what made the fighters collapse on the first stride.
            // Both aim at the CENTRE, not at their own stand-off mark, so they walk
            // into each other rather than halting a stand-off apart. The walk brain
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
        }

        /// Places a fighter at the far end, upright and simulating, ready to walk.
        /// Unlike PoseNeutral this leaves physics and motors ON — the walk brain
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
                for (int partIndex = 0; partIndex < body.Parts.Length; partIndex++)
                {
                    body.Parts[partIndex].simulated = true;
                }
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
            for (int partIndex = 0; partIndex < body.Parts.Length; partIndex++)
            {
                body.Parts[partIndex].simulated = false;
            }
            w.actionsEnabled = false;
        }


        /// Countdown hit zero: physics back on, fight brains live.
        private void BeginSimulation()
        {
            // When the walk brain held them through the countdown they are already
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
            var body = w.GetComponent<Agent_BipedBody>();
            if (body != null)
            {
                for (int partIndex = 0; partIndex < body.Parts.Length; partIndex++)
                {
                    body.Parts[partIndex].simulated = true;
                }
            }
            w.actionsEnabled = true;
        }

        /// Presentation, audio and face-mood are runtime-spawned children so
        /// scenes stay manager-only and older scenes pick them up automatically.
        private void SpawnCompanionSystems()
        {
            if (enablePresentation && FindAnyObjectByType<Systems_MatchPresentation>() == null)
            {
                var go = new GameObject("Presentation");
                go.transform.SetParent(transform, false);
                go.AddComponent<Systems_MatchPresentation>();
            }
            if (enableAudio && FindAnyObjectByType<Systems_MatchAudio>() == null)
            {
                var go = new GameObject("MatchAudio");
                go.transform.SetParent(transform, false);
                go.AddComponent<Systems_MatchAudio>();
            }
            // One mood driver per fighter that actually has face art.
            if (enableFaceMood && FindAnyObjectByType<Systems_FaceMood>() == null)
            {
                SpawnFaceMood(wrestlerA);
                SpawnFaceMood(wrestlerB);
            }
            if (enableMomentumGraph && FindAnyObjectByType<Systems_MomentumGraph>() == null)
            {
                var go = new GameObject("MomentumGraph");
                go.transform.SetParent(transform, false);
                go.AddComponent<Systems_MomentumGraph>().panelSettings = panelSettings;
            }
            if (enableBodyDamage && FindAnyObjectByType<Systems_BodyDamage>() == null)
            {
                SpawnBodyDamage(wrestlerA);
                SpawnBodyDamage(wrestlerB);
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
            if (enableImpactFx && FindAnyObjectByType<Systems_ImpactFx>() == null)
            {
                var go = new GameObject("ImpactFx");
                go.transform.SetParent(transform, false);
                go.AddComponent<Systems_ImpactFx>();
            }
            if (enableLighting && FindAnyObjectByType<Systems_ArenaLighting>() == null)
            {
                var go = new GameObject("ArenaLighting");
                go.transform.SetParent(transform, false);
                go.AddComponent<Systems_ArenaLighting>();
            }
            if (recordCareerStats && FindAnyObjectByType<Systems_CareerRecorder>() == null)
            {
                var go = new GameObject("CareerRecorder");
                go.transform.SetParent(transform, false);
                go.AddComponent<Systems_CareerRecorder>();
            }
            // PostFx binds to the volume Systems_ArenaLighting builds, so it must
            // be spawned after it — SpawnCompanionSystems runs in declaration order
            // and lighting is created above.
            if (enablePostFx && FindAnyObjectByType<Systems_PostFx>() == null)
            {
                var go = new GameObject("PostFx");
                go.transform.SetParent(transform, false);
                go.AddComponent<Systems_PostFx>();
            }
            if (enableAtmosphere && FindAnyObjectByType<Systems_ArenaAtmosphere>() == null)
            {
                var go = new GameObject("Atmosphere");
                go.transform.SetParent(transform, false);
                go.AddComponent<Systems_ArenaAtmosphere>();
            }
            if (enableMusic && FindAnyObjectByType<Systems_MusicDirector>() == null)
            {
                var go = new GameObject("Music");
                go.transform.SetParent(transform, false);
                go.AddComponent<Systems_MusicDirector>();
            }
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

        /// One voice per fighter. Systems_FighterVoice disables itself when that
        /// fighter has no recorded clips in Resources, so this is safe to call for
        /// everyone — today only Matt has a voice.
        private void SpawnVoice(Agent_Biped fighter, float pitchScale)
        {
            if (fighter == null) return;
            var go = new GameObject($"Voice_{fighter.behaviorName}");
            go.transform.SetParent(transform, false);
            var voice = go.AddComponent<Systems_FighterVoice>();
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
            // No on-screen pause chip. Pause itself is NOT gone — TogglePause is
            // still bound to escapeKey in Update, which is what Android maps its
            // hardware back button to, so the pause card is reachable on the
            // target platform without spending a corner of the HUD on it.
            // TopBarLeft stays as an empty fixed-width slot: it and TopBarRight
            // are what keep the scorebug optically centred.

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
            _hud.AddCentre(_banner);

            _countdown = Systems_UiKit.Text("", Systems_UiKit.FONT_MEGA, Systems_UiKit.Gold, true);
            _countdown.style.unityTextAlign = TextAnchor.MiddleCenter;
            _countdown.style.textShadow = Systems_UiKit.Outline;
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
            // press still work too.
            Button rematch = Systems_UiKit.PrimaryButton("REMATCH", ResetMatch);
            rematch.style.marginTop = Systems_UiKit.SPACE_5;
            _resultCard.Add(rematch);

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
                _scoreDigits.style.fontSize = Systems_UiKit.FONT_HERO;
                _scoreDigits.schedule
                    .Execute(() => _scoreDigits.style.fontSize = Systems_UiKit.FONT_TITLE)
                    .StartingIn(400);
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
            _countdown.style.fontSize = Systems_UiKit.FONT_MEGA;
            _countdown.text = digit.ToString();
            _hud.ShowCentre(_countdown);

            if (digit == 1) return; // let the last punch-in expire — wide for the engage
            var fighter = ((countdownSeconds - digit) & 1) == 0 ? wrestlerA : wrestlerB;
            PunchOnHead(fighter);
        }

        private void PunchOnHead(Agent_Biped fighter)
        {
            if (_camFollow == null || fighter == null) return;
            var body = fighter.GetComponent<Agent_BipedBody>();
            Transform focus = body != null && body.HeadRenderer != null
                ? body.HeadRenderer.transform
                : fighter.Torso.transform;
            _camFollow.PunchIn(focus, countdownHeadOrtho, 1.05f);
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
            if (_phase == Phase.MatchOver && ContinuePressed()) { ResetMatch(); return; }
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

        /// Dismiss the result card and rematch.
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
            return Input.GetKeyDown(KeyCode.Space);
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

                    // Backstop only — a fighter stumbled or never got walking. Fall
                    // back to the old opening so a bad walk-in cannot hang the match.
                    if (_walkInLeft <= 0f)
                    {
                        Debug.LogWarning($"WALK-IN RESULT: timed out after {walkInTimeout:F1}s with no contact — " +
                                         $"surfaceGap={WalkInSurfaceGap():F2} (need <= {walkInTouchGap:F2}), " +
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

            // Only accumulated when the rule that reads them is on. With
            // knockdownLoses off (the shipping config) these fed one unreachable
            // branch and were pure work every physics step. The HUD's KD stat does
            // NOT come from here — Systems_FightHud tracks Agent_Biped.IsDown itself.
            if (knockdownLoses)
            {
                _downA = wrestlerA.IsDown ? _downA + Time.fixedDeltaTime : 0f;
                _downB = wrestlerB.IsDown ? _downB + Time.fixedDeltaTime : 0f;
            }

            // Anyone off the mat goes limp the instant it happens, so the ragdoll
            // flop plays out before the result is announced.
            bool aOffMat = OutOfRing(wrestlerA, _bodyA);
            bool bOffMat = OutOfRing(wrestlerB, _bodyB);
            if (aOffMat) GoLimp(wrestlerA);
            if (bOffMat) GoLimp(wrestlerB);

            bool aOut = aOffMat || (knockdownLoses && _downA >= downGraceSeconds);
            bool bOut = bOffMat || (knockdownLoses && _downB >= downGraceSeconds);

            if (aOut && bOut) EndRound(null, "DOUBLE OUT — DRAW");
            else if (aOut) EndRound(wrestlerB, null);
            else if (bOut) EndRound(wrestlerA, null);
            else if (_elapsed >= roundTimeoutSeconds) DecideOnTimeout();
        }

        /// The shrinking ring used to force stalemates to resolve; with it gone,
        /// a timeout is settled the way a real bout goes to the judges — whoever
        /// held nearer the centre pushed the other toward the edge and takes the
        /// round. Only a near dead heat is still a draw.
        private void DecideOnTimeout()
        {
            if (!timeoutDecidesOnPosition)
            {
                EndRound(null, "TIME — DRAW");
                return;
            }
            float centerX = transform.position.x;
            float distanceA = Mathf.Abs(wrestlerA.TorsoX - centerX);
            float distanceB = Mathf.Abs(wrestlerB.TorsoX - centerX);
            if (Mathf.Abs(distanceA - distanceB) < timeoutDeadHeat)
            {
                EndRound(null, "TIME — DRAW");
                return;
            }
            Agent_Biped winner = distanceA < distanceB ? wrestlerA : wrestlerB;
            string label = WrapName(winner == wrestlerA ? nameA : nameB,
                                    winner == wrestlerA ? colorA : colorB);
            // Logged because this path is easy to assume dead: with a tight ring
            // and the stepping-out rule, rounds almost never reach the timeout.
            // It only matters for genuine stalemates on a wide mat.
            TimeoutDecisions++;
            Debug.Log($"[MATCH] timeout decision #{TimeoutDecisions}: " +
                      $"{(winner == wrestlerA ? nameA : nameB)} held the centre " +
                      $"({distanceA:F2}m vs {distanceB:F2}m from middle)");
            EndRound(winner, null, $"TIME — {label} DECISION");
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

        private void EndRound(Agent_Biped roundWinner, string drawText, string winText = null)
        {
            LongestRound = Mathf.Max(LongestRound, _elapsed);
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
                HideAfter(_scoreBug, announceDelayMs);
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
            HideAfter(_scoreBug, delayMs);

            // No RoundEnded is fired: no round was won. Raising it would hand the
            // career recorder a round result that never happened.
            MatchEnded?.Invoke(winner);
        }

        private static bool IsLimp(Agent_Biped fighter)
        {
            var body = fighter.GetComponent<Agent_BipedBody>();
            return body != null && body.IsLimp;
        }

        private static void HideAfter(VisualElement element, long delayMs)
        {
            if (element == null) return;
            if (delayMs <= 0L) { element.style.display = DisplayStyle.None; return; }
            element.schedule.Execute(() => element.style.display = DisplayStyle.None)
                   .StartingIn(delayMs);
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
            _hud.ShowModal(_resultCard);
        }

        private void ShowResultCardAfter(long delayMs)
        {
            if (delayMs <= 0L)
            {
                RevealResultCard();
                return;
            }
            _resultCard.schedule.Execute(RevealResultCard).StartingIn(delayMs);
        }

        /// Kills a fighter's motion outright: motors off, velocities zeroed and
        /// physics stopped, so the pose under the result card holds still.
        /// ResetMatch's EndEpisode -> HoldUpright -> round opening puts them back
        /// to simulating, so nothing has to undo this explicitly.
        private static void FreezeFighter(Agent_Biped fighter)
        {
            if (fighter == null) return;
            fighter.actionsEnabled = false;
            var body = fighter.GetComponent<Agent_BipedBody>();
            if (body == null || body.Parts == null) return;
            for (int partIndex = 0; partIndex < body.Parts.Length; partIndex++)
            {
                Rigidbody2D part = body.Parts[partIndex];
                if (part == null) continue;
                part.linearVelocity = Vector2.zero;
                part.angularVelocity = 0f;
                part.simulated = false;
            }
        }

        /// Raise a dialog now, or after a delay so the ragdoll lands first.
        /// Routing through the HUD root means the dim backdrop and the mutual
        /// exclusion with any other dialog come along automatically.
        private void ShowModalAfter(VisualElement element, long delayMs)
        {
            if (delayMs <= 0L)
            {
                _hud.ShowModal(element);
                return;
            }
            element.schedule.Execute(() => _hud.ShowModal(element)).StartingIn(delayMs);
        }

        /// Same, for a callout drawn over the arena rather than a dialog.
        private void ShowCentreAfter(VisualElement element, long delayMs)
        {
            if (delayMs <= 0L)
            {
                _hud.ShowCentre(element);
                return;
            }
            element.schedule.Execute(() => _hud.ShowCentre(element)).StartingIn(delayMs);
        }

        private static string WrapName(string n, Color c) => $"<color=#{Hex(c)}>{n}</color>";

        private void ResetMatch()
        {
            _scoreA = _scoreB = 0;
            _koA = _koB = 0;
            EndedByKnockout = false;
            _walkInPlayed = false;   // a rematch is a new match, so it walks in again
            MatchReset?.Invoke();
            UpdateScoreboard(false);
            _hud.HideModal();            // clears the result card and the backdrop
            _hud.HideCentre(_banner);
            if (_scoreBug != null) _scoreBug.style.display = DisplayStyle.Flex;
            wrestlerA.EndEpisode();
            wrestlerB.EndEpisode();
            HoldUpright();
            _downA = _downB = 0f;
            _phase = Phase.Grace;
            _phaseLeft = graceSeconds;
        }
    }
}
