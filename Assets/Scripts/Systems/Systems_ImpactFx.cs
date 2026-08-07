using UnityEngine;

namespace PoSumo
{
    /// Turns body-on-body collisions into visible force: a dust burst scaled by how
    /// hard the hit actually was, plus a distinct ImpactFlash for the hits that beat
    /// the running average — so a good hit is legible AS a good hit, instead of
    /// being the same puff of clay as the shove before it, only slightly bigger.
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

        [Header("Big-hit burst")]
        [Tooltip("Multiple of the running MEAN impact speed a hit must beat to earn the Systems_DustPuff.ImpactFlash burst. This is what \"above average force\" means literally — the bar is set by how hard this particular pair have been hitting each other, so it keeps meaning something whether the bout is a shoving match or a brawl.")]
        public float bigHitMargin = 1.35f;
        [Tooltip("Absolute floor on strength (0..1) the hit must ALSO clear. Without it a bout of feeble taps promotes its own best tap to a highlight — beating the average is meaningless when the average is nothing.")]
        public float bigHitFloor = 0.45f;
        [Tooltip("Separate, longer cooldown for the big burst. It rides on top of the dust cooldown so a genuine flurry still reads as several distinct hits rather than one continuous strobe.")]
        public float bigHitCooldown = 0.3f;

        /// Exponential moving average of impact speed. Weighted low so one
        /// enormous collision does not raise the bar and suppress the hits right
        /// after it — the thing you most want to see.
        private const float MEAN_BLEND = 0.08f;

        // Camera shake used to live here, permanently disabled (maxShake = 0) but
        // still running a LateUpdate every frame and carrying four fields. It
        // fought the follow camera's framing and made the fight harder to read, so
        // it was never going to be switched on. Deleted rather than left as a dial.
        // Impact weight used to be carried by Systems_PostFx (contrast/bloom/aberration
        // punch) and Systems_MatchAudio (layered thud), which do not move the frame.

        private float _nextAllowed;
        private float _nextBigAllowed;
        private float _meanSpeed;

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
            if (speed < minSpeed)
            {
                return;
            }

            // The mean is sampled from EVERY qualifying contact, including the ones
            // the cooldowns below refuse to draw. Sampling only what got drawn would
            // define "average" as the average of the hits already judged worth
            // showing, and that ratchets the bar upward until nothing qualifies.
            //
            // `mean` is deliberately the value from BEFORE this hit folded in —
            // otherwise every hit is partly measured against itself, and the harder
            // it lands the more it raises its own bar.
            float mean = _meanSpeed > 0f ? _meanSpeed : speed;
            _meanSpeed = Mathf.Lerp(mean, speed, MEAN_BLEND);

            float strength = Mathf.Clamp01((speed - minSpeed) / Mathf.Max(0.01f, maxSpeed - minSpeed));

            Vector3 point = collision.contactCount > 0
                ? (Vector3)collision.GetContact(0).point
                : reporter.transform.position;
            Vector2 away = collision.relativeVelocity.sqrMagnitude > 0.0001f
                ? -collision.relativeVelocity.normalized
                : Vector2.up;

            // A GOOD HIT: above the running average AND hard in absolute terms.
            //
            // Judged before — and independently of — the dust cooldown on purpose.
            // Sharing that gate meant a big hit landing just after a nothing-tap was
            // swallowed by the tap's cooldown, which loses exactly the hit this
            // effect exists to sell.
            if (speed > mean * bigHitMargin && strength >= bigHitFloor
                && Time.time >= _nextBigAllowed)
            {
                _nextBigAllowed = Time.time + bigHitCooldown;
                Systems_DustPuff.ImpactFlash(point, away, strength);
            }

            if (Time.time < _nextAllowed)
            {
                return;
            }
            _nextAllowed = Time.time + cooldown;

            Systems_DustPuff.Burst(point, Mathf.RoundToInt(Mathf.Lerp(4f, 18f, strength)));

            // Sweat comes off the fighter taking the hit, thrown along the impact
            // direction. Only on real shoves — a light contact does not spray.
            if (strength > 0.3f)
            {
                Systems_DustPuff.SweatSpray(point, away, Mathf.RoundToInt(Mathf.Lerp(2f, 9f, strength)));
            }
        }
    }
}
