using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace PoSumo
{
    /// ML-Agents brain for one sumo biped. One shared behavior ("Matt")
    /// serves both training phases; observations are mirrored by facing sign so
    /// a single policy works facing left or right. All observations are
    /// sanitized against NaN/Inf before submission.
    [RequireComponent(typeof(Agent_BipedBody))]
    public class Agent_Biped : Agent
    {
        public enum Mode { Walk, Sumo }

        public Mode mode = Mode.Walk;
        public int teamId;
        public Agent_Biped opponent;         // null in Walk mode
        public float ringHalfWidth = 7f;
        public float arenaCenterX = 0f;

        [Tooltip("Optional trained model for in-editor inference playback (no Python needed).")]
        public Unity.InferenceEngine.ModelAsset inferenceModel;

        [HideInInspector] public int NonFootGroundContacts;

        /// When false the brain still runs but all motors are cut — the body
        /// goes limp (used between rounds and after the match).
        [HideInInspector] public bool actionsEnabled = true;

        [Tooltip("ML-Agents behavior name; must exactly match the YAML config key.")]
        public string behaviorName = "Matt";
        public const int ObservationCount = 41; // 5 body + 26 joints + 4 feet + 4 opponent + 2 edges
        public const int ActionCount = 13;      // hips, knees, ankles, 3 spine, shoulders, elbows

        /// Last motor commands as sent to the joints (for HUD/brain-view display).
        [System.NonSerialized] public float[] LastActions = new float[ActionCount];

        /// Cumulative reward this episode (for HUD/brain-view display).
        public float EpisodeReward => GetCumulativeReward();

        Agent_BipedBody _b;

        protected override void Awake()
        {
            _b = GetComponent<Agent_BipedBody>();

            var bp = GetComponent<BehaviorParameters>();
            if (bp == null) bp = gameObject.AddComponent<BehaviorParameters>();
            bp.BehaviorName = behaviorName;
            bp.TeamId = teamId;
            bp.BrainParameters.VectorObservationSize = ObservationCount;
            bp.BrainParameters.NumStackedVectorObservations = 1;
            bp.BrainParameters.ActionSpec = ActionSpec.MakeContinuous(ActionCount);

            if (inferenceModel != null)
            {
                bp.Model = inferenceModel;
                bp.InferenceDevice = Unity.MLAgents.Policies.InferenceDevice.Burst;
            }

            if (GetComponent<DecisionRequester>() == null)
            {
                var dr = gameObject.AddComponent<DecisionRequester>();
                dr.DecisionPeriod = 5;
                dr.TakeActionsBetweenDecisions = true;
            }

            MaxStep = mode == Mode.Walk ? 1500 : 0; // sumo rounds are ended by the match manager

            base.Awake();
        }

        public override void OnEpisodeBegin()
        {
            _b.ResetPose();
            foreach (var c in GetComponentsInChildren<Sensor_BodyPartContact>()) c.Clear();
            NonFootGroundContacts = 0;
        }

        float Fs => _b.facingSign;
        public Rigidbody2D Torso => _b.Torso;
        public float TorsoX => Torso.position.x;
        public bool IsDown => NonFootGroundContacts > 0;

        /// NaN/Inf sanitization guard for every value submitted to the model.
        static float San(float v) => float.IsFinite(v) ? v : 0f;

        public override void CollectObservations(VectorSensor sensor)
        {
            Vector2 tp = Torso.position;
            Vector2 tv = Torso.linearVelocity;

            sensor.AddObservation(San(tp.y / 2f));                                // 1
            sensor.AddObservation(San(tv.x * Fs / 5f));                           // 1
            sensor.AddObservation(San(tv.y / 5f));                                // 1
            float lean = Vector2.SignedAngle(Vector2.up, _b.Chest.transform.up);
            sensor.AddObservation(San(lean * Fs / 180f));                         // 1
            sensor.AddObservation(San(_b.Chest.angularVelocity * Fs / 500f));     // 1

            for (int j = 0; j < ActionCount; j++)                                 // 20
            {
                sensor.AddObservation(San(_b.JointAngleNorm(j)));
                sensor.AddObservation(San(_b.JointSpeedNorm(j)));
            }

            Vector2 fn = _b.FootNear.position - tp;                               // 4
            Vector2 ff = _b.FootFar.position - tp;
            sensor.AddObservation(San(fn.x * Fs / 2f));
            sensor.AddObservation(San(fn.y / 2f));
            sensor.AddObservation(San(ff.x * Fs / 2f));
            sensor.AddObservation(San(ff.y / 2f));

            // Opponent torso (or, in Walk mode, a virtual target at ring center).
            Vector2 op, ov;
            if (mode == Mode.Sumo && opponent != null)
            {
                op = opponent.Torso.position; ov = opponent.Torso.linearVelocity;
            }
            else
            {
                op = new Vector2(arenaCenterX, 1.2f); ov = Vector2.zero;
            }
            sensor.AddObservation(San((op.x - tp.x) * Fs / 10f));                 // 1
            sensor.AddObservation(San((op.y - tp.y) / 3f));                       // 1
            sensor.AddObservation(San((ov.x - tv.x) * Fs / 5f));                  // 1
            sensor.AddObservation(San((ov.y - tv.y) / 5f));                       // 1

            float xLocal = (tp.x - arenaCenterX) * Fs;                            // 2
            sensor.AddObservation(San((ringHalfWidth - xLocal) / ringHalfWidth)); // dist to edge ahead
            sensor.AddObservation(San((ringHalfWidth + xLocal) / ringHalfWidth)); // dist to edge behind
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            if (!actionsEnabled)
            {
                for (int j = 0; j < ActionCount; j++)
                {
                    _b.ApplyMotor(j, 0f);
                    LastActions[j] = 0f;
                }
                return;
            }
            var a = actions.ContinuousActions;
            float energy = 0f;
            for (int j = 0; j < ActionCount; j++)
            {
                _b.ApplyMotor(j, a[j]);
                LastActions[j] = Mathf.Clamp(a[j], -1f, 1f);
                energy += Mathf.Abs(a[j]);
            }
            energy /= ActionCount;
            _b.ClampAngularVelocities();

            float upright = Mathf.Clamp01(Vector2.Dot(_b.Chest.transform.up, Vector2.up));
            Vector2 tv = Torso.linearVelocity;
            float xLocal = (Torso.position.x - arenaCenterX) * Fs;

            if (mode == Mode.Walk)
            {
                AddReward(San(tv.x * Fs) * 0.002f);     // progress toward center
                AddReward(upright * 0.001f);
                AddReward(-energy * 0.0005f);

                // Catastrophic posture failure is an absolute episode termination gate.
                if (IsDown) { SetReward(-1f); EndEpisode(); return; }
                if (xLocal > -0.3f) { AddReward(2f); EndEpisode(); }
            }
            else
            {
                // Shaping only; win/loss (+1/-1) is assigned by Systems_SumoMatchManager.
                AddReward(upright * 0.0005f);
                if (opponent != null)
                {
                    float toward = Mathf.Sign(opponent.TorsoX - Torso.position.x);
                    AddReward(San(tv.x) * toward * 0.0005f);
                }
                AddReward(-energy * 0.0002f);
            }
        }

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var a = actionsOut.ContinuousActions;
            for (int i = 0; i < a.Length; i++) a[i] = 0f;
        }
    }
}
