using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// Broadcast-style match stats. Stats aggregate over the WHOLE match (reset on
    /// rematch), sampled only while a round is live:
    ///   DOMINANCE — pairwise-normalized composite (territory/KD/shoves/balance)
    ///   TERRITORY — % of time the fight's midpoint is on the opponent's half
    ///   KD        — knockdowns dealt
    ///   PUSH      — time-averaged bar, averaged only while in contact
    ///
    /// Two surfaces, split by whether the fight is happening right now — plus one
    /// reparenting at match end:
    ///
    ///   LIVE STRIP (dock, always on) — the two damage mannequins flanking the
    ///     MAT meter, plus the round footer. Since the zero-scroll consolidation
    ///     it is ALSO the host for the two telemetry companions: the
    ///     win-probability row (Systems_TensionEngine) mounts into
    ///     `TensionAnchor` under the MAT meter, and each fighter's biometrics
    ///     caption + stamina sparkline (Systems_BiometricsCard) mounts into
    ///     `BioAnchorA`/`BioAnchorB` under its mannequin. The dock went from
    ///     three stacked cards to one instrument.
    ///   DETAIL CARD (stage, between rounds only) — the full aggregate table,
    ///     shown on RoundEnded and hidden again on RoundStarted. On MatchEnded it
    ///     MOVES INTO the manager's result card (`ResultStatsHost`) instead of
    ///     standing down: results merged with telemetry, one surface, one
    ///     dismissal — and it comes back to the Stage holder on the next
    ///     MatchReset.
    ///
    /// UI Toolkit only, drawn into the shared Systems_HudRoot.
    public sealed class Systems_FightHud : MonoBehaviour
    {
        public Systems_GameMatchManager manager;
        public PanelSettings panelSettings;
        // No visibility field. What is on screen is decided by the round state,
        // so there is no `startVisible` to serialize, no chip to press and no
        // state to get out of sync with the scene.

        private const float AVG_PUSH_MAX = 500f;    // bar scale for contact-averaged push
        private const float SHOVE_FORCE_N = 400f;   // momentum transfer that counts as a shove
        private const float SHOVE_COOLDOWN = 0.5f;  // seconds between countable shoves
        private const float TOUCH_DIST = 1.2f;      // torso distance treated as "in contact"
        private const float KD_REARM_SECONDS = 0.5f; // must stay up this long before next KD counts

        /// One metric, shown as [fighter A's value] METRIC [fighter B's value].
        private sealed class ComparePair
        {
            public Label a, b;
            public string shownA, shownB;
        }

        /// One metric drawn as a centre-out tug-of-war bar.
        private sealed class BarPair
        {
            public VisualElement track, fillA, fillB;
            public Label valueA, valueB;
            public string shownA, shownB;
        }

        /// Whole-match accumulators for one wrestler.
        private class Agg
        {
            public float sumBal, sumPush;
            public int territorySamples, touchSamples, shoves, kdDealt, kdSuffered;
            public float nextShoveTime, upSince;
            public bool downLatched;

            public void Reset()
            {
                sumBal = sumPush = 0f;
                territorySamples = touchSamples = shoves = kdDealt = kdSuffered = 0;
                nextShoveTime = 0f;
                upSince = 0f;
                downLatched = false;
            }
        }

        private readonly Agg _aggA = new Agg();
        private readonly Agg _aggB = new Agg();
        private int _samples;

        private Systems_HudRoot _hud;

        // ---- Telemetry anchors (one-strip dock) --------------------------------
        //
        // The dock used to carry THREE stacked cards: this live strip plus a
        // Systems_TensionEngine card and a Systems_BiometricsCard card. The
        // zero-scroll consolidation merged them into this one instrument strip, so
        // the two companions now draw INTO these anchors instead of mounting cards
        // of their own. Their GameTuning flags are unchanged — when a companion is
        // off or absent, its anchor simply stays empty.
        //
        // Created in Awake, not in BuildUi: a companion's Start may run before
        // this component's, and both look this component up with
        // FindAnyObjectByType<Systems_FightHud>() — so the anchors must exist by
        // the time any Start runs, and Awake guarantees that ordering. (Property
        // initializers looked equivalent but measured NULL on the live scene
        // instance in the first harness run — both companions silently fell back
        // to standalone dock cards. Awake assignment is the path this project
        // already trusts everywhere.) An anchor built early and parented later
        // carries any children with it — attaching children to an unparented
        // element is fine in UI Toolkit.

        /// Centre column, under the MAT meter: the win-probability row.
        public VisualElement TensionAnchor { get; private set; }

        /// Under the left mannequin: that fighter's biometrics caption + sparkline.
        public VisualElement BioAnchorA { get; private set; }

        /// Under the right mannequin.
        public VisualElement BioAnchorB { get; private set; }

        private void Awake()
        {
            TensionAnchor = Systems_UiKit.Column(Align.Center).NoPick();
            BioAnchorA = Systems_UiKit.Column(Align.Center).NoPick();
            BioAnchorB = Systems_UiKit.Column(Align.Center).NoPick();
        }

        // Live strip
        private VisualElement _liveCard;
        private Label _footer;
        private Label _matCaption;
        private VisualElement _matTrack;
        private VisualElement _matFill;
        /// Last mat fraction actually written to the bar. UI Toolkit style writes
        /// are not free and this is refreshed every frame, so the bar is only
        /// touched when it has moved a percentage point.
        private float _shownMat01 = -1f;

        // Between-rounds detail
        private VisualElement _detailCard;
        /// The absolute holder in the Stage band — the card returns here from the
        /// result card on the next MatchReset.
        private VisualElement _detailHolder;
        /// True while the table lives inside the manager's result card instead of
        /// the Stage holder (results merged with telemetry at match end).
        private bool _inResultCard;
        private bool _detailVisible;
        private ComparePair _territory, _knockdowns;
        private BarPair _push;

        // Display caches. Every one of these exists so the label is only written
        // when the value it shows actually changed: UpdateTable() used to rewrite
        // twelve interpolated strings, four bar widths and twelve swatch colours
        // on EVERY rendered frame, which is string allocation in a per-frame path
        // on an Android target.
        private int _shownRound = int.MinValue;
        private Color[] _mannShownA, _mannShownB;

        private Vector2 _prevVelA, _prevVelB;
        private Agent_BipedBody _bodyA, _bodyB;

        /// Live pairwise-normalized dominance (0-100, the pair sums to 100).
        /// Consumed by Systems_FaceMood, Systems_FighterVoice and
        /// the stats table; sampled every physics step.
        public float DominanceA { get; private set; } = 50f;
        public float DominanceB { get; private set; } = 50f;

        // Subscribed in OnEnable / unsubscribed in OnDisable, as every companion
        // should be. The manager is spawned before this component (it is what
        // spawns us), but `manager` may still be null on the FIRST OnEnable when
        // the field was not assigned — Start does the lookup and subscribes then;
        // a later re-enable finds `manager` set and subscribes here. Unsubscribe
        // first so a Start-after-OnEnable pair can never double-subscribe.
        private bool _subscribed;

        private void OnEnable()
        {
            Subscribe();
        }

        private void Start()
        {
            if (manager == null) manager = FindAnyObjectByType<Systems_GameMatchManager>();
            Subscribe();
            BuildUi();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void Subscribe()
        {
            if (manager == null || _subscribed) return;
            manager.MatchReset += ResetAggregates;
            manager.RoundStarted += OnRoundStarted;
            manager.RoundEnded += OnRoundEnded;
            manager.MatchEnded += OnMatchEnded;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (manager == null || !_subscribed) return;
            manager.MatchReset -= ResetAggregates;
            manager.RoundStarted -= OnRoundStarted;
            manager.RoundEnded -= OnRoundEnded;
            manager.MatchEnded -= OnMatchEnded;
            _subscribed = false;
        }

        private void ResetAggregates()
        {
            _aggA.Reset();
            _aggB.Reset();
            _samples = 0;
            // A rematch takes the table OUT of the previous match's result card
            // and back to the Stage holder, hidden — otherwise the next match
            // would play with the last one's stats parked in a modal host.
            MoveDetailToStage();
        }

        private void OnRoundStarted()
        {
            // Belt and braces with ResetAggregates: whatever path gets us back
            // into a live round, the stats table belongs to the Stage again.
            MoveDetailToStage();
            ShowDetail(false);
            ResolveBodies();
        }

        /// Caches the two `Agent_BipedBody`s off the manager's current wrestlers.
        ///
        /// Called on every RoundStarted rather than lazily from FixedUpdate, so the
        /// 50 Hz sampling path carries no GetComponent at once a round is live.
        /// FixedUpdate still re-resolves if either cache is null — the first physics
        /// step can land before the first RoundStarted, and a destroyed body reads
        /// as null through Unity's == override after a scene load.
        private void ResolveBodies()
        {
            Agent_Biped a = manager != null ? manager.wrestlerA : null;
            Agent_Biped b = manager != null ? manager.wrestlerB : null;
            _bodyA = a != null ? a.GetComponent<Agent_BipedBody>() : null;
            _bodyB = b != null ? b.GetComponent<Agent_BipedBody>() : null;
        }

        /// The aggregate table is a between-rounds read, so this is exactly when
        /// it belongs on screen — and the stage band it appears in is empty from
        /// here until the next countdown.
        private void OnRoundEnded(Agent_Biped winner, Agent_Biped loser) { ShowDetail(true); }

        /// Match end belongs to the result card — and since the zero-scroll
        /// consolidation the table MOVES INTO it rather than standing down.
        ///
        /// It used to hide here: the table pinned high over the dohyo and the
        /// result modal low, two surfaces competing for the same moment with a
        /// dead band between them. Merging results with telemetry (checklist #1)
        /// means the aggregate table becomes a section OF the result card — one
        /// surface, one dismissal. Falls back to the old hide when the manager has
        /// no stats host (defensive: a scene-built manager without the field).
        private void OnMatchEnded(Agent_Biped winner)
        {
            if (!MoveDetailToResult())
            {
                ShowDetail(false);
            }
        }

        // ---- Detail-card routing (Stage holder <-> result card) ---------------

        /// Reparents the stats table into the manager's result card and shows it.
        /// Returns false when there is no host to move to.
        private bool MoveDetailToResult()
        {
            if (_detailCard == null)
            {
                return false;
            }
            if (_inResultCard)
            {
                return true;
            }
            if (manager == null || manager.ResultStatsHost == null)
            {
                return false;
            }
            _detailCard.RemoveFromHierarchy();
            // Inside the card it shares the card's width instead of floating at
            // 92% of the Stage.
            _detailCard.style.width = Length.Percent(100f);
            manager.ResultStatsHost.Add(_detailCard);
            _inResultCard = true;
            _detailVisible = false;
            // A pending FadeOutHide (from the round start that hid it) must not
            // hide it out from under the card: this is the "re-shown mid-fade"
            // case the fade's completion guard exists for, and opacity is reset
            // so any in-flight fade sees a non-transparent element and stands down.
            _detailCard.style.opacity = 1f;
            _detailCard.style.display = DisplayStyle.Flex;
            RefreshDetail();
            return true;
        }

        /// Returns the table to the Stage holder, hidden. Idempotent.
        private void MoveDetailToStage()
        {
            if (_detailCard == null || !_inResultCard || _detailHolder == null)
            {
                return;
            }
            _detailCard.RemoveFromHierarchy();
            _detailCard.style.width = Length.Percent(92f);
            _detailCard.style.display = DisplayStyle.None;
            _detailCard.style.opacity = 1f;
            _detailHolder.Add(_detailCard);
            _inResultCard = false;
            _detailVisible = false;
        }

        // ---- Build ---------------------------------------------------------

        private void BuildUi()
        {
            // Own PanelSettings if the scene assigned one, otherwise the match
            // manager's — whichever component's Start runs first builds the root,
            // and that order is undefined.
            PanelSettings settings = panelSettings != null ? panelSettings
                : manager != null ? manager.panelSettings : null;
            _hud = Systems_HudRoot.Ensure(transform, settings);

            BuildLiveStrip();
            BuildDetailCard();
        }

        /// The always-on strip: damage left, MAT + win-prob centre, damage right,
        /// in one card. Everything here is readable at a glance without reading —
        /// two figures changing colour, a bar shrinking from both ends, a meter
        /// leaning one way, and two sparklines tracing stamina.
        private void BuildLiveStrip()
        {
            // Raised, not Overlay: the live strip is docked furniture that is
            // always on screen, so it belongs to the page rather than floating
            // over it. The result card below is the one that floats.
            //
            // Padding diet (zero-scroll consolidation): SPACE_4/SPACE_2 ->
            // SPACE_3/SPACE_1. The strip now carries the mannequins, the MAT
            // meter, the win-probability row AND both biometrics sparklines — the
            // merged instrument is taller than the old strip, so its own frame
            // gives back what it gained. Touch targets are untouched.
            _liveCard = Systems_UiKit.ElevatedCard(Systems_UiKit.Elevation.Raised).NoPick();
            _liveCard.Pad(Systems_UiKit.SPACE_3, Systems_UiKit.SPACE_1);
            _liveCard.style.marginBottom = Systems_UiKit.SPACE_2;

            _mannA = BuildMannequin(out VisualElement figureA);
            _mannB = BuildMannequin(out VisualElement figureB);
            _mannShownA = new Color[Systems_BodyDamage.REGION_COUNT];
            _mannShownB = new Color[Systems_BodyDamage.REGION_COUNT];

            // The mannequins are damage-coloured (green->amber->red), so they carry
            // no fighter identity of their own — left and right were two identical
            // green figures. A team-coloured base under each one labels it without
            // touching the damage ramp, which has to stay readable as damage.
            // The biometrics anchor rides below the base: that fighter's PK/AD
            // caption and stamina sparkline live here now (was a third dock card).
            VisualElement left = Systems_UiKit.Column(Align.Center).NoPick();
            left.Add(figureA);
            left.Add(TeamBase(manager.colorA));
            left.Add(BioAnchorA);
            VisualElement right = Systems_UiKit.Column(Align.Center).NoPick();
            right.Add(figureB);
            right.Add(TeamBase(manager.colorB));
            right.Add(BioAnchorB);

            // The centre used to carry the DOMINANCE tug-of-war bar and its two
            // numbers. Removed 2026-08-26 at the player's request: DominanceA/B are
            // still computed every step — Systems_CrowdMomentum and
            // Systems_FaceMood read them — and the DBG panel prints them.
            //
            // It now carries the MAT meter, which is the one number this game was
            // not showing and badly needed. Measured over 17 logged rounds, 16 of
            // them ran past `shrinkStartSeconds` and were decided by the mat
            // closing rather than by a push — mean round 18.0 s against a shrink
            // that starts at 8 s. The contraction was the actual referee and
            // nothing on screen said so, so a round read as two fighters milling
            // about until one inexplicably fell off. Telling the player how much
            // clay is left turns that into visible, rising pressure.
            VisualElement centre = Systems_UiKit.Column(Align.Center).NoPick();
            _matCaption = Systems_UiKit.Caption("MAT", Systems_UiKit.FONT_MICRO,
                                                Systems_UiKit.TextLow);
            centre.Add(_matCaption);
            _matTrack = new VisualElement();
            _matTrack.style.height = 6;
            _matTrack.style.width = Length.Percent(100f);
            _matTrack.style.backgroundColor = Systems_UiKit.Track;
            _matTrack.Round(3);
            _matTrack.style.marginTop = Systems_UiKit.SPACE_1;
            _matTrack.style.overflow = Overflow.Hidden;
            // The fill is centred and shrinks from BOTH ends, because that is what
            // the mat itself does — a bar that empties from one side would read as
            // a clock, which is the one thing this is not.
            _matTrack.style.alignItems = Align.Center;
            _matFill = new VisualElement();
            _matFill.style.height = 6;
            _matFill.style.width = Length.Percent(100f);
            _matFill.style.backgroundColor = Systems_UiKit.Good;
            _matFill.Round(3);
            _matTrack.Add(_matFill);
            centre.Add(_matTrack);
            // The win-probability row (Systems_TensionEngine) mounts here — the
            // second dock card merged into the strip. Empty when the companion's
            // flag is off; costs one empty element.
            centre.Add(TensionAnchor);

            _liveCard.Add(Systems_UiKit.Triplet(left, centre, right));

            // One footer for the whole strip. Compact badges (checklist #2): the
            // verbose "ROUND 2 · FIRST TO 3" is a 200pt string in a micro slot;
            // "R2 · FT3" says the same at half the width, and FIRST TO is a rule
            // the pause card still states in full.
            _footer = Systems_UiKit.Caption("", Systems_UiKit.FONT_MICRO, Systems_UiKit.TextLow);
            _footer.style.marginTop = Systems_UiKit.SPACE_1;
            _liveCard.Add(_footer);

            _liveCard.NoPickTree();
            _hud.Dock.Add(_liveCard);
        }

        /// The full aggregate table, shown between rounds in the stage band.
        private void BuildDetailCard()
        {
            // Overlay: this card appears over the dohyo on RoundEnded and has to
            // read as being in front of the fight, not part of the dock.
            _detailCard = Systems_UiKit
                .ElevatedCard(Systems_UiKit.Elevation.Overlay, Systems_UiKit.RADIUS_LG).NoPick();
            // Padding diet: SPACE_4/SPACE_3 -> SPACE_3/SPACE_2. The card moved
            // INTO the result card at match end, where every point of its frame
            // comes straight off the modal's budget.
            _detailCard.Pad(Systems_UiKit.SPACE_3, Systems_UiKit.SPACE_2);
            _detailCard.style.width = Length.Percent(92);
            _detailCard.style.maxWidth = 520;
            _detailCard.style.display = DisplayStyle.None;

            BuildHeader();
            _detailCard.Add(Systems_UiKit.Divider());

            // Three metrics, not six. SHOVES · BEST, WORK RATE and BALANCE were
            // cut: shoves and balance already reach the player as the DOMINANCE
            // bar that is on screen the whole match (RawDominance weights them
            // 0.20 and 0.15), so the card was restating its own inputs, and a
            // work-rate percentage never changed how anyone played a round. What
            // is left is the three things that decide a bout — where the fight is
            // happening, who is putting the other down, and who is pushing harder.
            _territory = CompareRow("TERRITORY", Systems_UiKit.FONT_BODY);
            // "KNOCKDOWNS" alone reads as knockdowns SUFFERED, which inverts the
            // table: the winner of a 3-0 showed 4 against the loser's 8 and looked
            // beaten on his own result screen. These are knockdowns dealt.
            _knockdowns = CompareRow("KNOCKDOWNS DEALT", Systems_UiKit.FONT_BODY);

            _detailCard.Add(Systems_UiKit.Divider());
            _push = MirrorBarRow("PUSH IN CONTACT");

            // This one sits over the arena rather than at the screen edge, so its
            // labels must not hit-test either.
            _detailCard.NoPickTree();

            // PINNED TO THE BOTTOM OF THE STAGE BAND, not centred in it.
            //
            // In flow the card inherited Stage's `justifyContent: Center` and landed
            // squarely over the dohyo, hiding the two fighters at the exact moment
            // the player wants to see how the round ended. The stage band's lower
            // portion is empty backdrop at gameplay framing, so the card costs
            // nothing there.
            //
            // An ABSOLUTE holder, not `marginTop: auto` on the card. An auto margin
            // absorbs the band's free space and would drag `_centre` — the countdown
            // and the "X SCORES!" banner — up to the top of the stage with it, and
            // those are the two things that genuinely belong centred over the arena.
            // Taking the card out of flow leaves them exactly where they were.
            //
            // The holder is what carries `alignItems: Center`: an absolutely
            // positioned element does not inherit the parent's centring, so the card
            // alone would have pinned to the left edge. Bottom offset (not 0) keeps
            // it clear of the Dock's live strip directly below — absolute
            // offsets resolve against the parent's PADDING box, and Stage has no
            // padding, so `bottom: 0` is precisely the Dock's top edge.
            var holder = new VisualElement().NoPick();
            holder.style.position = Position.Absolute;
            holder.style.left = 0;
            holder.style.right = 0;
            holder.style.bottom = Systems_UiKit.SPACE_3;
            holder.style.alignItems = Align.Center;
            holder.Add(_detailCard);
            _detailHolder = holder;
            _hud.Stage.Add(holder);
        }

        private void BuildHeader()
        {
            Label a = Systems_UiKit.Text(manager.nameA, Systems_UiKit.FONT_BODY, manager.colorA, true);
            a.style.unityTextAlign = TextAnchor.MiddleLeft;

            Label title = Systems_UiKit.Caption("MATCH STATS", Systems_UiKit.FONT_MICRO,
                                                Systems_UiKit.TextLow, true);

            Label b = Systems_UiKit.Text(manager.nameB, Systems_UiKit.FONT_BODY, manager.colorB, true);
            b.style.unityTextAlign = TextAnchor.MiddleRight;

            _detailCard.Add(Systems_UiKit.Triplet(a, title, b));
        }

        private ComparePair CompareRow(string label, int valueSize)
        {
            var pair = new ComparePair
            {
                a = SideValue(valueSize, TextAnchor.MiddleLeft),
                b = SideValue(valueSize, TextAnchor.MiddleRight),
            };
            Label name = Systems_UiKit.Caption(label, Systems_UiKit.FONT_SMALL, Systems_UiKit.TextLow);

            VisualElement row = Systems_UiKit.Triplet(pair.a, name, pair.b);
            // +6, not +10: padding diet (checklist #2). The row is one line of
            // 24pt type — four points of lead was spacing, not air.
            row.style.height = valueSize + 6;
            _detailCard.Add(row);
            return pair;
        }

        private static Label SideValue(int fontSize, TextAnchor align)
        {
            Label value = Systems_UiKit.Text("—", fontSize, Systems_UiKit.TextHi, true);
            value.style.unityTextAlign = align;
            return value;
        }

        /// A caption row plus one track that fills outward from the centre.
        private BarPair MirrorBarRow(string label)
        {
            BarPair pair = MakeTugBar(12);
            pair.valueA = Systems_UiKit.Text("", Systems_UiKit.FONT_SMALL, Systems_UiKit.TextLow);
            pair.valueB = Systems_UiKit.Text("", Systems_UiKit.FONT_SMALL, Systems_UiKit.TextLow);
            pair.valueB.style.unityTextAlign = TextAnchor.MiddleRight;

            Label name = Systems_UiKit.Caption(label, Systems_UiKit.FONT_SMALL, Systems_UiKit.TextLow);

            VisualElement caption = Systems_UiKit.Triplet(pair.valueA, name, pair.valueB);
            caption.style.marginTop = Systems_UiKit.SPACE_1;
            _detailCard.Add(caption);

            pair.track.style.marginTop = Systems_UiKit.SPACE_1;
            pair.track.style.marginBottom = Systems_UiKit.SPACE_1;
            _detailCard.Add(pair.track);
            return pair;
        }

        /// A single tug-of-war track: A's share grows leftward in A's colour, B's
        /// rightward in B's, so who leads is a shape and not a subtraction.
        private BarPair MakeTugBar(int height)
        {
            var pair = new BarPair();
            VisualElement track = Systems_UiKit.Row();
            track.style.height = height;
            track.style.backgroundColor = Systems_UiKit.Track;
            track.style.overflow = Overflow.Hidden;
            track.Round(3);

            pair.track = track;
            pair.fillA = HalfFill(track, Justify.FlexEnd, manager.colorA);
            pair.fillB = HalfFill(track, Justify.FlexStart, manager.colorB);
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

        private void ShowDetail(bool visible)
        {
            if (_detailCard == null || _detailVisible == visible) return;
            _detailVisible = visible;
            if (visible)
            {
                _detailCard.style.display = DisplayStyle.Flex;
                RefreshDetail();
                _detailCard.RiseIn(24f);
            }
            else
            {
                // Animated exit instead of a hard cut. FadeOutHide's completion
                // guard keeps a stale hide from killing a card that was re-shown
                // mid-fade (round restarts can land inside the 140 ms window).
                _detailCard.FadeOutHide();
            }
        }

        // ---- Sampling ------------------------------------------------------

        private void FixedUpdate()
        {
            var a = manager != null ? manager.wrestlerA : null;
            var b = manager != null ? manager.wrestlerB : null;
            if (a == null || b == null) return;
            if (_bodyA == null || _bodyB == null)
            {
                ResolveBodies();
                if (_bodyA == null || _bodyB == null) return;
            }

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
                SampleFighter(_aggA, _bodyA, cx, midX, touching, pushA, now);
                SampleFighter(_aggB, _bodyB, cx, midX, touching, pushB, now);

                // Knockdown attribution: your fall is their KD dealt.
                TrackKnockdown(_aggA, _aggB, a.IsDown, now);
                TrackKnockdown(_aggB, _aggA, b.IsDown, now);

                _samples++;
            }

            _prevVelA = velA; _prevVelB = velB;
        }

        private void SampleFighter(Agg agg, Agent_BipedBody body,
                           float centerX, float midX, bool touching, float push, float now)
        {
            // Balance is the only per-frame posture sample kept: it is a term in
            // RawDominance. sumSpd / sumLean / sumEdge were accumulated here and
            // read by nothing at all, and sumWork fed only the deleted WORK RATE
            // row — that one also cost a 13-iteration loop over LastActions every
            // frame for a number nobody acted on.
            agg.sumBal += Mathf.Clamp01(Vector2.Dot(body.Chest.transform.up, Vector2.up));

            // Field position: the fight's midpoint sits on the opponent's half
            // (which lies in this wrestler's facing direction).
            if ((midX - centerX) * body.facingSign > 0f) agg.territorySamples++;

            if (touching)
            {
                agg.sumPush += push;
                agg.touchSamples++;
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

            // Pure float maths, no allocation — and Systems_FaceMood and
            // Systems_FighterVoice read these every frame whether the HUD is
            // showing them or not.
            float domA = RawDominance(_aggA, _aggB, _samples);
            float domB = RawDominance(_aggB, _aggA, _samples);
            float domTotal = Mathf.Max(0.0001f, domA + domB);
            DominanceA = domA / domTotal * 100f;
            DominanceB = domB / domTotal * 100f;

            if (_liveCard == null) return;
            UpdateLiveStrip();

            // The detail table is only on screen between rounds, when sampling has
            // stopped and nothing it shows can change — so it is refreshed once as
            // it appears. This guard exists only for the case where it is somehow
            // still up while a round is live.
            if (_detailVisible && manager.ScoringLive) RefreshDetail();
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

        /// A short team-coloured bar sat under a mannequin so the two figures can be
        /// told apart. Deliberately not a tint ON the figure: the figure's colour is
        /// the damage reading.
        private static VisualElement TeamBase(Color team)
        {
            var bar = new VisualElement().NoPick();
            bar.style.width = 34f;
            bar.style.height = 3f;
            bar.style.marginTop = Systems_UiKit.SPACE_1;
            bar.style.backgroundColor = team;
            bar.Round(2);
            return bar;
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

        // Takes the body rather than resolving it: this runs for both fighters on
        // every rendered frame, and the class already caches _bodyA/_bodyB for its
        // FixedUpdate sampling — so the GetComponent here was pure repeat work.
        // `shown` caches the last colour written per region, because damage changes
        // a handful of times a round and this ran twelve style writes a frame.
        private void PaintMannequin(VisualElement[] parts, Color[] shown, Agent_BipedBody body)
        {
            if (parts == null || shown == null) return;
            Systems_BodyDamage damage = body != null ? Systems_BodyDamage.For(body) : null;
            if (damage == null) return;

            for (int regionIndex = 0; regionIndex < Systems_BodyDamage.REGION_COUNT; regionIndex++)
            {
                var region = (Systems_BodyDamage.Region)regionIndex;
                Color colour;
                if (damage.RegionDetached(region))
                {
                    colour = DamageGone;
                }
                else
                {
                    float t = damage.RegionDamage01(region);
                    // Two-stop ramp so amber is a real waypoint rather than a colour
                    // the bar passes through in one frame.
                    colour = t < 0.5f
                        ? Color.Lerp(DamageGreen, DamageAmber, t * 2f)
                        : Color.Lerp(DamageAmber, DamageRed, (t - 0.5f) * 2f);
                }
                if (colour == shown[regionIndex]) continue;
                shown[regionIndex] = colour;
                parts[regionIndex].style.backgroundColor = colour;
            }
        }

        private void UpdateLiveStrip()
        {
            int round = manager.RoundNumber;
            if (round != _shownRound)
            {
                _shownRound = round;
                _footer.text = $"R{round} · FT{manager.PointsToWin}";
            }

            PaintMannequin(_mannA, _mannShownA, _bodyA);
            PaintMannequin(_mannB, _mannShownB, _bodyB);
            UpdateMatMeter();
        }

        /// How much clay is left, as a share of the mat the round opened on.
        ///
        /// Green while there is room, amber as the tawara comes in, red once the
        /// mat is past half gone — the same three-stop ramp as the damage
        /// mannequins beside it, so the strip reads as one instrument rather than
        /// two unrelated ones. The bar shrinks from both ends because the mat does.
        private void UpdateMatMeter()
        {
            if (_matFill == null) return;

            float full = Mathf.Max(0.01f, manager.ringHalfWidth);
            float mat01 = Mathf.Clamp01(manager.CurrentRingHalfWidth / full);
            if (Mathf.Abs(mat01 - _shownMat01) < 0.01f) return;
            _shownMat01 = mat01;

            _matFill.style.width = Length.Percent(mat01 * 100f);
            _matFill.style.backgroundColor = mat01 > 0.5f
                ? Color.Lerp(Systems_UiKit.Warn, Systems_UiKit.Good, (mat01 - 0.5f) * 2f)
                : Color.Lerp(Systems_UiKit.Bad, Systems_UiKit.Warn, mat01 * 2f);
            // Metres, not a percentage: the fighters are about 0.4 m wide, so
            // "MAT 1.8m" is something a viewer can measure the bodies against.
            _matCaption.text = $"MAT {manager.CurrentRingHalfWidth * 2f:F1}m";
            _matCaption.style.color = mat01 > 0.5f ? Systems_UiKit.TextLow : Systems_UiKit.TextHi;
        }

        private void RefreshDetail()
        {
            float n = Mathf.Max(1, _samples);

            SetCompare(_territory, $"{_aggA.territorySamples / n * 100f:F0}%",
                                   $"{_aggB.territorySamples / n * 100f:F0}%");
            // One number per side, not "dealt–suffered": A's dealt IS B's suffered,
            // so the old pair printed the same two figures mirrored on both sides
            // (2–3 on the left, 3–2 on the right) and read as four separate stats.
            SetCompare(_knockdowns, _aggA.kdDealt.ToString(), _aggB.kdDealt.ToString());

            // The Max(1, ...) guard turns "the fighters never got within TOUCH_DIST"
            // into a printed "0 N", which is indistinguishable from "they clinched
            // the whole round and neither generated any push". A live match showed
            // 0 N on BOTH sides while TERRITORY and KNOCKDOWNS populated normally,
            // and there was no way to tell from the card which of the two it was —
            // territory and knockdowns are sampled unconditionally, push only while
            // touching, so an empty contact sample is the obvious suspect and the
            // card actively hid it. An em dash says "never measured"; 0 N now only
            // ever means a real, measured zero.
            bool anyContact = _aggA.touchSamples > 0 || _aggB.touchSamples > 0;
            if (!anyContact)
            {
                SetBar(_push, 0f, 0f, AVG_PUSH_MAX, "—");
                return;
            }
            float pushA = Mathf.Clamp(_aggA.sumPush / Mathf.Max(1, _aggA.touchSamples), 0f, AVG_PUSH_MAX);
            float pushB = Mathf.Clamp(_aggB.sumPush / Mathf.Max(1, _aggB.touchSamples), 0f, AVG_PUSH_MAX);
            SetBar(_push, pushA, pushB, AVG_PUSH_MAX, "{0:F0} N");
        }

        private static void SetCompare(ComparePair pair, string valueA, string valueB)
        {
            if (pair.shownA != valueA)
            {
                pair.shownA = valueA;
                pair.a.text = valueA;
            }
            if (pair.shownB != valueB)
            {
                pair.shownB = valueB;
                pair.b.text = valueB;
            }
        }

        private static void SetBar(BarPair bar, float valueA, float valueB, float scale, string format)
        {
            bar.fillA.style.width = Length.Percent(Mathf.Clamp01(valueA / scale) * 100f);
            bar.fillB.style.width = Length.Percent(Mathf.Clamp01(valueB / scale) * 100f);

            string textA = string.Format(format, valueA);
            string textB = string.Format(format, valueB);
            if (bar.shownA != textA)
            {
                bar.shownA = textA;
                bar.valueA.text = textA;
            }
            if (bar.shownB != textB)
            {
                bar.shownB = textB;
                bar.valueB.text = textB;
            }
        }
    }
}
