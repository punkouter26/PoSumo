using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// The broadcast win-probability meter, and the tension number the rest of
    /// the presentation layer reads.
    ///
    /// A logistic blend over the signals the fight is ALREADY producing — none
    /// of them are new measurements, and nothing here touches physics:
    ///
    ///   dominance  (Systems_FightHud.DominanceA/B, the territory/KD/push blend)
    ///   stamina    (Agent_BipedBody.Stamina, the fatigue model's whole-body read)
    ///   mat behind (ring half-width minus the fighter's distance from centre)
    ///   Elo prior  (Systems_CareerStats, the game's ladder — not training ELO)
    ///
    /// ...smoothed with an EMA so a single shove does not swing the bar, and
    /// sampled at 10 Hz. `WinProbA`/`WinProbB` are public because the caster and
    /// the director camera consume them the way other companions consume
    /// `Systems_FightHud.DominanceA` — a spawned sibling looked up once in Start.
    ///
    /// Read-only with respect to the fight: it decides nothing, is not mirrored
    /// into Systems_SumoMatchManager, and touches no observation, mass or
    /// collider, so no brain is affected.
    public sealed class Systems_TensionEngine : MonoBehaviour
    {
        /// Sample cadence. Fast enough that a swing reads as motion, slow enough
        /// to be nowhere near a per-frame cost.
        private const float SAMPLE_INTERVAL = 0.1f;

        /// EMA time constant, seconds. ~1.2 s is a half-life of about 0.8 s: a
        /// real shift lands inside two seconds, contact noise does not.
        private const float SMOOTH_TAU = 1.2f;

        /// Term weights. Dominance leads because it is the slow, cumulative
        /// read; the edge term is nearly as strong because the mat decides most
        /// rounds here — see the 17-round measurement in CLAUDE.md.
        private const float W_DOMINANCE = 1.6f;
        private const float W_STAMINA = 0.9f;
        private const float W_EDGE = 1.1f;
        private const float W_ELO = 0.6f;
        private const float LOGISTIC_GAIN = 2.2f;

        /// The bar is only repainted when a displayed PERCENT changes.
        private int _shownPercentA = int.MinValue;
        private int _shownPercentB = int.MinValue;

        private Systems_GameMatchManager _manager;
        private Systems_FightHud _hud;
        private Agent_BipedBody _bodyA, _bodyB;
        private string _behaviourA, _behaviourB;

        /// 0..1 — A's probability of taking the MATCH from this position.
        public float WinProbA { get; private set; } = 0.5f;
        public float WinProbB => 1f - WinProbA;

        /// 0 = one side is gone, 1 = a coin-flip. The director camera's "is this
        /// worth a drama shot" input.
        public float Tension01 => 1f - Mathf.Abs(WinProbA - 0.5f) * 2f;

        private Systems_HudRoot _hudRoot;
        private Label _labelA, _labelB;
        private VisualElement _fillA, _fillB;
        private float _sampleLeft;
        private float _eloA = 1000f, _eloB = 1000f;
        private bool _subscribed;

        private void Awake()
        {
            _manager = GetComponentInParent<Systems_GameMatchManager>();
        }

        private void OnEnable()
        {
            Subscribe();
        }

        private void Start()
        {
            if (_manager == null) _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            // The dominance feed is looked up once, the way Systems_CrowdMomentum
            // finds the fight HUD — it is scene furniture, not a per-round object.
            _hud = FindAnyObjectByType<Systems_FightHud>();
            BuildUi();
            Subscribe();
            RefreshPriors();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void Subscribe()
        {
            if (_manager == null || _subscribed) return;
            _manager.RoundStarted += OnRoundStarted;
            _manager.MatchReset += OnMatchReset;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (_manager == null || !_subscribed) return;
            _manager.RoundStarted -= OnRoundStarted;
            _manager.MatchReset -= OnMatchReset;
            _subscribed = false;
        }

        private void OnRoundStarted()
        {
            // Elo moves on match results, so the prior is re-read at every round
            // boundary rather than cached for the component's life.
            RefreshPriors();
            ResolveBodies();
        }

        private void OnMatchReset()
        {
            WinProbA = 0.5f;
            RefreshPriors();
            ResolveBodies();
            Repaint(true);
        }

        private void RefreshPriors()
        {
            _behaviourA = BehaviourName(_manager != null ? _manager.wrestlerA : null);
            _behaviourB = BehaviourName(_manager != null ? _manager.wrestlerB : null);
            _eloA = _behaviourA != null ? Systems_CareerStats.Get(_behaviourA).elo : 1000f;
            _eloB = _behaviourB != null ? Systems_CareerStats.Get(_behaviourB).elo : 1000f;
        }

        private void ResolveBodies()
        {
            if (_manager == null) return;
            _bodyA = _manager.wrestlerA != null
                ? _manager.wrestlerA.GetComponent<Agent_BipedBody>() : null;
            _bodyB = _manager.wrestlerB != null
                ? _manager.wrestlerB.GetComponent<Agent_BipedBody>() : null;
        }

        private void Update()
        {
            if (_manager == null || _fillA == null) return;

            // Frozen between rounds — the bar holds the last honest reading
            // while the referee shows its result cards.
            if (!(_manager.RoundActive && _manager.ScoringLive)) return;

            if (_bodyA == null || _bodyB == null)
            {
                ResolveBodies();
                if (_bodyA == null || _bodyB == null) return;
            }

            _sampleLeft -= Time.unscaledDeltaTime;
            if (_sampleLeft > 0f) return;
            _sampleLeft = SAMPLE_INTERVAL;

            Step();
        }

        private void Step()
        {
            float x = 0f;

            if (_hud != null)
            {
                x += W_DOMINANCE * (_hud.DominanceA - _hud.DominanceB) / 100f;
            }

            x += W_STAMINA * (Mathf.Clamp01(_bodyA.Stamina) - Mathf.Clamp01(_bodyB.Stamina));

            float ring = Mathf.Max(0.1f, _manager.CurrentRingHalfWidth);
            float centreX = _manager.transform.position.x;
            float edgeA = ring - Mathf.Abs(_bodyA.Torso.position.x - centreX);
            float edgeB = ring - Mathf.Abs(_bodyB.Torso.position.x - centreX);
            x += W_EDGE * (edgeA - edgeB) / ring;

            x += W_ELO * (_eloA - _eloB) / 400f;

            float target = 1f / (1f + Mathf.Exp(-LOGISTIC_GAIN * x));

            // EMA on the sample cadence. exp() per sample is nothing at 10 Hz.
            float blend = 1f - Mathf.Exp(-SAMPLE_INTERVAL / SMOOTH_TAU);
            WinProbA = Mathf.Lerp(WinProbA, target, blend);

            // Clamp inside the band where the number still means something: no
            // model this simple earns 100%, and a pinned bar stops being drama.
            WinProbA = Mathf.Clamp(WinProbA, 0.03f, 0.97f);
            Repaint(false);
        }

        // ---- HUD ------------------------------------------------------------

        private void BuildUi()
        {
            PanelSettings settings = _manager != null ? _manager.panelSettings : null;
            _hudRoot = Systems_HudRoot.Ensure(transform, settings);
            if (_hudRoot == null || _hudRoot.Dock == null || _manager == null) return;

            VisualElement card = Systems_UiKit.ElevatedCard(Systems_UiKit.Elevation.Base).NoPick();
            card.Pad(Systems_UiKit.SPACE_3, Systems_UiKit.SPACE_1);
            card.style.marginBottom = Systems_UiKit.SPACE_1;

            _labelA = Systems_UiKit.Text("50", Systems_UiKit.FONT_SMALL, _manager.colorA, true);
            _labelA.style.unityTextAlign = TextAnchor.MiddleRight;
            _labelB = Systems_UiKit.Text("50", Systems_UiKit.FONT_SMALL, _manager.colorB, true);
            _labelB.style.unityTextAlign = TextAnchor.MiddleLeft;

            VisualElement track = Systems_UiKit.Row();
            track.style.flexGrow = 1;
            track.style.height = 6;
            track.style.marginLeft = Systems_UiKit.SPACE_2;
            track.style.marginRight = Systems_UiKit.SPACE_2;
            track.style.backgroundColor = Systems_UiKit.Track;
            track.style.overflow = Overflow.Hidden;
            track.Round(3);

            _fillA = HalfFill(track, Justify.FlexEnd, _manager.colorA);
            _fillB = HalfFill(track, Justify.FlexStart, _manager.colorB);

            VisualElement row = Systems_UiKit.Row();
            row.Add(_labelA);
            row.Add(track);
            row.Add(_labelB);
            card.Add(row);

            card.NoPickTree();
            _hudRoot.Dock.Add(card);
        }

        /// Outward-from-centre tug fills, exactly the FightHud grammar: each
        /// side owns half the track and grows toward the other.
        private static VisualElement HalfFill(VisualElement track, Justify grow, Color colour)
        {
            VisualElement half = Systems_UiKit.Row();
            half.style.width = Length.Percent(50);
            half.style.height = Length.Percent(100);
            half.style.justifyContent = grow;

            var fill = new VisualElement();
            fill.style.height = Length.Percent(100);
            fill.style.width = Length.Percent(50);
            fill.style.backgroundColor = colour;
            half.Add(fill);
            track.Add(half);
            return fill;
        }

        private void Repaint(bool force)
        {
            if (_fillA == null) return;

            float shareA = Mathf.Clamp01(WinProbA);
            _fillA.style.width = Length.Percent(shareA * 100f);
            _fillB.style.width = Length.Percent((1f - shareA) * 100f);

            int percentA = Mathf.RoundToInt(shareA * 100f);
            int percentB = 100 - percentA;
            if (force || percentA != _shownPercentA)
            {
                _shownPercentA = percentA;
                _labelA.text = percentA.ToString();
            }
            if (force || percentB != _shownPercentB)
            {
                _shownPercentB = percentB;
                _labelB.text = percentB.ToString();
            }
        }

        /// Null for the heuristic bot, matching Systems_CareerRecorder: a bot
        /// bout is unrated, so it gets the neutral prior rather than a record
        /// invented for it under a key nothing else will ever read.
        private static string BehaviourName(Agent_Biped fighter)
        {
            if (fighter == null || fighter.character == null || fighter.character.useBot)
            {
                return null;
            }
            return fighter.character.behaviorName;
        }
    }
}
