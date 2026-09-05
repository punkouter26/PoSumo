using UnityEngine;

namespace PoSumo
{
    /// Physical feedback for the moments the fight already knows are important:
    /// device haptics, plus camera trauma on the discrete events that produced
    /// none.
    ///
    /// WHY THIS IS A SEPARATE COMPANION rather than lines added to the systems
    /// that raise these events. The events are spread across three owners —
    /// `Systems_BodyDamage`'s three statics and the referee's round/match events —
    /// and companions may not reach each other. A companion that subscribes to all
    /// of them is the sanctioned shape, and it means haptics can be switched off
    /// wholesale with one flag rather than unpicked out of five files.
    ///
    /// TWO GAPS IT FILLS, both measured on 2026-09-05:
    ///
    ///  - `Assets/Scripts/` contained ZERO haptic calls. On an Android title that
    ///    is the cheapest felt quality available: a ring-out, a knockout and a
    ///    decapitation all landed with the phone perfectly still.
    ///  - `Systems_CameraFollow.AddTrauma` had exactly ONE caller,
    ///    `Systems_ImpactFx`, which shakes on body-on-body collision speed. Its own
    ///    doc comment claims "Systems_ImpactFx and Systems_BodyDamage both call
    ///    it" — Systems_BodyDamage never did. So the loudest events in the game
    ///    (head KO, a limb coming off, the round ending) moved the frame LESS than
    ///    an ordinary shove, because a shove at least carries collision speed and a
    ///    decapitation carries none.
    ///
    /// It deliberately does NOT shake on ordinary impacts: `Systems_ImpactFx` owns
    /// that signal and doubling it would wash the two together. Discrete events
    /// only, which is why there is no overlap.
    ///
    /// Spawned by Systems_GameMatchManager.Start behind `enableFeelFx`.
    /// Presentation only — it reads outcomes and writes nothing a referee sees.
    public sealed class Systems_FeelFx : MonoBehaviour
    {
        [Tooltip("Vibrate the device on the big moments. Android only — there is no desktop or Editor haptic path by design, so this is a no-op there rather than a stub that pretends.")]
        [SerializeField] private bool _enableHaptics = true;

        [Tooltip("Scales every haptic pulse length, 0..1. The per-event durations are already short; this is the single dial for \"less of it\" without editing five numbers.")]
        [SerializeField] private float _hapticScale = 1f;

        [Tooltip("Shake the camera on the discrete events — knockout, dismemberment, gib, round end. Ordinary collision shake belongs to Systems_ImpactFx and is not affected by this.")]
        [SerializeField] private bool _enableEventShake = true;

        [Tooltip("Seconds between IMPACT haptics. Without it a clinch buzzes continuously: Sensor_Impact fires many times a second, and a vibrator that never stops is both unreadable and a real battery cost. The discrete events bypass this — they cannot repeat fast enough to need it.")]
        [SerializeField] private float _impactHapticCooldown = 0.22f;

        [Tooltip("Relative speed (m/s) an impact must reach to earn a haptic tick. Set against the SAME measured distribution Systems_StrikeImpulse calibrates to — landed blows log at 3.9-5.3 m/s — so this sits near the top of it and ticks on good hits rather than on every contact.")]
        [SerializeField] private float _impactHapticMinSpeed = 4.2f;

        /// Camera trauma per event, 0..1. Trauma ACCUMULATES and the camera squares
        /// it before applying, so these sit well under 1 on purpose:
        /// Systems_ImpactFx's ceiling for one full-strength blow is 0.55, and a
        /// knockout should read as harder than a shove without pinning the shake at
        /// maximum and losing every distinction above it.
        private const float TRAUMA_KNOCKOUT = 0.7f;
        private const float TRAUMA_DISMEMBER = 0.55f;
        private const float TRAUMA_GIB = 0.9f;
        private const float TRAUMA_ROUND_END = 0.35f;

        /// Haptic pulse lengths in milliseconds, before `_hapticScale`. Bounded by
        /// what the hardware can actually render rather than picked from a spec:
        /// below roughly 10 ms a linear resonant actuator has not spun up and the
        /// pulse cannot be felt at all, and above roughly 60 ms a single pulse
        /// stops reading as an impact and starts reading as a notification.
        private const int MS_IMPACT = 12;
        private const int MS_KNOCKOUT = 45;
        private const int MS_DISMEMBER = 30;
        private const int MS_ROUND_END = 35;

        /// Amplitudes, 0..1, mapped onto Android's 1..255 at the call site.
        private const float AMP_IMPACT = 0.45f;
        private const float AMP_KNOCKOUT = 1f;
        private const float AMP_DISMEMBER = 0.8f;
        private const float AMP_ROUND_END = 0.7f;

        /// Widening factor applied to the impact amplitude ramp, in m/s above the
        /// threshold. Past this the tick is at full strength.
        private const float IMPACT_AMP_RANGE = 3f;

        /// PlayerPrefs key, so a settings screen can turn haptics off for good.
        /// Deliberately separate from the audio mute in Systems_GameMatchManager: a
        /// player who mutes the game on a train has not asked the phone to stop
        /// buzzing, and one who wants the phone still has not asked for silence.
        private const string HAPTICS_PREF = "posumo.haptics";

        private Systems_GameMatchManager _manager;
        private Systems_CameraFollow _camera;
        private Systems_Haptics _haptics;

        private float _nextImpactHapticAt;
        private bool _resolved;
        private bool _subscribed;

        /// Player-facing haptics switch, persisted. Static so a settings screen can
        /// read and write it with no match loaded — this companion exists only
        /// while a bout is running.
        public static bool HapticsEnabled
        {
            get { return PlayerPrefs.GetInt(HAPTICS_PREF, 1) == 1; }
            set
            {
                PlayerPrefs.SetInt(HAPTICS_PREF, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }

        /// Split deliberately into a fixed part and a live part. `Available` is a
        /// hardware fact settled in the constructor, so it can gate a subscription;
        /// `HapticsEnabled` is the player's own switch and may be flipped by a
        /// settings screen mid-session, so it has to be re-read per pulse.
        private bool HapticsAvailable
        {
            get { return _enableHaptics && _haptics != null && _haptics.Available; }
        }

        private bool HapticsActive
        {
            get { return HapticsAvailable && HapticsEnabled; }
        }

        /// Resolved in Start rather than OnEnable for the reason
        /// Systems_MatchPresentation documents: companions are spawned in sequence,
        /// so an OnEnable lookup can run before the thing it looks for exists.
        private void Start()
        {
            _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            _camera = FindAnyObjectByType<Systems_CameraFollow>();
            _haptics = new Systems_Haptics();
            _resolved = true;
            Subscribe();
        }

        /// Start runs once, but OnDisable tears the subscriptions down — so without
        /// re-subscribing here a single disable/enable cycle silently kills every
        /// effect in this file for the rest of the session, with no error. Same
        /// trap and same fix as Systems_MatchPresentation.
        private void OnEnable()
        {
            if (_resolved)
            {
                Subscribe();
            }
        }

        private void OnDisable()
        {
            if (!_subscribed)
            {
                return;
            }
            _subscribed = false;
            if (_manager != null)
            {
                _manager.RoundEnded -= OnRoundEnded;
                _manager.MatchEnded -= OnMatchEnded;
            }
            // Static events. A subscription that outlives this object keeps a dead
            // scene's companion alive across the load into the next bout, and then
            // vibrates the phone off a destroyed manager.
            Systems_BodyDamage.Knockout -= OnKnockout;
            Systems_BodyDamage.Dismembered -= OnDismembered;
            Systems_BodyDamage.Gibbed -= OnGibbed;
            Sensor_Impact.AnyImpact -= OnAnyImpact;
        }

        private void OnDestroy()
        {
            if (_haptics != null)
            {
                _haptics.Dispose();
                _haptics = null;
            }
        }

        private void Subscribe()
        {
            if (_subscribed)
            {
                return;
            }
            _subscribed = true;
            if (_manager != null)
            {
                _manager.RoundEnded += OnRoundEnded;
                _manager.MatchEnded += OnMatchEnded;
            }
            Systems_BodyDamage.Knockout += OnKnockout;
            Systems_BodyDamage.Dismembered += OnDismembered;
            Systems_BodyDamage.Gibbed += OnGibbed;

            // ONLY when the device can actually vibrate. This is the one handler
            // here that runs per COLLISION rather than per event, and the impact
            // tick is the only thing it does — the shake on ordinary contact
            // belongs to Systems_ImpactFx. Off Android, and in the Editor, every
            // one of those calls would run the cooldown test, the speed test and a
            // GetComponentInParent only to reach a Pulse that cannot fire.
            //
            // Gated on the hardware fact, not on the player's preference: the
            // preference can be toggled mid-session and is re-read per pulse, so
            // binding the subscription to it would leave haptics dead until the
            // next bout after someone switched them back on.
            if (HapticsAvailable)
            {
                Sensor_Impact.AnyImpact += OnAnyImpact;
            }
        }

        /// A light tick on good hits only.
        ///
        /// Gated on the round actually being live: the ceremony, the walk-in and
        /// the result card all generate contacts — the fighters are posed, dropped
        /// and reset through all three — and a phone buzzing through a countdown
        /// reads as a fault rather than as feedback.
        private void OnAnyImpact(Sensor_Impact sensor, Collision2D collision)
        {
            if (sensor == null || collision == null || sensor.owner == null)
            {
                return;
            }
            if (_manager == null || !_manager.RoundActive)
            {
                return;
            }
            if (Time.unscaledTime < _nextImpactHapticAt)
            {
                return;
            }

            // Speed BEFORE the component lookup: this runs inside a collision
            // callback that fires many times a second during a clinch, and the
            // overwhelming majority of contacts fail this test.
            float speed = collision.relativeVelocity.magnitude;
            if (speed < _impactHapticMinSpeed)
            {
                return;
            }

            // Fighter-on-fighter only. Feet hitting clay every step is the single
            // most frequent contact in the game and it is not a blow.
            Agent_BipedBody other = collision.collider.GetComponentInParent<Agent_BipedBody>();
            if (other == null || other == sensor.owner)
            {
                return;
            }

            _nextImpactHapticAt = Time.unscaledTime + _impactHapticCooldown;
            // Amplitude tracks how hard it actually was, so a scrappy exchange
            // feels different from one clean shot instead of identical.
            float over = Mathf.Clamp01((speed - _impactHapticMinSpeed) / IMPACT_AMP_RANGE);
            Pulse(MS_IMPACT, Mathf.Lerp(AMP_IMPACT, 1f, over));
        }

        private void OnKnockout(Agent_BipedBody victim, Vector3 point)
        {
            Shake(TRAUMA_KNOCKOUT);
            Pulse(MS_KNOCKOUT, AMP_KNOCKOUT);
        }

        private void OnDismembered(Agent_BipedBody body, Systems_BodyDamage.Region region, Vector3 point)
        {
            // A head coming off is the showpiece finish here and must not feel the
            // same as an arm.
            bool decapitation = region == Systems_BodyDamage.Region.Head;
            Shake(decapitation ? TRAUMA_GIB : TRAUMA_DISMEMBER);
            Pulse(decapitation ? MS_KNOCKOUT : MS_DISMEMBER,
                  decapitation ? AMP_KNOCKOUT : AMP_DISMEMBER);
        }

        private void OnGibbed(Agent_BipedBody body, Vector3 point)
        {
            Shake(TRAUMA_GIB);
            Pulse(MS_KNOCKOUT, AMP_KNOCKOUT);
        }

        /// The thump of the finish, scaled by HOW the round ended. Read off the
        /// referee's `LastOutcome` rather than inferred from the winner: a fighter
        /// torn apart and a fighter edged off the rim arrive at this handler as the
        /// same event and should not land the same.
        private void OnRoundEnded(Agent_Biped winner, Agent_Biped loser)
        {
            if (loser == null)
            {
                return;   // draws get no punctuation, matching the slow-mo rule
            }

            float weight = 1f;
            if (_manager != null
                && _manager.LastOutcome == Systems_GameMatchManager.RoundOutcome.Gibbed)
            {
                weight = 1.6f;
            }

            Shake(TRAUMA_ROUND_END * weight);
            Pulse(Mathf.RoundToInt(MS_ROUND_END * weight), AMP_ROUND_END);
        }

        /// Two pulses for a match, one for a round. The PATTERN is what tells the
        /// hand which it was without looking at the screen, which is the whole
        /// reason this is not simply a longer buzz.
        private void OnMatchEnded(Agent_Biped winner)
        {
            if (!HapticsActive)
            {
                return;
            }
            _haptics.Pattern(new long[] { 0L, 55L, 70L, 90L }, new int[] { 0, 255, 0, 255 });
        }

        private void Pulse(int milliseconds, float amplitude01)
        {
            if (!HapticsActive)
            {
                return;
            }
            int scaled = Mathf.RoundToInt(milliseconds * Mathf.Clamp01(_hapticScale));
            if (scaled <= 0)
            {
                return;
            }
            _haptics.Pulse(scaled, amplitude01);
        }

        private void Shake(float trauma01)
        {
            if (!_enableEventShake)
            {
                return;
            }
            // Re-resolved lazily: the camera is a scene object and a bout that
            // reloads its arena can outlive the reference taken in Start.
            if (_camera == null)
            {
                _camera = FindAnyObjectByType<Systems_CameraFollow>();
            }
            if (_camera != null)
            {
                _camera.AddTrauma(trauma01);
            }
        }
    }
}
