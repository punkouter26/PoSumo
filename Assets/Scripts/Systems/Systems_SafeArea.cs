using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// Keeps a UI Toolkit root clear of notches, punch-holes and the gesture bar
    /// by padding it to the device safe area.
    ///
    /// Screen.safeArea is in pixels from the bottom-left; UI Toolkit lays out in
    /// points from the top-left, and the panel scales 720x1280 to the device. So
    /// the insets are converted to fractions of the screen and re-applied against
    /// the root's own resolved size, which is already in panel space.
    ///
    /// On desktop the safe area equals the full screen, so every inset is zero and
    /// nothing moves — the editor Game view looks exactly as before.
    public sealed class Systems_SafeArea : MonoBehaviour
    {
        private VisualElement _root;
        private Rect _lastSafeArea;
        private Vector2Int _lastScreen;
        private Vector2 _lastRootSize;

        /// Spawns a watcher that pads `root` and keeps it padded across rotation
        /// and resolution changes. Parented to `owner` so it dies with the scene.
        public static void Attach(Transform owner, VisualElement root)
        {
            if (root == null)
            {
                return;
            }
            var go = new GameObject("SafeArea");
            go.transform.SetParent(owner, false);
            Systems_SafeArea watcher = go.AddComponent<Systems_SafeArea>();
            watcher._root = root;
        }

        private void LateUpdate()
        {
            if (_root == null)
            {
                return;
            }

            // The root has no resolved size for the first frame or two after the
            // panel is built; skip until it does, or the insets divide by zero.
            var rootSize = new Vector2(_root.resolvedStyle.width, _root.resolvedStyle.height);
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

            float left = safe.xMin / screen.x * rootSize.x;
            float right = (screen.x - safe.xMax) / screen.x * rootSize.x;
            float bottom = safe.yMin / screen.y * rootSize.y;
            float top = (screen.y - safe.yMax) / screen.y * rootSize.y;

            _root.style.paddingLeft = Mathf.Max(0f, left);
            _root.style.paddingRight = Mathf.Max(0f, right);
            _root.style.paddingTop = Mathf.Max(0f, top);
            _root.style.paddingBottom = Mathf.Max(0f, bottom);
        }
    }
}
