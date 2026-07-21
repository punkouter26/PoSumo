using Unity.MLAgents;
using UnityEngine;

namespace PoSumo
{
    /// Referee for the sumo match. A wrestler loses when they are pushed past
    /// the ring edge / off the platform (torso beyond ringHalfWidth or below
    /// fallY) or — when knockdownLoses is on — thrown down (any non-foot body
    /// part touching the ground). Timeout is a draw.
    public class Systems_SumoMatchManager : MonoBehaviour
    {
        public Agent_Biped wrestlerA;
        public Agent_Biped wrestlerB;
        public float ringHalfWidth = 7f;
        public float roundTimeoutSeconds = 30f;
        public bool knockdownLoses = true;
        public float fallY = -1.5f;

        float _elapsed;

        void Start()
        {
            if (wrestlerA == null || wrestlerB == null)
            {
                var agents = FindObjectsByType<Agent_Biped>(FindObjectsSortMode.None);
                foreach (var a in agents)
                {
                    if (a.teamId == 0) wrestlerA = a; else wrestlerB = a;
                }
            }
            wrestlerA.opponent = wrestlerB;
            wrestlerB.opponent = wrestlerA;
            wrestlerA.ringHalfWidth = ringHalfWidth;
            wrestlerB.ringHalfWidth = ringHalfWidth;
            wrestlerA.arenaCenterX = transform.position.x;
            wrestlerB.arenaCenterX = transform.position.x;
        }

        void FixedUpdate()
        {
            if (wrestlerA == null || wrestlerB == null) return;
            _elapsed += Time.fixedDeltaTime;

            float cx = transform.position.x;
            bool aOut = Loses(wrestlerA, cx);
            bool bOut = Loses(wrestlerB, cx);

            if (aOut || bOut)
            {
                if (aOut && !bOut) EndRound(winner: wrestlerB, loser: wrestlerA);
                else if (bOut && !aOut) EndRound(winner: wrestlerA, loser: wrestlerB);
                else Draw(); // simultaneous — call it a draw
            }
            else if (_elapsed >= roundTimeoutSeconds)
            {
                Draw();
            }
        }

        bool Loses(Agent_Biped w, float centerX)
        {
            if (Mathf.Abs(w.TorsoX - centerX) > ringHalfWidth) return true; // pushed past edge
            if (w.Torso.position.y < fallY) return true;                    // fell off the platform
            if (knockdownLoses && w.IsDown) return true;                    // thrown to the ground
            return false;
        }

        void EndRound(Agent_Biped winner, Agent_Biped loser)
        {
            winner.AddReward(1f);
            loser.AddReward(-1f);
            winner.EndEpisode();
            loser.EndEpisode();
            _elapsed = 0f;
        }

        void Draw()
        {
            // Interrupted (not terminal) so value bootstrapping stays correct.
            wrestlerA.EpisodeInterrupted();
            wrestlerB.EpisodeInterrupted();
            _elapsed = 0f;
        }
    }
}
