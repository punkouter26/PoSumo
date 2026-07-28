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

        private void Start()
        {
            _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            _camFollow = FindAnyObjectByType<Systems_CameraFollow>();
            _audio = FindAnyObjectByType<Systems_MatchAudio>();
            if (_manager == null) return;

            _manager.RoundEnded += OnRoundEnded;
            _manager.RoundStarted += OnRoundStarted;
            _manager.MatchEnded += OnMatchEnded;
            Systems_BodyDamage.Knockout += OnKnockout;
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
                var body = loser.GetComponent<Agent_BipedBody>();
                Transform focus = body != null && body.HeadRenderer != null
                    ? body.HeadRenderer.transform
                    : loser.Torso.transform;
                _camFollow.PunchIn(focus, matchEndPunchOrtho, matchEndHoldSeconds);
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
            Time.timeScale = 1f;
            if (_manager != null)
            {
                _manager.RoundEnded -= OnRoundEnded;
                _manager.RoundStarted -= OnRoundStarted;
                _manager.MatchEnded -= OnMatchEnded;
            }
            // Static event — leaking this keeps a dead scene's presentation alive.
            Systems_BodyDamage.Knockout -= OnKnockout;
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
                var body = winner.GetComponent<Agent_BipedBody>();
                Transform focus = body != null && body.HeadRenderer != null
                    ? body.HeadRenderer.transform
                    : winner.Torso.transform;
                _camFollow.PunchIn(focus, winnerHeadOrtho, slowMoRealSeconds + 0.6f);
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
                Time.timeScale = 1f;
                _slowMoActive = false;
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
