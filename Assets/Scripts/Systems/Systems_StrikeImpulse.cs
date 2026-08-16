using System.Collections.Generic;
using UnityEngine;

namespace PoSumo
{
    /// Makes a landed punch or kick actually MOVE the man it hits.
    ///
    /// Without this a strike is only whatever momentum a limb carries into a
    /// collision, and the arithmetic is hopeless: a forearm is a couple of
    /// kilograms swinging into a 69.6 kg body, so even a clean head shot barely
    /// rocks the target. Every downstream system was already written expecting
    /// knockback that the physics never produced — Systems_MatchPresentation's
    /// knockback close-up requires the struck fighter to be travelling AWAY from
    /// the blow (`knockbackAwaySpeed`) before it will fire, and it almost never
    /// did. This supplies the momentum that shot was waiting for.
    ///
    /// GAME-ONLY, and deliberately so — the same category as knockoutsToLoseMatch
    /// and downOutSeconds-before-2026-08-15. Systems_SumoMatchManager never spawns
    /// it, so no training env has it and no brain has ever fought against it. That
    /// is a real asymmetry and it is the accepted price: putting launch impulses
    /// into the training physics would invalidate all four brains for a rule that
    /// is spectacle rather than sumo. If it is ever mirrored into training, every
    /// fighter needs a fresh run.
    ///
    /// Spawned by Systems_GameMatchManager.Start behind `enableStrikeImpulse`.
    /// Like Systems_BodyDamage it does NOT read GameTuning for its numbers, so the
    /// code defaults below are what actually run.
    public sealed class Systems_StrikeImpulse : MonoBehaviour
    {
        [Tooltip("Closing speed a contact must carry before it counts as a strike rather than a lean, in m/s. Below this the fighters are wrestling, and shoving them apart would wreck the grapple that sumo is mostly made of.")]
        [SerializeField] private float _minStrikeSpeed = 3.5f;

        [Tooltip("Newton-seconds of impulse per m/s of closing speed ABOVE the threshold. The whole launch curve starts at zero at the threshold, so a marginal hit still just nudges.")]
        // CALIBRATED AGAINST MEASURED PUNCH SPEEDS, not guessed. A live bout logs
        // landed blows at 3.9-5.3 m/s, so the curve has to do its whole job across
        // roughly 0.4-1.8 m/s ABOVE the threshold. The first value here was 26,
        // which put full power at 11.9 m/s — a speed this game never produces, so
        // every hit sat in the bottom 20% of the curve and the feature read as
        // "barely does anything" rather than "mistuned". Same failure the walk-tall
        // runs hit with WALK_TALL_Y: a term whose range sits outside the observed
        // distribution is not weak, it is absent. At 90: a 4 m/s jab is 45 N.s
        // (0.65 m/s of shove), a clean 5 m/s hit is 135 N.s (1.9 m/s, a real stagger)
        // and 6 m/s saturates the cap and launches him.
        [SerializeField] private float _impulsePerSpeed = 90f;

        [Tooltip("Hard ceiling on one strike's impulse, in N.s. At 69.6 kg a 220 N.s cap is about 3.2 m/s of bulk velocity change — enough to send a man off the rim of a 3.5 m mat, not enough to fire him into the crowd.")]
        [SerializeField] private float _maxImpulse = 220f;

        [Tooltip("Share of the upward component mixed into the push, 0..1. Purely horizontal reads as a shove along the mat; a little lift is what makes a good shot look like a knockdown instead.")]
        [SerializeField] private float _liftFraction = 0.35f;

        [Tooltip("Fraction of the impulse applied to the whole body by mass share, so the victim TRAVELS. The remainder lands on the part that was actually hit, which is what gives the hit its whip and spin.")]
        [SerializeField] private float _bulkShare = 0.6f;

        [Tooltip("Seconds before the same attacker can launch again. One swing generates a burst of collision callbacks across several colliders; without this a single punch stacks a dozen impulses and the victim leaves the arena.")]
        [SerializeField] private float _cooldown = 0.3f;

        [Tooltip("Log every strike. OFF by default — leave it off for training.\n\nMEASURED COST, do not turn this on in a training env without reading this: env builds are BuildOptions.Development, so Systems_Log.Info is compiled IN, and one bout generates strikes continuously. A 36-minute run wrote 37 MB per env worker — about 230 MB/hour per fighter across four workers — and it did not merely waste disk. One run's env logging died outright with 'Curl error 23: Failure writing output to destination', so the workers trained on blind. Turn it on in the Editor to tune the numbers below, never in a headless run.")]
        [SerializeField] private bool _logStrikes = false;

        [Tooltip("Multiplier when the blow lands on an arm or leg rather than the trunk or head. A hit that only finds a limb is a block, and should not move a man the way a clean body shot does.")]
        // Also the cure for a specific artefact: arm-vs-arm contact raises the event
        // for BOTH fighters, since each side's part passes the striking-limb test.
        // At full power that fired two opposed launches off one clash and both men
        // flew apart, which reads as a physics glitch rather than a blow landing.
        [SerializeField] private float _limbHitScale = 0.35f;

        /// The GAME referee, when there is one. Training has no ceremony, no
        /// countdown and no result card, so there is nothing to gate against there
        /// and this stays null — see RoundLive.
        private Systems_GameMatchManager _manager;
        private bool _training;

        /// Whether it is legal to launch a fighter right now.
        ///
        /// In the game the fighters are frozen or posed through the intro, the
        /// walk-in, the countdown and the result card, and an impulse then fights
        /// whatever is holding them. Systems_SumoMatchManager has none of those
        /// phases — a training round is live from the first physics step — so the
        /// gate is simply absent there rather than faked.
        private bool RoundLive => _training ? true : _manager != null && _manager.RoundLive;

        /// Rigidbody list per body. Rebuilt only when a body is first seen —
        /// GetComponentsInChildren allocates, and this runs inside a collision
        /// callback that fires many times per second during a clinch.
        private readonly Dictionary<Agent_BipedBody, Rigidbody2D[]> _partCache =
            new Dictionary<Agent_BipedBody, Rigidbody2D[]>();

        /// Last launch time per attacker, for the cooldown above.
        private readonly Dictionary<Agent_BipedBody, float> _readyAt =
            new Dictionary<Agent_BipedBody, float>();

        private void Awake()
        {
            _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            _training = _manager == null && FindAnyObjectByType<Systems_SumoMatchManager>() != null;
        }

        // Static event: an unsubscribe that does not happen outlives the scene load
        // into the next bout and launches fighters using a destroyed manager.
        private void OnEnable() => Sensor_Impact.AnyImpact += OnImpact;

        private void OnDisable()
        {
            Sensor_Impact.AnyImpact -= OnImpact;
            _partCache.Clear();
            _readyAt.Clear();
        }

        /// True for the four limb segments that can throw a blow.
        ///
        /// Read off the part name because that is the only thing Sensor_Impact
        /// carries besides `isFoot`, and `isFoot` alone would miss every punch and
        /// every shin kick. Thigh is excluded on purpose: it is under the body in a
        /// clinch almost permanently, and treating it as a striker turns ordinary
        /// grappling into both men repeatedly firing each other across the ring.
        private static bool IsStrikingPart(string partName)
        {
            return partName.StartsWith("UArm") || partName.StartsWith("FArm")
                || partName.StartsWith("Shin") || partName.StartsWith("Foot")
                || partName.StartsWith("Toe");
        }

        private void OnImpact(Sensor_Impact sensor, Collision2D collision)
        {
            if (sensor == null || collision == null) return;

            Agent_BipedBody attacker = sensor.owner;
            if (attacker == null || attacker.Torso == null) return;

            // The sensor sits on the part that OWNS this callback, and
            // Collision2D.collider is the other side. Both bodies raise the event
            // for one contact, so gating on "is my part a striking limb" is also
            // what picks the striker out of the pair.
            if (!IsStrikingPart(sensor.gameObject.name)) return;

            var victim = collision.collider.GetComponentInParent<Agent_BipedBody>();
            if (victim == null || victim == attacker || victim.Torso == null) return;

            if (!RoundLive) return;

            if (_readyAt.TryGetValue(attacker, out float ready) && Time.time < ready) return;

            float speed = collision.relativeVelocity.magnitude;
            if (speed < _minStrikeSpeed) return;

            _readyAt[attacker] = Time.time + _cooldown;

            float impulse = Mathf.Min((speed - _minStrikeSpeed) * _impulsePerSpeed, _maxImpulse);
            // A blow that only finds an arm or a shin is a block, not a landed shot.
            bool cleanHit = !IsStrikingPart(collision.collider.gameObject.name);
            if (!cleanHit) impulse *= _limbHitScale;
            if (impulse <= 0f) return;

            // Along the attacker->victim axis, flattened to horizontal and then
            // given a fixed share of lift. Taken between TORSOS rather than from
            // the contact normal: a contact normal off a limb points wherever that
            // limb happened to be, and a punch that lands slightly downward would
            // drive the victim into the mat instead of back off it.
            float awaySign = Mathf.Sign(victim.Torso.position.x - attacker.Torso.position.x);
            if (Mathf.Approximately(awaySign, 0f)) awaySign = 1f;
            Vector2 direction = new Vector2(awaySign * (1f - _liftFraction), _liftFraction).normalized;

            Rigidbody2D[] parts = PartsOf(victim);
            if (parts == null || parts.Length == 0) return;

            float totalMass = 0f;
            for (int index = 0; index < parts.Length; index++)
            {
                if (parts[index] != null) totalMass += parts[index].mass;
            }
            if (totalMass <= 0f) return;

            // Bulk share by mass gives every part the same velocity change, so the
            // body travels as one object instead of the torso tearing away from its
            // own limbs against the joint limits.
            Vector2 bulk = direction * (impulse * _bulkShare);
            for (int index = 0; index < parts.Length; index++)
            {
                Rigidbody2D part = parts[index];
                if (part == null) continue;
                part.AddForce(bulk * (part.mass / totalMass), ForceMode2D.Impulse);
            }

            // The remainder on the part actually hit — this is the whip that makes
            // a head shot snap the head and a body shot fold the torso.
            Rigidbody2D struckPart = collision.collider.attachedRigidbody;
            if (struckPart != null)
            {
                struckPart.AddForce(direction * (impulse * (1f - _bulkShare)), ForceMode2D.Impulse);
            }

            // Guarded by an `if` rather than left to [Conditional] on Systems_Log.Info,
            // which is the opposite of this project's usual rule. The rule exists so a
            // release player never builds the interpolated string; here the string is
            // built in every DEVELOPMENT build, which is exactly what a training env
            // is, and the volume is the whole problem. See _logStrikes.
            if (_logStrikes)
            {
                Systems_Log.Info($"[STRIKE] {sensor.gameObject.name} -> {collision.collider.gameObject.name} " +
                                 $"on {victim.name} speed={speed:F1} impulse={impulse:F0}Ns " +
                                 $"{(cleanHit ? "CLEAN" : "blocked")}");
            }
        }

        private Rigidbody2D[] PartsOf(Agent_BipedBody body)
        {
            if (_partCache.TryGetValue(body, out Rigidbody2D[] cached)) return cached;
            Rigidbody2D[] parts = body.GetComponentsInChildren<Rigidbody2D>();
            _partCache[body] = parts;
            return parts;
        }
    }
}
