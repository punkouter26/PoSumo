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
    /// The vector is composed from four blocks, and the ORDER IS THE INPUT LAYER'S
    /// LAYOUT — it must never be reordered once a brain has trained on it:
    ///     base (43) -> contact (+4) -> stamina (+1) -> extended (+3).
    /// All four fighters run the full 51.
    ///
    /// It has been 41, 42 and 44 in earlier generations; every one of those moves
    /// invalidated every brain that preceded it, so treat a change here as a cold
    /// retrain of the whole roster and nothing less.
    [RequireComponent(typeof(Agent_BipedBody))]
    public sealed class Agent_Biped : Agent
    {
        public enum Mode { Walk, Sumo }

        [Tooltip("Character sheet; when set, overrides behavior name, brain generation, and reward shaping at Awake.")]
        public Agent_CharacterDefinition character;

        public Mode mode = Mode.Walk;
        public int teamId;
        public Agent_Biped opponent;         // null in Walk mode
        public float ringHalfWidth = 7f;
        public float arenaCenterX = 0f;
        [Tooltip("World Y of the mat surface. Only used by the sumo stance shaping to tell a planted foot from a raised one, so both referees must set it or that reward reads against the wrong plane.")]
        public float arenaGroundY = 0f;

        [Tooltip("Adds 3 opponent-state observations (uprightness, down flag, edge distance). Must match the assigned model's input size.")]
        public bool extendedObservations = false;
        [Tooltip("Adds 4 proprioceptive observations: per-foot ground contact and normalised load. OFF for every shipped brain — it changes the vector length, so it can only be switched on together with a retrain.")]
        public bool contactObservations = false;
        [Tooltip("Adds 1 observation: whole-body stamina (1 fresh, 0 spent). OFF for every shipped brain — it changes the vector length, so it can only be switched on together with a retrain. Turn it on for the next run so the policy can perceive the fatigue that Agent_BipedBody now applies to it.")]
        public bool staminaObservation = false;
        [Tooltip("ML-Agents DecisionRequester period. Legacy brains trained at 5; the extended Matt brain at 3.")]
        public int decisionPeriod = 5;

        [Tooltip("Sparring dummy: run the assigned model locally and never contact a connected trainer.")]
        public bool inferenceOnly = false;

        /// Drive this fighter as the BOT (`Agent_Bot`) instead of from a trained
        /// policy. Sets BehaviorType.HeuristicOnly in Awake, which is ML-Agents'
        /// own hook for a code-driven agent — so the body, both referees, the
        /// damage model and every presentation system are untouched and the BOT
        /// can share a bout with a trained fighter.
        ///
        /// Deliberately NOT gated on `inferenceModel == null`: the BOT keeps its
        /// .onnx assigned so toggling this compares the two brains on exactly the
        /// same character sheet.
        [UnityEngine.Serialization.FormerlySerializedAs("useScriptedBrain")]
        public bool useBot = false;

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

        /// Scoreboard name when set. Set by nothing except `Systems_MatchRoster` on
        /// the second side of a MIRROR match, where both fighters share one
        /// character asset and the scorebug otherwise reads "NICK 1 : 1 NICK".
        ///
        /// Deliberately SEPARATE from `behaviorName`, which must keep matching the
        /// YAML key exactly — it is what `BehaviorParameters` resolves the policy
        /// through, so suffixing it would leave that fighter with no brain at all.
        /// This is presentation only.
        [System.NonSerialized] public string displayNameOverride;
        // 43 = 5 body + 26 joints + 4 feet + 1 task flag + 4 opponent/target
        //      + 2 edges + 1 mat width.
        // Was 41; the task flag was added when the walk and fight brains were merged
        // into one policy per fighter. Was 42 until the LIVE MAT fix below took the
        // edge block from two slots to three. Either change invalidates every brain
        // trained before it — there is no way to feed a 43-slot vector to a
        // 42-input model.
        public const int ObservationCount = 43;

        /// Reference half-width the edge observations are scaled by, in metres.
        ///
        /// It is a CONSTANT and `ringHalfWidth` is not — that separation is the whole
        /// point. The edge slots used to divide by `ringHalfWidth`, which made them a
        /// FRACTION of whatever mat was current; combined with a `ringHalfWidth` that
        /// never moved, the policy could read neither the absolute distance to the rim
        /// nor the fact that the rim was closing. Scaling by a fixed reference instead
        /// keeps the slots proportional to real metres, so "1.2 m of mat ahead" reads
        /// the same whether the mat started at 3.5 m or was randomised to 1.7.
        ///
        /// 3.5 is `GameTuning.ringHalfWidth`, i.e. a full mat maps to 1.0.
        private const float RING_REFERENCE_HALF = 3.5f;
        public const int ActionCount = 13;      // hips, knees, ankles, 3 spine, shoulders, elbows

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

        /// Objective / penalty providers, one per school. Every reward coefficient
        /// lives inside these; the agent owns only the terminals and the episode.
        ///
        /// Constructed as fields rather than resolved per step — an agent's school
        /// changes (BeginWalkIn switches mode mid-round) but its providers do not,
        /// and both carry configured state that must survive the switch.
        private readonly Reward_SumoObjective _sumoObjective = new Reward_SumoObjective();
        private readonly Reward_WalkObjective _walkObjective = new Reward_WalkObjective();
        /// Shared by both schools on purpose — see Reward_StepCadence.
        private readonly Reward_StepCadence _cadence = new Reward_StepCadence();

        /// Allocated unconditionally rather than behind `useBot`, because
        /// the flag can be toggled in the Inspector at runtime and a null here
        /// would be a NullReferenceException inside Heuristic on the next decision.
        private readonly Agent_Bot _bot = new Agent_Bot();

        protected override void Awake()
        {
            _b = GetComponent<Agent_BipedBody>();

            if (character != null)
            {
                behaviorName = character.behaviorName;
                extendedObservations = character.extendedObservations;
                contactObservations = character.contactObservations;
                staminaObservation = character.staminaObservation;
                decisionPeriod = character.decisionPeriod;
                if (inferenceModel == null) inferenceModel = character.inferenceModel;
                // ORed, not assigned: the BOT can also be set per-agent
                // in the scene, and the character sheet must not switch that back off.
                useBot |= character.useBot;
            }

            // The BOT decides EVERY physics step. decisionPeriod 3 is a constraint on
            // the trained brains — it is what their .onnx was trained at and must not
            // change — but a scripted controller has no such obligation, and 16.7 Hz
            // is coarse for a balance loop holding a 69.6 kg ragdoll upright. This
            // triples the control rate for the BOT alone and touches no policy.
            if (useBot)
            {
                decisionPeriod = 1;
            }

            // Both are configured unconditionally, including when `character` is
            // null: Configure no-ops on null and leaves the provider on the
            // pre-per-character constants, which is what an unassigned fighter has
            // always trained against.
            _sumoObjective.Configure(character);
            _walkObjective.Configure(character);

            var bp = GetComponent<BehaviorParameters>();
            if (bp == null) bp = gameObject.AddComponent<BehaviorParameters>();
            bp.BehaviorName = behaviorName;
            bp.TeamId = teamId;
            int vectorSize = ResolvedObservationCount;
            bp.BrainParameters.VectorObservationSize = vectorSize;
            AssertVectorSizeMatchesBehavior(vectorSize);
            bp.BrainParameters.NumStackedVectorObservations = 1;
            bp.BrainParameters.ActionSpec = ActionSpec.MakeContinuous(ActionCount);

            if (inferenceModel != null)
            {
                bp.Model = inferenceModel;
                bp.InferenceDevice = Unity.MLAgents.Policies.InferenceDevice.Burst;
            }
            if (inferenceOnly) bp.BehaviorType = BehaviorType.InferenceOnly;

            // Checked AFTER inferenceOnly so it wins: the BOT must not
            // fall through to the model even when the character sheet asked for
            // inference. HeuristicOnly routes every decision to Heuristic() below.
            if (useBot) bp.BehaviorType = BehaviorType.HeuristicOnly;

            if (GetComponent<DecisionRequester>() == null)
            {
                var dr = gameObject.AddComponent<DecisionRequester>();
                dr.DecisionPeriod = Mathf.Clamp(decisionPeriod, 1, 20);
                dr.TakeActionsBetweenDecisions = true;
            }

            MaxStep = mode == Mode.Walk ? 1500 : 0;

            base.Awake();
        }

        /// The vector length this fighter's flags actually produce.
        public int ResolvedObservationCount =>
            ObservationCount + (contactObservations ? 4 : 0) + (staminaObservation ? 1 : 0)
            + (extendedObservations ? 3 : 0);

        /// First observation size seen for each behavior name this session, and the
        /// GameObject that established it.
        ///
        /// Cleared on SubsystemRegistration like every other static holding game state
        /// here — Enter Play Mode domain reload is disabled in this project, so without
        /// it the registry would carry a previous Play session's agents and report
        /// mismatches against objects that no longer exist.
        private static readonly System.Collections.Generic.Dictionary<string, (int size, string owner)>
            _vectorSizeByBehavior = new System.Collections.Generic.Dictionary<string, (int, string)>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetVectorSizeRegistry() => _vectorSizeByBehavior.Clear();

        /// ONE behavior name is ONE policy, so every agent carrying that name must
        /// build the SAME observation vector. Nothing enforced that, and the flags
        /// come from three places that can disagree — the character sheet, the
        /// serialized scene values it overwrites, and the per-agent Inspector.
        ///
        /// This is not hypothetical. `Bot_Character.asset` ships `staminaObservation: 1`
        /// while all four trained fighters ship `0`; put a second character on that
        /// behavior name and one agent feeds 47 slots where the other feeds 46. The
        /// failure mode is not an exception — ML-Agents pads or truncates and the
        /// policy simply receives garbage in the slots after the disagreement, which
        /// is indistinguishable from a fighter that trained badly.
        ///
        /// ML-Agents' own SentisModelParamLoader already catches a mismatch between an
        /// assigned .onnx and the vector, so this deliberately checks the other axis:
        /// agent-against-agent, which nothing else looks at, and which is live during
        /// training when there is no .onnx to check against at all.
        private void AssertVectorSizeMatchesBehavior(int vectorSize)
        {
            if (string.IsNullOrEmpty(behaviorName)) return;

            if (_vectorSizeByBehavior.TryGetValue(behaviorName, out var first))
            {
                if (first.size != vectorSize)
                {
                    Debug.LogError(
                        $"[OBS] Behavior '{behaviorName}' is building TWO different observation " +
                        $"vectors: '{first.owner}' feeds {first.size} slots and '{name}' feeds " +
                        $"{vectorSize}. One behavior name is one policy, so one of them is " +
                        $"receiving garbage. Flags on '{name}': extended={extendedObservations} " +
                        $"contact={contactObservations} stamina={staminaObservation} " +
                        $"(character='{(character != null ? character.name : "none")}').", this);
                }
                return;
            }

            _vectorSizeByBehavior[behaviorName] = (vectorSize, name);
            AssertModelMatchesVector(vectorSize);
            Systems_Log.Info(
                $"[OBS] {behaviorName}: {vectorSize} slots " +
                $"(base {ObservationCount}{(contactObservations ? " +4 contact" : "")}" +
                $"{(staminaObservation ? " +1 stamina" : "")}" +
                $"{(extendedObservations ? " +3 extended" : "")}), " +
                $"{ActionCount} actions, decision period {decisionPeriod}.");
        }

        /// Input width of each `.onnx` seen this session, so the model is loaded once
        /// per asset rather than once per agent — a training scene is 10 agents sharing
        /// one model.
        private static readonly System.Collections.Generic.Dictionary<Unity.InferenceEngine.ModelAsset, int>
            _modelInputSize = new System.Collections.Generic.Dictionary<Unity.InferenceEngine.ModelAsset, int>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetModelSizeCache() => _modelInputSize.Clear();

        /// A STALE BRAIN IS SILENT, AND THAT IS THE WHOLE REASON THIS EXISTS.
        ///
        /// Feed a 51-slot vector to a model trained on 45 and nothing throws, nothing is
        /// logged, and the console stays clean. ML-Agents rejects the model and the
        /// policy falls back to `Heuristic`, which for a fighter that is not the BOT is
        /// "write 0 to all 13 motors" — so the failure renders as two ragdolls standing
        /// limp at the stand-off while the round never starts. Measured exactly that way
        /// on 2026-09-05 after the vector went 45 -> 51: `MatchTestHarness.Run(2)` ran
        /// for four minutes without completing a single round, max |action| 0.000 on
        /// both fighters, and ZERO console entries of any severity.
        ///
        /// That is indistinguishable from a physics bug, a referee bug, or a brain that
        /// simply trained badly, and it is the single most expensive way to lose an
        /// afternoon in this project. So it is an explicit Error naming both numbers.
        ///
        /// Deliberately NOT stripped from release builds: a shipped APK carrying a stale
        /// `.onnx` has exactly this symptom, and a player-facing "the fighters do not
        /// move" bug is worth one log line.
        private void AssertModelMatchesVector(int vectorSize)
        {
            if (inferenceModel == null) return;

            if (!_modelInputSize.TryGetValue(inferenceModel, out int modelSize))
            {
                modelSize = -1;
                try
                {
                    var loaded = Unity.InferenceEngine.ModelLoader.Load(inferenceModel);
                    if (loaded != null && loaded.inputs != null && loaded.inputs.Count > 0)
                    {
                        var shape = loaded.inputs[0].shape;
                        // ML-Agents' vector input is (batch, size) — e.g. "(d0, 45)", the
                        // leading axis dynamic. Read the TRAILING dimension rather than
                        // assuming index 1, and go through Get(): DynamicTensorShape has
                        // no indexer. A dynamic axis reports a non-positive value, which
                        // is why the comparison below requires modelSize > 0.
                        if (!shape.isRankDynamic && shape.rank > 0)
                        {
                            modelSize = shape.Get(shape.rank - 1);
                        }
                    }
                }
                catch (System.Exception e)
                {
                    // A model that cannot even be inspected is ML-Agents' problem to
                    // report, not ours; do not turn a diagnostic into a second failure.
                    Debug.LogWarning($"[OBS] could not inspect '{inferenceModel.name}' " +
                                     $"to verify its input size: {e.GetType().Name}.");
                }
                _modelInputSize[inferenceModel] = modelSize;
            }

            if (modelSize > 0 && modelSize != vectorSize)
            {
                Debug.LogError(
                    $"[OBS] STALE BRAIN: '{inferenceModel.name}' takes {modelSize} inputs " +
                    $"but '{behaviorName}' now builds {vectorSize}. This fighter will NOT " +
                    $"move — ML-Agents rejects the model and falls back to a limp heuristic, " +
                    $"silently. Retrain against the current vector " +
                    $"(Training/configs/{behaviorName}Rebuild01.yaml) and redeploy, or put " +
                    $"back the observation flags the model was trained with.", this);
            }
        }

        /// Cost of falling in Mode.Walk, as a POSITIVE magnitude.
        ///
        /// A CONSTANT again, and it must stay one. It was briefly a curriculum dial
        /// (`walk_fall_penalty`, ramped 0.05 -> 1.0 over 16M steps in the `gait01`
        /// runs) on the reasoning that the tall-vs-crawl shaping advantage of 0.0063
        /// per step cannot cross a ~-4 fall, so the gap had to be closed from the
        /// penalty side instead.
        ///
        /// MEASURED, AND IT MADE THE GAIT DRAMATICALLY WORSE: torso height fell to
        /// 0.16-0.20 m against the 0.55-0.76 m it was trying to raise, on a 1.06 m
        /// standing pose. The fighters stopped crawling and started dragging flat.
        /// The reason is worth keeping, because it is not obvious: cheap falls do not
        /// buy exploration, because A BODY ALREADY ON THE GROUND CANNOT FALL. Once the
        /// policy found the floor the terminal simply stopped firing, so there was no
        /// gradient left pointing up, and ramping the penalty back to 1.0 over the
        /// final 4M steps did not recover it.
        ///
        /// So the dial is removed rather than defaulted, because leaving it wired
        /// invites a seventh attempt at a lever that has been measured backwards.
        /// Both remaining routes are structural, not reward-shaped: a torso-height
        /// constraint the policy cannot opt out of, or a walk-only trunk that does not
        /// share capacity with self-play sumo.
        private const float WALK_FALL_PENALTY = 1f;

        public override void OnEpisodeBegin()
        {
            _b.ResetPose();
            if (_contactSensors == null) _contactSensors = GetComponentsInChildren<Sensor_BodyPartContact>();
            for (int sensorIndex = 0; sensorIndex < _contactSensors.Length; sensorIndex++)
            {
                _contactSensors[sensorIndex].Clear();
            }
            NonFootGroundContacts = 0;
            if (_b.FloorSensors != null)
            {
                for (int floorIndex = 0; floorIndex < _b.FloorSensors.Length; floorIndex++)
                {
                    _b.FloorSensors[floorIndex].Clear();
                }
            }
            _pendingImpact = 0f;
            _lastTorsoY = _b.Torso.position.y;
            _cadence.Reset();
            for (int actionIndex = 0; actionIndex < ActionCount; actionIndex++) _prevActions[actionIndex] = 0f;
        }

        private float Fs => _b.facingSign;
        public Rigidbody2D Torso => _b.Torso;
        public float TorsoX => Torso.position.x;
        public bool IsDown => NonFootGroundContacts > 0;

        /// ANY body part is on the arena floor below the dohyo — the ring-out since
        /// 2026-08-26 (ringOutOnFloorContact in both referees). A plain loop over
        /// 15 sensors, called once per fighter per physics step.
        public bool OnFloor
        {
            get
            {
                Sensor_FloorContact[] sensors = _b.FloorSensors;
                if (sensors == null) return false;
                for (int floorIndex = 0; floorIndex < sensors.Length; floorIndex++)
                {
                    if (sensors[floorIndex].TouchingFloor) return true;
                }
                return false;
            }
        }

        /// Referees call this once the arena is known: contacts below `floorLevelY`
        /// (world) count as the floor.
        public void BindArenaFloor(float floorLevelY)
        {
            Sensor_FloorContact[] sensors = _b.FloorSensors;
            if (sensors == null) return;
            for (int floorIndex = 0; floorIndex < sensors.Length; floorIndex++)
            {
                sensors[floorIndex].floorLevelY = floorLevelY;
            }
        }

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

            // ARENA-RELATIVE, not world. `tp.y` is a world coordinate and the walk
            // lane in every SCN_TRAIN_* scene sits at y = -60, so this slot fed
            // ~+0.53 for the four sumo agents and ~-29.5 for the six walk agents --
            // the same input, on the same policy, in two disjoint ranges. It had
            // been that way since the walk+fight merge.
            //
            // This is the identical bug `Reward_StepCadence` documents and was fixed
            // for ("against absolute world Y this test was meaningless for the walk
            // lane"). The REWARD side was corrected then; the OBSERVATION side was
            // missed, which is why it survived five gait retrains -- every one of
            // them tuned what the policy was PAID while leaving what it could
            // PERCEIVE broken.
            //
            // `arenaGroundY` is written by both referees (Systems_GameMatchManager
            // ~L394, Systems_SumoMatchManager ~L145), so it is live in game and
            // training alike. Note the other height-ish observations below are
            // already relative -- the foot slots are measured against `tp` -- so
            // this was the only absolute one in the vector.
            sensor.AddObservation(San((tp.y - arenaGroundY) / 2f));               // 1
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

            // Opponent torso (or, in Walk mode, a virtual target at ring center).
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

            // THE MAT, AS IT IS RIGHT NOW — three slots.                         // 3
            //
            // `ringHalfWidth` is now written by both referees EVERY physics step, so
            // it tracks the contraction and the per-round randomisation. It used to
            // be written once at Start and never again, which made these slots lie in
            // two separate ways at once:
            //
            //   1. THE SHRINK. The mat closes from 3.5 m to 1.8 m and on past it
            //      toward zero (Systems_*MatchManager.TickShrinkingRing). Measured in
            //      a bracket: 17 of 17 rounds ended in a ring-out and 16 of them ran
            //      past shrinkStartSeconds, so the closing mat decides essentially
            //      every round — and the policy could not see it happening. A fighter
            //      believed it had 3.5 m of mat while standing on 0.2 m.
            //   2. THE CURRICULUM. ResetRound randomises the round's start width
            //      anywhere in 1.7..3.5 m, and the `platform_difficulty` lesson exists
            //      to "teach edge distance across SCALES". Dividing by a constant
            //      3.5 made every mat read identically, so that lesson was a no-op
            //      for the policy's entire history.
            //
            // Scaled by RING_REFERENCE_HALF rather than by the live width: dividing by
            // the live width would give the FRACTION of the mat remaining, which is 1.0
            // at the centre of a 3.5 m mat and 1.0 at the centre of a 0.2 m sliver.
            // Metres are what the fighter has to act on.
            float xLocal = (tp.x - arenaCenterX) * Fs;
            if (fighting)
            {
                sensor.AddObservation(San((ringHalfWidth - xLocal) / RING_REFERENCE_HALF)); // edge ahead
                sensor.AddObservation(San((ringHalfWidth + xLocal) / RING_REFERENCE_HALF)); // edge behind
                // How much mat exists at all. Without this the two slots above are
                // ambiguous: "1.0 ahead, 1.0 behind" is a full mat and also a fighter
                // dead-centre on a mat half that size, and only one of those is safe.
                sensor.AddObservation(San(ringHalfWidth / RING_REFERENCE_HALF));
            }
            else
            {
                // Walk lane and the ceremonial walk-in have no rim. The scenes
                // serialise ringHalfWidth: 10 on the six walk agents — a mat that does
                // not exist — and those slots then carried a large constant plus a
                // duplicate of the target distance the opponent block already gives.
                // Neutral constants instead, matching how the extended block below
                // already handles having no opponent.
                sensor.AddObservation(1f);
                sensor.AddObservation(1f);
                sensor.AddObservation(1f);
            }

            if (contactObservations)                                              // +4
            {
                sensor.AddObservation(_b.FootDownNear ? 1f : 0f);
                sensor.AddObservation(San(_b.FootLoadNear));
                sensor.AddObservation(_b.FootDownFar ? 1f : 0f);
                sensor.AddObservation(San(_b.FootLoadFar));
            }

            if (staminaObservation)                                               // +1
            {
                // Appended AFTER the contact block and BEFORE the extended block is
                // not arbitrary — the order here IS the input layer's layout, and the
                // only rule that matters is that it never changes again once a brain
                // has been trained on it.
                sensor.AddObservation(San(_b.Stamina));
            }

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
            // episode control (prevents Walk-mode terminations mid-walk-in).
            if (suppressEpisodeControl)
            {
                _lastTorsoY = San(Torso.position.y);
                return;
            }

            float upright = Mathf.Clamp01(Vector2.Dot(_b.Chest.transform.up, Vector2.up));
            float torsoY = San(Torso.position.y);
            float xLocal = (Torso.position.x - arenaCenterX) * Fs;
            bool hasOpponent = opponent != null;

            var ctx = new Reward_Context(
                Fs, arenaGroundY, Torso.position, Torso.linearVelocity, _lastTorsoY,
                upright, KneeBendFactor(), energy, effort, jerk, _pendingImpact,
                IsDown, hasOpponent, hasOpponent ? opponent.TorsoX : 0f);

            if (mode == Mode.Walk)
            {
                AddReward(_walkObjective.Evaluate(_b, _cadence, in ctx));

                // Catastrophic posture failure is an absolute episode termination gate.
                // The two terminals stay hardcoded on purpose: shaping is per-character,
                // but fall (-1) and graduation (+3) fix the reward scale so different
                // characters' walk runs remain comparable to each other on TensorBoard.
                //
                // They also stay HERE rather than in Reward_WalkObjective, which is
                // structurally unable to end an episode. Note that SetReward discards
                // this step's shaping outright — a fall is worth exactly -1 no matter
                // what was earned on the way down — so the order of these two lines
                // against the Evaluate above is load-bearing.
                if (IsDown) { SetReward(-WALK_FALL_PENALTY); EndEpisode(); return; }
                if (xLocal > -0.3f) { AddReward(3f); EndEpisode(); }
            }
            else // Sumo
            {
                // Shaping only; win/loss (+1/-1) is assigned by Systems_SumoMatchManager.
                AddReward(_sumoObjective.Evaluate(_b, _cadence, in ctx));
                // Consumed by the impact term above. Cleared HERE and not in Walk
                // mode, exactly as before — a walk-in that brushes the other fighter
                // banks that momentum for the first sumo step after EndWalkIn.
                _pendingImpact = 0f;
            }

            _lastTorsoY = torsoY;
        }

        /// The BOT gets a solid blue head so it is obvious which fighter is not a
        /// trained policy.
        ///
        /// Done in Start, not Awake: Agent_BipedBody builds the ragdoll — head
        /// renderer included — in its own Awake, and nothing orders the two Awakes
        /// against each other, so painting the head in Awake would work or not
        /// depending on component order. By Start the body is always built.
        private void Start()
        {
            if (useBot && _b != null)
            {
                _b.ApplyBotHead(BOT_HEAD_COLOR);
            }
        }

        /// Deliberately a strong, flat blue: it has to read against the dark arena
        /// and against the four fighters' skin-toned face photos.
        private static readonly Color BOT_HEAD_COLOR = new Color(0.16f, 0.45f, 1f);

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var a = actionsOut.ContinuousActions;
            if (!useBot)
            {
                // Unchanged default: a limp ragdoll. This is what a fighter with no
                // model and no script has always done, and several call sites rely
                // on Heuristic being harmless.
                for (int index = 0; index < a.Length; index++) a[index] = 0f;
                return;
            }

            // The walk-in points the four "opponent" slots at a virtual target, so
            // the scripted brain is fed the same target and walks to it rather than
            // trying to wrestle a fighter who is not there.
            bool hasTarget = opponent != null;
            var ctx = new Agent_BotContext(
                Fs, arenaCenterX, ringHalfWidth,
                hasTarget, hasTarget ? opponent.TorsoX : arenaCenterX,
                Time.time);
            _bot.Decide(_b, in ctx, a);
        }
    }
}
