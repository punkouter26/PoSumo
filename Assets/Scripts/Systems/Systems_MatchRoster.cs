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
            // A tournament match overrides the scene's own roster, so SCN_SUMO
            // serves both the bracket and standalone exhibition play.
            bool tournament = Systems_TournamentState.Active;
            // A BOT LADDER bout overrides the roster the same way: challenger on
            // the left, the Bot on the right at the tier's torque.
            bool ladder = !tournament && Systems_BotLadderState.Active;
            Agent_CharacterDefinition slotA = tournament ? Systems_TournamentState.CurrentA
                                            : ladder ? Systems_BotLadderState.Challenger : _characterA;
            Agent_CharacterDefinition slotB = tournament ? Systems_TournamentState.CurrentB
                                            : ladder ? Systems_BotLadderState.Bot : _characterB;

            // A MIRROR match: both sides drew the same character asset.
            //
            // `Systems_TournamentState.SeparateFirstRoundMirrors` repairs the opening
            // round, but it documents that later mirrors are structural — with two
            // copies of a fighter alive in opposite halves nothing in a seeding pass
            // can stop them meeting, and a Nick-v-Nick FINAL was measured on
            // 2026-08-25. Both fighters rendered the same blue with the same face,
            // and the scorebug read "NICK 1 : 1 NICK", so nobody watching could tell
            // which side was which.
            //
            // Compared by REFERENCE, not by behaviorName: the bracket seeds the same
            // ScriptableObject instance into several slots, so this is the same
            // object rather than two assets that happen to agree.
            bool mirror = slotA != null && slotA == slotB;

            var agents = FindObjectsByType<Agent_Biped>(FindObjectsInactive.Include);
            for (int agentIndex = 0; agentIndex < agents.Length; agentIndex++)
            {
                Agent_Biped agent = agents[agentIndex];
                Agent_CharacterDefinition wanted = agent.teamId == 0 ? slotA : slotB;
                if (wanted == null)
                {
                    continue;
                }
                Apply(agent, wanted);

                if (ladder && agent.teamId != 0)
                {
                    // Before Agent_BipedBody.Awake (this runs at -500), so the joint
                    // torque caps are built from the multiplied value.
                    var body = agent.GetComponent<Agent_BipedBody>();
                    if (body != null)
                    {
                        body.torqueMultiplier = Systems_BotLadderState.TierTorque[Systems_BotLadderState.Tier];
                    }
                }

                // Only side B moves, so the fighter a player already recognises keeps
                // its own colour and name and the CHALLENGER is the one marked.
                if (mirror && agent.teamId != 0)
                {
                    MarkMirrorSide(agent, wanted);
                }
            }

            if (tournament && FindAnyObjectByType<Systems_TournamentReporter>() == null)
            {
                var go = new GameObject("TournamentReporter");
                go.transform.SetParent(transform, false);
                go.AddComponent<Systems_TournamentReporter>();
            }
            if (ladder && FindAnyObjectByType<Systems_BotLadderReporter>() == null)
            {
                var go = new GameObject("BotLadderReporter");
                go.transform.SetParent(transform, false);
                go.AddComponent<Systems_BotLadderReporter>();
            }
        }

        private static void Apply(Agent_Biped agent, Agent_CharacterDefinition character)
        {
            agent.character = character;
            // The brain travels with the character; clear any scene-serialized
            // model so Agent_Biped picks up the character's own inferenceModel.
            agent.inferenceModel = null;

            // Without a model the agent has no policy and simply collapses as a
            // ragdoll, with nothing in the log to explain why.
            //
            // ...UNLESS the character drives itself with `useBot`, and that exemption
            // is the whole point of this guard. `Agent_Bot` is a hand-written rules
            // policy and `Agent_Biped.Awake` switches BehaviorType to HeuristicOnly
            // for it, so a useBot character needs no ONNX and fights perfectly well
            // without one — measured 2026-08-25, Bot won its quarterfinal 2-0 by
            // ring-out. This fired at ERROR level on every match containing it and
            // said the fighter "will not fight" about the fighter that had just won,
            // which trains everyone reading the console to ignore a real error.
            if (character.inferenceModel == null && !character.useBot)
            {
                Debug.LogError($"Systems_MatchRoster: character '{character.behaviorName}' has no " +
                               $"inferenceModel — {agent.name} will have no brain and will not fight. " +
                               "Deploy a trained ONNX to that character asset, or set useBot " +
                               "to drive it with Agent_Bot instead.");
            }

            Agent_BipedBody body = agent.GetComponent<Agent_BipedBody>();
            if (body != null)
            {
                body.character = character;
            }
        }

        /// Makes the second side of a mirror match tellable from the first.
        ///
        /// Runs BEFORE `Agent_BipedBody.Awake` (this component is at -500), which is
        /// the only window in which the colour override lands — the body reads its
        /// colour once, while building the ragdoll, and every part's SpriteRenderer
        /// is tinted from it there.
        ///
        /// The shift is a HUE ROTATION rather than a fixed second colour, so it works
        /// for whichever fighter drew itself: a fixed "mirror blue" would collide
        /// with Nick's own blue in exactly the case that matters most. Saturation and
        /// value are pinned near the original so the challenger still reads as a lit
        /// body under the same rig rather than a flat swatch.
        private static void MarkMirrorSide(Agent_Biped agent, Agent_CharacterDefinition character)
        {
            Color.RGBToHSV(character.teamColor, out float h, out float s, out float v);
            Color shifted = Color.HSVToRGB(Mathf.Repeat(h + MIRROR_HUE_SHIFT, 1f),
                                           Mathf.Clamp01(s * 0.92f),
                                           Mathf.Clamp01(v));

            Agent_BipedBody body = agent.GetComponent<Agent_BipedBody>();
            if (body != null)
            {
                body.teamColorOverride = shifted;
            }

            // The scorebug, the result card and the DOMINANCE bar all read this
            // through Systems_GameMatchManager.AdoptCharacterIdentity.
            agent.displayNameOverride = character.behaviorName + " II";

            Systems_Log.Info($"[ROSTER] mirror match — side B recoloured and renamed " +
                             $"'{agent.displayNameOverride}' so the two sides are tellable apart.");
        }

        /// Far enough round the wheel to be unmistakable at a glance, short of the
        /// complement — which on a warm fighter lands on a sickly green.
        private const float MIRROR_HUE_SHIFT = 0.38f;
    }
}
