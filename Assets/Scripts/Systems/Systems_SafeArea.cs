using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// Keeps one or more UI Toolkit layers clear of notches, punch-holes and the
    /// gesture bar by padding them to the device safe area.
    ///
    /// Screen.safeArea is in pixels from the bottom-left; UI Toolkit lays out in
    /// points from the top-left, and the panel scales 720x1280 to the device. So
    /// the insets are converted to fractions of the screen and re-applied against
    /// the target's own resolved size, which is already in panel space.
    ///
    /// Several targets rather than one, because a screen's layers do not all want
    /// the inset. The match HUD insets its content and its dialogs but deliberately
    /// NOT its dim scrim: an absolutely positioned child resolves left/right/top/
    /// bottom against its parent's PADDING box, so a scrim under the safe-area
    /// padding stops short of the notch and leaves an undimmed strip across the top
    /// and bottom of every dialog. One watcher drives every inset layer so they
    /// cannot drift apart.
    ///
    /// On desktop the safe area equals the full screen, so every inset is zero and
    /// nothing moves — the editor Game view looks exactly as before.
    ///
    /// Which is also why the inset code has historically been untestable where it
    /// matters least and needed most: a desktop Game view has no cutout (and its
    /// `Screen.safeArea` is not even reliably the full window — see
    /// `DeviceSafeArea`), so every notch/cutout layout fault only ever surfaced
    /// on a device. `SafeAreaOverride` is the fix — a fake safe area, in the same
    /// bottom-left pixel units the real one uses, that this watcher applies in
    /// place of `Screen.safeArea` while it is non-degenerate. Editor harnesses
    /// and the portrait layout check drive it; there is no UI for it.
    public sealed class Systems_SafeArea : MonoBehaviour
    {
        private VisualElement[] _targets;
        private Rect _lastSafeArea;
        private Vector2Int _lastScreen;
        private Vector2 _lastRootSize;

        // Static, not per-watcher: there is one fake cutout per process, shared
        // by every panel the watcher drives.
        private static Rect _overrideSafeArea;

        // Enter Play Mode domain reload is OFF in this project, so a fake cutout
        // left armed when a session ended would still be armed in the NEXT one —
        // the classic second-Play-session bug, and the reason every static here
        // carries this reset.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOverride()
        {
            _overrideSafeArea = default;
        }

        /// A fake device safe area in real `Screen.safeArea` units (pixels,
        /// origin bottom-left), applied instead of the OS value while enabled.
        /// `default` — or any rect with zero width — disables the override and
        /// returns the watcher to the real value. Example for a 1080x1920 window
        /// with a 48 px top cutout and a 48 px gesture bar:
        /// `new Rect(0f, 48f, 1080f, 1824f)` (yMin 48 = bottom inset, yMax 1872 = top inset).
        ///
        /// Writes are ignored outside the Editor: a stray non-zero override in a
        /// store build would inset every HUD forever and there is no legitimate
        /// reason for the override to exist there. (UNITY_EDITOR only — this
        /// Unity version warns on the DEVELOPMENT_BUILD define, and the harnesses
        /// that drive the override are Editor-only anyway.)
        public static Rect SafeAreaOverride
        {
            get
            {
#if UNITY_EDITOR
                return _overrideSafeArea;
#else
                return default;
#endif
            }
            set
            {
#if UNITY_EDITOR
                _overrideSafeArea = value;
#endif
            }
        }

        /// Spawns a watcher that pads every `targets` element and keeps them padded
        /// across rotation and resolution changes. Parented to `owner` so it dies
        /// with the scene. All targets must resolve to the same box — in practice
        /// they are sibling full-bleed layers of one panel.
        public static void Attach(Transform owner, params VisualElement[] targets)
        {
            if (targets == null || targets.Length == 0 || targets[0] == null)
            {
                return;
            }
            var go = new GameObject("SafeArea");
            go.transform.SetParent(owner, false);
            Systems_SafeArea watcher = go.AddComponent<Systems_SafeArea>();
            watcher._targets = targets;
        }

        /// The OS safe area, or the whole screen where the OS value cannot be
        /// trusted.
        ///
        /// In the EDITOR's Game view `Screen.safeArea` is not reliable, and this
        /// class's header used to claim the opposite. MEASURED 2026-10-04 on a
        /// 1080x2400 Game view: `Screen.safeArea` = (0, 0, 960, 2566) — a rect
        /// that is narrower AND taller than the screen it is supposed to sit
        /// inside (it is a stale size from another view; the Game view had been
        /// 960x2658). Fed through the maths below it insets the right edge by
        /// 120 of 1080 px: the whole HUD was laid out in the left 89% of the
        /// panel, with the menu and the build stamp pulled in off their corners,
        /// in every capture taken that session.
        ///
        /// A desktop Game view has no cutout, so in the Editor the answer is the
        /// full screen — except under the Device Simulator, which reports a real
        /// simulated cutout and is recognised by its simulated device type. A
        /// rect that does not fit inside the screen is rejected everywhere: no
        /// device can report one, so it can only be a bad value.
        private static Rect DeviceSafeArea(Vector2Int screen)
        {
            var full = new Rect(0f, 0f, screen.x, screen.y);
            Rect safe = Screen.safeArea;
            if (safe.width <= 0f || safe.height <= 0f
                || safe.xMin < 0f || safe.yMin < 0f
                || safe.xMax > screen.x + 0.5f || safe.yMax > screen.y + 0.5f)
            {
                return full;
            }
#if UNITY_EDITOR
            if (UnityEngine.Device.SystemInfo.deviceType == DeviceType.Desktop)
            {
                return full;
            }
#endif
            return safe;
        }

        private void LateUpdate()
        {
            if (_targets == null || _targets[0] == null)
            {
                return;
            }

            // The panel has no resolved size for the first frame or two after it is
            // built; skip until it does, or the insets divide by zero.
            VisualElement measure = _targets[0];
            var rootSize = new Vector2(measure.resolvedStyle.width, measure.resolvedStyle.height);
            if (rootSize.x <= 0f || rootSize.y <= 0f)
            {
                return;
            }

            // The override stands in for the OS value whole — the comparison cache
            // below needs no changes because the override simply IS the safe area
            // for as long as it is enabled.
            var screen = new Vector2Int(Screen.width, Screen.height);
            Rect safe = _overrideSafeArea.width > 0f ? _overrideSafeArea : DeviceSafeArea(screen);
            if (safe == _lastSafeArea && screen == _lastScreen && rootSize == _lastRootSize)
            {
                return;
            }
            _lastSafeArea = safe;
            _lastScreen = screen;
            _lastRootSize = rootSize;

            if (screen.x <= 0 || screen.y <= 0)
            {
                return;
            }

            float left = Mathf.Max(0f, safe.xMin / screen.x * rootSize.x);
            float right = Mathf.Max(0f, (screen.x - safe.xMax) / screen.x * rootSize.x);
            float bottom = Mathf.Max(0f, safe.yMin / screen.y * rootSize.y);
            float top = Mathf.Max(0f, (screen.y - safe.yMax) / screen.y * rootSize.y);

            for (int targetIndex = 0; targetIndex < _targets.Length; targetIndex++)
            {
                VisualElement target = _targets[targetIndex];
                if (target == null)
                {
                    continue;
                }
                target.style.paddingLeft = left;
                target.style.paddingRight = right;
                target.style.paddingTop = top;
                target.style.paddingBottom = bottom;
            }
        }
    }
}
