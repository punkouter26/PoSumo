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
    public sealed class Systems_SafeArea : MonoBehaviour
    {
        private VisualElement[] _targets;
        private Rect _lastSafeArea;
        private Vector2Int _lastScreen;
        private Vector2 _lastRootSize;

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

            Rect safe = Screen.safeArea;
            var screen = new Vector2Int(Screen.width, Screen.height);
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
