using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// The five fixed corners of every screen in the game, in one place.
    ///
    ///     top-left ...... the game's title
    ///     top-centre .... live frame rate
    ///     top-right ..... the menu button
    ///     bottom-left ... the debug button (agent / brain telemetry)
    ///     bottom-right .. the build version
    ///
    /// Why a layer of its own rather than five additions to the existing HUD.
    /// Systems_HudRoot lays the match screen out as three FLOW bands whose heights
    /// are proportional (Stage >= 45%, Dock <= 28%) and whose top bar is a
    /// three-slot row already carrying the scorebug. Putting a frame-rate readout
    /// into TopBarCentre makes it compete with the score digits for width, and the
    /// panel scales on WIDTH, so "it fits on the 720pt reference" proves nothing
    /// about a 1080x2400 phone. Chrome is absolute on a full-bleed layer instead:
    /// it is positioned against the screen, it pushes nothing around, and it
    /// cannot be squeezed by a band it does not belong to.
    ///
    /// The layer it attaches to must be full-bleed AND carry the safe-area inset,
    /// because absolute offsets resolve against the parent's PADDING box — which
    /// is what makes SPACE_2 here mean "eight points inside the first safe pixel"
    /// rather than "eight points into the notch". In the arena that layer is
    /// Systems_HudRoot.Overlay; on the bracket it is a sibling of the ScrollView
    /// on the already-inset screen element.
    ///
    /// Everything here is scenery except the two buttons. The layer itself and
    /// every label are NoPick, so chrome can never swallow a pointer-down meant
    /// for the fighter palette, a bracket chip or REMATCH behind it.
    public sealed class Systems_ScreenChrome : MonoBehaviour
    {
        /// Seconds of frames one readout averages over.
        ///
        /// This was 0.25 s and the readout could not be trusted. MEASURED
        /// 2026-10-04 in the Editor: the chip read 60 while idle (worst frame
        /// 21 ms), and 2-22 in red in every capture — because one stalled frame
        /// (436 ms across a bridge call plus a screenshot; a scene load or a GC
        /// pause does the same on a phone) is LONGER than the whole window, so
        /// the "average" was that single frame and nothing else. A full second
        /// holds ~60 frames, which is what makes frames / elapsed a frame RATE.
        private const float SAMPLE_INTERVAL = 1f;

        /// A frame longer than this is a STALL, not a sample of the frame rate:
        /// a scene load, a screenshot readback, the app coming back from the
        /// background. It is left out of the average — counting it reports
        /// "35 FPS" for a second in which every rendered frame took 16 ms — and
        /// is what turns the readout amber instead.
        private const float STALL_SECONDS = 0.25f;

        /// Frame-time thresholds in milliseconds, matching the perf HUD's so the
        /// two readouts can never disagree about what "green" means. The Android
        /// target is 60 FPS, so amber starts where that budget is gone and red
        /// where 30 FPS has been missed as well.
        private const float MS_GOOD = 16.7f;
        private const float MS_WARN = 33.3f;

        /// Slack on the green threshold, in milliseconds. A frame cap of 60 gives
        /// a mean of 16.4-17.2 ms, not 16.7 exactly; without this the readout
        /// flickers green/amber at the very rate it is meant to call good.
        private const float MS_TOLERANCE = 1.5f;

        private Label _fps;
        private Button _debugButton;
        private System.Action _onDebug;

        /// One reused builder, the same discipline Systems_PerfHud and
        /// Systems_Telemetry follow. This refreshes four times a second for the
        /// whole session, so a fresh string per sample would be a permanent, if
        /// small, allocation floor under every screen in the game.
        private readonly StringBuilder _sb = new StringBuilder(32);

        private int _framesSinceSample;
        private float _timeSinceSample;
        private bool _stalledThisWindow;
        private int _shownFps = -1;

        /// Builds the chrome onto the given layer and returns the component
        /// driving it.
        ///
        /// onMenu is what the top-right button does — pause in the arena, the
        /// career screen on the bracket. onDebug is what the bottom-left button
        /// does. Either being null omits that button rather than shipping a
        /// control that does nothing when pressed: a dead affordance is worse than
        /// an absent one, because it reads as a bug in the game.
        public static Systems_ScreenChrome Attach(Transform owner, VisualElement layer,
                                                  System.Action onMenu, System.Action onDebug)
        {
            if (layer == null)
            {
                return null;
            }
            var go = new GameObject("ScreenChrome");
            if (owner != null)
            {
                go.transform.SetParent(owner, false);
            }
            Systems_ScreenChrome chrome = go.AddComponent<Systems_ScreenChrome>();
            chrome.Build(layer, onMenu, onDebug);
            return chrome;
        }

        private void Build(VisualElement layer, System.Action onMenu, System.Action onDebug)
        {
            _onDebug = onDebug;

            // Named for the corner-contract audit (HudOverflowAudit.Corners): the
            // gate asserts exactly these five children exist, in one layer, in
            // the right corners — TL title | TC FPS | TR menu | BL debug |
            // BR version — so the anchor rule is a PASS/FAIL check rather than a
            // convention someone has to remember. A sixth child is a finding too:
            // nothing else may draw into a corner.
            layer.name = "ScreenChromeLayer";

            // TOP-LEFT — the game's title.
            //
            // Application.productName rather than a literal, for the same reason
            // the version stamp reads Application.version: both are PlayerSettings
            // values that ship with the build, and a constant here would be free to
            // drift away from the name on the launcher icon.
            //
            // Given the same TOUCH_MIN height as the two buttons and centred within
            // it, so the three top items share one optical band instead of the text
            // sitting proud of the chips beside it.
            Label title = Systems_UiKit.Text(Application.productName,
                                             Systems_UiKit.FONT_BODY,
                                             Systems_UiKit.Gold, true);
            title.name = "ChromeTitle";
            title.style.textShadow = Systems_UiKit.Outline;
            Corner(title, layer, true, true);
            title.style.height = Systems_UiKit.TOUCH_MIN;
            title.style.unityTextAlign = TextAnchor.MiddleLeft;

            // TOP-CENTRE — frame rate.
            //
            // Stretched edge to edge and centred by text alignment rather than
            // sized to its content and offset by half. A fixed left offset would
            // only be centred at one panel width, and this panel's width in points
            // is a different number on every device.
            _fps = Systems_UiKit.Caption("--", Systems_UiKit.FONT_SMALL, Systems_UiKit.TextMid, true);
            _fps.name = "ChromeFps";
            _fps.style.textShadow = Systems_UiKit.Outline;
            _fps.style.position = Position.Absolute;
            _fps.style.left = 0;
            _fps.style.right = 0;
            _fps.style.top = Systems_UiKit.SPACE_2;
            _fps.style.height = Systems_UiKit.TOUCH_MIN;
            _fps.style.unityTextAlign = TextAnchor.MiddleCenter;
            layer.Add(_fps.NoPick());

            // TOP-RIGHT — the menu.
            if (onMenu != null)
            {
                Button menu = Systems_UiKit.MenuButton(onMenu);
                menu.name = "MenuButton";
                Corner(menu, layer, false, true);
            }

            // BOTTOM-LEFT — the debug button.
            //
            // This corner used to belong to Systems_PerfHud, which built its own
            // chip here and is spawned only in a development build — so a release
            // APK had no debug affordance at all. The button lives here now and the
            // panel it opens ships, because the agent readout is a thing to consult
            // ON the phone during a bout, not only in the Editor.
            if (onDebug != null)
            {
                _debugButton = Systems_UiKit.ChipButton("DBG", InvokeDebug, Systems_UiKit.TOUCH_MIN);
                _debugButton.name = "DebugToggle";
                _debugButton.style.fontSize = Systems_UiKit.FONT_MICRO;
                _debugButton.style.opacity = 0.85f;
                Corner(_debugButton, layer, true, false);
            }

            // BOTTOM-RIGHT — the build stamp.
            //
            // Application.version IS PlayerSettings.bundleVersion, so this is the
            // number that shipped rather than one that can drift from it. The game
            // reaches a phone by sideload and two installs an hour apart are
            // otherwise indistinguishable, with adb shell dumpsys package not
            // available to whoever is holding the device.
            Label version = Systems_UiKit.Text("v" + Application.version,
                                               Systems_UiKit.FONT_MICRO,
                                               Systems_UiKit.TextLow);
            version.name = "ChromeVersion";
            version.style.textShadow = Systems_UiKit.Outline;
            Corner(version, layer, false, false);
        }

        /// Pins an element to one corner of the layer. Offsets resolve against the
        /// parent's padding box, so SPACE_2 here is eight points inside the safe
        /// area rather than eight points into the notch.
        private static void Corner(VisualElement element, VisualElement layer, bool left, bool top)
        {
            element.style.position = Position.Absolute;
            if (left)
            {
                element.style.left = Systems_UiKit.SPACE_2;
            }
            else
            {
                element.style.right = Systems_UiKit.SPACE_2;
            }
            if (top)
            {
                element.style.top = Systems_UiKit.SPACE_2;
            }
            else
            {
                element.style.bottom = Systems_UiKit.SPACE_2;
            }
            // Labels are scenery; a Button must stay pickable or it cannot be
            // pressed, so only the non-interactive ones are excluded here.
            if (!(element is Button))
            {
                element.NoPick();
            }
            layer.Add(element);
        }

        private void InvokeDebug()
        {
            if (_onDebug == null)
            {
                return;
            }
            _onDebug();
        }

        /// Tints the debug chip while its panel is open, so the button reports the
        /// state it controls. Called by whoever owns the panel.
        public void SetDebugActive(bool active)
        {
            if (_debugButton == null)
            {
                return;
            }
            _debugButton.style.color = active ? Systems_UiKit.Gold : Systems_UiKit.TextHi;
        }

        private void Update()
        {
            // Frames counted over elapsed time, reported once a second.
            // unscaledDeltaTime, so pause (timeScale 0) and the slow-motion finish
            // do not move the number: neither changes how fast frames are drawn.
            //
            // The window is closed on its OWN accumulated time rather than against
            // Time.unscaledTime, so a stall cannot end a window that holds one
            // frame. Stalled frames are kept out of both sums — see STALL_SECONDS.
            float frameSeconds = Time.unscaledDeltaTime;
            if (frameSeconds > STALL_SECONDS)
            {
                _stalledThisWindow = true;
            }
            else
            {
                _framesSinceSample++;
                _timeSinceSample += frameSeconds;
            }

            if (_timeSinceSample < SAMPLE_INTERVAL)
            {
                return;
            }

            if (_fps != null && _framesSinceSample > 0)
            {
                float avgMs = (_timeSinceSample / _framesSinceSample) * 1000f;
                int fps = Mathf.RoundToInt(1000f / avgMs);

                // The digits only change when the rounded rate does, which at a
                // steady 60 is almost never — so the label is not rewritten, and
                // no string is built, on most samples.
                if (fps != _shownFps)
                {
                    _shownFps = fps;
                    _sb.Clear();
                    _sb.Append(fps).Append(" FPS");
                    _fps.text = _sb.ToString();
                }
                // Coloured on the AVERAGE frame, with a small tolerance: at a
                // capped 60 the mean lands a hair either side of 16.7 ms, and
                // colouring on the single worst frame painted a healthy 60 red
                // whenever one frame in the window ran long. A stall shows as
                // amber for that one readout rather than as a false number.
                _fps.style.color = avgMs > MS_WARN ? Systems_UiKit.Bad
                                 : avgMs > MS_GOOD + MS_TOLERANCE || _stalledThisWindow ? Systems_UiKit.Warn
                                 : Systems_UiKit.Good;
            }

            _framesSinceSample = 0;
            _timeSinceSample = 0f;
            _stalledThisWindow = false;
        }
    }
}
