using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace PoSumo
{
    /// ML-Agents brain for one sumo biped. Observations are mirrored by facing
    /// sign so a single policy works facing left or right. All observations are
    /// sanitized against NaN/Inf before submission.
    ///
    /// Two brain generations coexist:
    ///  - legacy (41 obs, decision period 5) — the Standard fighter's shipped policy;
    ///  - extended (44 obs: +opponent uprightness/down/edge, period 3) — Matt's
    ///    technique brain, trained with the stance/gait/impact shaping below.
    [RequireComponent(typeof(Agent_BipedBody))]
    public sealed class Agent_Biped : Agent
    {
        public enum Mode { Walk, Sumo, Recover }

        [Tooltip("Character sheet; when set, overrides behavior name, brain generation, and reward shaping at Awake.")]
        public Agent_CharacterDefinition character;

        public Mode mode = Mode.Walk;
        public int teamId;
        public Agent_Biped opponent;         // null in Walk/Recover mode
        public float ringHalfWidth = 7f;
        public float arenaCenterX = 0f;
        [Tooltip("World Y of the mat surface. Only used by the sumo stance shaping to tell a planted foot from a raised one, so both referees must set it or that reward reads against the wrong plane.")]
        public float arenaGroundY = 0f;

        [Tooltip("Adds 3 opponent-state observations (uprightness, down flag, edge distance). Must match the assigned model's input size.")]
        public bool extendedObservations = false;
        [Tooltip("ML-Agents DecisionRequester period. Legacy brains trained at 5; the extended Matt brain at 3.")]
        public int decisionPeriod = 5;

        [Tooltip("Sparring dummy: run the assigned model locally and never contact a connected trainer.")]
        public bool inferenceOnly = false;

        [Tooltip("Recover mode: chance an episode starts with a knockdown shove. 0 for pure walking demos.")]
        [Range(0f, 1f)] public float recoverShoveChance = 0.7f;

        [Tooltip("Optional trained model for in-editor inference playback (no Python needed).")]
        public Unity.InferenceEngine.ModelAsset inferenceModel;


        /// While true, OnActionReceived only drives motors — no rewards, no
        /// episode termination. Used while the presentation layer (walk-in)
        /// borrows the body.
        [System.NonSerialized] public bool suppressEpisodeControl;

        [HideInInspector] public int NonFootGroundContacts;

        /// When false the brain still runs but all motors are cut — the body
        /// goes limp (used between rounds and after the match).
        [HideInInspector] public bool actionsEnabled = true;

        /// Scales every motor command. The referee ramps this from a fraction back
        /// up to 1 over the first moment of a round: released from a dead stop the
        /// fight brain fires a full-power lunge on frame one and throws itself over,
        /// because it learned to act on a body that was already moving. Easing the
        /// first commands in lets the body take its own weight first.
        /// Observations and reward are untouched — only the torque that reaches the
        /// joints is scaled.
        [System.NonSerialized] public float actionScale = 1f;

        [Tooltip("ML-Agents behavior name; must exactly match the YAML config key.")]
        public string behaviorName = "Matt";
        // 42 = 5 body + 26 joints + 4 feet + 1 task flag + 4 opponent/target + 2 edges.
        // Was 41; the task flag was added when the walk and fight brains were merged
        // into one policy per fighter. That +1 invalidated every brain trained before
        // it — there is no way to feed a 42/45-slot vector to a 41/44-input model.
        public const int ObservationCount = 42;
        public const int ActionCount = 13;      // hips, knees, ankles, 3 spine, shoulders, elbows

        /// Height below which a foot counts as planted; the foot centre sits ~0.04
        /// when flat. Shared by StanceFactor and CadenceReward, which both judge
        /// "planted" and were drifting apart as two separate 0.12 literals.
        /// StanceFactor measures it against arenaGroundY, CadenceReward against
        /// world Y — that difference is deliberate and unchanged.
        private const float PLANTED_HEIGHT = 0.12f;

        /// Last motor commands as sent to the joints (for HUD display).
        [System.NonSerialized] public float[] LastActions = new float[ActionCount];

        private Agent_BipedBody _b;
        private Agent_BipedBody _opponentBody;
        /// Resolved once — the ragdoll is built in Awake and never gains or loses a
        /// part, so re-fetching this every episode only allocated a fresh array.
        private Sensor_BodyPartContact[] _contactSensors;
        private readonly float[] _prevActions = new float[ActionCount];
        private float _pendingImpact;
        private float _lastTorsoY;
        private int _lastSinglePlant;
        private float _lastStepTime;

        // Sumo reward coefficients — defaults here; overridden by `character`.
        private float _rUpright = 0.0005f, _rClosing = 0.0006f, _rLunge = 0.001f, _lungeThresh = 1.5f;
        private float _rImpact = 0.01f, _impactCap = 8f, _rKnee = 0.0004f, _rHips = 0.0003f;
        private float _rCadence = 0.0015f, _rRise = 0.02f, _pEnergy = 0.0004f, _pJerk = 0.0003f;
        private float _bendFloor = 0.3f;
        // New in the realism pass. Unlike the coefficients above these do NOT
        // default to the constant the branch used before, because there was no
        // such constant — neither term existed. They are deliberate behavioural
        // change and every fighter needs a corrective run to pick them up.
        private float _pEffort = 0.0015f, _rStance = 0.0009f;

        // Walk-school coefficients — defaults are the constants this branch used
        // before they became per-character, so an unassigned character trains the
        // identical gait it always did.
        private float _wForward = 0.004f, _wStanceFloor = 0.15f, _wBend = 0.0006f;
        private float _wUpright = 0.001f, _wCadence = 0.002f, _wEnergy = 0.0003f, _wStall = 0.0008f;

        protected override void Awake()
        {
            _b = GetComponent<Agent_BipedBody>();

            if (character != null)
            {
                behaviorName = character.behaviorName;
                extendedObservations = character.extendedObservations;
                decisionPeriod = character.decisionPeriod;
                if (inferenceModel == null) inferenceModel = character.inferenceModel;
                _rUpright = character.uprightReward;
                _rClosing = character.closingReward;
                _rLunge = character.lungeBonus;
                _lungeThresh = character.lungeThreshold;
                _rImpact = character.impactReward;
                _impactCap = character.impactCap;
                _rKnee = character.kneeBendReward;
                _rHips = character.hipsLowReward;
                _rCadence = character.cadenceReward;
                _rRise = character.riseReward;
                _pEnergy = character.energyPenalty;
                _pJerk = character.jerkPenalty;
                _pEffort = character.effortPenalty;
                _rStance = character.stanceReward;
                _bendFloor = character.straightLegEarnFraction;
                _wForward = character.walkForwardReward;
                _wStanceFloor = character.walkStanceFloor;
                _wBend = character.walkBendReward;
                _wUpright = character.walkUprightReward;
                _wCadence = character.walkCadenceReward;
                _wEnergy = character.walkEnergyPenalty;
                _wStall = character.walkStallPenalty;
            }

            var bp = GetComponent<BehaviorParameters>();
            if (bp == null) bp = gameObject.AddComponent<BehaviorParameters>();
            bp.BehaviorName = behaviorName;
            bp.TeamId = teamId;
            bp.BrainParameters.VectorObservationSize =
                ObservationCount + (extendedObservations ? 3 : 0);
            bp.BrainParameters.NumStackedVectorObservations = 1;
            bp.BrainParameters.ActionSpec = ActionSpec.MakeContinuous(ActionCount);

            if (inferenceModel != null)
            {
                bp.Model = inferenceModel;
                bp.InferenceDevice = Unity.MLAgents.Policies.InferenceDevice.Burst;
            }
            if (inferenceOnly) bp.BehaviorType = BehaviorType.InferenceOnly;

            if (GetComponent<DecisionRequester>() == null)
            {
                var dr = gameObject.AddComponent<DecisionRequester>();
                dr.DecisionPeriod = Mathf.Clamp(decisionPeriod, 1, 20);
                dr.TakeActionsBetweenDecisions = true;
            }

            MaxStep = mode == Mode.Walk ? 1500 : mode == Mode.Recover ? 2000 : 0;

            base.Awake();
        }

        public override void OnEpisodeBegin()
        {
            _b.ResetPose();
            if (_contactSensors == null) _contactSensors = GetComponentsInChildren<Sensor_BodyPartContact>();
            for (int sensorIndex = 0; sensorIndex < _contactSensors.Length; sensorIndex++)
            {
                _contactSensors[sensorIndex].Clear();
            }
            NonFootGroundContacts = 0;
            _pendingImpact = 0f;
            _lastTorsoY = _b.Torso.position.y;
            _lastSinglePlant = 0;
            _lastStepTime = 0f;
            for (int actionIndex = 0; actionIndex < ActionCount; actionIndex++) _prevActions[actionIndex] = 0f;

            // Recovery school: episodes may start with a knockdown shove. The
            // trainer's curriculum ramps the chance — gentle standing starts
            // while walking is being learned, shoves once it's mastered.
            if (mode == Mode.Recover && Academy.IsInitialized)
            {
                recoverShoveChance = Academy.Instance.EnvironmentParameters
                    .GetWithDefault("shove_chance", recoverShoveChance);
            }
            if (mode == Mode.Recover && Random.value < recoverShoveChance)
            {
                var dir = new Vector2(Random.Range(-1f, 1f), Random.Range(-0.3f, 0.5f)).normalized;
                _b.Chest.AddForce(dir * Random.Range(25f, 70f), ForceMode2D.Impulse);
                _b.Chest.AddTorque(Random.Range(-25f, 25f), ForceMode2D.Impulse);
            }
        }

        private float Fs => _b.facingSign;
        public Rigidbody2D Torso => _b.Torso;
        public float TorsoX => Torso.position.x;
        public bool IsDown => NonFootGroundContacts > 0;

        /// Called by Sensor_Impact when a body part hits the opponent.
        public void ReportOpponentImpact(float relativeSpeed)
        {
            _pendingImpact += relativeSpeed;
        }

        private float _savedCenterX;

        /// Round-opening walk-in: borrow the locomotion brain and walk toward
        /// targetX (the opponent). The fight brain takes back over via
        /// EndWalkIn(). No-op when no walk model is assigned.
        /// Round-opening walk-in: switch the SAME policy to its locomotion task and
        /// point it at targetX. EndWalkIn switches it back.
        ///
        /// There is no model swap any more. Walk and fight are one brain per fighter,
        /// told apart by the task flag in the observation vector, so this is a mode
        /// change rather than a SetModel handoff. That removes the whole class of bug
        /// where the wrong policy was left driving the body — the fight policy fed a
        /// target metres away used to collapse both fighters within half a second.
        public void BeginWalkIn(float targetX)
        {
            if (mode != Mode.Sumo) return;
            _savedCenterX = arenaCenterX;
            mode = Mode.Walk;                 // task flag -> 0, target replaces opponent
            arenaCenterX = targetX;
            suppressEpisodeControl = true;
        }

        public void EndWalkIn()
        {
            if (mode != Mode.Walk) return;
            mode = Mode.Sumo;                 // task flag -> 1
            arenaCenterX = _savedCenterX;
            suppressEpisodeControl = false;
        }

        /// NaN/Inf sanitization guard for every value submitted to the model.
        private static float San(float v) => float.IsFinite(v) ? v : 0f;

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

            for (int actionIndex = 0; actionIndex < ActionCount; actionIndex++)                                 // 26
            {
                sensor.AddObservation(San(_b.JointAngleNorm(actionIndex)));
                sensor.AddObservation(San(_b.JointSpeedNorm(actionIndex)));
            }

            Vector2 fn = _b.FootNear.position - tp;                               // 4
            Vector2 ff = _b.FootFar.position - tp;
            sensor.AddObservation(San(fn.x * Fs / 2f));
            sensor.AddObservation(San(fn.y / 2f));
            sensor.AddObservation(San(ff.x * Fs / 2f));
            sensor.AddObservation(San(ff.y / 2f));

            // TASK FLAG — 1 when this is a real bout, 0 when the four "opponent"
            // slots below are carrying a virtual target instead.
            //
            // This is what lets ONE brain do both jobs. Walk and fight always had an
            // identical 44-slot vector and an identical 13-action space; the only
            // difference was what occupied the opponent slots. Without a flag the
            // network had to infer the task from the constants that appear in
            // locomotion mode (1,0,1 in the extended block) — an accident, not a
            // signal. With it, the policy can condition on the task explicitly.
            bool fighting = mode == Mode.Sumo && opponent != null;
            sensor.AddObservation(fighting ? 1f : 0f);                            // 1

            // Opponent torso (or, in Walk/Recover mode, a virtual target at ring center).
            Vector2 op, ov;
            if (fighting)
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

            if (extendedObservations)                                             // +3
            {
                if (fighting)
                {
                    if (_opponentBody == null) _opponentBody = opponent.GetComponent<Agent_BipedBody>();
                    float oppUp = _opponentBody != null
                        ? Vector2.Dot(_opponentBody.Chest.transform.up, Vector2.up) : 1f;
                    float oppEdge = (ringHalfWidth - Mathf.Abs(opponent.TorsoX - arenaCenterX)) / ringHalfWidth;
                    sensor.AddObservation(San(oppUp));
                    sensor.AddObservation(opponent.IsDown ? 1f : 0f);
                    sensor.AddObservation(San(Mathf.Clamp01(oppEdge)));
                }
                else
                {
                    sensor.AddObservation(1f);
                    sensor.AddObservation(0f);
                    sensor.AddObservation(1f);
                }
            }
        }

        /// Mean knee flexion mapped to 0..1 (1 = both knees bent 60°+).
        private float KneeBendFactor()
        {
            float near = Mathf.Clamp01(_b.JointAngleNorm(1) * 180f / 60f);
            float far = Mathf.Clamp01(_b.JointAngleNorm(4) * 180f / 60f);
            return (near + far) * 0.5f;
        }

        private float HipsLowFactor() => Mathf.Clamp01((0.95f - San(Torso.position.y)) / 0.3f);

        /// How much this looks like a sumo stance, 0..1: both feet planted, and
        /// planted APART. Multiplied by knee bend so it cannot be farmed by
        /// standing straight-legged with the feet spread.
        ///
        /// Ground contact is judged against the mat plane rather than a collision
        /// query so it costs nothing per step; a foot within ankle height of the
        /// surface is planted. Spread saturates at SUMO_STANCE_WIDTH, roughly a
        /// shoulder-and-a-half, past which wider is not better.
        private float StanceFactor()
        {
            const float SUMO_STANCE_WIDTH = 0.55f;

            float nearY = San(_b.FootNear.position.y) - arenaGroundY;
            float farY = San(_b.FootFar.position.y) - arenaGroundY;
            float nearDown = Mathf.Clamp01(1f - nearY / PLANTED_HEIGHT);
            float farDown = Mathf.Clamp01(1f - farY / PLANTED_HEIGHT);
            // Product, not average: one foot planted is standing, not a stance.
            float grounded = nearDown * farDown;

            float spread = Mathf.Abs(San(_b.FootNear.position.x) - San(_b.FootFar.position.x));
            float wide = Mathf.Clamp01(spread / SUMO_STANCE_WIDTH);

            return grounded * wide * KneeBendFactor();
        }

        /// Step-cadence bonus: pays once per alternation of the single planted
        /// foot (near->far or far->near), i.e. actual stepping, not skating.
        private void CadenceReward(float scale)
        {
            bool nearPlanted = _b.FootNear.position.y < PLANTED_HEIGHT
                && Mathf.Abs(_b.FootNear.linearVelocity.x) < 0.5f;
            bool farPlanted = _b.FootFar.position.y < PLANTED_HEIGHT
                && Mathf.Abs(_b.FootFar.linearVelocity.x) < 0.5f;
            int pattern = (nearPlanted ? 1 : 0) | (farPlanted ? 2 : 0);
            if ((pattern == 1 && _lastSinglePlant == 2) || (pattern == 2 && _lastSinglePlant == 1))
            {
                if (Time.time - _lastStepTime > 0.25f)
                {
                    AddReward(scale);
                    _lastStepTime = Time.time;
                }
            }
            if (pattern == 1 || pattern == 2) _lastSinglePlant = pattern;
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            if (!actionsEnabled)
            {
                for (int actionIndex = 0; actionIndex < ActionCount; actionIndex++)
                {
                    _b.ApplyMotor(actionIndex, 0f);
                    LastActions[actionIndex] = 0f;
                    _prevActions[actionIndex] = 0f;
                }
                _pendingImpact = 0f;
                return;
            }
            var a = actions.ContinuousActions;
            float energy = 0f, jerk = 0f, effort = 0f;
            for (int actionIndex = 0; actionIndex < ActionCount; actionIndex++)
            {
                _b.ApplyMotor(actionIndex, a[actionIndex] * actionScale);
                float clamped = Mathf.Clamp(a[actionIndex], -1f, 1f);
                LastActions[actionIndex] = clamped;
                energy += Mathf.Abs(clamped);
                effort += clamped * clamped;
                jerk += Mathf.Abs(clamped - _prevActions[actionIndex]);
                _prevActions[actionIndex] = clamped;
            }
            energy /= ActionCount;
            // QUADRATIC, unlike `energy`. An L1 cost has a constant gradient, so it
            // shifts every action down uniformly and a policy can pay it off by
            // being slightly less lazy everywhere. A squared cost rises steeply
            // toward the rails, which is what actually discourages slamming a motor
            // to full torque — and slamming is exactly what was measured: 7 to 12
            // of the 13 motors sat above |0.9| with a mean magnitude of 0.75-0.91.
            effort /= ActionCount;
            jerk /= ActionCount;
            _b.ClampAngularVelocities();

            // Presentation layer owns the body: motors only, no rewards or
            // episode control (prevents Recover-mode terminations mid-walk-in).
            if (suppressEpisodeControl)
            {
                _lastTorsoY = San(Torso.position.y);
                return;
            }

            float upright = Mathf.Clamp01(Vector2.Dot(_b.Chest.transform.up, Vector2.up));
            Vector2 tv = Torso.linearVelocity;
            float torsoY = San(Torso.position.y);
            float xLocal = (Torso.position.x - arenaCenterX) * Fs;
            float bend = KneeBendFactor();
            float bendGate = _bendFloor + (1f - _bendFloor) * bend; // straight legs earn the floor fraction

            if (mode == Mode.Walk)
            {
                // The proven walk-school structure: falling ENDS the episode, so
                // no on-the-ground reward exploit can exist. Stance/cadence
                // shaping keeps the gait crouched and stepping. The coefficients
                // come from the character sheet, so a fighter's gait can carry the
                // same personality as its fight style (a driver rewards forward
                // speed, a dancer rewards cadence).
                float walkGate = _wStanceFloor + (1f - _wStanceFloor) * bend;
                AddReward(San(tv.x * Fs) * _wForward * walkGate);
                AddReward(bend * _wBend);
                AddReward(upright * _wUpright);
                CadenceReward(_wCadence);
                AddReward(-energy * _wEnergy);
                if (Mathf.Abs(San(tv.x)) < 0.15f) AddReward(-_wStall); // no statue farming

                // Catastrophic posture failure is an absolute episode termination gate.
                // The two terminals stay hardcoded on purpose: shaping is per-character,
                // but fall (-1) and graduation (+3) fix the reward scale so different
                // characters' walk runs remain comparable to each other on TensorBoard.
                if (IsDown) { SetReward(-1f); EndEpisode(); return; }
                if (xLocal > -0.3f) { AddReward(3f); EndEpisode(); }
            }
            else if (mode == Mode.Recover)
            {
                // Recovery school: get up from anything, then WALK — standing,
                // knees bent, stepping — to the target. Falling never ends the
                // episode, but time spent down costs, so crawling can't pay.
                if (IsDown || torsoY < 0.6f)
                {
                    // No passive income while down — ONLY rising pays, and every
                    // step on the ground bleeds. Lying comfortably cannot profit.
                    AddReward((torsoY - _lastTorsoY) * 0.08f);
                    AddReward(-0.0012f);
                    AddReward(-energy * 0.0001f);
                }
                else
                {
                    // Stricter stance gate than sumo: straight legs earn 15%.
                    float strictGate = 0.15f + 0.85f * bend;
                    AddReward(San(tv.x * Fs) * 0.004f * strictGate);
                    AddReward(bend * 0.0006f);
                    AddReward(upright * 0.001f);
                    AddReward(HipsLowFactor() * 0.0005f);
                    CadenceReward(0.002f);
                    AddReward(-energy * 0.0002f);
                    // Loitering bleeds: standing still must never be the best plan.
                    if (Mathf.Abs(San(tv.x)) < 0.15f) AddReward(-0.0008f);
                    // Graduation requires ARRIVING ON YOUR FEET.
                    if (xLocal > -0.3f && upright > 0.9f && torsoY > 0.7f)
                    {
                        AddReward(3f);
                        EndEpisode();
                        return;
                    }
                }
                AddReward(-jerk * 0.0002f);
            }
            else // Sumo
            {
                // Shaping only; win/loss (+1/-1) is assigned by Systems_SumoMatchManager.
                AddReward(upright * _rUpright);

                if (opponent != null)
                {
                    float toward = Mathf.Sign(opponent.TorsoX - Torso.position.x);
                    float closing = San(tv.x) * toward;
                    AddReward(closing * _rClosing * bendGate);
                    // Lunge: explosive bursts toward the opponent pay extra.
                    if (closing > _lungeThresh) AddReward((closing - _lungeThresh) * _rLunge * bendGate);
                }

                // Impact reward: momentum actually delivered into the opponent.
                float impact = Mathf.Min(_pendingImpact, _impactCap);
                _pendingImpact = 0f;
                if (impact > 0f) AddReward(impact * _rImpact);

                if (!IsDown && upright > 0.6f)
                {
                    AddReward(bend * _rKnee);
                    AddReward(HipsLowFactor() * _rHips);
                    CadenceReward(_rCadence);
                }
                else if (IsDown)
                {
                    // Getting back up mid-round pays (falls are not losses).
                    AddReward(Mathf.Max(0f, tv.y) * 0.0005f);
                    AddReward((torsoY - _lastTorsoY) * _rRise);
                }

                // Sumo base: a wide, low, two-footed stance. Nothing rewarded this
                // before, and it showed — the fighters were measured mid-bout with
                // the chest 0.87 m above the mat at a 61-degree lean, and in
                // another sample standing on one foot with the other 0.48 m in the
                // air. That is a collapse, not a stance.
                AddReward(StanceFactor() * _rStance);

                // Useless effort costs; driving toward the opponent is cheap.
                float useful = Mathf.Clamp01(Mathf.Abs(San(tv.x)) / 1.5f);
                AddReward(-energy * _pEnergy * (1f - useful));
                AddReward(-jerk * _pJerk);

                // UNGATED, unlike the line above. That `(1f - useful)` gate makes
                // effort free whenever the fighter is moving fast, which is the
                // mechanism that produced the saturated motors: drive hard and the
                // torque bill disappears. This term always applies, so full-power
                // flailing costs something even mid-charge.
                AddReward(-effort * _pEffort);
            }

            _lastTorsoY = torsoY;
        }

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var a = actionsOut.ContinuousActions;
            for (int index = 0; index < a.Length; index++) a[index] = 0f;
        }
    }
}
