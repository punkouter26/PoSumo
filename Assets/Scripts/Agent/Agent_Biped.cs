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
    public class Agent_Biped : Agent
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

        [Tooltip("Locomotion brain for the round-opening walk-in (copied from the character sheet).")]
        public Unity.InferenceEngine.ModelAsset walkModel;

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
        public const int ObservationCount = 41; // 5 body + 26 joints + 4 feet + 4 opponent + 2 edges
        public const int ActionCount = 13;      // hips, knees, ankles, 3 spine, shoulders, elbows

        /// Last motor commands as sent to the joints (for HUD display).
        [System.NonSerialized] public float[] LastActions = new float[ActionCount];

        Agent_BipedBody _b;
        Agent_BipedBody _opponentBody;
        readonly float[] _prevActions = new float[ActionCount];
        float _pendingImpact;
        float _lastTorsoY;
        int _lastSinglePlant;
        float _lastStepTime;

        // Sumo reward coefficients — defaults here; overridden by `character`.
        float _rUpright = 0.0005f, _rClosing = 0.0006f, _rLunge = 0.001f, _lungeThresh = 1.5f;
        float _rImpact = 0.01f, _impactCap = 8f, _rKnee = 0.0004f, _rHips = 0.0003f;
        float _rCadence = 0.0015f, _rRise = 0.02f, _pEnergy = 0.0004f, _pJerk = 0.0003f;
        float _bendFloor = 0.3f;
        // New in the realism pass. Unlike the coefficients above these do NOT
        // default to the constant the branch used before, because there was no
        // such constant — neither term existed. They are deliberate behavioural
        // change and every fighter needs a corrective run to pick them up.
        float _pEffort = 0.0015f, _rStance = 0.0009f;

        // Walk-school coefficients — defaults are the constants this branch used
        // before they became per-character, so an unassigned character trains the
        // identical gait it always did.
        float _wForward = 0.004f, _wStanceFloor = 0.15f, _wBend = 0.0006f;
        float _wUpright = 0.001f, _wCadence = 0.002f, _wEnergy = 0.0003f, _wStall = 0.0008f;

        protected override void Awake()
        {
            _b = GetComponent<Agent_BipedBody>();

            if (character != null)
            {
                behaviorName = character.behaviorName;
                extendedObservations = character.extendedObservations;
                decisionPeriod = character.decisionPeriod;
                if (inferenceModel == null) inferenceModel = character.inferenceModel;
                if (walkModel == null) walkModel = character.walkModel;
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
            foreach (var c in GetComponentsInChildren<Sensor_BodyPartContact>()) c.Clear();
            NonFootGroundContacts = 0;
            _pendingImpact = 0f;
            _lastTorsoY = _b.Torso.position.y;
            _lastSinglePlant = 0;
            _lastStepTime = 0f;
            for (int j = 0; j < ActionCount; j++) _prevActions[j] = 0f;

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

        float Fs => _b.facingSign;
        public Rigidbody2D Torso => _b.Torso;
        public float TorsoX => Torso.position.x;
        public bool IsDown => NonFootGroundContacts > 0;

        /// Called by Sensor_Impact when a body part hits the opponent.
        public void ReportOpponentImpact(float relativeSpeed)
        {
            _pendingImpact += relativeSpeed;
        }

        Unity.InferenceEngine.ModelAsset _fightModel;
        float _savedCenterX;

        /// Round-opening walk-in: borrow the locomotion brain and walk toward
        /// targetX (the opponent). The fight brain takes back over via
        /// EndWalkIn(). No-op when no walk model is assigned.
        public void BeginWalkIn(float targetX)
        {
            if (walkModel == null || mode != Mode.Sumo) return;
            var bp = GetComponent<BehaviorParameters>();
            _fightModel = bp.Model;
            _savedCenterX = arenaCenterX;
            mode = Mode.Recover;              // walk-brain observation layout (virtual target)
            arenaCenterX = targetX;
            suppressEpisodeControl = true;
            // SetModel, not `bp.Model = ...`: the policy is built once at Awake and
            // cached, so assigning the field alone leaves the FIGHT brain driving
            // the walk-in. That is what made both fighters collapse within half a
            // second of a round opening — the fight policy was being fed a target
            // 6.8 m away, far outside anything it ever trained on.
            SetModel(behaviorName, walkModel, InferenceDevice.Burst);
        }

        public void EndWalkIn()
        {
            if (mode != Mode.Recover) return;
            mode = Mode.Sumo;
            arenaCenterX = _savedCenterX;
            suppressEpisodeControl = false;
            // Same reason as BeginWalkIn: the handoff back to the fight brain only
            // takes effect through SetModel.
            if (_fightModel != null)
            {
                SetModel(behaviorName, _fightModel, InferenceDevice.Burst);
            }
        }

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

            for (int j = 0; j < ActionCount; j++)                                 // 26
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

            // Opponent torso (or, in Walk/Recover mode, a virtual target at ring center).
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

            if (extendedObservations)                                             // +3
            {
                if (mode == Mode.Sumo && opponent != null)
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
        float KneeBendFactor()
        {
            float near = Mathf.Clamp01(_b.JointAngleNorm(1) * 180f / 60f);
            float far = Mathf.Clamp01(_b.JointAngleNorm(4) * 180f / 60f);
            return (near + far) * 0.5f;
        }

        float HipsLowFactor() => Mathf.Clamp01((0.95f - San(Torso.position.y)) / 0.3f);

        /// How much this looks like a sumo stance, 0..1: both feet planted, and
        /// planted APART. Multiplied by knee bend so it cannot be farmed by
        /// standing straight-legged with the feet spread.
        ///
        /// Ground contact is judged against the mat plane rather than a collision
        /// query so it costs nothing per step; a foot within ankle height of the
        /// surface is planted. Spread saturates at SUMO_STANCE_WIDTH, roughly a
        /// shoulder-and-a-half, past which wider is not better.
        float StanceFactor()
        {
            const float PLANTED_HEIGHT = 0.12f;   // foot centre sits ~0.04 when flat
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
        void CadenceReward(float scale)
        {
            bool nearPlanted = _b.FootNear.position.y < 0.12f
                && Mathf.Abs(_b.FootNear.linearVelocity.x) < 0.5f;
            bool farPlanted = _b.FootFar.position.y < 0.12f
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
                for (int j = 0; j < ActionCount; j++)
                {
                    _b.ApplyMotor(j, 0f);
                    LastActions[j] = 0f;
                    _prevActions[j] = 0f;
                }
                _pendingImpact = 0f;
                return;
            }
            var a = actions.ContinuousActions;
            float energy = 0f, jerk = 0f, effort = 0f;
            for (int j = 0; j < ActionCount; j++)
            {
                _b.ApplyMotor(j, a[j] * actionScale);
                float clamped = Mathf.Clamp(a[j], -1f, 1f);
                LastActions[j] = clamped;
                energy += Mathf.Abs(clamped);
                effort += clamped * clamped;
                jerk += Mathf.Abs(clamped - _prevActions[j]);
                _prevActions[j] = clamped;
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
            for (int i = 0; i < a.Length; i++) a[i] = 0f;
        }
    }
}
