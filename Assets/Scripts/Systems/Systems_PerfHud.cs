using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// Real-time diagnostic overlay: frame time, FPS, GC pressure, render counters
    /// (draw calls, batches, SetPass, triangles, vertices), screen mode, physics
    /// cost and per-fighter stamina. HIDDEN by default; the DBG button in the
    /// lower-left of the stage shows and hides it, and the choice persists across
    /// the bouts of a bracket.
    ///
    /// `Systems_Telemetry` already computes most of this and publishes it twice — as
    /// JSON on `http://127.0.0.1:<port>/metrics` and into ML-Agents' `StatsRecorder`
    /// — but neither surface is visible while you are actually watching the game.
    /// This is the third surface: the one you can read without a second window.
    ///
    /// Spawned by `Systems_GameMatchManager` behind `enablePerfHud`; never placed in
    /// a scene.
    ///
    /// **Three rules keep the overlay from becoming the thing it measures:**
    ///
    /// - It samples on a timer (`SAMPLE_INTERVAL`), not every frame. A per-frame
    ///   readout is unreadable anyway — the numbers blur — and it would put string
    ///   formatting in the hottest path in the project.
    /// - It writes `.text` on RETAINED elements and never rebuilds the hierarchy.
    ///   Rebuilding per update is UI Toolkit's equivalent of a full Canvas rebuild,
    ///   which is exactly the cost a perf overlay must not add.
    /// - It formats through one reused `StringBuilder`, the same discipline
    ///   `Systems_Telemetry` uses on its socket thread, so the overlay itself does
    ///   not generate the GC pressure it is reporting.
    ///
    /// Positioned ABSOLUTELY over the stage band rather than docked into a layout
    /// slot, so it competes with nothing. The top bar's three slots are spoken for
    /// (the centre carries the score) and the dock carries the live strip and crowd
    /// meter; a diagnostic panel in either either overlaps gameplay UI or gets its
    /// own numbers clipped. Both were measured on live captures before this moved.
    public sealed class Systems_PerfHud : MonoBehaviour
    {
        /// Seconds between refreshes. 4 Hz is fast enough to see a hitch land and
        /// slow enough to read.
        private const float SAMPLE_INTERVAL = 0.25f;

        /// Frame-time thresholds in milliseconds. The Android target is 60 FPS
        /// (16.7 ms), so amber starts where a 60 FPS budget is already gone and red
        /// where the frame has missed 30 FPS as well.
        /// Bars in the rolling frame-time graph. At the 0.25 s sample interval
        /// this is 12 seconds of history — long enough to see a hitch land and
        /// still be on screen while you look away and back.
        private const int GRAPH_BARS = 48;
        /// Graph ceiling in milliseconds. A bar at full height is a 50 ms frame
        /// (20 FPS); anything worse clamps rather than rescaling the whole graph,
        /// because an auto-scaling graph makes a good run and a terrible one look
        /// identical.
        private const float GRAPH_MAX_MS = 50f;
        private const int GRAPH_HEIGHT = 22;

        private const float MS_GOOD = 16.7f;
        private const float MS_WARN = 33.3f;

        private Systems_GameMatchManager _manager;
        private Agent_BipedBody _bodyA, _bodyB;

        private VisualElement _panel;
        private Button _toggle;
        private Label _frame, _memory, _render, _screen, _sim, _fighters;
        private Label _assets;
        /// Rolling frame-time history. Retained bars whose HEIGHT is rewritten
        /// per sample — never rebuilt, which would be UI Toolkit's equivalent of
        /// a full Canvas rebuild inside the thing measuring the frame.
        private VisualElement[] _graphBars;
        private readonly float[] _graphMs = new float[GRAPH_BARS];
        private int _graphHead;
        private Label _agentA, _agentB;
        private Systems_FightHud _fightHud;
        private bool _fightHudLookedUp;

        // Render statistics straight from the profiler counters. These are live in
        // the Editor and in Development builds — the only places this component is
        // spawned (see the developmentBuild gate in Systems_GameMatchManager).
        private ProfilerRecorder _drawCalls, _batches, _triangles, _vertices, _setPass;

        /// Whether the readout is showing. STATIC so the choice survives the scene
        /// load between bracket bouts (this component is spawned fresh per match);
        /// cleared on SubsystemRegistration like every static here, because domain
        /// reload is off. Hidden by default — the DBG button in the lower-left of
        /// the stage shows it (2026-08-26; it used to be always on).
        private static bool s_visible;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatic() { s_visible = false; }

        private readonly StringBuilder _sb = new StringBuilder(512);

        private float _nextSample;
        private int _framesSinceSample;
        private float _timeSinceSample;
        private float _worstMsThisWindow;

        /// Seconds of heap samples averaged into the displayed allocation rate.
        /// Long enough to span the gaps between collections (measured at ~4 s apart)
        /// so the number reads as a trend rather than as noise.
        private const float ALLOC_WINDOW = 3f;

        /// Amber above this. A phone can absorb a few MB/s; it is a sustained tens
        /// of MB/s that turns into visible GC hitching.
        private const float ALLOC_WARN_MB_PER_SECOND = 12f;

        private long _allocWindowBytes;
        private float _allocWindowSeconds;
        private float _allocRateMbPerSecond;
        private long _lastGcBytes;

        private void Awake()
        {
            _manager = GetComponentInParent<Systems_GameMatchManager>();
        }

        private void Start()
        {
            if (_manager != null)
            {
                if (_manager.wrestlerA != null)
                    _bodyA = _manager.wrestlerA.GetComponent<Agent_BipedBody>();
                if (_manager.wrestlerB != null)
                    _bodyB = _manager.wrestlerB.GetComponent<Agent_BipedBody>();
            }
            _lastGcBytes = System.GC.GetTotalMemory(false);
            // Counter names measured on 6000.5.6f1 via ProfilerRecorderHandle.GetAvailable:
            // there is no plain "Draw Calls Count" / "Batches Count" in the Render
            // category, only the per-path ones below.
            _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Standard Draw Calls Count");
            _batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SRP Batcher Draw Calls Count");
            _triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            _vertices = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Vertices Count");
            _setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            BuildUi();
        }

        private void OnDestroy()
        {
            _drawCalls.Dispose();
            _batches.Dispose();
            _triangles.Dispose();
            _vertices.Dispose();
            _setPass.Dispose();
        }

        /// Show/hide the readout. Bound to the DBG button; public so a test or the
        /// MCP bridge can flip it without simulating a tap.
        public void Toggle()
        {
            s_visible = !s_visible;
            ApplyVisibility();
        }

        private void ApplyVisibility()
        {
            if (_panel != null)
            {
                _panel.style.display = s_visible ? DisplayStyle.Flex : DisplayStyle.None;
            }
            if (_toggle != null)
            {
                _toggle.style.color = s_visible ? Systems_UiKit.Gold : Systems_UiKit.TextLow;
            }
        }

        private void BuildUi()
        {
            var hud = FindAnyObjectByType<Systems_HudRoot>();
            if (hud == null || hud.Stage == null) return;

            _panel = Systems_UiKit.Column(Align.FlexStart);
            _panel.style.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
            _panel.style.paddingLeft = Systems_UiKit.SPACE_2;
            _panel.style.paddingRight = Systems_UiKit.SPACE_2;
            _panel.style.paddingTop = Systems_UiKit.SPACE_1;
            _panel.style.paddingBottom = Systems_UiKit.SPACE_1;
            _panel.Round(Systems_UiKit.RADIUS_SM);
            // ABSOLUTE, over the stage — not a slot in the top bar.
            //
            // The top bar is a three-slot row whose CENTRE carries the score. Docked
            // in the left slot this panel either ran under the fighter names
            // (unconstrained) or had its numbers clipped mid-value (constrained to
            // 34%), both measured on live captures. Neither is acceptable for a
            // readout whose entire job is to be read.
            //
            // Absolute positioning takes it out of that competition: it floats over
            // the stage band, which at gameplay framing is empty backdrop above the
            // dohyo, and it cannot push any gameplay UI around. Offsets resolve
            // against the parent's PADDING box, which is what we want here — the
            // stage already carries the safe-area inset.
            _panel.style.position = Position.Absolute;
            _panel.style.left = Systems_UiKit.SPACE_2;
            _panel.style.top = Systems_UiKit.SPACE_2;
            // Must never eat a pointer-down meant for the content behind it.
            _panel.NoPickTree();

            _frame = Line();
            _memory = Line();
            _render = Line();
            _screen = Line();
            _sim = Line();
            _fighters = Line();

            _assets = Line();

            _panel.Add(_frame);
            _panel.Add(BuildGraph());
            _panel.Add(_memory);
            _panel.Add(_render);
            _panel.Add(_screen);
            _panel.Add(_sim);
            _panel.Add(_assets);
            _panel.Add(_fighters);

            // Per-agent detail: one column per fighter, refreshed on the same 4 Hz
            // timer. Everything here is read off state the agents already keep;
            // nothing is computed for the panel's sake.
            VisualElement agents = Systems_UiKit.Row(Align.FlexStart).NoPick();
            agents.style.marginTop = Systems_UiKit.SPACE_1;
            _agentA = Line();
            _agentB = Line();
            _agentA.style.marginRight = Systems_UiKit.SPACE_3;
            _agentA.style.whiteSpace = WhiteSpace.Normal;
            _agentB.style.whiteSpace = WhiteSpace.Normal;
            agents.Add(_agentA);
            agents.Add(_agentB);
            _panel.Add(agents);

            hud.Stage.Add(_panel);

            // The DBG toggle: lower-left of the stage, just above the dock. The
            // stage itself is NoPick, which excludes only the stage element — a
            // child button still receives its own taps.
            // True lower-left screen corner: on the HudRoot overlay layer, which
            // sits above the dock. (It was on the stage before, i.e. above the dock
            // strip rather than in the corner.)
            _toggle = Systems_UiKit.ChipButton("DBG", Toggle, Systems_UiKit.TOUCH_MIN);
            _toggle.name = "DebugToggle";
            _toggle.style.position = Position.Absolute;
            _toggle.style.left = Systems_UiKit.SPACE_2;
            _toggle.style.bottom = Systems_UiKit.SPACE_2;
            _toggle.style.fontSize = Systems_UiKit.FONT_MICRO;
            _toggle.style.opacity = 0.85f;
            (hud.Overlay ?? hud.Stage).Add(_toggle);

            ApplyVisibility();
        }

        /// The rolling frame-time graph: GRAPH_BARS thin columns in a bottom-
        /// aligned row. Built once; only `height` and `backgroundColor` are
        /// written afterwards.
        private VisualElement BuildGraph()
        {
            VisualElement graph = Systems_UiKit.Row(Align.FlexEnd);
            graph.style.height = GRAPH_HEIGHT;
            graph.style.marginTop = 2;
            graph.style.marginBottom = 2;

            _graphBars = new VisualElement[GRAPH_BARS];
            for (int barIndex = 0; barIndex < GRAPH_BARS; barIndex++)
            {
                var bar = new VisualElement();
                bar.style.width = 3;
                bar.style.marginRight = 1;
                bar.style.height = 1;
                bar.style.backgroundColor = Systems_UiKit.Good;
                _graphBars[barIndex] = bar;
                graph.Add(bar);
            }
            return graph.NoPickTree();
        }

        /// Pushes one sample and repaints the bars. The history is a ring buffer,
        /// so the OLDEST sample is drawn leftmost and the newest rightmost without
        /// shuffling the array every sample.
        private void PushGraphSample(float worstMs)
        {
            if (_graphBars == null) return;

            _graphMs[_graphHead] = worstMs;
            _graphHead = (_graphHead + 1) % GRAPH_BARS;

            for (int barIndex = 0; barIndex < GRAPH_BARS; barIndex++)
            {
                float ms = _graphMs[(_graphHead + barIndex) % GRAPH_BARS];
                VisualElement bar = _graphBars[barIndex];
                if (ms <= 0f)
                {
                    bar.style.height = 1;
                    bar.style.backgroundColor = Systems_UiKit.Track;
                    continue;
                }
                float fill = Mathf.Clamp01(ms / GRAPH_MAX_MS);
                bar.style.height = Mathf.Max(1f, fill * GRAPH_HEIGHT);
                bar.style.backgroundColor = ms > MS_WARN ? Systems_UiKit.Bad
                                          : ms > MS_GOOD ? Systems_UiKit.Warn
                                          : Systems_UiKit.Good;
            }
        }

        /// Scene-load-scoped caches for the asset row. Re-scanned on a slow timer
        /// rather than every sample: these are FindObjectsByType calls, and the one
        /// thing a performance overlay must not do is become the cost it reports.
        private AudioSource[] _audioSources;
        private ParticleSystem[] _particleSystems;
        private int _lightCount = -1;
        private float _nextRescan;

        /// Pressure on the three budgets that fail SILENTLY in this project.
        ///
        /// Each of these has a real failure mode with no error attached: the blood
        /// particle budget is sized against two simultaneous wounds and simply
        /// STARVES when exceeded (the pour thins instead of erroring), audio voices
        /// past the platform limit are dropped without a log line, and a Light2D
        /// count that creeps up costs a pass per light on an Android target. None
        /// of the three shows up in frame time until it is already bad.
        private void RefreshAssets()
        {
            if (_assets == null) return;

            // 5 s rescan. Companions are spawned in Start and particle systems are
            // built once, so the counts only change on a scene load — but a rescan
            // is what makes this correct across the bouts of a bracket without
            // needing to subscribe to anything.
            if (Time.unscaledTime >= _nextRescan)
            {
                _nextRescan = Time.unscaledTime + 5f;
                _audioSources = FindObjectsByType<AudioSource>(FindObjectsSortMode.None);
                _particleSystems = FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None);
                _lightCount = FindObjectsByType<UnityEngine.Rendering.Universal.Light2D>(
                    FindObjectsSortMode.None).Length;
            }

            int voices = 0;
            if (_audioSources != null)
            {
                for (int sourceIndex = 0; sourceIndex < _audioSources.Length; sourceIndex++)
                {
                    AudioSource source = _audioSources[sourceIndex];
                    if (source != null && source.isPlaying) voices++;
                }
            }

            int particles = 0;
            if (_particleSystems != null)
            {
                for (int systemIndex = 0; systemIndex < _particleSystems.Length; systemIndex++)
                {
                    ParticleSystem system = _particleSystems[systemIndex];
                    if (system != null) particles += system.particleCount;
                }
            }

            _sb.Clear();
            _sb.Append("voices ").Append(voices)
               .Append(" parts ").Append(particles)
               .Append(" lights ").Append(_lightCount);
            _assets.text = _sb.ToString();
        }

        private Label Line()
        {
            Label l = Systems_UiKit.Caption("", Systems_UiKit.FONT_MICRO, Systems_UiKit.TextMid);
            l.style.unityTextAlign = TextAnchor.MiddleLeft;
            return l;
        }

        private void Update()
        {
            // Accumulate every frame — the WORST frame in the window is the number
            // that matters, and a 4 Hz sample of the instantaneous deltaTime would
            // miss every hitch it is meant to catch.
            _framesSinceSample++;
            _timeSinceSample += Time.unscaledDeltaTime;
            _worstMsThisWindow = Mathf.Max(_worstMsThisWindow, Time.unscaledDeltaTime * 1000f);

            if (Time.unscaledTime < _nextSample) return;
            _nextSample = Time.unscaledTime + SAMPLE_INTERVAL;

            // Hidden = no formatting at all; the accumulators above still run so
            // the first visible sample after a toggle is honest.
            if (s_visible) Refresh();

            _framesSinceSample = 0;
            _timeSinceSample = 0f;
            _worstMsThisWindow = 0f;
        }

        private void Refresh()
        {
            if (_frame == null) return;

            float avgMs = _framesSinceSample > 0
                ? (_timeSinceSample / _framesSinceSample) * 1000f : 0f;
            float fps = avgMs > 0.001f ? 1000f / avgMs : 0f;

            _sb.Clear();
            _sb.Append(Mathf.RoundToInt(fps)).Append("fps ")
               .Append(avgMs.ToString("F0")).Append('/')
               .Append(_worstMsThisWindow.ToString("F0")).Append("ms");
            _frame.text = _sb.ToString();
            // Colour on the PEAK, not the average: a smooth average hiding a 40 ms
            // spike is the case worth flagging.
            _frame.style.color = _worstMsThisWindow <= MS_GOOD ? Systems_UiKit.Good
                               : _worstMsThisWindow <= MS_WARN ? Systems_UiKit.Warn
                               : Systems_UiKit.Bad;

            long gcNow = System.GC.GetTotalMemory(false);
            long delta = gcNow - _lastGcBytes;
            _lastGcBytes = gcNow;

            // SUSTAINED rate over ALLOC_WINDOW, not the instantaneous one.
            //
            // This used to print `delta / SAMPLE_INTERVAL` from a single 0.25 s
            // window, and only when the delta was POSITIVE. Both halves of that
            // inflate it: a window that straddles a collection is discarded rather
            // than netted off, so the readout samples only the peaks of a bursty
            // allocator; and dividing by the CONSTANT rather than the measured
            // elapsed time over-reports again whenever a frame overruns, which on
            // this project is most of them (measured frames of 380 ms).
            //
            // MEASURED 2026-08-25: the HUD read a steady "+280 MB/s" during a live
            // match while the heap's actual growth was ~19 MB/s and only ~13 MB/s of
            // that was the Editor itself — with just 2 gen-0 collections in 8
            // seconds, so there was no hidden gross allocation to explain the gap.
            // The overlay was crying wolf by roughly 15x, which is worse than not
            // having it: it points at a GC emergency that is not there and hides the
            // real one if it ever arrives.
            //
            // Negative deltas are now KEPT, so a collection nets against the
            // allocation that preceded it and what is displayed is real growth.
            _allocWindowBytes += delta;
            _allocWindowSeconds += _timeSinceSample;
            if (_allocWindowSeconds >= ALLOC_WINDOW)
            {
                _allocRateMbPerSecond = _allocWindowBytes / 1048576f / _allocWindowSeconds;
                _allocWindowBytes = 0L;
                _allocWindowSeconds = 0f;
            }

            _sb.Clear();
            _sb.Append("mono ").Append((gcNow / 1048576f).ToString("F0")).Append("MB");
            if (_allocRateMbPerSecond > 0.05f)
            {
                _sb.Append(" +").Append(_allocRateMbPerSecond.ToString("F1")).Append("MB/s");
            }
            _memory.text = _sb.ToString();
            // The golden rule is zero allocation in Update/FixedUpdate, but a live
            // Editor session never reaches zero and amber-on-any-growth made the
            // line permanently amber and therefore unreadable as a signal. Amber now
            // means a rate that would actually matter on a phone.
            _memory.style.color = _allocRateMbPerSecond > ALLOC_WARN_MB_PER_SECOND
                ? Systems_UiKit.Warn
                : Systems_UiKit.TextMid;

            // Render counters. LastValue is the previous frame's total; -1 when the
            // counter is unavailable (a non-development player), printed as "--".
            _sb.Clear();
            _sb.Append("draw ");
            AppendCount(_drawCalls);
            _sb.Append(" srp ");
            AppendCount(_batches);
            _sb.Append(" setpass ");
            AppendCount(_setPass);
            _sb.Append("\ntris ");
            AppendCount(_triangles);
            _sb.Append(" verts ");
            AppendCount(_vertices);
            _render.text = _sb.ToString();

            _sb.Clear();
            _sb.Append("screen ").Append(Screen.width).Append('x').Append(Screen.height)
               .Append(" @").Append(Screen.currentResolution.refreshRateRatio.value.ToString("F0")).Append("Hz")
               .Append(" vsync ").Append(QualitySettings.vSyncCount)
               .Append(" target ").Append(Application.targetFrameRate);
            _screen.text = _sb.ToString();

            _sb.Clear();
            _sb.Append("phys ").Append((Time.fixedDeltaTime * 1000f).ToString("F0")).Append("ms x")
               .Append(Time.timeScale.ToString("F1"));
            if (_manager != null)
            {
                _sb.Append(' ').Append(_manager.RoundActive ? "live" : "idle");
            }
            _sim.text = _sb.ToString();

            PushGraphSample(_worstMsThisWindow);
            RefreshAssets();

            _sb.Clear();
            _sb.Append("stam ");
            AppendStamina(_bodyA);
            _sb.Append('/');
            AppendStamina(_bodyB);
            _fighters.text = _sb.ToString();

            if (!_fightHudLookedUp)
            {
                _fightHudLookedUp = true;
                _fightHud = FindAnyObjectByType<Systems_FightHud>();
            }
            if (_manager != null)
            {
                RefreshAgent(_agentA, _manager.wrestlerA, _bodyA, _fightHud != null ? _fightHud.DominanceA : -1f, _manager.colorA);
                RefreshAgent(_agentB, _manager.wrestlerB, _bodyB, _fightHud != null ? _fightHud.DominanceB : -1f, _manager.colorB);
            }
        }

        /// One fighter's column. Brain, task mode, where it is on the mat, how it
        /// is standing, how much it has left, and the referee-facing flags — the
        /// things you want when a fighter is doing something you cannot explain.
        private void RefreshAgent(Label label, Agent_Biped agent, Agent_BipedBody body, float dominance, Color colour)
        {
            if (label == null) return;
            if (agent == null || body == null)
            {
                label.text = "--";
                return;
            }
            label.style.color = colour;

            _sb.Clear();
            _sb.Append(agent.behaviorName).Append('\n');
            _sb.Append(agent.useBot ? "brain BOT" : agent.inferenceModel != null ? "brain " + agent.inferenceModel.name : "brain NONE");
            _sb.Append(" ").Append(agent.mode == Agent_Biped.Mode.Sumo ? "sumo" : "walk").Append('\n');

            Rigidbody2D torso = body.Torso;
            float x = torso.position.x - agent.arenaCenterX;
            float edge = agent.ringHalfWidth - Mathf.Abs(x);
            _sb.Append("x ").Append(x.ToString("F2")).Append(" edge ").Append(edge.ToString("F2")).Append('\n');
            _sb.Append("y ").Append((torso.position.y - agent.arenaGroundY).ToString("F2"));
            float upright = body.Chest != null ? Vector2.Dot(body.Chest.transform.up, Vector2.up) : 0f;
            _sb.Append(" up ").Append(Mathf.RoundToInt(Mathf.Clamp01(upright) * 100f)).Append("%\n");
            _sb.Append("v ").Append(torso.linearVelocity.magnitude.ToString("F2")).Append("m/s");
            _sb.Append(" spin ").Append(Mathf.RoundToInt(torso.angularVelocity)).Append("°/s\n");

            _sb.Append("stam ").Append(Mathf.RoundToInt(body.Stamina * 100f));
            _sb.Append(" adr x").Append(body.adrenaline.ToString("F2"));
            _sb.Append(" trq x").Append((body.torqueScale).ToString("F2")).Append('\n');

            float actionSum = 0f;
            float[] actions = agent.LastActions;
            for (int actionIndex = 0; actionIndex < actions.Length; actionIndex++)
            {
                actionSum += Mathf.Abs(actions[actionIndex]);
            }
            _sb.Append("act ").Append((actions.Length > 0 ? actionSum / actions.Length : 0f).ToString("F2"));
            _sb.Append(" feet ").Append(body.FootDownNear ? 'N' : '-').Append(body.FootDownFar ? 'F' : '-').Append('\n');

            _sb.Append(agent.IsDown ? "DOWN " : "").Append(body.IsLimp ? "LIMP " : "").Append(agent.OnFloor ? "FLOOR " : "");
            if (!agent.actionsEnabled) _sb.Append("NOMOTOR ");
            if (dominance >= 0f) _sb.Append("dom ").Append(Mathf.RoundToInt(dominance));
            label.text = _sb.ToString();
        }

        /// Thousands separator by hand: `ToString("N0")` allocates a culture lookup
        /// per call and this runs four times a second for five counters.
        private void AppendCount(ProfilerRecorder recorder)
        {
            if (!recorder.Valid)
            {
                _sb.Append("--");
                return;
            }
            long value = recorder.LastValue;
            if (value >= 1000000L)
            {
                _sb.Append((value / 1000000f).ToString("F1")).Append('M');
            }
            else if (value >= 10000L)
            {
                _sb.Append((value / 1000f).ToString("F1")).Append('k');
            }
            else
            {
                _sb.Append(value);
            }
        }

        /// Stamina is 1 fresh down to 0 spent, averaged over the 13 POWERED joints.
        /// Shown as a percentage because that is how the fatigue model is described
        /// everywhere else (`FATIGUE_DEPTH` 0.35 = a fully spent joint still gives
        /// 65%), and a 0-1 float invites reading it as a fraction of maximum torque.
        private void AppendStamina(Agent_BipedBody body)
        {
            if (body == null)
            {
                _sb.Append("--");
                return;
            }
            _sb.Append(Mathf.RoundToInt(body.Stamina * 100f));
        }
    }
}
