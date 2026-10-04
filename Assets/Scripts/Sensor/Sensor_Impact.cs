using UnityEngine;

namespace PoSumo
{
    /// Broadcasts every collision of a biped body part (feet included) so
    /// audio/FX systems can react to impact strength without polling physics.
    /// Attached at runtime by Agent_BipedBody.Build().
    public sealed class Sensor_Impact : MonoBehaviour
    {
        /// (reporter, collision). Subscribers must unsubscribe in OnDisable —
        /// this is a static event and outlives scene loads.
        public static event System.Action<Sensor_Impact, Collision2D> AnyImpact;

        [System.NonSerialized] public Agent_BipedBody owner;
        [System.NonSerialized] public bool isFoot;

        /// Body-on-body normal impulse (N·s) where a contact starts to count as
        /// HEAVY, and where it counts as a maximum-weight shove.
        ///
        /// MEASURED, not chosen: 2188 body-on-body contacts over a Matt-v-Nick
        /// bout (2026-10-04) gave p50 0.50, p75 1.62, p90 3.13, p95 4.40,
        /// p99 7.92, max 20.9. The ramp opens at about p94 and saturates at p99 —
        /// inside the observed range, because a term whose ramp sits outside the
        /// distribution it reads is not weak, it is absent.
        ///
        /// It first opened at p75 (1.5), which is also inside the range and was
        /// wrong for a different reason: replaying the same sample through the
        /// gates showed Systems_ImpactFx reacting to 758 contacts where it had
        /// reacted to 246. "Heavy" has to mean the top few percent, or every
        /// clinch is a dust storm. At 4.0 the same sample admits 112 contacts on
        /// weight alone (246 -> 358).
        ///
        /// Why impulse at all: approach speed alone ranks a 14 m/s graze that
        /// transferred 0.0 N·s above a 1.3 m/s chest-to-chest shove that
        /// transferred 20.9 — the single heaviest contact in the sample, and one
        /// every speed gate in the presentation layer ignored.
        private const float IMPULSE_MIN = 4f;
        private const float IMPULSE_FULL = 8f;
        /// Blend weights. A fast contact with nothing behind it tops out at 0.6,
        /// a slow one with full weight behind it at 0.7, and only a contact that
        /// is both reaches 1 — so the heavy shove reads heavier than the tap.
        private const float SPEED_SHARE = 0.6f;
        private const float IMPULSE_SHARE = 0.7f;

        /// Total normal impulse the solver applied across this collision's
        /// contact points this step. GetContact copies a struct, so this is
        /// allocation-free and safe on the per-collision path.
        public static float NormalImpulse(Collision2D collision)
        {
            float total = 0f;
            int contactCount = collision.contactCount;
            for (int contactIndex = 0; contactIndex < contactCount; contactIndex++)
            {
                total += collision.GetContact(contactIndex).normalImpulse;
            }
            return total;
        }

        /// 0..1 heaviness of a body-on-body contact from its impulse alone.
        public static float Heaviness(Collision2D collision) =>
            Mathf.Clamp01((NormalImpulse(collision) - IMPULSE_MIN) / (IMPULSE_FULL - IMPULSE_MIN));

        /// 0..1 strength of a body-on-body contact for PRESENTATION (dust, flash,
        /// thud level): the caller's own speed ramp blended with heaviness.
        /// Presentation only — the impact reward below still reads raw speed, so
        /// nothing a brain was trained against has moved.
        public static float Strength(float speed01, float heaviness01) =>
            Mathf.Clamp01(speed01 * SPEED_SHARE + heaviness01 * IMPULSE_SHARE);

        private Agent_Biped _ownerAgent;

        private void OnCollisionEnter2D(Collision2D c)
        {
            AnyImpact?.Invoke(this, c);

            // Opponent contact feeds the impact reward (momentum delivered).
            if (owner == null) return;
            var otherBody = c.collider.GetComponentInParent<Agent_BipedBody>();
            if (otherBody == null || otherBody == owner) return;
            if (_ownerAgent == null) _ownerAgent = owner.GetComponent<Agent_Biped>();
            if (_ownerAgent != null) _ownerAgent.ReportOpponentImpact(c.relativeVelocity.magnitude);
        }
    }
}
