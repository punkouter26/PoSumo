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
    /// dims the arena and waits for Space to rematch. UI Toolkit only.
    public class Systems_GameMatchManager : MonoBehaviour
    {
        enum Phase { Fighting, RoundEnded, Grace, WalkIn, MatchOver }

        public Agent_Biped wrestlerA;           // teamId 0
        public Agent_Biped wrestlerB;           // teamId 1
        public string nameA = "MATT";
        public string nameB = "STANDARD";
        public Color colorA = new Color(0.85f, 0.25f, 0.2f);
        public Color colorB = new Color(0.2f, 0.5f, 0.3f);
        public int pointsToWin = 3;
        public float ringHalfWidth = 2.75f;
        public float fallY = -1.5f;
        public bool knockdownLoses = true;
        public float roundTimeoutSeconds = 30f;
        public float betweenRoundsPause = 2.5f;
        public float graceSeconds = 0.4f;
        public float downGraceSeconds = 0.2f;
        public PanelSettings panelSettings;
        public Systems_GameTuning tuning;
        [Tooltip("Spawn Systems_MatchPresentation (slow-mo, punch-in, replay) at startup.")]
        public bool enablePresentation = true;
        [Tooltip("Spawn Systems_MatchAudio (impacts, crowd, gong) at startup.")]
        public bool enableAudio = true;
        [Tooltip("Spawn Systems_FaceMood (Matt's expression follows dominance) at startup.")]
        public bool enableFaceMood = true;
        [Tooltip("Persist results to the career record (W/L, head-to-head, Elo, titles). Turn off for throwaway test matches such as MatchTestHarness runs.")]
        public bool recordCareerStats = true;
        [Tooltip("Spawn Systems_ArenaLighting (2D light rig + post-processing) at startup.")]
        public bool enableLighting = true;
        [Tooltip("Spawn Systems_ImpactFx (dust bursts + camera shake scaled by hit strength).")]
        public bool enableImpactFx = true;
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
        [Tooltip("Half the gap the fighters start from. 3.0 against a 1.2 stand-off = 1.8 m of walking each, about two strides at the measured 1.4 m/s.")]
        public float walkInStartGapHalf = 3f;
        [Tooltip("Distance from its mark at which a fighter counts as arrived.")]
        public float walkInArriveTolerance = 0.3f;
        [Tooltip("Hard cap on the walk-in. Only a backstop: the walk takes ~1.3 s, so this should never be reached unless a fighter stumbles.")]
        public float walkInTimeout = 2.5f;
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

        int _scoreA, _scoreB;
        float _elapsed, _phaseLeft, _downA, _downB;
        int _lastShownSeconds = -1;
        float _countdownLeft;
        int _lastCountdownDigit = -1;
        Phase _phase = Phase.Fighting;
        float _walkInLeft;
        float _savedGroundWidth;
        float _braceStarted = -999f;
        float _releaseTime = -1f;
        bool _walkBrainHolding;
        bool _parkedA, _parkedB;
        Systems_SumoArena _arena;
        Systems_CameraFollow _camFollow;

        /// The walk-in only runs if BOTH fighters actually have a locomotion brain;
        /// Agent_Biped.BeginWalkIn is a silent no-op without one, which would strand
        /// them at the far ends until the timeout.
        bool WalkBrainsReady()
        {
            return wrestlerA != null && wrestlerB != null
                && wrestlerA.character != null && wrestlerB.character != null
                && wrestlerA.character.walkModel != null && wrestlerB.character.walkModel != null;
        }
        // Cached in Start: the foot check runs every FixedUpdate, so no
        // GetComponent in the hot path.
        Agent_BipedBody _bodyA, _bodyB;

        // Read-only stats for HUDs.
        public int ScoreA => _scoreA;
        public int ScoreB => _scoreB;
        public int MatchWinsA { get; private set; }
        public int MatchWinsB { get; private set; }
        public float RoundElapsed => _elapsed;
        public float LongestRound { get; private set; }
        /// How many rounds were settled by the timeout tiebreak rather than a
        /// ring-out. Zero over a long session means the tiebreak is unreachable.
        public int TimeoutDecisions { get; private set; }
        public bool RoundActive => _phase == Phase.Fighting;

        /// Fired when a round is decided: (roundWinner, roundLoser) — both null on a draw.
        public event System.Action<Agent_Biped, Agent_Biped> RoundEnded;
        /// Fired when scoring resumes for a fresh round.
        public event System.Action RoundStarted;
        /// Fired once when the match is decided, with the match winner.
        public event System.Action<Agent_Biped> MatchEnded;
        /// Fired when a rematch resets the scores (HUD aggregates restart here).
        public event System.Action MatchReset;
        Label _scoreLabel, _banner, _clock, _countdown;
        VisualElement _pauseCard;
        bool _paused;
        VisualElement _scoreBlock;   // hidden while the result card is up
        Button _rematchBtn;
        VisualElement _overlay, _resultCard;
        Label _resultTitle, _resultScore;
        Color _scoreBaseColor = new Color(0.95f, 0.93f, 0.85f);

        static TextShadow OutlineShadow => new TextShadow
        {
            offset = new Vector2(0f, 2f),
            blurRadius = 4f,
            color = new Color(0f, 0f, 0f, 0.85f),
        };

        void Start()
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

            // Size the dohyo to the configured ring at startup. The platform is
            // resized once more per round — wide for the walk-in, back to
            // ringHalfWidth before the fight — but it never moves DURING a round,
            // so the fighting ring is always the size the brains trained on.
            // Baked scenes ship a 5.5 m-wide platform, so without this the
            // physical edge would ignore ringHalfWidth entirely.
            _arena = FindAnyObjectByType<Systems_SumoArena>();
            if (_arena != null) _arena.SetPlatformHalfWidth(ringHalfWidth);

            _camFollow = FindAnyObjectByType<Systems_CameraFollow>();
            BuildUi();
            UpdateScoreboard(false);
            SpawnCompanionSystems();

            // The very first round gets the same opening as every later one.
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
        void Awake()
        {
            ResolveWrestlers();
            AdoptCharacterIdentity();
        }

        void ResolveWrestlers()
        {
            if (wrestlerA != null && wrestlerB != null) return;
            foreach (var a in FindObjectsByType<Agent_Biped>(FindObjectsSortMode.None))
            {
                if (a.teamId == 0) wrestlerA = a; else wrestlerB = a;
            }
        }

        /// Scoreboard name and colour follow whichever characters are actually
        /// fighting, so swapping the roster needs no further scene edits.
        void AdoptCharacterIdentity()
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
        void BeginWalkInPhase()
        {
            if (_arena != null)
            {
                // SetPlatformHalfWidth clamps to groundWidth * 0.5, so widening the
                // mat past its configured span needs groundWidth raised first or the
                // request is silently capped and the fighters spawn off the edge.
                _savedGroundWidth = _arena.groundWidth;
                _arena.groundWidth = Mathf.Max(_savedGroundWidth, walkInHalfWidth * 2f + 0.8f);
                _arena.SetPlatformHalfWidth(walkInHalfWidth);
            }
            _parkedA = _parkedB = false;
            float centre = transform.position.x;

            // Order matters. Swap the brain FIRST, then EndEpisode so
            // OnEpisodeBegin resets pose, contact sensors and action history under
            // the policy that is about to drive, and only then place the body.
            // Handing the walk brain a body still carrying the fight brain's
            // leftover state is what made the fighters collapse on the first stride.
            wrestlerA.BeginWalkIn(centre - neutralGapHalf);
            wrestlerB.BeginWalkIn(centre + neutralGapHalf);
            wrestlerA.EndEpisode();
            wrestlerB.EndEpisode();

            PoseForWalkIn(wrestlerA, -walkInStartGapHalf);
            PoseForWalkIn(wrestlerB, +walkInStartGapHalf);
            wrestlerA.actionScale = 1f;
            wrestlerB.actionScale = 1f;
            _walkInLeft = walkInTimeout;
        }

        /// Places a fighter at the far end, upright and simulating, ready to walk.
        /// Unlike PoseNeutral this leaves physics and motors ON — the walk brain
        /// needs a live body to drive.
        void PoseForWalkIn(Agent_Biped w, float offsetX)
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

        /// Parks each fighter the instant IT reaches its own mark, and reports when
        /// both are parked.
        ///
        /// The walk brain does not stop: it is trained to reach a target and have
        /// the episode end there, so with episode control suppressed it strides
        /// straight past. Waiting for both to be near their marks at the same
        /// moment therefore never fires — the first arrival has already overshot
        /// by the time the second gets there.
        bool TickWalkInArrivals()
        {
            float centre = transform.position.x;
            ParkIfArrived(wrestlerA, centre - neutralGapHalf, ref _parkedA);
            ParkIfArrived(wrestlerB, centre + neutralGapHalf, ref _parkedB);
            return _parkedA && _parkedB;
        }

        void ParkIfArrived(Agent_Biped w, float mark, ref bool parked)
        {
            if (parked || w == null) return;
            // Park on arrival OR on overshoot — a fighter that has passed its mark
            // is only getting further away.
            float toMark = (mark - w.TorsoX) * (w.TorsoX < mark ? 1f : -1f);
            bool reached = Mathf.Abs(w.TorsoX - mark) <= walkInArriveTolerance;
            bool overshot = (w == wrestlerA && w.TorsoX > mark) || (w == wrestlerB && w.TorsoX < mark);
            if (!reached && !overshot) return;

            w.EndWalkIn();                       // hand the body back off the walk brain
            PoseNeutral(w, mark - transform.position.x);   // snap to the mark, physics off
            parked = true;
        }

        /// Hands both fighters back to their fight brains and shrinks the mat to
        /// the real ring before the countdown starts.
        /// Shrinks the intro mat back to the real fighting ring.
        void ContractMat()
        {
            if (_arena == null) return;
            _arena.SetPlatformHalfWidth(ringHalfWidth);
            if (_savedGroundWidth > 0f) _arena.groundWidth = _savedGroundWidth;
        }

        /// Hands both bodies from the locomotion brain back to the fight brain.
        void EndWalkInPhase()
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
        void HoldUpright()
        {
            PoseNeutral(wrestlerA, -neutralGapHalf);
            PoseNeutral(wrestlerB, +neutralGapHalf);
        }

        void PoseNeutral(Agent_Biped w, float offsetX)
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
        void BeginSimulation()
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
        void TickActionRamp()
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

        static void Unfreeze(Agent_Biped w)
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
        void SpawnCompanionSystems()
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
        }

        void SpawnFaceMood(Agent_Biped fighter)
        {
            if (fighter == null) return;
            var body = fighter.GetComponent<Agent_BipedBody>();
            if (body == null || body.character == null || body.character.headSprite == null) return;
            var go = new GameObject($"FaceMood_{fighter.behaviorName}");
            go.transform.SetParent(transform, false);
            var mood = go.AddComponent<Systems_FaceMood>();
            mood.fighterBehaviorName = fighter.behaviorName;
        }

        static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        void BuildUi()
        {
            var doc = gameObject.AddComponent<UIDocument>();
            if (panelSettings != null) doc.panelSettings = panelSettings;
            var root = doc.rootVisualElement;
            root.style.flexGrow = 1;
            Systems_SafeArea.Attach(transform, root);

            _overlay = new VisualElement();
            _overlay.style.position = Position.Absolute;
            _overlay.style.top = 0; _overlay.style.bottom = 0;
            _overlay.style.left = 0; _overlay.style.right = 0;
            _overlay.style.backgroundColor = new Color(0f, 0f, 0f, 0.7f);
            _overlay.style.display = DisplayStyle.None;
            root.Add(_overlay);

            // Score + clock live in the dead band between the dohyo and the
            // stat panels — never over the wrestlers, clear of any notch.
            var scoreBlock = new VisualElement();
            scoreBlock.style.position = Position.Absolute;
            scoreBlock.style.bottom = 480;
            scoreBlock.style.left = 0;
            scoreBlock.style.right = 0;
            scoreBlock.style.alignItems = Align.Center;
            root.Add(scoreBlock);
            _scoreBlock = scoreBlock;

            var scoreCard = new VisualElement();
            scoreCard.style.backgroundColor = new Color(0f, 0f, 0f, 0.45f);
            scoreCard.style.borderTopLeftRadius = 12;
            scoreCard.style.borderTopRightRadius = 12;
            scoreCard.style.borderBottomLeftRadius = 12;
            scoreCard.style.borderBottomRightRadius = 12;
            scoreCard.style.paddingLeft = 22; scoreCard.style.paddingRight = 22;
            scoreCard.style.paddingTop = 6; scoreCard.style.paddingBottom = 8;
            scoreCard.style.alignItems = Align.Center;
            scoreBlock.Add(scoreCard);

            _scoreLabel = new Label();
            _scoreLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _scoreLabel.style.fontSize = 44;
            _scoreLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _scoreLabel.style.color = _scoreBaseColor;
            _scoreLabel.style.textShadow = OutlineShadow;
            scoreCard.Add(_scoreLabel);

            _clock = new Label();
            _clock.style.unityTextAlign = TextAnchor.MiddleCenter;
            _clock.style.fontSize = 26;
            _clock.style.color = new Color(0.8f, 0.76f, 0.68f);
            _clock.style.textShadow = OutlineShadow;
            scoreCard.Add(_clock);

            // Round callout rides the dohyo floor band so it never covers a fighter.
            _banner = new Label();
            _banner.style.position = Position.Absolute;
            _banner.style.top = Length.Percent(49);
            _banner.style.left = 0;
            _banner.style.right = 0;
            _banner.style.unityTextAlign = TextAnchor.MiddleCenter;
            _banner.style.fontSize = 56;
            _banner.style.unityFontStyleAndWeight = FontStyle.Bold;
            _banner.style.color = new Color(1f, 0.85f, 0.3f);
            _banner.style.textShadow = OutlineShadow;
            _banner.style.display = DisplayStyle.None;
            root.Add(_banner);

            // Round-opening countdown: one huge digit over the ring center.
            _countdown = new Label();
            _countdown.style.position = Position.Absolute;
            _countdown.style.top = Length.Percent(36);
            _countdown.style.left = 0;
            _countdown.style.right = 0;
            _countdown.style.unityTextAlign = TextAnchor.MiddleCenter;
            _countdown.style.fontSize = 120;
            _countdown.style.unityFontStyleAndWeight = FontStyle.Bold;
            _countdown.style.color = new Color(1f, 0.85f, 0.3f);
            _countdown.style.textShadow = OutlineShadow;
            _countdown.style.display = DisplayStyle.None;
            root.Add(_countdown);

            // Match-over: one centered results card owns the moment.
            _resultCard = new VisualElement();
            _resultCard.style.position = Position.Absolute;
            _resultCard.style.top = Length.Percent(32);
            _resultCard.style.left = Length.Percent(12);
            _resultCard.style.right = Length.Percent(12);
            _resultCard.style.backgroundColor = new Color(0.05f, 0.045f, 0.06f, 0.94f);
            _resultCard.style.borderTopWidth = 5;
            _resultCard.style.borderTopLeftRadius = 14;
            _resultCard.style.borderTopRightRadius = 14;
            _resultCard.style.borderBottomLeftRadius = 14;
            _resultCard.style.borderBottomRightRadius = 14;
            _resultCard.style.paddingLeft = 24; _resultCard.style.paddingRight = 24;
            _resultCard.style.paddingTop = 22; _resultCard.style.paddingBottom = 22;
            _resultCard.style.alignItems = Align.Stretch;
            _resultCard.style.display = DisplayStyle.None;
            root.Add(_resultCard);

            _resultTitle = new Label();
            _resultTitle.style.unityTextAlign = TextAnchor.MiddleCenter;
            _resultTitle.style.fontSize = 46;
            _resultTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            _resultTitle.style.color = new Color(1f, 0.85f, 0.3f);
            _resultCard.Add(_resultTitle);

            _resultScore = new Label();
            _resultScore.style.unityTextAlign = TextAnchor.MiddleCenter;
            _resultScore.style.fontSize = 28;
            _resultScore.style.color = new Color(0.8f, 0.77f, 0.72f);
            _resultScore.style.marginTop = 4;
            _resultCard.Add(_resultScore);

            BuildPauseUi(root);

            // Big touch-friendly rematch button (mobile) — Space still works too.
            _rematchBtn = new Button(ResetMatch) { text = "REMATCH" };
            _rematchBtn.style.height = 72;
            _rematchBtn.style.marginTop = 18;
            _rematchBtn.style.fontSize = 32;
            _rematchBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            _rematchBtn.style.color = new Color(0.08f, 0.06f, 0.05f);
            _rematchBtn.style.backgroundColor = new Color(1f, 0.85f, 0.3f);
            _rematchBtn.style.borderTopLeftRadius = 12;
            _rematchBtn.style.borderTopRightRadius = 12;
            _rematchBtn.style.borderBottomLeftRadius = 12;
            _rematchBtn.style.borderBottomRightRadius = 12;
            _resultCard.Add(_rematchBtn);
        }

        /// Pause / quit. Before this the only exit from a match was playing it to
        /// the end — on Android the hardware back button did nothing at all.
        void BuildPauseUi(VisualElement root)
        {
            var pauseBtn = new Button(TogglePause) { text = "❚❚" };
            pauseBtn.style.position = Position.Absolute;
            pauseBtn.style.top = 10;
            pauseBtn.style.left = 10;
            pauseBtn.style.width = 60;      // >=44pt: comfortable thumb target
            pauseBtn.style.height = 44;
            pauseBtn.style.fontSize = 18;
            pauseBtn.style.color = new Color(0.95f, 0.93f, 0.85f);
            pauseBtn.style.backgroundColor = new Color(0.12f, 0.11f, 0.13f, 0.75f);
            Round(pauseBtn, 8);
            root.Add(pauseBtn);

            _pauseCard = new VisualElement();
            _pauseCard.style.position = Position.Absolute;
            _pauseCard.style.top = Length.Percent(34);
            _pauseCard.style.left = Length.Percent(14);
            _pauseCard.style.right = Length.Percent(14);
            _pauseCard.style.backgroundColor = new Color(0.05f, 0.045f, 0.06f, 0.96f);
            _pauseCard.style.paddingLeft = 22; _pauseCard.style.paddingRight = 22;
            _pauseCard.style.paddingTop = 20; _pauseCard.style.paddingBottom = 20;
            Round(_pauseCard, 14);
            _pauseCard.style.display = DisplayStyle.None;
            root.Add(_pauseCard);

            var title = new Label("PAUSED");
            title.style.unityTextAlign = TextAnchor.MiddleCenter;
            title.style.fontSize = 40;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = new Color(1f, 0.85f, 0.3f);
            _pauseCard.Add(title);

            var resume = new Button(TogglePause) { text = "RESUME" };
            StyleMenuButton(resume, new Color(1f, 0.85f, 0.3f), new Color(0.08f, 0.06f, 0.05f));
            _pauseCard.Add(resume);

            var quit = new Button(QuitToBracket) { text = "QUIT MATCH" };
            StyleMenuButton(quit, new Color(0.22f, 0.2f, 0.21f), new Color(0.85f, 0.82f, 0.78f));
            _pauseCard.Add(quit);
        }

        static void StyleMenuButton(Button b, Color background, Color text)
        {
            b.style.height = 64;
            b.style.marginTop = 14;
            b.style.fontSize = 26;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            b.style.color = text;
            b.style.backgroundColor = background;
            Round(b, 12);
        }

        static void Round(VisualElement e, int radius)
        {
            e.style.borderTopLeftRadius = radius;
            e.style.borderTopRightRadius = radius;
            e.style.borderBottomLeftRadius = radius;
            e.style.borderBottomRightRadius = radius;
        }

        void TogglePause()
        {
            if (_phase == Phase.MatchOver) return;   // result card owns that moment
            _paused = !_paused;
            _pauseCard.style.display = _paused ? DisplayStyle.Flex : DisplayStyle.None;
            // Restore to 1 rather than to the pre-pause value: presentation slow-mo
            // ends on a realtime timer that keeps running while paused, so the saved
            // value would be stale.
            Time.timeScale = _paused ? 0f : 1f;
        }

        void QuitToBracket()
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

        void UpdateScoreboard(bool flash)
        {
            if (_scoreLabel == null) return;
            _scoreLabel.text =
                $"<color=#{Hex(colorA)}>{nameA}</color>  {_scoreA} : {_scoreB}  <color=#{Hex(colorB)}>{nameB}</color>";
            if (flash)
            {
                _scoreLabel.style.fontSize = 54;
                _scoreLabel.schedule.Execute(() => _scoreLabel.style.fontSize = 44).StartingIn(400);
            }
        }

        void StartCountdown()
        {
            _countdownLeft = countdownSeconds;
            _lastCountdownDigit = -1;
        }

        /// 3-2-1 over the walk-in: the camera punches in on one fighter's head
        /// per digit (A on the first, B on the second), then releases back to
        /// the wide two-shot on the final digit so the engage reads clearly.
        void TickCountdown()
        {
            _countdownLeft -= Time.fixedDeltaTime;
            if (_countdownLeft <= 0f)
            {
                _countdown.text = "FIGHT!";
                _countdown.schedule.Execute(HideCountdown).StartingIn(600);
                return;
            }

            int digit = Mathf.CeilToInt(_countdownLeft);
            if (digit == _lastCountdownDigit) return;
            _lastCountdownDigit = digit;
            _countdown.style.display = DisplayStyle.Flex;
            _countdown.text = digit.ToString();

            if (digit == 1) return; // let the last punch-in expire — wide for the engage
            var fighter = ((countdownSeconds - digit) & 1) == 0 ? wrestlerA : wrestlerB;
            PunchOnHead(fighter);
        }

        void PunchOnHead(Agent_Biped fighter)
        {
            if (_camFollow == null || fighter == null) return;
            var body = fighter.GetComponent<Agent_BipedBody>();
            Transform focus = body != null && body.HeadRenderer != null
                ? body.HeadRenderer.transform
                : fighter.Torso.transform;
            _camFollow.PunchIn(focus, countdownHeadOrtho, 1.05f);
        }

        void HideCountdown()
        {
            _countdown.style.display = DisplayStyle.None;
        }

        void UpdateClock()
        {
            int remaining = Mathf.Max(0, Mathf.CeilToInt(roundTimeoutSeconds - _elapsed));
            if (remaining != _lastShownSeconds)
            {
                _lastShownSeconds = remaining;
                _clock.text = remaining.ToString();
                _clock.style.color = remaining <= 5
                    ? new Color(1f, 0.4f, 0.3f)
                    : new Color(0.8f, 0.76f, 0.68f);
            }
        }

        void Update()
        {
            if (_phase == Phase.MatchOver && SpacePressed()) { ResetMatch(); return; }
            // Android maps its hardware back button onto escapeKey, so this is the
            // system-back handler as well as the desktop shortcut. Update still runs
            // at timeScale 0, so it can also un-pause.
            if (BackPressed()) TogglePause();
        }

        bool BackPressed()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
#else
            return false;
#endif
        }

        bool SpacePressed()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Space);
#endif
        }

        void FixedUpdate()
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
                        _banner.style.display = DisplayStyle.None;
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
                        if (enableWalkIn && WalkBrainsReady())
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
                    // Locomotion brains drive both fighters to their marks. No
                    // scoring, no clock — Agent_Biped.suppressEpisodeControl keeps
                    // the walk policy from ending episodes while it owns the body.
                    _walkInLeft -= Time.fixedDeltaTime;
                    if (TickWalkInArrivals() || _walkInLeft <= 0f)
                    {
                        // Contract the mat, but leave the LOCOMOTION brain driving
                        // through the countdown. It actively balances, so the
                        // fighters stand on their marks under their own control
                        // instead of being frozen — and the fight brain then
                        // inherits a settled, upright, already-simulating body
                        // rather than one that snaps from kinematic to dynamic and
                        // flops. BeginSimulation performs the handoff at FIGHT!.
                        // Anyone still walking when the timeout hit gets parked too,
                        // so the countdown always starts from both marks.
                        HoldUpright();
                        EndWalkInPhase();
                        ContractMat();
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

            _downA = wrestlerA.IsDown ? _downA + Time.fixedDeltaTime : 0f;
            _downB = wrestlerB.IsDown ? _downB + Time.fixedDeltaTime : 0f;

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
        void DecideOnTimeout()
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
        static void GoLimp(Agent_Biped fighter)
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
        bool OutOfRing(Agent_Biped w, Agent_BipedBody body)
        {
            if (body != null)
            {
                float matTop = transform.position.y;
                float limit = matTop + footOffMatY;
                if (body.FootNear != null && body.FootNear.position.y < limit) return true;
                if (body.FootFar != null && body.FootFar.position.y < limit) return true;
            }
            return w.Torso.position.y < fallY;
        }

        void EndRound(Agent_Biped roundWinner, string drawText, string winText = null)
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
                _banner.style.display = DisplayStyle.None;
                _resultTitle.text = $"{WrapName(aWon ? nameA : nameB, aWon ? colorA : colorB)} WINS";
                _resultScore.text = $"{Mathf.Max(_scoreA, _scoreB)} — {Mathf.Min(_scoreA, _scoreB)}";
                _resultCard.style.borderTopColor = aWon ? colorA : colorB;
                ShowAfter(_resultCard, announceDelayMs);
                ShowAfter(_overlay, announceDelayMs);
                // The running scoreboard would otherwise sit under the result
                // card competing with it; the card already states the score.
                HideAfter(_scoreBlock, announceDelayMs);
                MatchEnded?.Invoke(aWon ? wrestlerA : wrestlerB);
                return;
            }

            _banner.text = roundWinner != null
                ? (winText ?? $"{WrapName(roundWinner == wrestlerA ? nameA : nameB, roundWinner == wrestlerA ? colorA : colorB)} SCORES!")
                : drawText;
            ShowAfter(_banner, announceDelayMs);

            _phase = Phase.RoundEnded;
            // Extend the pause by the announce delay so the result stays on
            // screen just as long as it did before.
            _phaseLeft = betweenRoundsPause + announceDelayMs / 1000f;
        }

        static bool IsLimp(Agent_Biped fighter)
        {
            var body = fighter.GetComponent<Agent_BipedBody>();
            return body != null && body.IsLimp;
        }

        static void HideAfter(VisualElement element, long delayMs)
        {
            if (element == null) return;
            if (delayMs <= 0L) { element.style.display = DisplayStyle.None; return; }
            element.schedule.Execute(() => element.style.display = DisplayStyle.None)
                   .StartingIn(delayMs);
        }

        /// Reveal a UI element now, or after a delay so the ragdoll lands first.
        static void ShowAfter(VisualElement element, long delayMs)
        {
            if (delayMs <= 0L)
            {
                element.style.display = DisplayStyle.Flex;
                return;
            }
            element.style.display = DisplayStyle.None;
            element.schedule.Execute(() => element.style.display = DisplayStyle.Flex)
                   .StartingIn(delayMs);
        }

        static string WrapName(string n, Color c) => $"<color=#{Hex(c)}>{n}</color>";

        void ResetMatch()
        {
            _scoreA = _scoreB = 0;
            MatchReset?.Invoke();
            UpdateScoreboard(false);
            _overlay.style.display = DisplayStyle.None;
            _banner.style.display = DisplayStyle.None;
            _resultCard.style.display = DisplayStyle.None;
            if (_scoreBlock != null) _scoreBlock.style.display = DisplayStyle.Flex;
            wrestlerA.EndEpisode();
            wrestlerB.EndEpisode();
            HoldUpright();
            _downA = _downB = 0f;
            _phase = Phase.Grace;
            _phaseLeft = graceSeconds;
        }
    }
}
