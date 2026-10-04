using System.Collections.Generic;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// The debug surface behind the DBG button in the bottom-left corner, and the
    /// answer to "what are these agents actually doing".
    ///
    /// It is deliberately NOT a second perf overlay. Systems_PerfHud already
    /// reports the engine — frame time, GC, draw calls, SetPass — and that readout
    /// answers a question about the BUILD. This one answers a question about the
    /// FIGHTERS, and it is written for someone holding a phone rather than someone
    /// reading a profiler: every row is a plain-language statement with the number
    /// that backs it, and the three graphs are the three things about a PoSumo
    /// fighter that change over the course of a bout and decide it.
    ///
    /// Two modes, chosen by whether a match referee exists in the scene:
    ///
    ///  - LIVE (the arena). Per fighter: which brain is driving it, one sentence
    ///    saying what it is doing and what that means, and three 30-second graphs —
    ///    STAMINA (the fatigue model, which is invisible everywhere else),
    ///    MAT (how much floor is left ahead of it, the thing that decides almost
    ///    every round), and EFFORT (how hard the policy is pushing the joints).
    ///  - CAREER (the bracket). Every fighter that has a record, as a rank / elo /
    ///    win-loss / streak table — the same "over time" question at the scale of a
    ///    tournament rather than a round.
    ///
    /// Three rules keep it from becoming the thing it measures, the same three
    /// Systems_PerfHud follows:
    ///
    ///  - it samples on a timer, not per frame;
    ///  - it writes text and heights onto RETAINED elements and never rebuilds the
    ///    hierarchy, which in UI Toolkit is the equivalent of a full Canvas rebuild;
    ///  - it formats through one reused StringBuilder.
    ///
    /// And a fourth of its own: while the panel is hidden it does no sampling at
    /// all beyond the ring buffers, so the cost of shipping it in a release build
    /// is a display:none element and a 2 Hz array write.
    public sealed class Systems_AgentDebug : MonoBehaviour
    {
        /// Seconds between samples. 2 Hz over HISTORY samples is a 30-second
        /// window — comfortably longer than a round (measured mean 18.0 s), so a
        /// whole bout is on screen at once and the shape of it can be read after
        /// the fact rather than only while it happens.
        private const float SAMPLE_INTERVAL = 0.5f;
        private const int HISTORY = 60;
        private const int GRAPH_HEIGHT = 26;

        /// Below this share of the mat ahead, a fighter is one shove from losing.
        private const float EDGE_DANGER = 0.5f;
        /// Below this stamina a fighter is materially weaker for the rest of the
        /// bout — the fatigue model tops out at FATIGUE_DEPTH 0.35, so a spent
        /// joint still delivers 65% and "spent" is a slope, not a cliff.
        private const float STAMINA_LOW = 0.35f;
        /// Below this uprightness the torso is far enough off vertical that the
        /// next contact decides where the fighter goes, not the policy.
        private const float UPRIGHT_LOW = 0.55f;

        /// The mat observation is scaled against this constant everywhere in
        /// Agent_Biped, so the MAT graph uses it too and the graph and the policy
        /// are reading the same ruler.
        private const float MAT_REFERENCE = 3.5f;

        /// Torso height above the mat in the standing pose (CLAUDE.md, measured).
        private const float STANDING_HEIGHT = 1.06f;
        /// Below this smoothed torso height a fighter is on its knees or lower.
        /// A real crouch still carries the torso at ~0.8 m; the shipped crawl was
        /// measured at 0.26-0.62 m.
        private const float KNEES_BELOW = 0.7f;
        /// Posture samples (2 Hz) before the height is trusted enough to report.
        private const int MIN_HEIGHT_SAMPLES = 6;
        /// Rounds needed before a "share of rounds" is worth stating as one.
        private const int MIN_ROUNDS_FOR_SHARE = 3;
        /// A physics step costing more than this is worth a line. The step is
        /// 20 ms of game time; measured cost with two fighters is well under
        /// 1 ms, so 5 ms means something has gone wrong, not that it is busy.
        private const float PHYSICS_MS_HIGH = 5f;
        /// Frames of Physics2D.Simulate the recorder keeps — about half a second
        /// at 60 FPS, i.e. one sample interval.
        private const int PHYSICS_WINDOW = 30;

        private Systems_GameMatchManager _manager;
        private Systems_ScreenChrome _chrome;

        private VisualElement _attentionBox;
        private Label _attention;
        private float _fightHeightA, _fightHeightB;
        private int _heightSamplesA, _heightSamplesB;
        private ProfilerRecorder _physicsStep;

        private VisualElement _panel;
        private Label _headline;
        private FighterCard _cardA, _cardB;
        private VisualElement _careerBody;

        private readonly StringBuilder _sb = new StringBuilder(256);
        private float _nextSample;

        /// Whether the panel is showing. STATIC so the choice survives the scene
        /// load between bracket bouts — this component is spawned fresh per match,
        /// exactly like Systems_PerfHud, and being made to re-open it after every
        /// bout is what makes a diagnostic go unused. Cleared on
        /// SubsystemRegistration because domain reload is off in this project.
        private static bool s_visible;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatic()
        {
            s_visible = false;
        }

        /// One fighter's retained elements. Built once; only text, colours and bar
        /// heights are written afterwards.
        private sealed class FighterCard
        {
            public Label Name;
            public Label Brain;
            public Label Verdict;
            public Label Advice;
            public Label Career;
            public Track Stamina;
            public Track Mat;
            public Track Effort;
        }

        /// One graph plus its caption and current value. The history is a ring
        /// buffer, so the oldest sample is drawn leftmost and the newest rightmost
        /// without shuffling the array on every push.
        private sealed class Track
        {
            public readonly float[] Samples = new float[HISTORY];
            public int Head;
            public bool Warm;
            /// How many slots have ever been written. A slot that has not been
            /// written is NOT a zero reading — it is no reading — and the two have
            /// to be drawn differently or a graph that has only just started looks
            /// like a fighter with no stamina and no mat left.
            public int Count;
            public VisualElement[] Bars;
            public Label Value;

            public void Push(float value01)
            {
                Samples[Head] = Mathf.Clamp01(value01);
                Head = (Head + 1) % HISTORY;
                if (Count < HISTORY)
                {
                    Count++;
                }
                if (Head == 0)
                {
                    Warm = true;
                }
            }

            /// Mean of the oldest third against the mean of the newest third.
            /// A single first-to-last comparison reads one noisy sample against
            /// another; thirds give a trend that survives a fighter being shoved.
            public float Trend()
            {
                int third = HISTORY / 3;
                float oldSum = 0f, newSum = 0f;
                for (int step = 0; step < third; step++)
                {
                    oldSum += Samples[(Head + step) % HISTORY];
                    newSum += Samples[(Head + HISTORY - 1 - step) % HISTORY];
                }
                return (newSum - oldSum) / third;
            }
        }

        /// Builds the panel onto a full-bleed, safe-area-inset layer and returns
        /// the component driving it. The layer is the same one Systems_ScreenChrome
        /// uses, so the panel and the button that opens it cannot drift apart.
        public static Systems_AgentDebug Attach(Transform owner, VisualElement layer,
                                                Systems_GameMatchManager manager)
        {
            if (layer == null)
            {
                return null;
            }
            var go = new GameObject("AgentDebug");
            if (owner != null)
            {
                go.transform.SetParent(owner, false);
            }
            Systems_AgentDebug panel = go.AddComponent<Systems_AgentDebug>();
            panel._manager = manager;
            // Only the arena has physics worth timing; the bracket screen runs
            // none. Named by string because the marker is Unity's own — it is
            // absent from a release player, where Valid is simply false.
            if (manager != null)
            {
                panel._physicsStep = ProfilerRecorder.StartNew(
                    ProfilerCategory.Physics2D, "Physics2D.Simulate", PHYSICS_WINDOW);
            }
            panel.Build(layer);
            return panel;
        }

        /// Lets the chrome tint its DBG chip to match the panel's state.
        public void BindChrome(Systems_ScreenChrome chrome)
        {
            _chrome = chrome;
            if (_chrome != null)
            {
                _chrome.SetDebugActive(s_visible);
            }
        }

        public void Toggle()
        {
            s_visible = !s_visible;
            ApplyVisibility();
        }

        /// Shows or hides the engine overlay and closes this panel on the way, so
        /// the two diagnostics are never stacked on top of each other.
        private void ToggleEngineOverlay()
        {
            Systems_PerfHud perf = FindAnyObjectByType<Systems_PerfHud>();
            if (perf == null)
            {
                return;
            }
            perf.Toggle();
            s_visible = false;
            ApplyVisibility();
        }

        private void ApplyVisibility()
        {
            if (_panel != null)
            {
                _panel.style.display = s_visible ? DisplayStyle.Flex : DisplayStyle.None;
            }
            if (_chrome != null)
            {
                _chrome.SetDebugActive(s_visible);
            }
            // Repaint immediately rather than waiting up to half a second for the
            // next tick, or the panel opens showing the state it had when it was
            // last closed.
            if (s_visible)
            {
                Refresh();
            }
        }

        private void Build(VisualElement layer)
        {
            // Inset from the corners so the chrome's own four corner items stay
            // readable with the panel open. The DBG button that opened it is in the
            // bottom-left, and a panel covering its own toggle is a trap.
            _panel = Systems_UiKit.ElevatedCard(Systems_UiKit.Elevation.Overlay,
                                                Systems_UiKit.RADIUS_LG);
            _panel.style.position = Position.Absolute;
            _panel.style.left = Systems_UiKit.SPACE_3;
            _panel.style.right = Systems_UiKit.SPACE_3;
            _panel.style.top = Systems_UiKit.TOUCH_MIN + Systems_UiKit.SPACE_4;
            _panel.style.bottom = Systems_UiKit.TOUCH_MIN + Systems_UiKit.SPACE_4;
            _panel.style.display = DisplayStyle.None;

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            // Vertical mode alone still leaves the horizontal scroller on Auto, and
            // a row a few points too wide then draws a bar across the panel.
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _panel.Add(scroll);

            VisualElement content = scroll.contentContainer;
            content.style.paddingLeft = Systems_UiKit.SPACE_3;
            content.style.paddingRight = Systems_UiKit.SPACE_3;
            content.style.paddingTop = Systems_UiKit.SPACE_3;
            content.style.paddingBottom = Systems_UiKit.SPACE_3;

            Label heading = Systems_UiKit.Text("AGENT TELEMETRY", Systems_UiKit.FONT_LEAD,
                                               Systems_UiKit.Gold, true);
            content.Add(heading);

            // NEEDS ATTENTION — above everything else, and absent when there is
            // nothing to say. The rest of the panel is a readout: it lists what is
            // true and leaves the reader to notice which line matters. This block
            // is the opposite — only the lines that are WRONG, in plain words, so
            // "the fighters are on their knees" is the first thing read rather
            // than a 0.41 buried in a column. A warm border marks it as the one
            // part of the panel that is asking for something.
            _attentionBox = Systems_UiKit.ElevatedCard(Systems_UiKit.Elevation.Raised);
            _attentionBox.Pad(Systems_UiKit.SPACE_3, Systems_UiKit.SPACE_2);
            _attentionBox.style.marginTop = Systems_UiKit.SPACE_2;
            _attentionBox.style.marginBottom = Systems_UiKit.SPACE_2;
            _attentionBox.style.borderLeftWidth = 4;
            _attentionBox.style.borderLeftColor = Systems_UiKit.Warn;
            _attentionBox.style.display = DisplayStyle.None;
            _attentionBox.Add(Systems_UiKit.Text("NEEDS ATTENTION", Systems_UiKit.FONT_MICRO,
                                                 Systems_UiKit.Warn, true));
            _attention = Systems_UiKit.Text("", Systems_UiKit.FONT_SMALL, Systems_UiKit.TextHi);
            _attention.style.whiteSpace = WhiteSpace.Normal;
            _attentionBox.Add(_attention);
            content.Add(_attentionBox.NoPickTree());

            _headline = Systems_UiKit.Text("", Systems_UiKit.FONT_SMALL, Systems_UiKit.TextMid);
            _headline.style.whiteSpace = WhiteSpace.Normal;
            _headline.style.marginBottom = Systems_UiKit.SPACE_2;
            content.Add(_headline);

            if (_manager != null)
            {
                _cardA = BuildFighterCard(content);
                _cardB = BuildFighterCard(content);
            }
            else
            {
                _careerBody = Systems_UiKit.Column();
                content.Add(_careerBody);
            }

            content.Add(Systems_UiKit.Divider());

            // The way through to Systems_PerfHud, which used to own the bottom-left
            // corner and now has no button of its own. It only exists in the Editor
            // and in development builds, so the row is built only when one is there
            // — a control that silently does nothing on a release phone is worse
            // than an absent one.
            //
            // Looked up rather than injected: this panel is attached from the
            // manager's Start and the perf HUD is a sibling companion whose own
            // Start has not necessarily run yet, so the reference is resolved on
            // press instead of now.
            if (FindAnyObjectByType<Systems_PerfHud>() != null)
            {
                Button engine = Systems_UiKit.QuietButton("ENGINE: frame time, GC, draw calls",
                                                          ToggleEngineOverlay);
                engine.style.marginBottom = Systems_UiKit.SPACE_2;
                content.Add(engine);
            }

            Label hint = Systems_UiKit.Text("DBG or the TAB key closes this panel.",
                                            Systems_UiKit.FONT_MICRO, Systems_UiKit.TextLow);
            content.Add(hint);

            // A ScrollView's content children inherit flex-shrink: 1, so a column
            // taller than the viewport is silently COMPRESSED to fit instead of
            // scrolling — no error, no scroller, just overlapping rows further
            // down. This is the documented trap that cost the bracket screen its
            // roster palette; the fix is the same one, applied once after the
            // content is built.
            for (int childIndex = 0; childIndex < content.childCount; childIndex++)
            {
                content[childIndex].style.flexShrink = 0;
            }

            layer.Add(_panel);
            ApplyVisibility();
        }

        private FighterCard BuildFighterCard(VisualElement parent)
        {
            var card = new FighterCard();

            VisualElement box = Systems_UiKit.ElevatedCard(Systems_UiKit.Elevation.Raised);
            box.style.marginBottom = Systems_UiKit.SPACE_3;
            box.Pad(Systems_UiKit.SPACE_3, Systems_UiKit.SPACE_2);

            card.Name = Systems_UiKit.Text("--", Systems_UiKit.FONT_BODY, Systems_UiKit.TextHi, true);
            box.Add(card.Name);

            card.Brain = Systems_UiKit.Text("", Systems_UiKit.FONT_MICRO, Systems_UiKit.TextLow);
            card.Brain.style.whiteSpace = WhiteSpace.Normal;
            box.Add(card.Brain);

            card.Verdict = Systems_UiKit.Text("", Systems_UiKit.FONT_SMALL, Systems_UiKit.TextHi, true);
            card.Verdict.style.whiteSpace = WhiteSpace.Normal;
            card.Verdict.style.marginTop = Systems_UiKit.SPACE_2;
            box.Add(card.Verdict);

            card.Advice = Systems_UiKit.Text("", Systems_UiKit.FONT_MICRO, Systems_UiKit.TextMid);
            card.Advice.style.whiteSpace = WhiteSpace.Normal;
            card.Advice.style.marginBottom = Systems_UiKit.SPACE_2;
            box.Add(card.Advice);

            card.Stamina = BuildTrack(box, "STAMINA");
            card.Mat = BuildTrack(box, "MAT AHEAD");
            card.Effort = BuildTrack(box, "EFFORT");

            card.Career = Systems_UiKit.Text("", Systems_UiKit.FONT_MICRO, Systems_UiKit.TextLow);
            card.Career.style.whiteSpace = WhiteSpace.Normal;
            card.Career.style.marginTop = Systems_UiKit.SPACE_2;
            box.Add(card.Career);

            parent.Add(box);
            return card;
        }

        /// One captioned graph. Bars flex rather than carrying a fixed width, so
        /// the graph fills whatever the panel resolves to — the panel scales on
        /// WIDTH, and a fixed bar width is only correct at one device size.
        private Track BuildTrack(VisualElement parent, string caption)
        {
            var track = new Track();

            VisualElement header = Systems_UiKit.Row();
            header.style.justifyContent = Justify.SpaceBetween;
            header.Add(Systems_UiKit.Text(caption, Systems_UiKit.FONT_MICRO, Systems_UiKit.TextLow, true));
            track.Value = Systems_UiKit.Text("--", Systems_UiKit.FONT_MICRO, Systems_UiKit.TextMid, true);
            header.Add(track.Value);
            parent.Add(header.NoPickTree());

            VisualElement graph = Systems_UiKit.Row(Align.FlexEnd);
            graph.style.height = GRAPH_HEIGHT;
            graph.style.marginBottom = Systems_UiKit.SPACE_2;

            track.Bars = new VisualElement[HISTORY];
            for (int barIndex = 0; barIndex < HISTORY; barIndex++)
            {
                var bar = new VisualElement();
                bar.style.flexGrow = 1;
                bar.style.flexBasis = 0;
                bar.style.marginRight = 1;
                bar.style.height = 1;
                bar.style.backgroundColor = Systems_UiKit.Track;
                track.Bars[barIndex] = bar;
                graph.Add(bar);
            }
            parent.Add(graph.NoPickTree());
            return track;
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextSample)
            {
                return;
            }
            _nextSample = Time.unscaledTime + SAMPLE_INTERVAL;

            // The ring buffers keep filling while the panel is closed, so opening
            // it mid-bout shows the thirty seconds that already happened rather
            // than starting from nothing. Only the FORMATTING is gated on
            // visibility, which is where the cost actually is.
            SampleFighter(_cardA, _manager != null ? _manager.wrestlerA : null);
            SampleFighter(_cardB, _manager != null ? _manager.wrestlerB : null);
            SamplePosture();

            if (s_visible)
            {
                Refresh();
            }
        }

        private void OnDestroy()
        {
            _physicsStep.Dispose();
        }

        /// Smoothed torso height above the mat per fighter, sampled only while a
        /// round is actually being fought. A countdown, a walk-in or a body lying
        /// limp after the bell would otherwise drag the figure toward whatever
        /// pose the ceremony left them in — the question this answers is "how tall
        /// do they FIGHT", not "where is the torso right now".
        private void SamplePosture()
        {
            if (_manager == null || !_manager.ScoringLive)
            {
                return;
            }
            SampleHeight(_manager.wrestlerA, ref _fightHeightA, ref _heightSamplesA);
            SampleHeight(_manager.wrestlerB, ref _fightHeightB, ref _heightSamplesB);
        }

        private static void SampleHeight(Agent_Biped agent, ref float smoothed, ref int samples)
        {
            if (agent == null)
            {
                return;
            }
            Agent_BipedBody body = agent.GetComponent<Agent_BipedBody>();
            if (body == null || body.Torso == null || body.IsLimp)
            {
                return;
            }
            float height = body.Torso.position.y - agent.arenaGroundY;
            // An exponential mean over roughly the last ten samples (5 s at 2 Hz):
            // long enough that one shove does not trip the line, short enough that
            // a fighter who gets up stops being reported as down.
            smoothed = samples == 0 ? height : Mathf.Lerp(smoothed, height, 0.1f);
            samples++;
        }

        /// Mean Physics2D.Simulate cost per step over the recorder's window, in
        /// milliseconds, or -1 when the marker is unavailable — which is every
        /// release build, where profiler markers are compiled out. The line it
        /// feeds simply does not appear there.
        private float PhysicsMsPerStep()
        {
            if (!_physicsStep.Valid || _physicsStep.Count == 0)
            {
                return -1f;
            }
            long nanoseconds = 0;
            long steps = 0;
            for (int sampleIndex = 0; sampleIndex < _physicsStep.Count; sampleIndex++)
            {
                ProfilerRecorderSample sample = _physicsStep.GetSample(sampleIndex);
                nanoseconds += sample.Value;
                steps += sample.Count;
            }
            return steps > 0 ? nanoseconds / (float)steps * 1e-6f : -1f;
        }

        /// The NEEDS ATTENTION block: only lines that are true, most serious
        /// first, and the whole box hidden when there are none.
        ///
        /// Thresholds are the measured ones, not round numbers. Standing torso
        /// height is 1.06 m and the shipped fighters were measured fighting at
        /// 0.26-0.62 m, so anything under KNEES_BELOW is the crawl, not a crouch.
        /// The round share is the "what fraction of rounds end before
        /// shrinkStartSeconds" measurement CLAUDE.md asks to be re-run before
        /// touching the fight — it was 6% — now simply kept running.
        private void RefreshAttention()
        {
            if (_attention == null)
            {
                return;
            }
            _sb.Clear();

            if (_manager != null)
            {
                AppendBrainLine(_manager.wrestlerA);
                AppendBrainLine(_manager.wrestlerB);
                AppendPostureLine(_manager.wrestlerA, _fightHeightA, _heightSamplesA);
                AppendPostureLine(_manager.wrestlerB, _fightHeightB, _heightSamplesB);
            }

            int rounds = Systems_SessionStats.Rounds;
            if (rounds >= MIN_ROUNDS_FOR_SHARE)
            {
                int early = Systems_SessionStats.RoundsBeforeShrink;
                if (early * 4 < rounds)
                {
                    AttentionLine();
                    _sb.Append("Only ").Append(early).Append(" of ").Append(rounds)
                       .Append(" rounds this session ended before the mat started closing. ")
                       .Append("The shrinking mat is deciding the rounds, not the fighters.");
                }
            }

            int stalls = Systems_SessionStats.WalkInStalls;
            if (stalls > 0)
            {
                AttentionLine();
                _sb.Append(stalls).Append(" of ").Append(Systems_SessionStats.WalkIns)
                   .Append(stalls == 1 ? " walk-in" : " walk-ins")
                   .Append(" this session stopped short of contact. ")
                   .Append("The fighters could not walk to each other.");
            }

            float physicsMs = PhysicsMsPerStep();
            if (physicsMs > PHYSICS_MS_HIGH)
            {
                AttentionLine();
                _sb.Append("Physics is slow: ").Append(physicsMs.ToString("F1"))
                   .Append(" ms per step, of a 20 ms budget.");
            }

            bool any = _sb.Length > 0;
            _attentionBox.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;
            if (any)
            {
                _attention.text = _sb.ToString();
            }
        }

        /// Separates lines with a blank one; the first line gets nothing.
        private void AttentionLine()
        {
            if (_sb.Length > 0)
            {
                _sb.Append("\n\n");
            }
        }

        private void AppendBrainLine(Agent_Biped agent)
        {
            if (agent == null || agent.useBot)
            {
                return;
            }
            if (agent.inferenceModel == null)
            {
                AttentionLine();
                _sb.Append(DisplayName(agent)).Append(" has no brain assigned and will not fight.");
            }
            else if (agent.BrainStale)
            {
                AttentionLine();
                _sb.Append(DisplayName(agent))
                   .Append("'s brain was rejected: it was trained for a different set of senses, ")
                   .Append("so this fighter will not move. It needs retraining.");
            }
        }

        private void AppendPostureLine(Agent_Biped agent, float height, int samples)
        {
            if (agent == null || samples < MIN_HEIGHT_SAMPLES || height >= KNEES_BELOW)
            {
                return;
            }
            AttentionLine();
            _sb.Append(DisplayName(agent)).Append(" is fighting on the knees (torso ")
               .Append(height.ToString("F2")).Append(" m up; standing is ")
               .Append(STANDING_HEIGHT.ToString("F2")).Append(" m).");
        }

        private static string DisplayName(Agent_Biped agent)
        {
            string name = string.IsNullOrEmpty(agent.displayNameOverride)
                ? agent.behaviorName
                : agent.displayNameOverride;
            return name != null ? name.ToUpperInvariant() : "FIGHTER";
        }

        private void SampleFighter(FighterCard card, Agent_Biped agent)
        {
            if (card == null || agent == null)
            {
                return;
            }
            Agent_BipedBody body = agent.GetComponent<Agent_BipedBody>();
            if (body == null || body.Torso == null)
            {
                return;
            }

            card.Stamina.Push(body.Stamina);
            card.Mat.Push(MatAhead(agent, body) / MAT_REFERENCE);
            card.Effort.Push(MeanEffort(agent));
        }

        /// Metres of mat between this fighter and the rim it is facing. Read off
        /// the agent's LIVE ringHalfWidth, which both referees now republish every
        /// time the mat is resized — so this shrinks with the floor, exactly as the
        /// observation the policy sees does.
        private static float MatAhead(Agent_Biped agent, Agent_BipedBody body)
        {
            float x = body.Torso.position.x - agent.arenaCenterX;
            return Mathf.Max(0f, agent.ringHalfWidth - Mathf.Abs(x));
        }

        /// Mean absolute action across the 13 joints — how hard the policy is
        /// driving, on the same 0..1 scale the actions are normalised to.
        private static float MeanEffort(Agent_Biped agent)
        {
            float[] actions = agent.LastActions;
            if (actions == null || actions.Length == 0)
            {
                return 0f;
            }
            float sum = 0f;
            for (int actionIndex = 0; actionIndex < actions.Length; actionIndex++)
            {
                sum += Mathf.Abs(actions[actionIndex]);
            }
            return Mathf.Clamp01(sum / actions.Length);
        }

        private void Refresh()
        {
            RefreshAttention();
            if (_manager != null)
            {
                RefreshHeadline();
                RefreshFighter(_cardA, _manager.wrestlerA, _manager.colorA);
                RefreshFighter(_cardB, _manager.wrestlerB, _manager.colorB);
            }
            else
            {
                RefreshCareer();
            }
        }

        /// What is deciding this round, said plainly.
        ///
        /// The round clock is off and the mat contracts at roughly 0.14 m/s, so
        /// almost every round is settled by the floor being withdrawn rather than
        /// by a push — measured, 16 of 17 rounds ran past the point the mat starts
        /// closing. That is legible on screen now via the MAT meter and the squeeze
        /// cue, but the NUMBER behind it belongs here.
        private void RefreshHeadline()
        {
            if (_headline == null)
            {
                return;
            }
            float live = _manager.CurrentRingHalfWidth;
            float full = _manager.ringHalfWidth;
            float shrunk = full > 0.01f ? 1f - (live / full) : 0f;

            _sb.Clear();
            _sb.Append("Round ").Append(_manager.RoundNumber);
            _sb.Append("  ·  score ").Append(_manager.ScoreA).Append('-').Append(_manager.ScoreB);
            _sb.Append("\nMat ").Append(live.ToString("F2")).Append(" m of ")
               .Append(full.ToString("F2")).Append(" m");
            if (shrunk > 0.02f)
            {
                _sb.Append("  ·  closed ").Append(Mathf.RoundToInt(shrunk * 100f)).Append('%');
                _sb.Append("\nThe mat is deciding this round, not a push.");
            }
            else
            {
                _sb.Append("\nMat still at full width.");
            }
            _headline.text = _sb.ToString();
        }

        private void RefreshFighter(FighterCard card, Agent_Biped agent, Color colour)
        {
            if (card == null)
            {
                return;
            }
            if (agent == null)
            {
                card.Name.text = "--";
                return;
            }
            Agent_BipedBody body = agent.GetComponent<Agent_BipedBody>();

            card.Name.text = string.IsNullOrEmpty(agent.displayNameOverride)
                ? agent.behaviorName
                : agent.displayNameOverride;
            card.Name.style.color = colour;

            // WHICH BRAIN, and the shape of the contract it was trained against.
            //
            // The observation vector has moved 44 -> 45 -> 51 over this project's
            // life and every move silently invalidated every brain that preceded
            // it — ML-Agents rejects a mismatched model and falls back to a limp
            // heuristic with no error a player would ever see. Printing the vector
            // shape next to the model name is what makes that visible on a phone.
            _sb.Clear();
            if (agent.useBot)
            {
                _sb.Append("brain: hand-coded bot (no neural policy)");
            }
            else if (agent.inferenceModel != null)
            {
                _sb.Append("brain: ").Append(agent.inferenceModel.name).Append(".onnx");
            }
            else
            {
                _sb.Append("brain: NONE");
            }
            _sb.Append("  ·  ").Append(agent.ResolvedObservationCount).Append(" obs · ")
               .Append(Agent_Biped.ActionCount).Append(" act · decides every ")
               .Append(agent.decisionPeriod).Append(" steps");
            card.Brain.text = _sb.ToString();

            AppendVerdict(card, agent, body);
            RefreshTrack(card.Stamina,
                         Mathf.RoundToInt(card.Stamina.Samples[LastIndex(card.Stamina)] * 100f),
                         "%", MeterKind.Resource);
            RefreshTrackMetres(card.Mat, card.Mat.Samples[LastIndex(card.Mat)] * MAT_REFERENCE);
            RefreshTrack(card.Effort,
                         Mathf.RoundToInt(card.Effort.Samples[LastIndex(card.Effort)] * 100f),
                         "%", MeterKind.Effort);
            RefreshCareerLine(card, agent);
        }

        private static int LastIndex(Track track)
        {
            return (track.Head + HISTORY - 1) % HISTORY;
        }

        /// The one sentence that says what this fighter is doing, and the one
        /// underneath that says what it means. Ordered by severity so the most
        /// important thing is always the thing on the first line — a readout that
        /// lists everything equally is a readout nobody acts on.
        private void AppendVerdict(FighterCard card, Agent_Biped agent, Agent_BipedBody body)
        {
            string verdict;
            string advice;
            Color colour;

            float stamina = body != null ? body.Stamina : 1f;
            float matAhead = body != null && body.Torso != null ? MatAhead(agent, body) : MAT_REFERENCE;
            float upright = body != null && body.Chest != null
                ? Mathf.Clamp01(Vector2.Dot(body.Chest.transform.up, Vector2.up))
                : 1f;
            float effort = MeanEffort(agent);

            if (!agent.useBot && agent.inferenceModel == null)
            {
                verdict = "No brain assigned.";
                advice = "This fighter is running ML-Agents' fallback heuristic, not a policy. "
                       + "Deploy an .onnx for " + agent.behaviorName + ".";
                colour = Systems_UiKit.Bad;
            }
            else if (body != null && body.IsLimp)
            {
                verdict = "Limp — the motors are off.";
                advice = "The round is already decided; this is the fall playing out.";
                colour = Systems_UiKit.TextLow;
            }
            else if (agent.OnFloor)
            {
                verdict = "Off the dohyo.";
                advice = "Any body part on the arena floor is the ring-out rule.";
                colour = Systems_UiKit.Bad;
            }
            else if (agent.IsDown)
            {
                verdict = "Down on the mat.";
                advice = "Cannot drive from here; the closing mat will squeeze it off the edge.";
                colour = Systems_UiKit.Bad;
            }
            else if (matAhead < EDGE_DANGER)
            {
                verdict = "Cornered — " + matAhead.ToString("F2") + " m of mat left.";
                advice = "One clean push and this is a ring-out.";
                colour = Systems_UiKit.Bad;
            }
            else if (stamina < STAMINA_LOW)
            {
                verdict = "Spent — " + Mathf.RoundToInt(stamina * 100f) + "% left in the joints.";
                advice = "Fatigue is capped at 35%, so it still has 65% torque — but it will "
                       + "lose any long exchange from here.";
                colour = Systems_UiKit.Warn;
            }
            else if (upright < UPRIGHT_LOW)
            {
                verdict = "Off balance.";
                advice = "The next contact decides where it goes, not the policy.";
                colour = Systems_UiKit.Warn;
            }
            else if (effort > 0.55f && matAhead > 1.5f)
            {
                verdict = "Driving forward with room behind it.";
                advice = "Working hard from a safe position — this is the winning shape.";
                colour = Systems_UiKit.Good;
            }
            else if (effort < 0.15f)
            {
                verdict = "Barely acting.";
                advice = "Near-zero output. Suspect a stale brain or a rejected model.";
                colour = Systems_UiKit.Warn;
            }
            else
            {
                verdict = "Holding position.";
                advice = "Nothing decisive happening; the mat will close on both of them.";
                colour = Systems_UiKit.TextMid;
            }

            card.Verdict.text = verdict;
            card.Verdict.style.color = colour;
            card.Advice.text = advice;
        }

        private void RefreshCareerLine(FighterCard card, Agent_Biped agent)
        {
            Systems_CareerStats.Record record = Systems_CareerStats.Get(agent.behaviorName);
            if (record == null)
            {
                card.Career.text = "";
                return;
            }
            _sb.Clear();
            _sb.Append(Systems_CareerLadder.NameFor(record));
            _sb.Append("  ·  elo ").Append(Mathf.RoundToInt(record.elo));
            _sb.Append("  ·  ").Append(record.matchWins).Append('-').Append(record.matchLosses);
            _sb.Append("  ·  rounds ").Append(record.roundWins).Append('-').Append(record.roundLosses);
            if (record.winStreak > 1)
            {
                _sb.Append("  ·  ").Append(record.winStreak).Append(" in a row");
            }
            if (record.titles > 0)
            {
                _sb.Append("  ·  ").Append(record.titles).Append(" title");
                if (record.titles > 1)
                {
                    _sb.Append('s');
                }
            }
            card.Career.text = _sb.ToString();
        }

        /// What a meter MEANS, which decides both how its trend reads and how its
        /// bars are coloured. Stamina and mat are resources: less is worse, and the
        /// graph should go red as they run out. Effort is not a resource — a
        /// fighter pushing hard is not in trouble — so colouring it on the same
        /// ramp painted a perfectly healthy 80% output bright red. It has exactly
        /// one bad reading, near-zero, which is the signature of a rejected model
        /// or a stale brain.
        private enum MeterKind
        {
            Resource,
            Effort,
        }

        private void RefreshTrack(Track track, int value, string suffix, MeterKind kind)
        {
            _sb.Clear();
            _sb.Append(value).Append(suffix).Append("  ")
               .Append(TrendWord(track, kind == MeterKind.Resource));
            track.Value.text = _sb.ToString();
            PaintBars(track, kind);
        }

        private void RefreshTrackMetres(Track track, float metres)
        {
            _sb.Clear();
            _sb.Append(metres.ToString("F2")).Append(" m  ").Append(TrendWord(track, true));
            track.Value.text = _sb.ToString();
            PaintBars(track, MeterKind.Resource);
        }

        /// Words rather than arrow glyphs. This project ships no font asset of its
        /// own, and an arrow that is missing from the device's default font renders
        /// as a box — the same reason Systems_UiKit draws the pause control as two
        /// bars instead of setting a pause character.
        private static string TrendWord(Track track, bool lowIsBad)
        {
            if (!track.Warm)
            {
                return "";
            }
            float trend = track.Trend();
            if (trend < -0.12f)
            {
                return lowIsBad ? "falling fast" : "easing off";
            }
            if (trend < -0.04f)
            {
                return "falling";
            }
            if (trend > 0.12f)
            {
                return lowIsBad ? "recovering" : "pushing harder";
            }
            if (trend > 0.04f)
            {
                return "rising";
            }
            return "steady";
        }

        /// Bars are coloured on the VALUE, not on the trend: the question a graph
        /// like this answers is "is this bad right now", and a fighter recovering
        /// from 10% is still at 10%.
        ///
        /// Slots that have never been written are drawn as a flat Track-coloured
        /// line rather than as a reading of zero. They used to fall through the
        /// same ramp as real samples, so the unfilled left-hand side of every graph
        /// came out BRIGHT RED — measured on a live bout, a healthy fighter fifteen
        /// seconds into a round appeared to have a long history of critical
        /// stamina and no mat. Same distinction Systems_PerfHud draws for its own
        /// empty bars, and for the same reason.
        private static void PaintBars(Track track, MeterKind kind)
        {
            int firstFilled = HISTORY - Mathf.Min(track.Count, HISTORY);
            for (int barIndex = 0; barIndex < HISTORY; barIndex++)
            {
                VisualElement bar = track.Bars[barIndex];
                if (barIndex < firstFilled)
                {
                    bar.style.height = 1;
                    bar.style.backgroundColor = Systems_UiKit.Track;
                    continue;
                }

                float sample = track.Samples[(track.Head + barIndex) % HISTORY];
                bar.style.height = Mathf.Max(1f, sample * GRAPH_HEIGHT);

                if (kind == MeterKind.Effort)
                {
                    // Neutral unless it is near zero, which is the one reading that
                    // means something is wrong rather than something is happening.
                    bar.style.backgroundColor = sample < 0.15f
                        ? Systems_UiKit.Bad
                        : Systems_UiKit.TextMid;
                    continue;
                }

                bar.style.backgroundColor = sample > 0.5f ? Systems_UiKit.Good
                                          : sample > 0.25f ? Systems_UiKit.Warn
                                          : Systems_UiKit.Bad;
            }
        }

        /// The bracket-screen mode: the same "how are these agents doing over
        /// time" question at the scale of a career rather than a round. Rebuilt on
        /// each open rather than retained, because it changes only between matches
        /// and the list length is not fixed.
        private void RefreshCareer()
        {
            if (_careerBody == null)
            {
                return;
            }
            _careerBody.Clear();

            List<Systems_CareerStats.Record> ranked = Systems_CareerStats.Ranked();
            if (ranked == null || ranked.Count == 0)
            {
                _headline.text = "No fighter has a decided match yet. Play a bracket and this "
                               + "fills in with each agent's rank, rating and form.";
                return;
            }

            _sb.Clear();
            _sb.Append(ranked.Count).Append(" rated fighters over ")
               .Append(Systems_CareerStats.MatchesPlayed).Append(" matches. Elo is zero-sum "
               + "and starts at 1000, so read the distance from 1000, not the number.");
            _headline.text = _sb.ToString();

            for (int rankIndex = 0; rankIndex < ranked.Count; rankIndex++)
            {
                Systems_CareerStats.Record record = ranked[rankIndex];

                VisualElement row = Systems_UiKit.ElevatedCard(Systems_UiKit.Elevation.Raised);
                row.style.marginBottom = Systems_UiKit.SPACE_2;
                row.style.flexShrink = 0;
                row.Pad(Systems_UiKit.SPACE_3, Systems_UiKit.SPACE_2);

                _sb.Clear();
                _sb.Append(rankIndex + 1).Append(". ").Append(record.fighter);
                Label name = Systems_UiKit.Text(_sb.ToString(), Systems_UiKit.FONT_BODY,
                                                Systems_UiKit.TextHi, true);
                row.Add(name);

                _sb.Clear();
                _sb.Append(Systems_CareerLadder.NameFor(record));
                _sb.Append("  ·  elo ").Append(Mathf.RoundToInt(record.elo));
                _sb.Append("  ·  ").Append(record.matchWins).Append('-').Append(record.matchLosses);
                _sb.Append("  ·  rounds ").Append(record.roundWins).Append('-')
                   .Append(record.roundLosses);
                Label detail = Systems_UiKit.Text(_sb.ToString(), Systems_UiKit.FONT_MICRO,
                                                  Systems_UiKit.TextMid);
                detail.style.whiteSpace = WhiteSpace.Normal;
                row.Add(detail);

                _sb.Clear();
                _sb.Append("best streak ").Append(record.bestStreak);
                _sb.Append("  ·  upsets ").Append(record.upsets);
                _sb.Append("  ·  titles ").Append(record.titles);
                Label form = Systems_UiKit.Text(_sb.ToString(), Systems_UiKit.FONT_MICRO,
                                                Systems_UiKit.TextLow);
                form.style.whiteSpace = WhiteSpace.Normal;
                row.Add(form);

                _careerBody.Add(row);
            }
        }
    }
}
