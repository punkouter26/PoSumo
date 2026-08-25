using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// Announces the winning technique after every round — the gyoji's call.
    ///
    /// Spawned by `Systems_GameMatchManager` behind `enableKimarite`; never placed
    /// in a scene. Subscribes to `RoundEnded` and reads the manager it was spawned
    /// by (allowed) rather than requiring a fifth match event (discouraged — four
    /// events are the whole coupling surface between the referee and ~15
    /// companions).
    ///
    /// Splits cleanly in two: this class MEASURES, `Systems_Kimarite` DECIDES. The
    /// decision half is pure and unit-tested; this half touches physics and UI and
    /// is verified the way everything behavioural here is, with a harness run.
    ///
    /// Read-only with respect to the fight: it never changes who won, only what the
    /// finish is called. Safe to disable.
    public sealed class Systems_KimariteCaller : MonoBehaviour
    {
        /// How long the call stays on screen. Shorter than the result card, which
        /// it sits above — the name is a punctuation mark, not a stat readout.
        private const float SHOW_SECONDS = 2.6f;

        /// Contact history is only meaningful for the last moment of the round, so
        /// the ring buffer is tiny. One entry per fighter is enough: the classifier
        /// asks "who last touched whom, how fast, and with what".
        private struct Touch
        {
            public float time;
            public bool fromArms;
            public float speed;
        }

        private Systems_GameMatchManager _manager;
        // Cached in Start: OnImpact is a hot path (every collision of every body
        // part of both bipeds) and a GetComponent per collision there is exactly
        // what .claude/rules/performance.md forbids.
        private Agent_BipedBody _bodyA;
        private Systems_HudRoot _hud;
        private Label _call;
        private Label _gloss;
        private VisualElement _panel;
        private float _hideAt = -1f;

        // Last contact DELIVERED by A onto B, and by B onto A.
        private Touch _aOnB, _bOnA;
        // Loser torso X one physics step back, so we can tell which way they fell.
        private float _prevAx, _prevBx;

        private void Awake()
        {
            _manager = GetComponentInParent<Systems_GameMatchManager>();
        }

        private void OnEnable()
        {
            Sensor_Impact.AnyImpact += OnImpact;
            if (_manager != null)
            {
                _manager.RoundEnded += OnRoundEnded;
                _manager.RoundStarted += OnRoundStarted;
            }
        }

        private void OnDisable()
        {
            Sensor_Impact.AnyImpact -= OnImpact;
            if (_manager != null)
            {
                _manager.RoundEnded -= OnRoundEnded;
                _manager.RoundStarted -= OnRoundStarted;
            }
        }

        private void Start()
        {
            _hud = FindAnyObjectByType<Systems_HudRoot>();
            if (_manager != null && _manager.wrestlerA != null)
            {
                _bodyA = _manager.wrestlerA.GetComponent<Agent_BipedBody>();
            }
            BuildUi();
        }

        private void BuildUi()
        {
            Systems_HudRoot hud = _hud;
            if (hud == null) return;

            _panel = Systems_UiKit.Column(Align.Center);
            _panel.style.backgroundColor = new Color(0f, 0f, 0f, 0.62f);
            _panel.style.paddingLeft = Systems_UiKit.SPACE_4;
            _panel.style.paddingRight = Systems_UiKit.SPACE_4;
            _panel.style.paddingTop = Systems_UiKit.SPACE_2;
            _panel.style.paddingBottom = Systems_UiKit.SPACE_2;
            _panel.Round(Systems_UiKit.RADIUS_MD);
            _panel.NoPickTree();

            _call = Systems_UiKit.Text("", Systems_UiKit.FONT_TITLE, Systems_UiKit.Gold, true);
            _call.style.unityTextAlign = TextAnchor.MiddleCenter;
            _call.style.textShadow = Systems_UiKit.Outline;

            _gloss = Systems_UiKit.Text("", Systems_UiKit.FONT_SMALL, Systems_UiKit.TextMid);
            _gloss.style.unityTextAlign = TextAnchor.MiddleCenter;

            _panel.Add(_call);
            _panel.Add(_gloss);
            hud.AddCentre(_panel);
            hud.HideCentre(_panel);
        }

        private void FixedUpdate()
        {
            if (_manager == null) return;
            // One-step position history, used only to decide which direction the
            // loser went. Sampled in FixedUpdate because that is the clock the
            // finish is detected on.
            if (_manager.wrestlerA != null) _prevAx = _manager.wrestlerA.TorsoX;
            if (_manager.wrestlerB != null) _prevBx = _manager.wrestlerB.TorsoX;
        }

        private void Update()
        {
            if (_hideAt > 0f && Time.time >= _hideAt)
            {
                _hideAt = -1f;
                if (_hud != null && _panel != null) _hud.HideCentre(_panel);
            }
        }

        private void OnRoundStarted()
        {
            _aOnB = default;
            _bOnA = default;
            _hideAt = -1f;
            if (_hud != null && _panel != null) _hud.HideCentre(_panel);
        }

        /// Records who touched whom. `Sensor_Impact` is static and fires for every
        /// body part of every biped in the scene, so this must filter hard — an
        /// unfiltered handler here would also count a fighter's own foot hitting
        /// the clay as a strike on the opponent.
        private void OnImpact(Sensor_Impact reporter, Collision2D collision)
        {
            if (reporter == null || reporter.owner == null || _manager == null) return;

            var otherBody = collision.collider.GetComponentInParent<Agent_BipedBody>();
            if (otherBody == null || otherBody == reporter.owner) return;

            var t = new Touch
            {
                time = Time.time,
                // Feet are never "arms"; the reporter knows which part it is on.
                fromArms = !reporter.isFoot && IsArmPart(reporter.transform),
                speed = collision.relativeVelocity.magnitude,
            };

            if (reporter.owner == _bodyA) _aOnB = t;
            else _bOnA = t;
        }

        /// Arm parts are named by `Agent_BipedBody.PART_DEFS`; matching on the name
        /// avoids adding a second public flag to `Sensor_Impact` for one caller.
        ///
        /// The four arm segments are `UArmNear`/`UArmFar` (upper) and
        /// `FArmNear`/`FArmFar` (fore), so "Arm" alone is exact: no trunk or leg
        /// part — Chest, UpperBack, LowerBack, Pelvis, Thigh*, Shin*, Foot*, Toe* —
        /// contains it. There are no Hand parts; do not add a "Hand" branch back
        /// on the assumption that there are.
        private static bool IsArmPart(Transform partTransform)
        {
            if (partTransform == null) return false;
            return partTransform.name.Contains("Arm");
        }

        private void OnRoundEnded(Agent_Biped winner, Agent_Biped loser)
        {
            if (_manager == null || _call == null) return;

            Systems_Kimarite.Result result = Systems_Kimarite.Classify(
                BuildInput(winner, loser));

            // The two non-techniques (draw, decision) are still worth showing —
            // they explain an outcome that otherwise reads as the game giving up.
            _call.text = result.Name;
            _gloss.text = result.Gloss;
            _call.style.color = result.IsTechnique
                ? Systems_UiKit.Gold : Systems_UiKit.TextMid;

            if (_hud != null && _panel != null)
            {
                _hud.ShowCentre(_panel);
                _panel.FadeIn();
            }
            _hideAt = Time.time + SHOW_SECONDS;

            // Read back by the harness, same as [ROUND] and [MATCH].
            Systems_Log.Info($"[KIMARITE] {result.Name} — {result.Gloss}");
        }

        private Systems_Kimarite.Input BuildInput(Agent_Biped winner, Agent_Biped loser)
        {
            var outcome = _manager.LastOutcome;

            // A draw has no loser, so every measured field is meaningless. Feed the
            // classifier a zeroed input rather than dereferencing null — it only
            // reads Outcome on that branch.
            if (winner == null || loser == null)
            {
                return new Systems_Kimarite.Input(outcome, 0f, _manager.ringHalfWidth,
                                                  false, 999f, false, 0f, 0f, false);
            }

            bool winnerIsA = winner == _manager.wrestlerA;
            Touch last = winnerIsA ? _aOnB : _bOnA;
            float sinceContact = last.time <= 0f ? 999f : Time.time - last.time;

            float centre = _manager.transform.position.x;
            float loserX = loser.TorsoX - centre;
            float prevLoserX = (loser == _manager.wrestlerA ? _prevAx : _prevBx) - centre;

            // Toward the centre = the magnitude of the offset shrank.
            bool fellInward = Mathf.Abs(loserX) < Mathf.Abs(prevLoserX);

            var winnerBody = winner.GetComponent<Agent_BipedBody>();
            float winnerHeight = winnerBody != null && winnerBody.Torso != null
                ? winnerBody.Torso.position.y - _manager.transform.position.y
                : 1f;

            var loserBody = loser.GetComponent<Agent_BipedBody>();
            bool grounded = loserBody != null && loser.IsDown;

            return new Systems_Kimarite.Input(
                outcome,
                Mathf.Abs(loserX),
                _manager.ringHalfWidth,
                grounded,
                sinceContact,
                last.fromArms,
                last.speed,
                winnerHeight,
                fellInward);
        }
    }
}
