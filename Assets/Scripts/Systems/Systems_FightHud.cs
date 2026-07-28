using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// Broadcast-style match stats, drawn as ONE comparison table in the HUD dock
    /// and always on during a match. Stats aggregate over the WHOLE
    /// match (reset on rematch), sampled only while a round is live:
    ///   DOMINANCE — pairwise-normalized composite (territory/KD/shoves/balance)
    ///   TERRITORY — % of time the fight's midpoint is on the opponent's half
    ///   KD        — knockdowns dealt–suffered
    ///   SHOVES    — big momentum transfers landed, plus the hardest one
    ///   WORK      — average motor effort
    ///   BALANCE / PUSH — time-averaged bars (push averaged only in contact)
    ///
    /// This used to be two mirrored panels, one per wrestler, pinned to the bottom
    /// screen corners at 48% width each. Same seven rows twice, so comparing any
    /// metric meant reading a number on the left, tracking across the screen and
    /// reading its twin on the right — and the footer line printed the
    /// match-global LONGEST RD in both copies. One table with the metric name down
    /// the middle and the two fighters' values on either side makes the comparison
    /// the layout instead of a job for the reader, in half the width and half the
    /// elements. The bars go further: each is a single tug-of-war track that fills
    /// outward from the centre in each fighter's colour, so who leads is a shape,
    /// not a subtraction.
    ///
    /// UI Toolkit only, drawn into the shared Systems_HudRoot.
    public sealed class Systems_FightHud : MonoBehaviour
    {
        public Systems_GameMatchManager manager;
        public PanelSettings panelSettings;
        // No visibility field. The table is always on during a match, so there is
        // no `startVisible` to serialize, no chip to press and no state to get
        // out of sync with the scene.

        private const float AVG_PUSH_MAX = 500f;    // bar scale for contact-averaged push
        private const float SHOVE_FORCE_N = 400f;   // momentum transfer that counts as a shove
        private const float SHOVE_COOLDOWN = 0.5f;  // seconds between countable shoves
        private const float TOUCH_DIST = 1.2f;      // torso distance treated as "in contact"
        private const float KD_REARM_SECONDS = 0.5f; // must stay up this long before next KD counts

        // The table's three columns. The middle column carries the metric name, so
        // the two value columns sit directly under their fighter's name plate.
        private const int SIDE_PERCENT = 27;
        private const int LABEL_PERCENT = 46;

        /// One metric, shown as [fighter A's value] METRIC [fighter B's value].
        private sealed class ComparePair
        {
            public Label a, b;
        }

        /// One metric drawn as a centre-out tug-of-war bar.
        private sealed class BarPair
        {
            public VisualElement fillA, fillB;
            public Label valueA, valueB;
        }

        /// Whole-match accumulators for one wrestler.
        private class Agg
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

        private readonly Agg _aggA = new Agg();
        private readonly Agg _aggB = new Agg();
        private int _samples;
        private float _sumClash;
        private int _clashRounds;
        private bool _clashThisRound;

        private Systems_HudRoot _hud;
        private VisualElement _card;
        private ComparePair _dominance, _territory, _knockdowns, _shoves, _work;
        private BarPair _balance, _push;
        private Label _footer;

        private Vector2 _prevVelA, _prevVelB;
        private Agent_BipedBody _bodyA, _bodyB;

        /// Live pairwise-normalized dominance (0-100, the pair sums to 100).
        /// Consumed by Systems_FaceMood, Systems_FighterVoice and
        /// the stats table; sampled every physics step.
        public float DominanceA { get; private set; } = 50f;
        public float DominanceB { get; private set; } = 50f;

        private void Start()
        {
            if (manager == null) manager = FindAnyObjectByType<Systems_GameMatchManager>();
            manager.MatchReset += ResetAggregates;
            manager.RoundStarted += OnRoundStarted;
            BuildUi();
        }

        private void OnDisable()
        {
            if (manager != null)
            {
                manager.MatchReset -= ResetAggregates;
                manager.RoundStarted -= OnRoundStarted;
            }
        }

        private void ResetAggregates()
        {
            _aggA.Reset();
            _aggB.Reset();
            _samples = 0;
            _sumClash = 0f;
            _clashRounds = 0;
            _clashThisRound = false;
        }

        private void OnRoundStarted() { _clashThisRound = false; }

        // ---- Build ---------------------------------------------------------

        private void BuildUi()
        {
            // Own PanelSettings if the scene assigned one, otherwise the match
            // manager's — whichever component's Start runs first builds the root,
            // and that order is undefined.
            PanelSettings settings = panelSettings != null ? panelSettings
                : manager != null ? manager.panelSettings : null;
            _hud = Systems_HudRoot.Ensure(transform, settings);

            _card = Systems_UiKit.Card(Systems_UiKit.Panel).NoPick();
            _card.Pad(Systems_UiKit.SPACE_4, Systems_UiKit.SPACE_3);
            _card.style.marginBottom = Systems_UiKit.SPACE_2;

            BuildHeader();

            _dominance = CompareRow("DOMINANCE", Systems_UiKit.FONT_TITLE);
            _card.Add(Systems_UiKit.Divider());
            _territory = CompareRow("TERRITORY", Systems_UiKit.FONT_BODY);
            _knockdowns = CompareRow("KNOCKDOWNS", Systems_UiKit.FONT_BODY);
            _shoves = CompareRow("SHOVES · BEST", Systems_UiKit.FONT_BODY);
            _work = CompareRow("WORK RATE", Systems_UiKit.FONT_BODY);
            _card.Add(Systems_UiKit.Divider());
            _balance = MirrorBarRow("BALANCE");
            _push = MirrorBarRow("PUSH IN CONTACT");
            _card.Add(Systems_UiKit.Divider());
            BuildDamageRow();
            _card.Add(Systems_UiKit.Divider());

            // One footer for the whole table. The old layout printed this line in
            // both panels, and LONGEST RD is a property of the match, not of a
            // fighter, so half of it was always redundant.
            _footer = Systems_UiKit.Text("", Systems_UiKit.FONT_MICRO, Systems_UiKit.TextLow);
            _footer.style.unityTextAlign = TextAnchor.MiddleCenter;
            _footer.style.marginTop = Systems_UiKit.SPACE_2;
            _card.Add(_footer);

            // Above the momentum graph, which stays pinned to the bottom edge.
            // Insert rather than Add so the order holds whichever of the two
            // components reaches the dock first.
            _hud.Dock.Insert(0, _card);
        }

        private void BuildHeader()
        {
            VisualElement header = Systems_UiKit.Row();
            header.style.marginBottom = Systems_UiKit.SPACE_1;

            Label a = Systems_UiKit.Text(manager.nameA, Systems_UiKit.FONT_BODY, manager.colorA, true);
            a.style.width = Length.Percent(SIDE_PERCENT);
            a.style.unityTextAlign = TextAnchor.MiddleLeft;

            Label title = Systems_UiKit.Text("MATCH STATS", Systems_UiKit.FONT_MICRO, Systems_UiKit.TextLow, true);
            title.style.width = Length.Percent(LABEL_PERCENT);
            title.style.unityTextAlign = TextAnchor.MiddleCenter;

            Label b = Systems_UiKit.Text(manager.nameB, Systems_UiKit.FONT_BODY, manager.colorB, true);
            b.style.width = Length.Percent(SIDE_PERCENT);
            b.style.unityTextAlign = TextAnchor.MiddleRight;

            header.Add(a);
            header.Add(title);
            header.Add(b);
            _card.Add(header);
        }

        private ComparePair CompareRow(string label, int valueSize)
        {
            VisualElement row = Systems_UiKit.Row();
            row.style.height = valueSize + 10;

            var pair = new ComparePair
            {
                a = SideValue(valueSize, TextAnchor.MiddleLeft),
                b = SideValue(valueSize, TextAnchor.MiddleRight),
            };

            Label name = Systems_UiKit.Text(label, Systems_UiKit.FONT_SMALL, Systems_UiKit.TextLow);
            name.style.width = Length.Percent(LABEL_PERCENT);
            name.style.unityTextAlign = TextAnchor.MiddleCenter;

            row.Add(pair.a);
            row.Add(name);
            row.Add(pair.b);
            _card.Add(row);
            return pair;
        }

        /// Percentage widths, not the old fixed `minWidth: 96`. The panel resolves
        /// narrower than its 720pt reference on tall phones, and a fixed value
        /// plus a caption overflowed the row there.
        private static Label SideValue(int fontSize, TextAnchor align)
        {
            Label value = Systems_UiKit.Text("—", fontSize, Systems_UiKit.TextHi, true);
            value.style.width = Length.Percent(SIDE_PERCENT);
            value.style.unityTextAlign = align;
            return value;
        }

        /// A caption row plus one track that fills outward from the centre: A's
        /// share grows leftward in A's colour, B's rightward in B's.
        private BarPair MirrorBarRow(string label)
        {
            var pair = new BarPair();

            VisualElement caption = Systems_UiKit.Row();
            caption.style.marginTop = Systems_UiKit.SPACE_1;

            pair.valueA = Systems_UiKit.Text("", Systems_UiKit.FONT_SMALL, Systems_UiKit.TextLow);
            pair.valueA.style.width = Length.Percent(SIDE_PERCENT);

            Label name = Systems_UiKit.Text(label, Systems_UiKit.FONT_SMALL, Systems_UiKit.TextLow);
            name.style.width = Length.Percent(LABEL_PERCENT);
            name.style.unityTextAlign = TextAnchor.MiddleCenter;

            pair.valueB = Systems_UiKit.Text("", Systems_UiKit.FONT_SMALL, Systems_UiKit.TextLow);
            pair.valueB.style.width = Length.Percent(SIDE_PERCENT);
            pair.valueB.style.unityTextAlign = TextAnchor.MiddleRight;

            caption.Add(pair.valueA);
            caption.Add(name);
            caption.Add(pair.valueB);
            _card.Add(caption);

            VisualElement track = Systems_UiKit.Row();
            track.style.height = 12;
            track.style.marginTop = Systems_UiKit.SPACE_1;
            track.style.marginBottom = Systems_UiKit.SPACE_1;
            track.style.backgroundColor = Systems_UiKit.Track;
            track.style.overflow = Overflow.Hidden;
            track.Round(3);

            pair.fillA = HalfFill(track, Justify.FlexEnd, manager.colorA);
            pair.fillB = HalfFill(track, Justify.FlexStart, manager.colorB);
            _card.Add(track);
            return pair;
        }

        private static VisualElement HalfFill(VisualElement track, Justify grow, Color colour)
        {
            VisualElement half = Systems_UiKit.Row();
            half.style.width = Length.Percent(50);
            half.style.height = Length.Percent(100);
            half.style.justifyContent = grow;

            var fill = new VisualElement();
            fill.style.height = Length.Percent(100);
            fill.style.width = 0;
            fill.style.backgroundColor = colour;
            half.Add(fill);
            track.Add(half);
            return fill;
        }

        // The table used to be hideable — a STATS chip, a Tab shortcut and a
        // SetStatsVisible() the momentum graph called on start to hide it. All of
        // that is gone: the stats HUD is always on during a match, so there is
        // nothing to toggle and no state to track.

        // ---- Sampling ------------------------------------------------------

        private void FixedUpdate()
        {
            var a = manager != null ? manager.wrestlerA : null;
            var b = manager != null ? manager.wrestlerB : null;
            if (a == null || b == null) return;
            if (_bodyA == null) _bodyA = a.GetComponent<Agent_BipedBody>();
            if (_bodyB == null) _bodyB = b.GetComponent<Agent_BipedBody>();

            Vector2 velA = a.Torso.linearVelocity, velB = b.Torso.linearVelocity;

            // ScoringLive, not RoundActive: the latter is already true through the
            // countdown, and averaging over three seconds of frozen fighters is
            // what made TERRITORY fail to total 100 between the two of them.
            if (manager.ScoringLive)
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

        private void SampleFighter(Agg agg, Agent_Biped w, Agent_BipedBody body, Vector2 vel,
                           float centerX, float midX, bool touching, float push, float now)
        {
            agg.sumSpd += vel.magnitude;
            agg.sumLean += Mathf.Abs(Vector2.SignedAngle(Vector2.up, body.Chest.transform.up));
            agg.sumEdge += Mathf.Max(0f, w.ringHalfWidth - Mathf.Abs(w.TorsoX - w.arenaCenterX));
            agg.sumBal += Mathf.Clamp01(Vector2.Dot(body.Chest.transform.up, Vector2.up));

            float work = 0f;
            for (int actionIndex = 0; actionIndex < Agent_Biped.ActionCount; actionIndex++) work += Mathf.Abs(w.LastActions[actionIndex]);
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

        private void TrackKnockdown(Agg faller, Agg opponent, bool isDown, float now)
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
        private static float RawDominance(Agg self, Agg other, int samples)
        {
            float n = Mathf.Max(1, samples);
            float territory = self.territorySamples / n;
            float balance = self.sumBal / n;
            float kdShare = (self.kdDealt + 1f) / (self.kdDealt + self.kdSuffered + 2f);
            float shoveShare = (self.shoves + 1f) / (self.shoves + other.shoves + 2f);
            return 0.35f * territory + 0.30f * kdShare + 0.20f * shoveShare + 0.15f * balance;
        }

        // ---- Display -------------------------------------------------------

        private void Update()
        {
            if (manager == null || manager.wrestlerA == null) return;

            float domA = RawDominance(_aggA, _aggB, _samples);
            float domB = RawDominance(_aggB, _aggA, _samples);
            float domTotal = Mathf.Max(0.0001f, domA + domB);
            DominanceA = domA / domTotal * 100f;
            DominanceB = domB / domTotal * 100f;

            if (_card == null) return;
            UpdateTable();
        }

        // ---- Damage mannequin ----------------------------------------------
        //

        // A six-region stick figure per fighter: head, torso, both arms, both legs.
        // Green when untouched, through amber, to red as Systems_BodyDamage
        // accumulates hits — and a hollow stump once the part is actually gone.
        // The head is in here because it can now be knocked off.
        //
        // Six regions, not fourteen: the body has 14 parts, but this sits in a dock
        // on a portrait phone during a short round, and nobody can read fourteen
        // swatches.
        private VisualElement[] _mannA, _mannB;

        private static readonly Color DamageGreen = new Color(0.36f, 0.78f, 0.40f);
        private static readonly Color DamageAmber = new Color(0.94f, 0.72f, 0.22f);
        private static readonly Color DamageRed = new Color(0.88f, 0.22f, 0.18f);
        private static readonly Color DamageGone = new Color(0.20f, 0.20f, 0.24f, 0.45f);

        private void BuildDamageRow()
        {
            VisualElement row = Systems_UiKit.Row();
            row.style.alignItems = Align.Center;
            row.style.marginTop = Systems_UiKit.SPACE_1;

            _mannA = BuildMannequin(out VisualElement figureA);
            _mannB = BuildMannequin(out VisualElement figureB);

            var left = new VisualElement().NoPick();
            left.style.width = Length.Percent(SIDE_PERCENT);
            left.style.alignItems = Align.Center;
            left.Add(figureA);

            Label title = Systems_UiKit.Text("DAMAGE", Systems_UiKit.FONT_MICRO, Systems_UiKit.TextLow);
            title.style.width = Length.Percent(LABEL_PERCENT);
            title.style.unityTextAlign = TextAnchor.MiddleCenter;

            var right = new VisualElement().NoPick();
            right.style.width = Length.Percent(SIDE_PERCENT);
            right.style.alignItems = Align.Center;
            right.Add(figureB);

            row.Add(left);
            row.Add(title);
            row.Add(right);
            _card.Add(row);
        }

        /// Indices match Systems_BodyDamage.Region: Head, Torso, ArmNear, ArmFar,
        /// LegNear, LegFar.
        private VisualElement[] BuildMannequin(out VisualElement figure)
        {
            const float W = 56f, H = 78f;
            figure = new VisualElement().NoPick();
            figure.style.width = W;
            figure.style.height = H;

            var parts = new VisualElement[Systems_BodyDamage.REGION_COUNT];
            parts[(int)Systems_BodyDamage.Region.Head] = Piece(figure, 20f, 0f, 16f, 16f, 8);
            parts[(int)Systems_BodyDamage.Region.Torso] = Piece(figure, 19f, 18f, 18f, 30f, 3);
            parts[(int)Systems_BodyDamage.Region.ArmNear] = Piece(figure, 8f, 20f, 8f, 26f, 4);
            parts[(int)Systems_BodyDamage.Region.ArmFar] = Piece(figure, 40f, 20f, 8f, 26f, 4);
            parts[(int)Systems_BodyDamage.Region.LegNear] = Piece(figure, 19f, 50f, 8f, 28f, 4);
            parts[(int)Systems_BodyDamage.Region.LegFar] = Piece(figure, 29f, 50f, 8f, 28f, 4);
            return parts;
        }

        private static VisualElement Piece(VisualElement parent, float left, float top,
                                           float w, float h, int radius)
        {
            var piece = new VisualElement().NoPick();
            piece.style.position = Position.Absolute;
            piece.style.left = left;
            piece.style.top = top;
            piece.style.width = w;
            piece.style.height = h;
            piece.style.backgroundColor = DamageGreen;
            piece.Round(radius);
            parent.Add(piece);
            return piece;
        }

        private void PaintMannequin(VisualElement[] parts, Agent_Biped fighter)
        {
            if (parts == null || fighter == null) return;
            var body = fighter.GetComponent<Agent_BipedBody>();
            Systems_BodyDamage damage = body != null ? Systems_BodyDamage.For(body) : null;
            if (damage == null) return;

            for (int regionIndex = 0; regionIndex < Systems_BodyDamage.REGION_COUNT; regionIndex++)
            {
                var region = (Systems_BodyDamage.Region)regionIndex;
                if (damage.RegionDetached(region))
                {
                    parts[regionIndex].style.backgroundColor = DamageGone;
                    continue;
                }
                float t = damage.RegionDamage01(region);
                // Two-stop ramp so amber is a real waypoint rather than a colour the
                // bar passes through in one frame.
                parts[regionIndex].style.backgroundColor = t < 0.5f
                    ? Color.Lerp(DamageGreen, DamageAmber, t * 2f)
                    : Color.Lerp(DamageAmber, DamageRed, (t - 0.5f) * 2f);
            }
        }

        private void UpdateTable()
        {
            float n = Mathf.Max(1, _samples);


            _dominance.a.text = $"{DominanceA:F0}";
            _dominance.b.text = $"{DominanceB:F0}";
            _dominance.a.style.color = DominanceColour(DominanceA);
            _dominance.b.style.color = DominanceColour(DominanceB);

            _territory.a.text = $"{_aggA.territorySamples / n * 100f:F0}%";
            _territory.b.text = $"{_aggB.territorySamples / n * 100f:F0}%";
            // One number per side, not "dealt–suffered": A's dealt IS B's suffered,
            // so the old pair printed the same two figures mirrored on both sides
            // (2–3 on the left, 3–2 on the right) and read as four separate stats.
            _knockdowns.a.text = $"{_aggA.kdDealt}";
            _knockdowns.b.text = $"{_aggB.kdDealt}";
            _shoves.a.text = $"{_aggA.shoves} · {_aggA.bestPush:F0}N";
            _shoves.b.text = $"{_aggB.shoves} · {_aggB.bestPush:F0}N";
            _work.a.text = $"{_aggA.sumWork / n * 100f:F0}%";
            _work.b.text = $"{_aggB.sumWork / n * 100f:F0}%";

            float balA = _aggA.sumBal / n;
            float balB = _aggB.sumBal / n;
            SetBar(_balance, balA * 100f, balB * 100f, 100f, "{0:F0}%");

            float pushA = Mathf.Clamp(_aggA.sumPush / Mathf.Max(1, _aggA.touchSamples), 0f, AVG_PUSH_MAX);
            float pushB = Mathf.Clamp(_aggB.sumPush / Mathf.Max(1, _aggB.touchSamples), 0f, AVG_PUSH_MAX);
            SetBar(_push, pushA, pushB, AVG_PUSH_MAX, "{0:F0} N");

            PaintMannequin(_mannA, manager.wrestlerA);
            PaintMannequin(_mannB, manager.wrestlerB);

            // Was "MATCHES 0–0 · LONGEST RD 0s", which reset every match and so read
            // 0–0 through the whole bout while the career screen said 68 matches.
            // The round number and the target are what a viewer actually needs.
            _footer.text = $"ROUND {manager.RoundNumber} · FIRST TO {manager.PointsToWin}";
        }

        private static Color DominanceColour(float dominance)
        {
            if (dominance >= 55f) return Systems_UiKit.Good;
            if (dominance <= 45f) return Systems_UiKit.Bad;
            return Systems_UiKit.TextHi;
        }

        private static void SetBar(BarPair bar, float valueA, float valueB, float scale, string format)
        {
            bar.fillA.style.width = Length.Percent(Mathf.Clamp01(valueA / scale) * 100f);
            bar.fillB.style.width = Length.Percent(Mathf.Clamp01(valueB / scale) * 100f);
            bar.valueA.text = string.Format(format, valueA);
            bar.valueB.text = string.Format(format, valueB);
        }
    }
}
