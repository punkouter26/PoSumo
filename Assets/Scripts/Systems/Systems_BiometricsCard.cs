using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// The biometrics card: a 27-second stamina history per fighter plus the two
    /// round-scalar reads that have no other home on screen — peak impact
    /// delivered this round and the crowd-momentum adrenaline peak.
    ///
    /// WHAT IS DELIBERATELY NOT HERE: work rates, shove counts, balance shares.
    /// Systems_FightHud already carries the between-rounds table and the
    /// FighterPanel carries identity and push; this card is the TIME-SERIES
    /// surface — who is fading, and how fast — because a bar shows a moment and
    /// a curve shows a trajectory. The stamina sparkline is drawn from the same
    /// `Agent_BipedBody.Stamina` the fatigue model and the observation vector
    /// read, so it cannot disagree with itself.
    ///
    /// Drawing follows the Systems_PerfHud pattern: a fixed ring of thin bars,
    /// each bar's style written only when its value actually moved. An unfilled
    /// slot draws flat and neutral — an unwritten slot is not a zero reading,
    /// and painting it through the value ramp made a healthy fighter look like
    /// a history of collapse in the panel that taught this lesson.
    ///
    /// Curves persist across the rounds of a match (the arc IS the story) and
    /// clear on MatchReset; the per-round scalars reset every round.
    /// Read-only with respect to the fight.
    public sealed class Systems_BiometricsCard : MonoBehaviour
    {
        private const int BARS = 36;
        private const float SAMPLE_INTERVAL = 0.75f;

        private const float IMPACT_STRIKE_MIN = 1.2f; // m/s of relative speed that counts as a blow

        private Systems_GameMatchManager _manager;
        private Systems_FightHud _fightHud;
        /// True when drawing into the FightHud's side anchors (one-strip dock)
        /// rather than a standalone card — there the history sits under that
        /// fighter's STAMINA bar and carries no caption of its own.
        private bool _stripMode;
        private Agent_BipedBody _bodyA, _bodyB;

        private readonly float[] _historyA = new float[BARS];
        private readonly float[] _historyB = new float[BARS];
        private readonly VisualElement[] _barsA = new VisualElement[BARS];
        private readonly VisualElement[] _barsB = new VisualElement[BARS];
        private readonly float[] _writtenA = new float[BARS];
        private readonly float[] _writtenB = new float[BARS];
        private int _headA = BARS;   // BARS = "never written" for the ring heads
        private int _headB = BARS;

        private Label _captionA, _captionB;
        private float _peakImpactA, _peakImpactB;
        /// The peak each caption was last written for. -1 = never written, so
        /// the first refresh always paints (a fresh round's peak is 0).
        private float _shownPeakA = -1f, _shownPeakB = -1f;

        private float _sampleLeft;
        private bool _subscribed;

        private void Awake()
        {
            _manager = GetComponentInParent<Systems_GameMatchManager>();
        }

        private void OnEnable()
        {
            Sensor_Impact.AnyImpact += OnImpact;
            Subscribe();
        }

        private void OnDisable()
        {
            Sensor_Impact.AnyImpact -= OnImpact;
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

        private void Start()
        {
            if (_manager == null) _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            // Looked up once, the way Systems_CrowdMomentum finds the fight HUD:
            // the strip is scene furniture, not a per-round object.
            _fightHud = FindAnyObjectByType<Systems_FightHud>();
            ResolveBodies();
            BuildUi();
        }

        private void ResolveBodies()
        {
            if (_manager == null) return;
            _bodyA = _manager.wrestlerA != null
                ? _manager.wrestlerA.GetComponent<Agent_BipedBody>() : null;
            _bodyB = _manager.wrestlerB != null
                ? _manager.wrestlerB.GetComponent<Agent_BipedBody>() : null;
        }

        private void OnRoundStarted()
        {
            ResolveBodies();
            _peakImpactA = _peakImpactB = 0f;
        }

        private void OnMatchReset()
        {
            OnRoundStarted();
            _headA = _headB = BARS;
            System.Array.Clear(_writtenA, 0, _writtenA.Length);
            System.Array.Clear(_writtenB, 0, _writtenB.Length);
            for (int barIndex = 0; barIndex < BARS; barIndex++)
            {
                if (_barsA[barIndex] != null) _barsA[barIndex].style.height = 2f;
                if (_barsB[barIndex] != null) _barsB[barIndex].style.height = 2f;
            }
        }

        /// Peak impact DELIVERED per fighter this round. The static fires for
        /// every body part of every biped, so it is filtered exactly the way
        /// Systems_KimariteCaller filters it: own-body hits and non-opponent
        /// contacts are not strikes.
        private void OnImpact(Sensor_Impact reporter, Collision2D collision)
        {
            // The peak only feeds the standalone card's caption; the strip has
            // none, so there is nothing to measure for.
            if (_stripMode) return;
            if (reporter == null || reporter.owner == null) return;

            var other = collision.collider.GetComponentInParent<Agent_BipedBody>();
            if (other == null || other == reporter.owner) return;

            float speed = collision.relativeVelocity.magnitude;
            if (speed < IMPACT_STRIKE_MIN) return;

            if (reporter.owner == _bodyA)
            {
                _peakImpactA = Mathf.Max(_peakImpactA, speed);
            }
            else if (reporter.owner == _bodyB)
            {
                _peakImpactB = Mathf.Max(_peakImpactB, speed);
            }
        }

        private void Update()
        {
            if (_manager == null) return;

            // History is only written while the round is actually being scored —
            // three frozen seconds of countdown would otherwise read as a flat
            // line of fresh stamina, which is a lie about the fight.
            if (_manager.RoundActive && _manager.ScoringLive)
            {
                if (_bodyA == null || _bodyB == null) ResolveBodies();

                _sampleLeft -= Time.unscaledDeltaTime;
                if (_sampleLeft <= 0f)
                {
                    _sampleLeft = SAMPLE_INTERVAL;
                    WriteHistory();
                }
            }

            RefreshCaptions();
        }

        private void WriteHistory()
        {
            if (_bodyA != null)
            {
                _headA = (_headA >= BARS) ? 0 : (_headA + 1) % BARS;
                PaintBar(_barsA, _writtenA, _headA, Mathf.Clamp01(_bodyA.Stamina));
            }
            if (_bodyB != null)
            {
                _headB = (_headB >= BARS) ? 0 : (_headB + 1) % BARS;
                PaintBar(_barsB, _writtenB, _headB, Mathf.Clamp01(_bodyB.Stamina));
            }
        }

        private static void PaintBar(VisualElement[] bars, float[] written, int slot, float value01)
        {
            VisualElement bar = bars[slot];
            if (bar == null) return;
            written[slot] = value01;
            bar.style.height = 2f + 14f * value01;
            bar.style.backgroundColor = value01 > 0.6f ? Systems_UiKit.Good
                : value01 > 0.3f ? Systems_UiKit.Warn : Systems_UiKit.Bad;
        }

        private void RefreshCaptions()
        {
            if (_captionA == null) return;

            // Compared on the NUMBER, not on the finished string. This used to
            // build both caption strings on every rendered frame and then compare
            // them to the last ones shown — four string allocations a frame to
            // discover that nothing had changed, in a per-frame path on Android.
            if (!Mathf.Approximately(_peakImpactA, _shownPeakA))
            {
                _shownPeakA = _peakImpactA;
                _captionA.text = BuildCaptionLine(_manager != null ? _manager.wrestlerA : null,
                                                  _peakImpactA);
                _captionA.style.color = _manager != null ? _manager.colorA : Systems_UiKit.TextHi;
            }

            if (!Mathf.Approximately(_peakImpactB, _shownPeakB))
            {
                _shownPeakB = _peakImpactB;
                _captionB.text = BuildCaptionLine(_manager != null ? _manager.wrestlerB : null,
                                                  _peakImpactB);
                _captionB.style.color = _manager != null ? _manager.colorB : Systems_UiKit.TextHi;
            }
        }

        private string BuildCaptionLine(Agent_Biped fighter, float peakImpact)
        {
            // Plain words. This read "PK 5.4  AD 1.12" — peak impact speed and the
            // crowd's torque multiplier — which nobody watching could decode. The
            // hardest hit is the one a viewer can picture, so it is spelled out
            // with its unit; the crowd figure went, because the caster already
            // SAYS when a fighter has the crowd and a bare multiplier adds nothing.
            // Only the standalone card (a scene with no fight HUD) shows this line;
            // in the dock strip the history sits under the STAMINA bar uncaptioned.
            string readings = peakImpact > 0f
                ? "BEST HIT " + peakImpact.ToString("F1") + " m/s"
                : "NO HIT YET";
            string name = fighter == null ? "—"
                : !string.IsNullOrEmpty(fighter.displayNameOverride)
                    ? fighter.displayNameOverride
                    : fighter.character != null ? fighter.character.behaviorName : fighter.name;
            return name + "  " + readings;
        }

        // ---- UI -------------------------------------------------------------

        private void BuildUi()
        {
            // ONE-STRIP DOCK (zero-scroll consolidation): mount the caption +
            // sparkline into the FightHud's side anchors, under each fighter's
            // team base — the standalone dock card below is the fallback for a
            // scene with no Systems_FightHud.
            if (_fightHud != null && _fightHud.BioAnchorA != null && _fightHud.BioAnchorB != null)
            {
                // History only, no caption: in the strip it sits directly under
                // that fighter's STAMINA bar, which is the label it needs. The
                // caption it used to carry ("PK 5.4  AD 1.12") was the densest
                // line in the dock and the reason the strip overflowed its card
                // on a 4:3 panel; _captionA/B stay null and RefreshCaptions
                // returns on that.
                _stripMode = true;
                BuildHistory(_fightHud.BioAnchorA, _barsA);
                BuildHistory(_fightHud.BioAnchorB, _barsB);
                return;
            }

            PanelSettings settings = _manager != null ? _manager.panelSettings : null;
            Systems_HudRoot hud = Systems_HudRoot.Ensure(transform, settings);
            if (hud == null || hud.Dock == null) return;

            VisualElement card = Systems_UiKit.ElevatedCard(Systems_UiKit.Elevation.Base).NoPick();
            card.Pad(Systems_UiKit.SPACE_3, Systems_UiKit.SPACE_1);
            card.style.marginBottom = Systems_UiKit.SPACE_1;

            VisualElement left = Systems_UiKit.Column();
            VisualElement right = Systems_UiKit.Column();
            BuildSide(left, _barsA, out _captionA);
            BuildSide(right, _barsB, out _captionB);

            card.Add(Systems_UiKit.Triplet(left,
                                           Systems_UiKit.Caption("LAST 27s", Systems_UiKit.FONT_MICRO,
                                                                 Systems_UiKit.TextLow),
                                           right));
            card.NoPickTree();
            hud.Dock.Add(card);
        }

        private static void BuildSide(VisualElement host, VisualElement[] bars,
                                      out Label caption)
        {
            caption = Systems_UiKit.Caption("—", Systems_UiKit.FONT_MICRO, Systems_UiKit.TextMid, true);
            caption.style.unityTextAlign = TextAnchor.MiddleLeft;
            host.Add(caption);
            BuildHistory(host, bars);
        }

        /// The stamina sparkline on its own: BARS thin columns, newest rightmost.
        private static void BuildHistory(VisualElement host, VisualElement[] bars)
        {
            VisualElement row = Systems_UiKit.Row(Align.FlexEnd);
            row.style.height = 16;
            row.style.marginTop = Systems_UiKit.SPACE_1;
            for (int barIndex = 0; barIndex < BARS; barIndex++)
            {
                // 3pt bars on a 4pt pitch (was 4+2): in the strip this row sits in
                // a 27%-wide side column, where the old 6pt pitch overflowed it
                // (36 x 6 = 216 vs ~186 available). 36 x 4 = 144 fits with room.
                var bar = new VisualElement().NoPick();
                bar.style.width = 3;
                bar.style.height = 2f;
                bar.style.marginRight = 1;
                bar.style.backgroundColor = Systems_UiKit.Track;
                bar.Round(1);
                bars[barIndex] = bar;
                row.Add(bar);
            }
            host.Add(row);
        }
    }
}
