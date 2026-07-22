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
        [Tooltip("Spawn Systems_BrainView (policy overlay, toggle with B) at startup.")]
        public bool enableBrainView = true;
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
        VisualElement _overlay;
        Color _scoreBaseColor = new Color(0.95f, 0.93f, 0.85f);

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

        /// Presentation, audio and brain-view are runtime-spawned children so
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
            if (enableBrainView && FindAnyObjectByType<Systems_BrainView>() == null)
            {
                var go = new GameObject("BrainView");
                go.transform.SetParent(transform, false);
                go.AddComponent<Systems_BrainView>();
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
            _overlay.style.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
            _overlay.style.display = DisplayStyle.None;
            root.Add(_overlay);

            _scoreLabel = new Label();
            _scoreLabel.style.position = Position.Absolute;
            _scoreLabel.style.top = 18;
            _scoreLabel.style.left = 0;
            _scoreLabel.style.right = 0;
            _scoreLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _scoreLabel.style.fontSize = 44;
            _scoreLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _scoreLabel.style.color = _scoreBaseColor;
            root.Add(_scoreLabel);

            _clock = new Label();
            _clock.style.position = Position.Absolute;
            _clock.style.top = 74;
            _clock.style.left = 0;
            _clock.style.right = 0;
            _clock.style.unityTextAlign = TextAnchor.MiddleCenter;
            _clock.style.fontSize = 26;
            _clock.style.color = new Color(0.8f, 0.76f, 0.68f);
            root.Add(_clock);

            _banner = new Label();
            _banner.style.position = Position.Absolute;
            _banner.style.top = Length.Percent(15);
            _banner.style.left = 0;
            _banner.style.right = 0;
            _banner.style.unityTextAlign = TextAnchor.MiddleCenter;
            _banner.style.fontSize = 56;
            _banner.style.unityFontStyleAndWeight = FontStyle.Bold;
            _banner.style.color = new Color(1f, 0.85f, 0.3f);
            _banner.style.display = DisplayStyle.None;
            root.Add(_banner);

            // Big touch-friendly rematch button (mobile) — Space still works too.
            _rematchBtn = new Button(ResetMatch) { text = "REMATCH" };
            _rematchBtn.style.position = Position.Absolute;
            _rematchBtn.style.top = Length.Percent(48);
            _rematchBtn.style.left = Length.Percent(20);
            _rematchBtn.style.right = Length.Percent(20);
            _rematchBtn.style.height = 72;
            _rematchBtn.style.fontSize = 32;
            _rematchBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            _rematchBtn.style.color = new Color(0.08f, 0.06f, 0.05f);
            _rematchBtn.style.backgroundColor = new Color(1f, 0.85f, 0.3f);
            _rematchBtn.style.borderTopLeftRadius = 12;
            _rematchBtn.style.borderTopRightRadius = 12;
            _rematchBtn.style.borderBottomLeftRadius = 12;
            _rematchBtn.style.borderBottomRightRadius = 12;
            _rematchBtn.style.display = DisplayStyle.None;
            root.Add(_rematchBtn);
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
                _lastShownSeconds = remaining;
                _clock.text = remaining.ToString();
                _clock.style.color = remaining <= 5
                    ? new Color(1f, 0.4f, 0.3f)
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
                        if (_arena != null) _arena.SetVisualRing(_arena.ringHalfWidth);
                        RoundStarted?.Invoke();
                    }
                    return;
            }

            // Phase.Fighting
            _elapsed += Time.fixedDeltaTime;
            UpdateClock();

            _downA = wrestlerA.IsDown ? _downA + Time.fixedDeltaTime : 0f;
            _downB = wrestlerB.IsDown ? _downB + Time.fixedDeltaTime : 0f;

            // Stall breaker: past stallBreakStart, the ring closes in so cagey
            // rounds resolve by pressure instead of a timeout.
            float effectiveRing = Mathf.Max(minRingHalfWidth,
                ringHalfWidth - Mathf.Max(0f, _elapsed - stallBreakStart) * stallShrinkRate);
            EffectiveRingHalfWidth = effectiveRing;

            // The tawara track the shrink at the same ratio so it reads on screen.
            if (_arena != null && effectiveRing < ringHalfWidth)
                _arena.SetVisualRing(_arena.ringHalfWidth * (effectiveRing / ringHalfWidth));

            float cx = transform.position.x;
            bool aOut = OutOfRing(wrestlerA, cx, effectiveRing) || (knockdownLoses && _downA >= downGraceSeconds);
            bool bOut = OutOfRing(wrestlerB, cx, effectiveRing) || (knockdownLoses && _downB >= downGraceSeconds);

            if (aOut && bOut) EndRound(null, "DOUBLE OUT — DRAW");
            else if (aOut) EndRound(wrestlerB, null);
            else if (bOut) EndRound(wrestlerA, null);
            else if (_elapsed >= roundTimeoutSeconds) EndRound(null, "TIME — DRAW");
        }

        bool OutOfRing(Agent_Biped w, float centerX, float effectiveRing)
        {
            if (Mathf.Abs(w.TorsoX - centerX) > effectiveRing) return true;
            if (w.Torso.position.y < fallY) return true;
            return false;
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
                if (_scoreA > _scoreB) MatchWinsA++; else MatchWinsB++;
                string winner = _scoreA > _scoreB ? WrapName(nameA, colorA) : WrapName(nameB, colorB);
                _banner.text = $"{winner} WINS {Mathf.Max(_scoreA, _scoreB)}-{Mathf.Min(_scoreA, _scoreB)}";
                _banner.style.display = DisplayStyle.Flex;
                _overlay.style.display = DisplayStyle.Flex;
                _rematchBtn.style.display = DisplayStyle.Flex;
                MatchEnded?.Invoke(_scoreA > _scoreB ? wrestlerA : wrestlerB);
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
            _rematchBtn.style.display = DisplayStyle.None;
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
