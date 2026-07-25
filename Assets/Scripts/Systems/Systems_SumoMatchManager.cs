using Unity.MLAgents;
using UnityEngine;

namespace PoSumo
{
    /// Training referee for the sumo env. Mirrors the deployed game's rules
    /// exactly: falling down is NOT a loss — a wrestler loses only after
    /// physically dropping off the dohyo (torso below fallY). Each round can
    /// randomize the starting platform width and surface friction so policies
    /// train across the whole endgame space. Timeout is a draw.
    public class Systems_SumoMatchManager : MonoBehaviour
    {
        public Agent_Biped wrestlerA;
        public Agent_Biped wrestlerB;
        public Systems_SumoArena arena;
        public float ringHalfWidth = 2.75f;
        public float roundTimeoutSeconds = 30f;
        [Tooltip("Legacy rule: any non-foot ground contact loses. Off for game parity.")]
        public bool knockdownLoses = false;
        public float fallY = -0.2f;

        [Header("Domain randomization")]
        public bool randomizeRounds = true;
        public Vector2 startHalfRange = new Vector2(1.7f, 2.75f);
        public Vector2 frictionRange = new Vector2(0.5f, 1.1f);

        [Header("Curriculum dials (overridden by trainer environment_parameters)")]
        [Tooltip("Half the spawn gap between wrestlers. Lessons start close (0.9) and widen to 1.2.")]
        public float spawnGapHalf = 1.2f;
        [Tooltip("Random mid-round perturbation shove impulse (N·s). Lessons start heavy (45) and fade to 0.")]
        public float shoveImpulse = 0f;
        [Tooltip("0 = always full stable platform, 1 = full width/friction randomization.")]
        public float platformDifficulty = 1f;

        float _elapsed;
        float _startHalf;
        float _nextShoveTime;
        Agent_BipedBody _bodyA, _bodyB;

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
            if (arena == null) arena = FindAnyObjectByType<Systems_SumoArena>();
            wrestlerA.opponent = wrestlerB;
            wrestlerB.opponent = wrestlerA;
            wrestlerA.ringHalfWidth = ringHalfWidth;
            wrestlerB.ringHalfWidth = ringHalfWidth;
            wrestlerA.arenaCenterX = transform.position.x;
            wrestlerB.arenaCenterX = transform.position.x;
            _bodyA = wrestlerA.GetComponent<Agent_BipedBody>();
            _bodyB = wrestlerB.GetComponent<Agent_BipedBody>();
            _startHalf = ringHalfWidth;
            ResetRound();
        }

        void FixedUpdate()
        {
            if (wrestlerA == null || wrestlerB == null) return;
            _elapsed += Time.fixedDeltaTime;

            // Curriculum perturbation shoves: random balance attacks keep the
            // recovery skill exercised in context; lessons fade them out.
            if (shoveImpulse > 0.5f && _elapsed >= _nextShoveTime)
            {
                var body = Random.value < 0.5f ? _bodyA : _bodyB;
                if (body != null)
                {
                    var dir = new Vector2(Random.Range(-1f, 1f), Random.Range(-0.1f, 0.35f)).normalized;
                    body.Chest.AddForce(dir * shoveImpulse, ForceMode2D.Impulse);
                }
                _nextShoveTime = _elapsed + Random.Range(3f, 7f);
            }

            bool aOut = Loses(wrestlerA);
            bool bOut = Loses(wrestlerB);

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

        bool Loses(Agent_Biped w)
        {
            if (w.Torso.position.y < fallY) return true;   // physically fell off
            if (knockdownLoses && w.IsDown) return true;   // legacy instant-loss rule
            return false;
        }

        void EndRound(Agent_Biped winner, Agent_Biped loser)
        {
            winner.AddReward(1f);
            loser.AddReward(-1f);
            winner.EndEpisode();
            loser.EndEpisode();
            ResetRound();
        }

        void Draw()
        {
            // Interrupted (not terminal) so value bootstrapping stays correct.
            wrestlerA.EpisodeInterrupted();
            wrestlerB.EpisodeInterrupted();
            ResetRound();
        }

        void ResetRound()
        {
            _elapsed = 0f;
            _nextShoveTime = Random.Range(2f, 5f);

            // Curriculum lessons override the dials each round.
            if (Academy.IsInitialized)
            {
                var ep = Academy.Instance.EnvironmentParameters;
                spawnGapHalf = ep.GetWithDefault("spawn_gap_half", spawnGapHalf);
                shoveImpulse = ep.GetWithDefault("shove_impulse", shoveImpulse);
                platformDifficulty = ep.GetWithDefault("platform_difficulty", platformDifficulty);
            }

            float cx = transform.position.x;
            if (wrestlerA != null)
                wrestlerA.transform.position = new Vector3(cx - spawnGapHalf, 0f, 0f);
            if (wrestlerB != null)
                wrestlerB.transform.position = new Vector3(cx + spawnGapHalf, 0f, 0f);

            float d = Mathf.Clamp01(platformDifficulty);
            if (randomizeRounds && d > 0f)
            {
                float minStart = Mathf.Lerp(ringHalfWidth, startHalfRange.x, d);
                _startHalf = Random.Range(minStart, startHalfRange.y);
                float frLo = Mathf.Lerp(0.9f, frictionRange.x, d);
                float frHi = Mathf.Lerp(0.9f, frictionRange.y, d);
                if (arena != null) arena.SetSurfaceFriction(Random.Range(frLo, frHi));
            }
            else
            {
                _startHalf = ringHalfWidth;
                if (arena != null) arena.SetSurfaceFriction(0.9f);
            }
            if (arena != null) arena.SetPlatformHalfWidth(_startHalf);
        }
    }
}
