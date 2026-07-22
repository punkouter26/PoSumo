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
        enum Phase { Fighting, RoundEnded, Grace, MatchOver }

        public Agent_Biped wrestlerA;           // teamId 0
        public Agent_Biped wrestlerB;           // teamId 1
        public string nameA = "MATT";
        public string nameB = "DAVE";
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
        public float stallBreakStart = 12f;
        public float stallShrinkRate = 0.15f;
        public float minRingHalfWidth = 0.6f;
        public PanelSettings panelSettings;
        public Systems_GameTuning tuning;
        [Tooltip("Spawn Systems_MatchPresentation (slow-mo, punch-in, replay) at startup.")]
        public bool enablePresentation = true;
        [Tooltip("Spawn Systems_MatchAudio (impacts, crowd, gong) at startup.")]
        public bool enableAudio = true;
        [Tooltip("Spawn Systems_FaceMood (Matt's expression follows dominance) at startup.")]
        public bool enableFaceMood = true;

        int _scoreA, _scoreB;
        float _elapsed, _phaseLeft, _downA, _downB;
        int _lastShownSeconds = -1;
        Phase _phase = Phase.Fighting;
        Systems_SumoArena _arena;

        // Read-only stats for HUDs.
        public int ScoreA => _scoreA;
        public int ScoreB => _scoreB;
        public int MatchWinsA { get; private set; }
        public int MatchWinsB { get; private set; }
        public float RoundElapsed => _elapsed;
        public float LongestRound { get; private set; }
        public bool RoundActive => _phase == Phase.Fighting;
        /// Current stall-shrunk scoring boundary (equals ringHalfWidth early in a round).
        public float EffectiveRingHalfWidth { get; private set; }

        /// Fired when a round is decided: (roundWinner, roundLoser) — both null on a draw.
        public event System.Action<Agent_Biped, Agent_Biped> RoundEnded;
        /// Fired when scoring resumes for a fresh round.
        public event System.Action RoundStarted;
        /// Fired once when the match is decided, with the match winner.
        public event System.Action<Agent_Biped> MatchEnded;
        /// Fired when a rematch resets the scores (HUD aggregates restart here).
        public event System.Action MatchReset;
        Label _scoreLabel, _banner, _clock;
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
                pointsToWin = tuning.pointsToWin;
                roundTimeoutSeconds = tuning.roundTimeoutSeconds;
                betweenRoundsPause = tuning.betweenRoundsPause;
                graceSeconds = tuning.graceSeconds;
                downGraceSeconds = tuning.downGraceSeconds;
                stallBreakStart = tuning.stallBreakStart;
                stallShrinkRate = tuning.stallShrinkRate;
                minRingHalfWidth = tuning.minRingHalfWidth;
            }

            if (wrestlerA == null || wrestlerB == null)
            {
                foreach (var a in FindObjectsByType<Agent_Biped>(FindObjectsSortMode.None))
                {
                    if (a.teamId == 0) wrestlerA = a; else wrestlerB = a;
                }
            }
            wrestlerA.opponent = wrestlerB;
            wrestlerB.opponent = wrestlerA;
            wrestlerA.ringHalfWidth = ringHalfWidth;
            wrestlerB.ringHalfWidth = ringHalfWidth;
            wrestlerA.arenaCenterX = transform.position.x;
            wrestlerB.arenaCenterX = transform.position.x;
            _arena = FindAnyObjectByType<Systems_SumoArena>();
            EffectiveRingHalfWidth = ringHalfWidth;
            BuildUi();
            UpdateScoreboard(false);
            SpawnCompanionSystems();
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
            if (enableFaceMood && FindAnyObjectByType<Systems_FaceMood>() == null)
            {
                var go = new GameObject("FaceMood");
                go.transform.SetParent(transform, false);
                go.AddComponent<Systems_FaceMood>();
            }
        }

        static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        void BuildUi()
        {
            var doc = gameObject.AddComponent<UIDocument>();
            if (panelSettings != null) doc.panelSettings = panelSettings;
            var root = doc.rootVisualElement;
            root.style.flexGrow = 1;

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

        void UpdateClock()
        {
            int remaining = Mathf.Max(0, Mathf.CeilToInt(roundTimeoutSeconds - _elapsed));
            if (remaining != _lastShownSeconds)
            {
                bool shrinking = _elapsed > stallBreakStart;
                _lastShownSeconds = remaining;
                _clock.text = shrinking ? $"{remaining} · RING CLOSING" : remaining.ToString();
                _clock.style.color = remaining <= 5
                    ? new Color(1f, 0.4f, 0.3f)
                    : shrinking
                        ? new Color(1f, 0.62f, 0.25f)
                        : new Color(0.8f, 0.76f, 0.68f);
            }
        }

        void Update()
        {
            if (_phase == Phase.MatchOver && SpacePressed()) ResetMatch();
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
                        wrestlerA.actionsEnabled = true;
                        wrestlerB.actionsEnabled = true;
                        _downA = _downB = 0f;
                        _banner.style.display = DisplayStyle.None;
                        _phase = Phase.Grace;
                        _phaseLeft = graceSeconds;
                    }
                    return;

                case Phase.Grace:
                    // Freshly reset; give physics a beat before scoring resumes.
                    _phaseLeft -= Time.fixedDeltaTime;
                    if (_phaseLeft <= 0f)
                    {
                        _phase = Phase.Fighting;
                        _elapsed = 0f;
                        _downA = _downB = 0f;
                        EffectiveRingHalfWidth = ringHalfWidth;
                        if (_arena != null) _arena.SetPlatformHalfWidth(ringHalfWidth);
                        RoundStarted?.Invoke();
                    }
                    return;
            }

            // Phase.Fighting
            _elapsed += Time.fixedDeltaTime;
            UpdateClock();

            _downA = wrestlerA.IsDown ? _downA + Time.fixedDeltaTime : 0f;
            _downB = wrestlerB.IsDown ? _downB + Time.fixedDeltaTime : 0f;

            // Stall breaker: past stallBreakStart, the dohyo physically closes
            // in — the ground vanishes under cagey wrestlers until someone
            // drops off the edge.
            float effectiveRing = Mathf.Max(minRingHalfWidth,
                ringHalfWidth - Mathf.Max(0f, _elapsed - stallBreakStart) * stallShrinkRate);
            EffectiveRingHalfWidth = effectiveRing;
            if (_arena != null) _arena.SetPlatformHalfWidth(effectiveRing);

            bool aOut = OutOfRing(wrestlerA) || (knockdownLoses && _downA >= downGraceSeconds);
            bool bOut = OutOfRing(wrestlerB) || (knockdownLoses && _downB >= downGraceSeconds);

            if (aOut && bOut) EndRound(null, "DOUBLE OUT — DRAW");
            else if (aOut) EndRound(wrestlerB, null);
            else if (bOut) EndRound(wrestlerA, null);
            else if (_elapsed >= roundTimeoutSeconds) EndRound(null, "TIME — DRAW");
        }

        /// Ring-out is physical: you lose only after actually falling off the
        /// dohyo (the shrinking platform removes the ground; gravity does the
        /// rest). No invisible scoring line.
        bool OutOfRing(Agent_Biped w)
        {
            return w.Torso.position.y < fallY;
        }

        void EndRound(Agent_Biped roundWinner, string drawText)
        {
            LongestRound = Mathf.Max(LongestRound, _elapsed);
            if (roundWinner == wrestlerA) _scoreA++;
            else if (roundWinner == wrestlerB) _scoreB++;
            UpdateScoreboard(roundWinner != null);

            // Clay dust where the loser went down / out.
            var loser = roundWinner == wrestlerA ? wrestlerB : roundWinner == wrestlerB ? wrestlerA : null;
            if (loser != null)
                Systems_DustPuff.Burst(loser.Torso.position);

            // Cut motors so the loser ragdolls off / down in full view.
            wrestlerA.actionsEnabled = false;
            wrestlerB.actionsEnabled = false;

            _clock.text = "";
            _lastShownSeconds = -1;

            RoundEnded?.Invoke(roundWinner, loser);

            if (_scoreA >= pointsToWin || _scoreB >= pointsToWin)
            {
                _phase = Phase.MatchOver;
                bool aWon = _scoreA > _scoreB;
                if (aWon) MatchWinsA++; else MatchWinsB++;
                _banner.style.display = DisplayStyle.None;
                _resultTitle.text = $"{WrapName(aWon ? nameA : nameB, aWon ? colorA : colorB)} WINS";
                _resultScore.text = $"{Mathf.Max(_scoreA, _scoreB)} — {Mathf.Min(_scoreA, _scoreB)}";
                _resultCard.style.borderTopColor = aWon ? colorA : colorB;
                _resultCard.style.display = DisplayStyle.Flex;
                _overlay.style.display = DisplayStyle.Flex;
                MatchEnded?.Invoke(aWon ? wrestlerA : wrestlerB);
                return;
            }

            _banner.text = roundWinner != null
                ? $"{WrapName(roundWinner == wrestlerA ? nameA : nameB, roundWinner == wrestlerA ? colorA : colorB)} SCORES!"
                : drawText;
            _banner.style.display = DisplayStyle.Flex;

            _phase = Phase.RoundEnded;
            _phaseLeft = betweenRoundsPause;
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
            wrestlerA.EndEpisode();
            wrestlerB.EndEpisode();
            wrestlerA.actionsEnabled = true;
            wrestlerB.actionsEnabled = true;
            _downA = _downB = 0f;
            _phase = Phase.Grace;
            _phaseLeft = graceSeconds;
        }
    }
}
