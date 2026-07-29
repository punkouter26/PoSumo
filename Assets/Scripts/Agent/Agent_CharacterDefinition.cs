using UnityEngine;

namespace PoSumo
{
    /// One asset per fighter: identity, body build, brain generation, and all
    /// sumo reward-shaping coefficients. Agents copy from this at Awake when
    /// assigned (same pattern as Systems_GameTuning), so creating or tuning a
    /// character means editing an asset — never code.
    ///
    /// Training a new character: duplicate an asset, tweak, duplicate a YAML
    /// with the matching behavior name, and point the training env builder at
    /// the asset.
    [CreateAssetMenu(fileName = "CharacterDefinition", menuName = "PoSumo/Character Definition")]
    public sealed class Agent_CharacterDefinition : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("ML-Agents behavior name; must exactly match the YAML config key.")]
        public string behaviorName = "Matt";
        public Color teamColor = new Color(0.85f, 0.25f, 0.2f);
        [Tooltip("Face texture for the head; null = plain circle.")]
        public Sprite headSprite;

        [Header("Face")]
        [Tooltip("Tick when the source art faces LEFT. Rig geometry is right-facing, so the head flip is inverted for this character.")]
        public bool faceArtFacesLeft = false;
        [Tooltip("Resources name of the neutral face; empty disables the mood ladder for this character.")]
        public string faceNeutralName = "";
        [Tooltip("Resources names, mild to extreme (happy1..3).")]
        public string[] faceHappyNames = new string[0];
        [Tooltip("Resources names, mild to extreme (sad1..3).")]
        public string[] faceSadNames = new string[0];

        [Header("Body build")]
        [Tooltip("Multiplies every part's mass. Heavyweight sumo ~2.")]
        public float massScale = 1f;
        [Tooltip("Widens the four torso segments (sumo belly). Heavyweight ~1.3.")]
        public float widthScale = 1f;
        [Tooltip("Multiplies joint motor torque caps to carry the extra mass.")]
        public float torqueScale = 1f;

        [Header("Brain generation")]
        [Tooltip("44-obs brain (+opponent uprightness/down/edge). Must match the model's input size. Standard for all new characters; only legacy brains use 41 obs.")]
        public bool extendedObservations = true;
        [Tooltip("DecisionRequester period the model was trained at. Standard is 3; legacy brains used 5.")]
        public int decisionPeriod = 3;
        [Tooltip("Trained model for inference playback.")]
        public Unity.InferenceEngine.ModelAsset inferenceModel;

        [Header("Sumo reward shaping (training only)")]
        public float uprightReward = 0.0005f;
        [Tooltip("Per-step reward for closing speed toward the opponent (gated by stance).")]
        public float closingReward = 0.0006f;
        [Tooltip("Extra reward per m/s of closing speed above lungeThreshold.")]
        public float lungeBonus = 0.001f;
        public float lungeThreshold = 1.5f;
        [Tooltip("Reward per m/s of relative impact speed delivered into the opponent.")]
        public float impactReward = 0.01f;
        public float impactCap = 8f;
        public float kneeBendReward = 0.0004f;
        public float hipsLowReward = 0.0003f;
        [Tooltip("Bonus per alternating-foot step while standing.")]
        public float cadenceReward = 0.0015f;
        [Tooltip("Reward per meter of torso rise while down (getting back up).")]
        public float riseReward = 0.02f;
        [Tooltip("UNGATED per-step cost of squared motor effort. The older energyPenalty is switched off whenever the fighter is moving fast, which made full-power flailing free mid-charge and left 7-12 of the 13 motors railed. This one always applies and is quadratic, so it bites hardest exactly at the rails. 0 restores the old behaviour.")]
        public float effortPenalty = 0.0015f;
        [Tooltip("Per-step reward for a real sumo base: both feet planted, planted apart, and knees bent. Nothing rewarded stance before, and fighters were measured mid-bout standing on one foot with the chest 0.87 m off the mat.")]
        public float stanceReward = 0.0009f;
        public float energyPenalty = 0.0004f;
        public float jerkPenalty = 0.0003f;
        [Range(0f, 1f)]
        [Tooltip("Fraction of movement rewards earned with straight legs; deep stance earns 100%.")]
        public float straightLegEarnFraction = 0.3f;

        [Header("Walk school shaping (training only)")]
        // Defaults are exactly the constants the Mode.Walk branch used before these
        // fields existed, so an untouched character trains the identical gait it
        // always did. Only deviate deliberately.
        [Tooltip("Reward per m/s of forward speed toward the target, gated by stance.")]
        public float walkForwardReward = 0.004f;
        [Range(0f, 1f)]
        [Tooltip("Fraction of the forward reward earned with straight legs; deep stance earns 100%. Low = must crouch to be paid.")]
        public float walkStanceFloor = 0.15f;
        [Tooltip("Per-step reward for knee bend while walking.")]
        public float walkBendReward = 0.0006f;
        [Tooltip("Per-step reward for an upright chest while walking.")]
        public float walkUprightReward = 0.001f;
        [Tooltip("Bonus per alternating-foot step. High = dances, low = drives.")]
        public float walkCadenceReward = 0.002f;
        [Tooltip("Per-step cost of motor effort while walking.")]
        public float walkEnergyPenalty = 0.0003f;
        [Tooltip("Per-step penalty for standing still — stops the walker farming upright rewards as a statue.")]
        public float walkStallPenalty = 0.0008f;
    }
}
