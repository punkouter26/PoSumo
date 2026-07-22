using UnityEngine;

namespace PoSumo
{
    /// Broadcast-style follow camera for portrait viewing: tracks the midpoint
    /// of both wrestlers and zooms as tight as possible while keeping both in
    /// frame (plus margin). Attach to the camera.
    [RequireComponent(typeof(Camera))]
    public class Systems_CameraFollow : MonoBehaviour
    {
        // min 1.9: tightest zoom that still fits a full body (~1.85 m) in the
        // top half of the frame with the feet at screen center.
        // max 3.5: in portrait the visible width is ortho * aspect (~0.45), so
        // anything tighter crops the wrestlers off-screen at spawn separation.
        // NOTE: GameTuning.asset (and scene-serialized values) win over these
        // defaults — change the asset, not just here.
        public float minOrtho = 1.9f;
        public float maxOrtho = 3.5f;
        public float feetDrop = 0.95f; // camera centers this far below the average torso — at the feet
        public float horizontalMargin = 0.5f;
        public float smoothing = 4f;
        public Systems_GameTuning tuning;

        Camera _cam;
        Agent_Biped _a, _b;

        // Temporary punch-in override (slow-mo finishes): blend toward a focus
        // transform at a tighter ortho until the realtime deadline passes.
        Transform _focus;
        float _focusOrtho;
        float _focusUntil;

        /// Blend the camera toward `focus` at `ortho` for `realSeconds` of
        /// unscaled time. Used by the match presentation on round-deciding falls.
        public void PunchIn(Transform focus, float ortho, float realSeconds)
        {
            _focus = focus;
            _focusOrtho = ortho;
            _focusUntil = Time.realtimeSinceStartup + realSeconds;
        }

        void Awake()
        {
            _cam = GetComponent<Camera>();
            if (tuning != null)
            {
                minOrtho = tuning.minOrtho;
                maxOrtho = tuning.maxOrtho;
                feetDrop = tuning.feetDrop;
                horizontalMargin = tuning.horizontalMargin;
                smoothing = tuning.smoothing;
            }
        }

        void LateUpdate()
        {
            if (_a == null || _b == null)
            {
                var agents = FindObjectsByType<Agent_Biped>(FindObjectsSortMode.None);
                if (agents.Length >= 2) { _a = agents[0]; _b = agents[1]; }
                else return;
            }

            float ax = _a.TorsoX, bx = _b.TorsoX;
            float mid = (ax + bx) * 0.5f;
            float halfDist = Mathf.Abs(ax - bx) * 0.5f;

            float halfWidthNeeded = halfDist + horizontalMargin;
            float orthoNeeded = halfWidthNeeded / _cam.aspect;
            float targetOrtho = Mathf.Clamp(orthoNeeded, minOrtho, maxOrtho);

            bool focusActive = _focus != null && Time.realtimeSinceStartup < _focusUntil;
            if (focusActive) targetOrtho = Mathf.Min(targetOrtho, _focusOrtho);

            float t = 1f - Mathf.Exp(-smoothing * Time.deltaTime);
            _cam.orthographicSize = Mathf.Lerp(_cam.orthographicSize, targetOrtho, t);

            // Vertical center of the frame sits at the wrestlers' feet.
            float midY = (_a.Torso.position.y + _b.Torso.position.y) * 0.5f - feetDrop;

            if (focusActive)
            {
                mid = Mathf.Lerp(mid, _focus.position.x, 0.75f);
                midY = Mathf.Lerp(midY, _focus.position.y, 0.75f);
            }

            var p = transform.position;
            transform.position = new Vector3(Mathf.Lerp(p.x, mid, t), Mathf.Lerp(p.y, midY, t), p.z);
        }
    }
}
