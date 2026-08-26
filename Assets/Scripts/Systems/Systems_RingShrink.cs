using UnityEngine;

namespace PoSumo
{
    /// The one piece of shrinking-mat arithmetic BOTH referees share, so the
    /// game (Systems_GameMatchManager) and training (Systems_SumoMatchManager)
    /// close the ring on exactly the same curve. Pure and static: no state, no
    /// scene, nothing serialized.
    internal static class Systems_RingShrink
    {
        /// Half-width the mat should be at `elapsed` seconds into a round.
        ///
        /// Returns `startHalf` until `startAt` has passed (or when `duration` is
        /// not positive — no shrink). Afterwards it is an UNCLAMPED lerp from
        /// `startHalf` to `endHalf` over `duration`: past 1 the mat keeps closing
        /// at the same rate all the way to zero, floored there, so a round with
        /// no clock still cannot last forever.
        internal static float ShrinkTarget(float startHalf, float endHalf, float elapsed,
                                           float startAt, float duration)
        {
            if (duration <= 0f || elapsed <= startAt)
            {
                return startHalf;
            }

            float progress = (elapsed - startAt) / duration;
            return Mathf.Max(0f, Mathf.LerpUnclamped(startHalf, endHalf, progress));
        }
    }
}
