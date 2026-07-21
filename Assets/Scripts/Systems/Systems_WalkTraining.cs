using UnityEngine;

namespace PoSumo
{
    /// Phase-1 training rig: builds N vertically-stacked, fully self-contained
    /// lane environments, each with the standard 7-group hierarchy and one solo
    /// biped in Walk mode learning to reach ring center. Lane 0 sits at the
    /// origin so the fixed camera shows it in Game view. No cross-environment
    /// state is shared.
    public class Systems_WalkTraining : MonoBehaviour
    {
        public int lanes = 8;
        public float laneSpacing = 8f;
        public float spawnX = -5f;

        [Tooltip("Optional trained model; when set, spawned walkers run inference (no Python needed).")]
        public Unity.InferenceEngine.ModelAsset inferenceModel;

        [Header("Wrestler build")]
        public string behaviorName = "Matt";
        public float massScale = 1f;
        public float widthScale = 1f;
        public float torqueScale = 1f;
        public Color teamColor = new Color(0.85f, 0.25f, 0.2f);

        static readonly string[] GROUPS =
            { "Agents", "Obstacles", "Goals", "SpawnPoints", "Cameras", "UI", "Systems" };

        void Awake()
        {
            // Viewing mode (trained model assigned): one lane is enough — the
            // parallel lanes only exist to speed up training.
            int laneCount = inferenceModel != null ? 1 : lanes;
            for (int i = 0; i < laneCount; i++)
            {
                var env = new GameObject($"Environment_Lane{i}");
                env.transform.position = new Vector3(0f, i * laneSpacing, 0f);

                var groups = new Transform[GROUPS.Length];
                for (int g = 0; g < GROUPS.Length; g++)
                {
                    var node = new GameObject(GROUPS[g]);
                    node.transform.SetParent(env.transform, false);
                    groups[g] = node.transform;
                }

                var arenaGo = new GameObject("Arena");
                arenaGo.transform.SetParent(groups[1], false); // Obstacles
                var arena = arenaGo.AddComponent<Systems_SumoArena>();
                arena.showPosts = true;
                // Walk lanes need a runway wider than the match dohyo.
                arena.groundWidth = 13f;
                arena.ringHalfWidth = 6f;
                arena.floorWidth = 18f;

                var spawn = new GameObject("Spawn_Walker");
                spawn.transform.SetParent(groups[3], false); // SpawnPoints
                spawn.transform.localPosition = new Vector3(spawnX, 0f, 0f);

                // Created inactive so Awake() runs only after all fields are set —
                // AddComponent on an active object fires Awake immediately, which
                // would bake default behaviorName/body scales into the build.
                var walker = new GameObject("Walker");
                walker.SetActive(false);
                walker.transform.SetParent(groups[0], false); // Agents
                walker.transform.localPosition = new Vector3(spawnX, 0f, 0f);
                var body = walker.AddComponent<Agent_BipedBody>();
                body.facingSign = 1;
                body.teamColor = teamColor;
                body.massScale = massScale;
                body.widthScale = widthScale;
                body.torqueScale = torqueScale;
                var agent = walker.AddComponent<Agent_Biped>();
                agent.behaviorName = behaviorName;
                agent.mode = Agent_Biped.Mode.Walk;
                agent.teamId = 0;
                agent.arenaCenterX = env.transform.position.x;
                walker.SetActive(true);
                if (inferenceModel != null)
                    agent.SetModel(behaviorName, inferenceModel,
                                   Unity.MLAgents.Policies.InferenceDevice.Burst);
            }
        }
    }
}
