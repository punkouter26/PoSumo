using UnityEngine;

namespace PoSumo
{
    /// Broadcast dressing for round finishes: hit-stun slow motion on the
    /// deciding fall and a camera punch-in on the loser. Spawned at runtime by
    /// Systems_GameMatchManager; subscribes to its round events.
    public class Systems_MatchPresentation : MonoBehaviour
    {
        public float slowMoScale = 0.35f;
        public float slowMoRealSeconds = 1.1f;
        public float punchOrtho = 1.7f;
        [Tooltip("Chance a round win zooms on the winner's head instead of the loser's fall.")]
        public float winnerHeadZoomChance = 0.2f;
        public float winnerHeadOrtho = 0.8f;

        Systems_GameMatchManager _manager;
        Systems_CameraFollow _camFollow;

        bool _slowMoActive;
        float _slowMoEndReal;

        void Start()
        {
            _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            _camFollow = FindAnyObjectByType<Systems_CameraFollow>();
            if (_manager == null) return;

            _manager.RoundEnded += OnRoundEnded;
        }

        void OnDisable()
        {
            Time.timeScale = 1f;
            if (_manager != null)
            {
                _manager.RoundEnded -= OnRoundEnded;
            }
        }

        void OnRoundEnded(Agent_Biped winner, Agent_Biped loser)
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

        void Update()
        {
            if (_slowMoActive && Time.realtimeSinceStartup >= _slowMoEndReal)
            {
                Time.timeScale = 1f;
                _slowMoActive = false;
            }
        }
    }
}
