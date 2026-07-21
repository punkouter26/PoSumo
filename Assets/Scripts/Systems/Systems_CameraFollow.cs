using UnityEngine;

namespace PoSumo
{
    /// Broadcast-style follow camera for portrait viewing: tracks the midpoint
    /// of both wrestlers and zooms as tight as possible while keeping both in
    /// frame (plus margin). Attach to the camera.
    [RequireComponent(typeof(Camera))]
    public class Systems_CameraFollow : MonoBehaviour
    {
        // min 1.56: close-up during engagement (head ~7.7% of screen height).
        // max 3.5: wide enough that both wrestlers always stay in frame,
        // including at spawn separation. NOTE: scene-serialized values win over
        // these defaults — change the component in the scene, not just here.
        public float minOrtho = 1.56f;
        public float maxOrtho = 3.5f;
        public float verticalOffset = -0.05f; // relative to the wrestlers' average torso height
        public float horizontalMargin = 0.35f;
        public float smoothing = 4f;
        public Systems_GameTuning tuning;

        Camera _cam;
        Agent_Biped _a, _b;

        void Awake()
        {
            _cam = GetComponent<Camera>();
            if (tuning != null)
            {
                minOrtho = tuning.minOrtho;
                maxOrtho = tuning.maxOrtho;
                verticalOffset = tuning.verticalOffset;
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

            float midY = (_a.Torso.position.y + _b.Torso.position.y) * 0.5f + verticalOffset;

            float t = 1f - Mathf.Exp(-smoothing * Time.deltaTime);
            _cam.orthographicSize = Mathf.Lerp(_cam.orthographicSize, targetOrtho, t);
            var p = transform.position;
            transform.position = new Vector3(Mathf.Lerp(p.x, mid, t), Mathf.Lerp(p.y, midY, t), p.z);
        }
    }
}
