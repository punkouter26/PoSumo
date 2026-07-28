using UnityEngine;

namespace PoSumo
{
    /// Turns body-on-body collisions into visible force: a dust burst and a camera
    /// shake scaled by how hard the hit actually was.
    ///
    /// Sensor_Impact already measures relative impact speed and feeds it to the
    /// agent's impact reward, so the fighters were being *rewarded* for hits the
    /// audience could not see. This subscribes to the same signal, which means the
    /// visuals and the reward can never disagree about what counted as a big shove.
    ///
    /// Spawned at runtime by Systems_GameMatchManager.
    public sealed class Systems_ImpactFx : MonoBehaviour
    {
        [Tooltip("Impacts below this relative speed (m/s) are ignored — walking contact, not a shove.")]
        public float minSpeed = 2.2f;
        [Tooltip("Relative speed treated as a maximum-strength hit.")]
        public float maxSpeed = 9f;
        [Tooltip("Minimum gap between bursts, so a scrum does not spray dust every frame.")]
        public float cooldown = 0.12f;

        // Camera shake used to live here, permanently disabled (maxShake = 0) but
        // still running a LateUpdate every frame and carrying four fields. It
        // fought the follow camera's framing and made the fight harder to read, so
        // it was never going to be switched on. Deleted rather than left as a dial.
        // Impact weight used to be carried by Systems_PostFx (contrast/bloom/aberration
        // punch) and Systems_MatchAudio (layered thud), which do not move the frame.

        private float _nextAllowed;

        private void OnEnable()
        {
            Sensor_Impact.AnyImpact += OnImpact;
        }

        private void OnDisable()
        {
            // Static event: leaking this subscription would keep a dead scene's FX
            // object alive across loads.
            Sensor_Impact.AnyImpact -= OnImpact;
        }

        private void OnImpact(Sensor_Impact reporter, Collision2D collision)
        {
            if (reporter == null || reporter.owner == null)
            {
                return;
            }
            // Body-on-body only. Feet hitting clay every step is not an impact.
            var other = collision.collider.GetComponentInParent<Agent_BipedBody>();
            if (other == null || other == reporter.owner)
            {
                return;
            }

            float speed = collision.relativeVelocity.magnitude;
            if (speed < minSpeed || Time.time < _nextAllowed)
            {
                return;
            }
            _nextAllowed = Time.time + cooldown;

            float strength = Mathf.Clamp01((speed - minSpeed) / Mathf.Max(0.01f, maxSpeed - minSpeed));

            Vector3 point = collision.contactCount > 0
                ? (Vector3)collision.GetContact(0).point
                : reporter.transform.position;
            Systems_DustPuff.Burst(point, Mathf.RoundToInt(Mathf.Lerp(4f, 18f, strength)));

            // Sweat comes off the fighter taking the hit, thrown along the impact
            // direction. Only on real shoves — a light contact does not spray.
            if (strength > 0.3f)
            {
                Vector2 away = -collision.relativeVelocity.normalized;
                Systems_DustPuff.SweatSpray(point, away, Mathf.RoundToInt(Mathf.Lerp(2f, 9f, strength)));
            }
        }
    }
}
