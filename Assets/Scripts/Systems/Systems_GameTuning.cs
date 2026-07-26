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
        public float minOrtho = 1.9f;
        public float maxOrtho = 3.5f;
        [Tooltip("Camera centers this far below the wrestlers' average torso — at the feet.")]
        public float feetDrop = 0.95f;
        public float horizontalMargin = 0.5f;
        public float smoothing = 4f;

        [Header("Match")]
        [Tooltip("Round wins needed in a standalone exhibition match.")]
        public int pointsToWin = 3;
        [Tooltip("Round wins needed to take a tournament bracket. 2 = best of three. Single source for both modes so they cannot drift apart.")]
        public int tournamentPointsToWin = 2;
        public float roundTimeoutSeconds = 30f;
        public float betweenRoundsPause = 2.5f;
        public float graceSeconds = 0.4f;
        [Tooltip("Non-foot ground contact must persist this long to count as a throw-down.")]
        public float downGraceSeconds = 0.2f;
    }
}
