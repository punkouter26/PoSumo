using UnityEngine;
using UnityEngine.UIElements;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace PoSumo
{
    /// Broadcast-style match HUD: one compact panel per wrestler in the top
    /// screen corners (empty backdrop space — never covers the ring), toggled
    /// with Tab or the STATS chip. Stats aggregate over the WHOLE match (reset
    /// on rematch), sampled only while a round is live:
    ///   DOMINANCE — pairwise-normalized composite (territory/KD/shoves/balance)
    ///   TERRITORY — % of time the fight's midpoint is on the opponent's half
    ///   KD        — knockdowns dealt–suffered
    ///   SHOVES    — big momentum transfers landed, plus the hardest one
    ///   WORK      — average motor effort
    ///   BALANCE / PUSH — time-averaged bars (push averaged only in contact)
    /// UI Toolkit only.
    public class Systems_FightHud : MonoBehaviour
    {
        public Systems_GameMatchManager manager;
        public PanelSettings panelSettings;
        public bool startVisible = true;

        const float AVG_PUSH_MAX = 900f;    // bar scale for contact-averaged push
        const float SHOVE_FORCE_N = 400f;   // momentum transfer that counts as a shove
        const float SHOVE_COOLDOWN = 0.5f;  // seconds between countable shoves
        const float TOUCH_DIST = 1.2f;      // torso distance treated as "in contact"
        const float KD_REARM_SECONDS = 0.5f; // must stay up this long before next KD counts

        static readonly Color PanelBg = new Color(0.04f, 0.04f, 0.06f, 0.8f);
        static readonly Color LabelDim = new Color(0.62f, 0.6f, 0.56f);
        static readonly Color ValueBright = new Color(0.96f, 0.95f, 0.92f);
        static readonly Color BarBg = new Color(0.2f, 0.2f, 0.24f);

        class PanelRefs
        {
            public Label dom, territory, kd, shoves, work, pushVal, footer;
            public VisualElement root, balFill, pushFill;
        }

        /// Whole-match accumulators for one wrestler.
        class Agg
        {
            public float sumSpd, sumLean, sumEdge, sumBal, sumWork, sumPush, bestPush;
            public int territorySamples, touchSamples, shoves, kdDealt, kdSuffered;
            public float nextShoveTime, upSince;
            public bool downLatched;

            public void Reset()
            {
                sumSpd = sumLean = sumEdge = sumBal = sumWork = sumPush = bestPush = 0f;
                territorySamples = touchSamples = shoves = kdDealt = kdSuffered = 0;
                nextShoveTime = 0f;
                upSince = 0f;
                downLatched = false;
            }
        }

        PanelRefs _panelA, _panelB;
        readonly Agg _aggA = new Agg();
        readonly Agg _aggB = new Agg();
        int _samples;
        float _sumClash;
        int _clashRounds;
        bool _clashThisRound;

        bool _visible;
        Vector2 _prevVelA, _prevVelB;
        Agent_BipedBody _bodyA, _bodyB;

        /// Live pairwise-normalized dominance (0-100, the pair sums to 100).
        /// Consumed by Systems_FaceMood; updated even while the HUD is hidden.
        public float DominanceA { get; private set; } = 50f;
        public float DominanceB { get; private set; } = 50f;

        void Start()
        {
            if (manager == null) manager = FindAnyObjectByType<Systems_GameMatchManager>();
            manager.MatchReset += ResetAggregates;
            manager.RoundStarted += OnRoundStarted;
            _visible = startVisible;
            BuildUi();
        }

        void OnDisable()
        {
            if (manager != null)
            {
                manager.MatchReset -= ResetAggregates;
                manager.RoundStarted -= OnRoundStarted;
            }
        }

        void ResetAggregates()
        {
            _aggA.Reset();
            _aggB.Reset();
            _samples = 0;
            _sumClash = 0f;
            _clashRounds = 0;
            _clashThisRound = false;
        }

        void OnRoundStarted() { _clashThisRound = false; }

        static VisualElement Divider()
        {
            var d = new VisualElement();
            d.style.height = 1;
            d.style.backgroundColor = new Color(1f, 1f, 1f, 0.12f);
            d.style.marginTop = 5;
            d.style.marginBottom = 5;
            return d;
        }

        static Label StatRow(VisualElement parent, string label, int valueSize)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.alignItems = Align.Center;
            row.style.height = valueSize + 7;

            var lbl = new Label(label);
            lbl.style.fontSize = 11;
            lbl.style.color = LabelDim;
            row.Add(lbl);

            var val = new Label("—");
            val.style.fontSize = valueSize;
            val.style.color = ValueBright;
            val.style.unityFontStyleAndWeight = FontStyle.Bold;
            val.style.unityTextAlign = TextAnchor.MiddleRight;
            row.Add(val);

            parent.Add(row);
            return val;
        }

        static VisualElement BarRow(VisualElement parent, string label, Color fillColor, out Label valueLabel)
        {
            var caption = new VisualElement();
            caption.style.flexDirection = FlexDirection.Row;
            caption.style.justifyContent = Justify.SpaceBetween;
            caption.style.marginTop = 3;

            var lbl = new Label(label);
            lbl.style.fontSize = 10;
            lbl.style.color = LabelDim;
            caption.Add(lbl);

            valueLabel = new Label("");
            valueLabel.style.fontSize = 10;
            valueLabel.style.color = LabelDim;
            caption.Add(valueLabel);
            parent.Add(caption);

            var bar = new VisualElement();
            bar.style.height = 7;
            bar.style.marginTop = 2;
            bar.style.marginBottom = 2;
            bar.style.backgroundColor = BarBg;
            bar.style.borderTopLeftRadius = 2;
            bar.style.borderTopRightRadius = 2;
            bar.style.borderBottomLeftRadius = 2;
            bar.style.borderBottomRightRadius = 2;

            var fill = new VisualElement();
            fill.style.height = Length.Percent(100);
            fill.style.width = 0;
            fill.style.backgroundColor = fillColor;
            fill.style.borderTopLeftRadius = 2;
            fill.style.borderBottomLeftRadius = 2;
            bar.Add(fill);
            parent.Add(bar);
            return fill;
        }

        PanelRefs MakePanel(bool left, Color accent, string fighterName)
        {
            var refs = new PanelRefs();
            var p = new VisualElement();
            refs.root = p;
            p.style.position = Position.Absolute;
            p.style.top = 12;
            if (left) p.style.left = 10; else p.style.right = 10;
            p.style.width = 172;
            p.style.backgroundColor = PanelBg;
            p.style.borderTopWidth = 3;
            p.style.borderTopColor = accent;
            p.style.borderBottomLeftRadius = 8;
            p.style.borderBottomRightRadius = 8;
            p.style.paddingLeft = 12; p.style.paddingRight = 12;
            p.style.paddingTop = 8; p.style.paddingBottom = 9;

            var name = new Label(fighterName);
            name.style.fontSize = 19;
            name.style.color = accent;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.marginBottom = 2;
            p.Add(name);

            refs.dom = StatRow(p, "DOMINANCE", 16);
            p.Add(Divider());
            refs.territory = StatRow(p, "TERRITORY", 12);
            refs.kd = StatRow(p, "KNOCKDOWNS", 12);
            refs.shoves = StatRow(p, "SHOVES · BEST", 12);
            refs.work = StatRow(p, "WORK RATE", 12);
            p.Add(Divider());
            refs.balFill = BarRow(p, "BALANCE", new Color(0.35f, 0.75f, 0.4f), out _);
            refs.pushFill = BarRow(p, "PUSH (in contact)", new Color(0.95f, 0.55f, 0.2f), out refs.pushVal);

            refs.footer = new Label("");
            refs.footer.style.fontSize = 10;
            refs.footer.style.color = LabelDim;
            refs.footer.style.marginTop = 4;
            p.Add(refs.footer);

            return refs;
        }

        void BuildUi()
        {
            var doc = gameObject.AddComponent<UIDocument>();
            if (panelSettings != null) doc.panelSettings = panelSettings;
            var root = doc.rootVisualElement;
            root.style.flexGrow = 1;

            _panelA = MakePanel(true, manager.colorA, manager.nameA);
            _panelB = MakePanel(false, manager.colorB, manager.nameB);
            root.Add(_panelA.root);
            root.Add(_panelB.root);

            // Touch-friendly toggle chip, bottom-right corner — Tab works too.
            var toggle = new Button(() => { _visible = !_visible; ApplyVisibility(); }) { text = "STATS" };
            toggle.style.position = Position.Absolute;
            toggle.style.bottom = 10;
            toggle.style.right = 10;
            toggle.style.width = 76;
            toggle.style.height = 34;
            toggle.style.fontSize = 14;
            toggle.style.unityFontStyleAndWeight = FontStyle.Bold;
            toggle.style.color = ValueBright;
            toggle.style.backgroundColor = new Color(0.12f, 0.11f, 0.13f, 0.75f);
            toggle.style.borderTopLeftRadius = 8;
            toggle.style.borderTopRightRadius = 8;
            toggle.style.borderBottomLeftRadius = 8;
            toggle.style.borderBottomRightRadius = 8;
            root.Add(toggle);

            ApplyVisibility();
        }

        void ApplyVisibility()
        {
            var d = _visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (_panelA != null) _panelA.root.style.display = d;
            if (_panelB != null) _panelB.root.style.display = d;
        }

        bool TabPressed()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Tab);
#endif
        }

        void FixedUpdate()
        {
            var a = manager != null ? manager.wrestlerA : null;
            var b = manager != null ? manager.wrestlerB : null;
            if (a == null || b == null) return;
            if (_bodyA == null) _bodyA = a.GetComponent<Agent_BipedBody>();
            if (_bodyB == null) _bodyB = b.GetComponent<Agent_BipedBody>();

            Vector2 velA = a.Torso.linearVelocity, velB = b.Torso.linearVelocity;

            if (manager.RoundActive)
            {
                float dt = Time.fixedDeltaTime;
                float now = Time.time;
                float cx = a.arenaCenterX;
                bool touching = Mathf.Abs(a.TorsoX - b.TorsoX) < TOUCH_DIST;

                // Momentum transfer directed away from the shover (N).
                float dirAB = Mathf.Sign(b.TorsoX - a.TorsoX);
                float pushA = Mathf.Max(0f, (velB.x - _prevVelB.x) * dirAB) * _bodyB.TotalMass / dt;
                float pushB = Mathf.Max(0f, (_prevVelA.x - velA.x) * dirAB) * _bodyA.TotalMass / dt;

                float midX = (a.TorsoX + b.TorsoX) * 0.5f;
                SampleFighter(_aggA, a, _bodyA, velA, cx, midX, touching, pushA, now);
                SampleFighter(_aggB, b, _bodyB, velB, cx, midX, touching, pushB, now);

                // Knockdown attribution: your fall is their KD dealt.
                TrackKnockdown(_aggA, _aggB, a.IsDown, now);
                TrackKnockdown(_aggB, _aggA, b.IsDown, now);

                if (!_clashThisRound && touching)
                {
                    _clashThisRound = true;
                    _sumClash += manager.RoundElapsed;
                    _clashRounds++;
                }
                _samples++;
            }

            _prevVelA = velA; _prevVelB = velB;
        }

        void SampleFighter(Agg agg, Agent_Biped w, Agent_BipedBody body, Vector2 vel,
                           float centerX, float midX, bool touching, float push, float now)
        {
            agg.sumSpd += vel.magnitude;
            agg.sumLean += Mathf.Abs(Vector2.SignedAngle(Vector2.up, body.Chest.transform.up));
            agg.sumEdge += Mathf.Max(0f, w.ringHalfWidth - Mathf.Abs(w.TorsoX - w.arenaCenterX));
            agg.sumBal += Mathf.Clamp01(Vector2.Dot(body.Chest.transform.up, Vector2.up));

            float work = 0f;
            for (int j = 0; j < Agent_Biped.ActionCount; j++) work += Mathf.Abs(w.LastActions[j]);
            agg.sumWork += work / Agent_Biped.ActionCount;

            // Field position: the fight's midpoint sits on the opponent's half
            // (which lies in this wrestler's facing direction).
            if ((midX - centerX) * body.facingSign > 0f) agg.territorySamples++;

            if (touching)
            {
                agg.sumPush += push;
                agg.touchSamples++;
                agg.bestPush = Mathf.Max(agg.bestPush, push);
                if (push > SHOVE_FORCE_N && now >= agg.nextShoveTime)
                {
                    agg.shoves++;
                    agg.nextShoveTime = now + SHOVE_COOLDOWN;
                }
            }
        }

        void TrackKnockdown(Agg faller, Agg opponent, bool isDown, float now)
        {
            if (isDown)
            {
                if (!faller.downLatched && now - faller.upSince >= 0f)
                {
                    faller.downLatched = true;
                    faller.kdSuffered++;
                    opponent.kdDealt++;
                }
                faller.upSince = now + KD_REARM_SECONDS; // must stay up this long to re-arm
            }
            else if (faller.downLatched && now >= faller.upSince)
            {
                faller.downLatched = false;
            }
        }

        /// Unnormalized dominance blend; both fighters' values are scaled to
        /// sum to 100 at display time.
        static float RawDominance(Agg self, Agg other, int samples)
        {
            float n = Mathf.Max(1, samples);
            float territory = self.territorySamples / n;
            float balance = self.sumBal / n;
            float kdShare = (self.kdDealt + 1f) / (self.kdDealt + self.kdSuffered + 2f);
            float shoveShare = (self.shoves + 1f) / (self.shoves + other.shoves + 2f);
            return 0.35f * territory + 0.30f * kdShare + 0.20f * shoveShare + 0.15f * balance;
        }

        void Update()
        {
            if (TabPressed()) { _visible = !_visible; ApplyVisibility(); }
            if (manager == null || manager.wrestlerA == null) return;

            float domA = RawDominance(_aggA, _aggB, _samples);
            float domB = RawDominance(_aggB, _aggA, _samples);
            float domTotal = Mathf.Max(0.0001f, domA + domB);
            DominanceA = domA / domTotal * 100f;
            DominanceB = domB / domTotal * 100f;

            if (!_visible || _panelA == null) return;
            UpdatePanel(_panelA, _aggA, manager.MatchWinsA, DominanceA);
            UpdatePanel(_panelB, _aggB, manager.MatchWinsB, DominanceB);
        }

        void UpdatePanel(PanelRefs p, Agg agg, int matchWins, float dominance)
        {
            float n = Mathf.Max(1, _samples);

            p.dom.text = $"{dominance:F0}";
            p.dom.style.color = dominance >= 55f
                ? new Color(0.55f, 0.9f, 0.5f)
                : dominance <= 45f ? new Color(0.95f, 0.5f, 0.4f) : ValueBright;

            p.territory.text = $"{agg.territorySamples / n * 100f:F0}%";
            p.kd.text = $"{agg.kdDealt}–{agg.kdSuffered}";
            p.shoves.text = agg.shoves > 0 ? $"{agg.shoves} · {agg.bestPush:F0}N" : "0";
            p.work.text = $"{agg.sumWork / n * 100f:F0}%";

            float avgBal = agg.sumBal / n;
            p.balFill.style.width = Length.Percent(avgBal * 100f);
            p.balFill.style.backgroundColor = avgBal > 0.7f
                ? new Color(0.35f, 0.75f, 0.4f)
                : avgBal > 0.4f ? new Color(0.9f, 0.75f, 0.25f) : new Color(0.9f, 0.35f, 0.25f);

            float avgPush = Mathf.Clamp(agg.sumPush / Mathf.Max(1, agg.touchSamples), 0f, AVG_PUSH_MAX);
            p.pushFill.style.width = Length.Percent(avgPush / AVG_PUSH_MAX * 100f);
            p.pushVal.text = $"{avgPush:F0} N";

            p.footer.text = $"MATCHES {matchWins} · LONGEST RD {manager.LongestRound:F0}s";
        }
    }
}
