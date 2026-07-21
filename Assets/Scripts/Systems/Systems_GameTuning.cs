using UnityEngine;

namespace PoSumo
{
    /// Single source of truth for camera and match tuning. Scene components
    /// copy from this asset at startup when it is assigned, so numbers live in
    /// one place instead of being scattered across serialized scene values.
    [CreateAssetMenu(fileName = "GameTuning", menuName = "PoSumo/Game Tuning")]
    public class Systems_GameTuning : ScriptableObject
    {
        [Header("Camera")]
        public float minOrtho = 1.56f;
        public float maxOrtho = 3.5f;
        public float verticalOffset = -0.05f;
        public float horizontalMargin = 0.35f;
        public float smoothing = 4f;

        [Header("Match")]
        public int pointsToWin = 3;
        public float roundTimeoutSeconds = 30f;
        public float betweenRoundsPause = 2.5f;
        public float graceSeconds = 0.4f;
        [Tooltip("Non-foot ground contact must persist this long to count as a throw-down.")]
        public float downGraceSeconds = 0.2f;

        [Header("Stall breaker")]
        [Tooltip("After this many seconds of fighting, the effective ring starts shrinking.")]
        public float stallBreakStart = 12f;
        [Tooltip("Meters per second the ring boundary closes in from each side.")]
        public float stallShrinkRate = 0.15f;
        public float minRingHalfWidth = 0.6f;
    }
}
