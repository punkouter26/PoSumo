using UnityEngine;

namespace PoSumo
{
    /// Picks which two characters fight in this scene. Assign two character
    /// assets and the fighters are rebuilt as those characters — physique,
    /// brain, colour, face and name all follow from the asset.
    ///
    /// Execution order matters: Agent_Biped and Agent_BipedBody both read their
    /// `character` field during their own Awake (the body builds its ragdoll
    /// from the physique scales there). This component therefore runs at -500 so
    /// the assignment lands BEFORE those Awakes; doing it from a Start() would
    /// leave the body already built at the wrong size.
    [DefaultExecutionOrder(-500)]
    public sealed class Systems_MatchRoster : MonoBehaviour
    {
        [Tooltip("Character for the team-0 fighter (left). Leave empty to keep whatever the scene already has.")]
        [SerializeField] private Agent_CharacterDefinition _characterA;
        [Tooltip("Character for the team-1 fighter (right). Leave empty to keep whatever the scene already has.")]
        [SerializeField] private Agent_CharacterDefinition _characterB;

        private void Awake()
        {
            var agents = FindObjectsByType<Agent_Biped>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int agentIndex = 0; agentIndex < agents.Length; agentIndex++)
            {
                Agent_Biped agent = agents[agentIndex];
                Agent_CharacterDefinition wanted = agent.teamId == 0 ? _characterA : _characterB;
                if (wanted == null)
                {
                    continue;
                }
                Apply(agent, wanted);
            }
        }

        private static void Apply(Agent_Biped agent, Agent_CharacterDefinition character)
        {
            agent.character = character;
            // The brain travels with the character; clear any scene-serialized
            // model so Agent_Biped picks up the character's own inferenceModel.
            agent.inferenceModel = null;
            agent.walkModel = null;

            Agent_BipedBody body = agent.GetComponent<Agent_BipedBody>();
            if (body != null)
            {
                body.character = character;
            }
        }

        /// Scoreboard identity for a fighter, derived from its character.
        public static string DisplayName(Agent_Biped agent)
        {
            if (agent == null || agent.character == null)
            {
                return "?";
            }
            return agent.character.behaviorName.ToUpperInvariant();
        }
    }
}
