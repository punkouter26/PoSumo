using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// Real-time diagnostic overlay: frame time, FPS, GC pressure, physics cost,
    /// draw-call proxy and per-fighter stamina.
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
        private const float MS_GOOD = 16.7f;
        private const float MS_WARN = 33.3f;

        private Systems_GameMatchManager _manager;
        private Agent_BipedBody _bodyA, _bodyB;

        private VisualElement _panel;
        private Label _frame, _memory, _sim, _fighters;

        private readonly StringBuilder _sb = new StringBuilder(160);

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
            BuildUi();
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
            _sim = Line();
            _fighters = Line();

            _panel.Add(_frame);
            _panel.Add(_memory);
            _panel.Add(_sim);
            _panel.Add(_fighters);

            hud.Stage.Add(_panel);
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

            Refresh();

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

            _sb.Clear();
            _sb.Append("phys ").Append((Time.fixedDeltaTime * 1000f).ToString("F0")).Append("ms x")
               .Append(Time.timeScale.ToString("F1"));
            if (_manager != null)
            {
                _sb.Append(' ').Append(_manager.RoundLive ? "live" : "idle");
            }
            _sim.text = _sb.ToString();

            _sb.Clear();
            _sb.Append("stam ");
            AppendStamina(_bodyA);
            _sb.Append('/');
            AppendStamina(_bodyB);
            _fighters.text = _sb.ToString();
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
