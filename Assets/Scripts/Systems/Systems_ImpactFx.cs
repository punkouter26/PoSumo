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
        [Tooltip("Camera shake is off: it fought the follow camera's own framing and made the fight harder to read. Left as a dial rather than deleted, but 0 is the shipping value.")]
        public float maxShake = 0f;
        [Tooltip("Seconds a shake decays over.")]
        public float shakeDecay = 0.28f;
        [Tooltip("Minimum gap between bursts, so a scrum does not spray dust every frame.")]
        public float cooldown = 0.12f;

        private Transform _camera;
        private Vector3 _cameraBase;
        private float _shake;
        private float _shakeLeft;
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

        private void Start()
        {
            if (Camera.main != null)
            {
                _camera = Camera.main.transform;
            }
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

            if (maxShake > 0f)
            {
                _shake = Mathf.Max(_shake, Mathf.Lerp(0.02f, maxShake, strength));
                _shakeLeft = shakeDecay;
            }
        }

        private void LateUpdate()
        {
            if (_camera == null)
            {
                if (Camera.main == null) return;
                _camera = Camera.main.transform;
            }
            if (_shakeLeft <= 0f)
            {
                return;
            }

            // Runs in LateUpdate so it is applied on top of Systems_CameraFollow's
            // position for the frame, and fully removed before the next one — the
            // follow camera never sees the offset and cannot drift.
            _shakeLeft -= Time.deltaTime;
            float remaining = Mathf.Clamp01(_shakeLeft / shakeDecay);
            float amount = _shake * remaining * remaining;
            _camera.position += new Vector3(Random.Range(-amount, amount), Random.Range(-amount, amount), 0f);
            if (_shakeLeft <= 0f)
            {
                _shake = 0f;
            }
        }
    }
}
