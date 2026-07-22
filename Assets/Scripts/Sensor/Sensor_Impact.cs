using UnityEngine;

namespace PoSumo
{
    /// Broadcasts every collision of a biped body part (feet included) so
    /// audio/FX systems can react to impact strength without polling physics.
    /// Attached at runtime by Agent_BipedBody.Build().
    public class Sensor_Impact : MonoBehaviour
    {
        /// (reporter, collision). Subscribers must unsubscribe in OnDisable —
        /// this is a static event and outlives scene loads.
        public static event System.Action<Sensor_Impact, Collision2D> AnyImpact;

        [System.NonSerialized] public Agent_BipedBody owner;
        [System.NonSerialized] public bool isFoot;

        void OnCollisionEnter2D(Collision2D c)
        {
            AnyImpact?.Invoke(this, c);
        }
    }
}
