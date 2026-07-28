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
