using UnityEngine;

namespace PoSumo
{
    /// Soft-tissue lag on a torso segment: the drawn mass keeps moving for a beat
    /// after the bone stops, then springs back.
    ///
    /// Lives on the "Art" child, which carries the SpriteRenderer and NO collider —
    /// so this only ever moves pixels. The rigidbody it samples is read, never
    /// written. That is what makes it safe to add to a project whose brains were
    /// all trained against the undressed body.
    ///
    /// Attached at runtime by Agent_BipedBody when enableJiggle is on.
    public sealed class Systems_SoftBodyJiggle : MonoBehaviour
    {
        [Tooltip("Body part whose motion drives the wobble. Read-only to this component.")]
        public Rigidbody2D source;

        [Tooltip("Maximum offset in local units. Scaled by build: a wide, heavy fighter carries more meat.")]
        public float amplitude = 0.06f;
        [Tooltip("Spring rate back to the bone position. Higher = tighter, less flesh.")]
        public float stiffness = 55f;
        [Tooltip("Fraction of oscillation removed per second. Higher = fewer wobbles.")]
        public float damping = 7.5f;
        [Tooltip("Acceleration (m/s^2) that produces a full-amplitude displacement.")]
        public float accelerationForFull = 45f;

        private Vector2 _offset;
        private Vector2 _velocity;
        private Vector2 _lastSourceVelocity;
        private Vector3 _restLocalPosition;

        // A ragdoll being teleported home on a round reset shows up as an enormous
        // one-frame acceleration; past this it is a respawn, not a hit.
        private const float TELEPORT_ACCEL = 400f;

        private void Start()
        {
            _restLocalPosition = transform.localPosition;
            if (source != null)
            {
                _lastSourceVelocity = source.linearVelocity;
            }
        }

        private void LateUpdate()
        {
            if (source == null)
            {
                return;
            }
            float dt = Time.deltaTime;
            if (dt <= 0f)
            {
                return;
            }

            Vector2 velocity = source.linearVelocity;
            Vector2 acceleration = (velocity - _lastSourceVelocity) / dt;
            _lastSourceVelocity = velocity;

            if (acceleration.magnitude > TELEPORT_ACCEL)
            {
                // Respawn: drop the wobble outright rather than launching the art
                // across the screen.
                _offset = Vector2.zero;
                _velocity = Vector2.zero;
                transform.localPosition = _restLocalPosition;
                return;
            }

            // The flesh lags the bone, so it is pushed OPPOSITE the acceleration.
            Vector2 target = -acceleration / accelerationForFull * amplitude;
            target = Vector2.ClampMagnitude(target, amplitude);

            // Critically-ish damped spring toward that target.
            Vector2 springForce = (target - _offset) * stiffness;
            _velocity += springForce * dt;
            _velocity *= Mathf.Exp(-damping * dt);
            _offset += _velocity * dt;
            _offset = Vector2.ClampMagnitude(_offset, amplitude);

            // The parent part carries a non-uniform scale, so an offset expressed
            // in local units would be squashed along one axis. localPosition is in
            // PARENT space, so divide the world offset by the parent's scale — not
            // this transform's — to keep the wobble round.
            Vector3 lossy = transform.parent != null ? transform.parent.lossyScale : Vector3.one;
            float x = Mathf.Approximately(lossy.x, 0f) ? 0f : _offset.x / lossy.x;
            float y = Mathf.Approximately(lossy.y, 0f) ? 0f : _offset.y / lossy.y;
            transform.localPosition = _restLocalPosition + new Vector3(x, y, 0f);
        }
    }
}
