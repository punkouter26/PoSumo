using UnityEngine;

namespace PoSumo
{
    /// Broadcast dressing for round finishes: hit-stun slow motion on the
    /// deciding fall and a camera punch-in on the loser. Spawned at runtime by
    /// Systems_GameMatchManager; subscribes to its round events.
    public sealed class Systems_MatchPresentation : MonoBehaviour
    {
        public float slowMoScale = 0.35f;
        public float slowMoRealSeconds = 1.1f;
        public float punchOrtho = 1.7f;
        [Tooltip("Chance a round win zooms on the winner's head instead of the loser's fall.")]
        public float winnerHeadZoomChance = 0.2f;
        public float winnerHeadOrtho = 0.8f;

        [Tooltip("Throw ceremonial salt over the ring at the start of each round (shio-maki).")]
        public bool enableSaltThrow = true;

        [Header("Big-hit knockback close-up")]
        [Tooltip("Cut to a close-up of the fighter who just got driven backwards by a heavy blow, and hold it while he falls.")]
        public bool enableKnockbackCloseUp = true;
        [Tooltip("Impact speed (m/s) a blow must reach to be worth cutting to. Below koSpeed (7.5) so this fires on solid pushes, not only on the rare head knockout. MEASURED: 6.5 with an away-speed of 1.2 fired ZERO times across 15 rounds - sumo is a pushing contest, not a striking one, so bodies separate slowly and both bars were set for a sport this is not. Lowered again after the joint-speed reduction, which cuts impact speeds further.")]
        public float knockbackSpeed = 4.5f;
        [Tooltip("How fast the struck fighter must actually be travelling AWAY from the one who hit him. This is what makes it a knockback rather than a clash: without it the shot fires on any hard contact, including two fighters driving into each other and going nowhere.")]
        public float knockbackAwaySpeed = 0.4f;
        [Tooltip("Ortho for the knockback close-up. Looser than the KO punch so the whole falling body stays in frame — the fall is the shot, not the face.")]
        public float knockbackOrtho = 1.9f;
        [Tooltip("Realtime seconds the close-up is held. Long enough to cover the fall and the landing.")]
        public float knockbackHoldSeconds = 1.3f;
        [Tooltip("Minimum realtime gap between knockback close-ups. Without it a scrappy exchange cuts every few frames and the camera becomes unwatchable.")]
        public float knockbackCooldown = 3f;

        [Header("Head KO (presentation only — does not end the round)")]
        public float koSlowMoScale = 0.18f;
        public float koSlowMoRealSeconds = 1.8f;
        public float koPunchOrtho = 1.1f;

        [Header("Match end")]
        [Tooltip("Seconds held tight on the fighter who lost the match before the camera pulls back.")]
        public float matchEndHoldSeconds = 2f;
        [Tooltip("Ortho for the tight hold on the fallen fighter.")]
        public float matchEndPunchOrtho = 1.6f;
        [Tooltip("Seconds the wide establishing shot of the whole arena is held after the pull-back.")]
        public float matchEndWideSeconds = 6f;

        private Systems_GameMatchManager _manager;
        private Systems_CameraFollow _camFollow;
        private Systems_MatchAudio _audio;

        private bool _slowMoActive;
        private float _slowMoEndReal;
        private bool _widePending;
        private float _wideAtReal;
        private bool _resolved;
        private bool _subscribed;
        /// Realtime stamp the knockback close-up is allowed to fire again.
        private float _knockbackReadyAt;

        /// References are resolved here rather than in OnEnable because this
        /// companion is spawned BEFORE Systems_MatchAudio is, so an OnEnable
        /// lookup would find no audio.
        private void Start()
        {
            _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            _camFollow = FindAnyObjectByType<Systems_CameraFollow>();
            _audio = FindAnyObjectByType<Systems_MatchAudio>();
            _resolved = true;
            Subscribe();
        }

        /// Start runs once, but OnDisable tears the subscriptions down — so
        /// without re-subscribing here a single disable/enable cycle of this
        /// object silently killed the KO slow motion, the round-end punch-in,
        /// the salt throw and the match-end camera beat for the rest of the
        /// session, with no error.
        private void OnEnable()
        {
            if (_resolved) Subscribe();
        }

        private void Subscribe()
        {
            if (_subscribed) return;
            _subscribed = true;
            if (_manager != null)
            {
                _manager.RoundEnded += OnRoundEnded;
                _manager.RoundStarted += OnRoundStarted;
                _manager.MatchEnded += OnMatchEnded;
            }
            Systems_BodyDamage.Knockout += OnKnockout;
            Sensor_Impact.AnyImpact += OnAnyImpact;
        }

        /// Cuts to a close-up of the fighter who has just been driven backwards by a
        /// heavy blow, and holds it while he goes over.
        ///
        /// Three guards, each of which this needs to be watchable:
        ///
        ///  - It must be a KNOCKBACK, not merely a hard contact. Two fighters
        ///    slamming into each other and going nowhere is the single most common
        ///    thing in a bout; without the away-speed test the camera cut on almost
        ///    every exchange. The struck fighter has to actually be leaving.
        ///  - It must not fight a bigger shot. A knockout fires on the same class of
        ///    blow and owns the camera and the clock when it does, and the round-end
        ///    and match-end beats own it afterwards — so this yields to slow motion
        ///    and only runs while the round is actually live.
        ///  - It must be rare. `knockbackCooldown` is realtime, like every other
        ///    shot deadline here, so a slow-motion finish does not stretch it.
        private void OnAnyImpact(Sensor_Impact sensor, Collision2D collision)
        {
            if (!enableKnockbackCloseUp || _camFollow == null || sensor == null) return;
            if (_slowMoActive || _widePending) return;                  // a bigger shot owns the camera
            if (_manager == null || !_manager.RoundLive) return;        // not during ceremony or result
            if (Time.realtimeSinceStartup < _knockbackReadyAt) return;

            Agent_BipedBody struck = sensor.owner;
            if (struck == null || struck.Torso == null) return;

            // Fighter-on-fighter only: the mat is by far the most frequent thing a
            // body hits, and landing hard is not a knockback.
            var attacker = collision.collider.GetComponentInParent<Agent_BipedBody>();
            if (attacker == null || attacker == struck || attacker.Torso == null) return;

            float hitSpeed = collision.relativeVelocity.magnitude;
            if (hitSpeed < knockbackSpeed) return;

            // Is he actually going backwards? Positive means the struck fighter is
            // travelling along the attacker->struck axis, i.e. away from the blow.
            // Read after the solver has run, which is when collision callbacks fire.
            float awaySign = Mathf.Sign(struck.Torso.position.x - attacker.Torso.position.x);
            float awaySpeed = struck.Torso.linearVelocity.x * awaySign;
            if (awaySpeed < knockbackAwaySpeed) return;

            _knockbackReadyAt = Time.realtimeSinceStartup + knockbackCooldown;
            // Body-level lookup rather than Systems_CameraFollow.FocusPoint, which
            // takes an Agent_Biped — Sensor_Impact only knows the Agent_BipedBody.
            // Same preference it applies: the head if there is drawn art, else the
            // torso. OnKnockout resolves its focus the same way.
            Transform focus = struck.HeadRenderer != null
                ? struck.HeadRenderer.transform
                : struck.Torso.transform;
            _camFollow.PunchIn(focus, knockbackOrtho, knockbackHoldSeconds);
            // Load-bearing, like [ROUND] and WALK-IN RESULT: a camera shot leaves no
            // other trace, and the ortho alone cannot tell this apart from the KO
            // punch that fires on the same class of blow.
            Systems_Log.Info($"[SHOT] knockback close-up on {struck.name} — " +
                             $"hit {hitSpeed:F1} m/s, driven back at {awaySpeed:F1} m/s");
        }

        /// Match-end camera beat: hold tight on the fighter who lost for
        /// matchEndHoldSeconds, then pull back to an establishing shot of the
        /// whole arena.
        ///
        /// Both timings are REALTIME, matching the slow-motion timer and the UI
        /// scheduler, so the beat plays at the same pace whatever Time.timeScale
        /// the finish left behind.
        private void OnMatchEnded(Agent_Biped winner)
        {
            if (_camFollow == null || _manager == null) return;

            Agent_Biped loser = winner == _manager.wrestlerA ? _manager.wrestlerB : _manager.wrestlerA;
            if (loser != null && loser.Torso != null)
            {
                _camFollow.PunchIn(Systems_CameraFollow.FocusPoint(loser),
                                   matchEndPunchOrtho, matchEndHoldSeconds);
            }

            // Queued rather than chained off a coroutine so it survives the
            // fighters being reset, and so a rematch can simply cancel it.
            _wideAtReal = Time.realtimeSinceStartup + matchEndHoldSeconds;
            _widePending = true;
        }

        /// A head KO gets the full treatment even though it does NOT end the round:
        /// harder and longer slow motion than a normal finish, and the camera drives
        /// into the head that just got hit. The referee is untouched — the round is
        /// still won by pushing the limp body out.
        private void OnKnockout(Agent_BipedBody victim, Vector3 point)
        {
            if (victim == null) return;

            Time.timeScale = koSlowMoScale;
            _slowMoActive = true;
            _slowMoEndReal = Time.realtimeSinceStartup + koSlowMoRealSeconds;

            if (_camFollow != null && victim.HeadRenderer != null)
            {
                _camFollow.PunchIn(victim.HeadRenderer.transform, koPunchOrtho,
                                   koSlowMoRealSeconds + 0.5f);
            }
            if (_audio != null)
            {
                // Duck the crowd so the hit lands in a hole in the mix, then let
                // the reaction come up behind it.
                _audio.Duck(0.6f, 0.9f);
            }
        }

        private void OnDisable()
        {
            // Only undo slow motion this component actually applied, and never
            // over a pause — a blanket write to 1 also cancelled the player's.
            if (_slowMoActive)
            {
                _slowMoActive = false;
                RestoreTimeScale();
            }
            if (!_subscribed) return;
            _subscribed = false;
            if (_manager != null)
            {
                _manager.RoundEnded -= OnRoundEnded;
                _manager.RoundStarted -= OnRoundStarted;
                _manager.MatchEnded -= OnMatchEnded;
            }
            // Static events — leaking these keeps a dead scene's presentation alive.
            Systems_BodyDamage.Knockout -= OnKnockout;
            Sensor_Impact.AnyImpact -= OnAnyImpact;
        }

        /// Ends slow motion without stepping on a pause.
        ///
        /// The slow-mo deadline is REALTIME, so it keeps running while paused at
        /// timeScale 0 — and Update runs there too. Writing 1 unconditionally
        /// therefore resumed the match a second or so after the player paused
        /// during a round-end or KO finish, with the PAUSED card still up.
        /// TogglePause restores the scale itself on resume, so skipping it here
        /// loses nothing.
        private void RestoreTimeScale()
        {
            if (_manager != null && _manager.IsPaused) return;
            Time.timeScale = 1f;
        }

        /// Shio-maki: each wrestler throws purifying salt across the dohyo before
        /// the bout. Costs two particle bursts and is the most recognisable thing
        /// sumo does.
        private void OnRoundStarted()
        {
            // A rematch cancels the post-match camera beat: without this the wide
            // shot could still be pending or live when the next bout opens.
            _widePending = false;
            if (_camFollow != null) _camFollow.ClearShots();

            if (!enableSaltThrow || _manager == null) return;
            ThrowSalt(_manager.wrestlerA);
            ThrowSalt(_manager.wrestlerB);
        }

        private void ThrowSalt(Agent_Biped fighter)
        {
            if (fighter == null || fighter.Torso == null) return;
            var body = fighter.GetComponent<Agent_BipedBody>();
            float facing = body != null ? body.facingSign : 1f;
            Vector3 hand = fighter.Torso.transform.position + new Vector3(0.25f * facing, 0.45f, 0f);
            Systems_DustPuff.SaltThrow(hand, facing);
            if (_audio != null)
            {
                _audio.PlaySalt(hand.x);
            }
        }

        private void OnRoundEnded(Agent_Biped winner, Agent_Biped loser)
        {
            if (loser == null) return; // draws get no dramatics

            Time.timeScale = slowMoScale;
            _slowMoActive = true;
            _slowMoEndReal = Time.realtimeSinceStartup + slowMoRealSeconds;
            if (_camFollow == null) return;

            // Occasionally celebrate the winner's face instead of the fall.
            if (winner != null && Random.value < winnerHeadZoomChance)
            {
                _camFollow.PunchIn(Systems_CameraFollow.FocusPoint(winner),
                                   winnerHeadOrtho, slowMoRealSeconds + 0.6f);
            }
            else if (loser.Torso != null)
            {
                _camFollow.PunchIn(loser.Torso.transform, punchOrtho, slowMoRealSeconds + 0.4f);
            }
        }

        private void Update()
        {
            if (_slowMoActive && Time.realtimeSinceStartup >= _slowMoEndReal)
            {
                _slowMoActive = false;
                RestoreTimeScale();
            }

            // The hold on the fallen fighter has expired — pull back to the arena.
            if (_widePending && Time.realtimeSinceStartup >= _wideAtReal)
            {
                _widePending = false;
                if (_camFollow != null)
                {
                    _camFollow.PullBackWide(matchEndWideSeconds);
                }
            }
        }
    }
}
