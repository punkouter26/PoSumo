# PoSumo — ML-Agents Architecture

> Generated 2026-08-06 against the active codebase.
> Scope: the 4 trained fighters (`Matt`, `Standard`, `Nick`, `Kim`), their 13-action / 45-observation
> contract, the single inference runtime path, and the per-character reward providers that shape them.
>
> **Authoritative sources**: `Assets/Scripts/Agent/Agent_Biped.cs`,
> `Assets/Scripts/Agent/Agent_BipedBody.cs`, the four `*_Character.asset`,
> the four `Training/configs/*Unified*.yaml`, and the four shipped `.onnx` files at
> `Assets/Agents/<Name>_v01/<Name>.onnx`. Per CLAUDE.md, code constants win over
> prose in any disagreement.

---

## 1. Runtime Inference Loop

The shipped brains are PPO continuous policies executed by the ML-Agents
`ModelRunner` against a `Unity.InferenceEngine.ModelAsset` (`com.unity.ai.inference`
**2.6.1** — the runtime is Unity Inference Engine, although the local package's
internal class names still read `SentisModel`). Inference device is `Burst`; the
network has **no LSTM** (`memory_size = [1]`).

### 1a. Simple

```mermaid
flowchart TD
    A[FixedUpdate @ 50 Hz] --> B[Agent_Biped.OnActionReceived]
    B --> C[Build Reward_Context]
    C --> D[Reward_StepCadence +<br/>Reward_SumoObjective OR Reward_WalkObjective]
    D --> E[Apply 13 motor commands]
    E --> F[PhysX-2D step]
    F --> A
    H[DecisionPeriod=3] --> A
    I[Agent_Biped.CollectObservations<br/>45-dim vector] --> J[BehaviorParameters<br/>obs_0 shape 45]
    J --> K[ONNX forward pass<br/>via InferenceEngine/Burst]
    K --> L[continuous_actions shape 13]
    L --> B
```

### 1b. Detailed (with tensor shapes and execution path)

```mermaid
flowchart TD
    FB["FixedUpdate<br/>dt = 0.02 s<br/>50 Hz"] --> DR{DecisionRequester<br/>DecisionPeriod=3<br/>TakeActionsBetweenDecisions=true}
    DR -- "every 3rd step" --> CO["Agent_Biped.CollectObservations<br/>VectorSensor.AddObservation"]
    DR -- "non-decision steps" --> REUSE["reuse last action<br/>LastActions[]"]

    subgraph OBS["Observation assembly — 45 floats, sanitized via San()"]
        B1["body[5]<br/>torso.y/2, vx*Fs/5, vy/5, lean*Fs/180, chest.angVel*Fs/500"]
        B2["joints[26]<br/>for i in 0..12: JointAngleNorm(i), JointSpeedNorm(i)"]
        B3["feet[4]<br/>FootNear/FootFar relative offsets, *Fs/2"]
        B4["task_flag[1]<br/>1 in bout, 0 in walk"]
        B5["opponent[4]<br/>x diff *Fs/10, y diff /3, vel diff *Fs/5 /5"]
        B6["edges[2]<br/>(ringHalfWidth ∓ xLocal)/ringHalfWidth"]
        B7["extended[3]<br/>oppUpright, oppDown, oppEdge  (only if extendedObservations)"]
        B1 --> SAN[San finite-check] --> SENSOR[VectorSensor]
        B2 --> SAN
        B3 --> SAN
        B4 --> SAN
        B5 --> SAN
        B6 --> SAN
        B7 --> SAN
        SENSOR --> VEC["obs vector [45]<br/>dtype float32"]
    end
    CO --> OBS
    OBS --> BP["BehaviorParameters<br/>VectorObservationSize = 45<br/>NumStackedVectorObservations = 1<br/>ActionSpec = MakeContinuous(13)<br/>InferenceDevice = Burst"]

    BP --> MR["ModelRunner<br/>(com.unity.ml-agents 4.1.0)"]
    MR --> IE["Unity.InferenceEngine.ModelAsset<br/>obs_0 [batch, 45]"]
    IE --> NET["MLP forward<br/>Linear 45→512 → 512 → 512<br/>+ value head 512→1<br/>+ action head 512→13<br/>(Gaussian policy, no LSTM)"]
    NET --> OUT["continuous_actions [batch, 13]<br/>deterministic_continuous_actions [batch, 13]<br/>memory_size [1]<br/>version_number [1]"]

    OUT --> ACT["ActionBuffers.ContinuousActions<br/>clamped to [-1, 1]"]
    ACT --> APPLY["Agent_BipedBody.ApplyMotor(i, a[i]*actionScale)<br/>for i in 0..12 — 13 powered joints"]
    APPLY --> MOTORS["13 HingeJoint2D motors<br/>+ 2 unpowered toe hinges<br/>torque scaled by (1 - 0.35*fatigue)"]
    MOTORS --> PHYSX["PhysX 2D integration<br/>gravity -9.81<br/>PositionIterations/ VelocityIterations per ProjectSettings"]
    PHYSX --> SENS["Sensor_Impact / Sensor_BodyPartContact<br/>fire ReportOpponentImpact(relativeSpeed)"]
    SENS --> PEN["_pendingImpact += relativeSpeed"]
    PHYSX --> FB

    PHYSX -.->|"per joint"| FTG["Agent_BipedBody.IntegrateFatigue<br/>rate 0.06/s, recovery 0.10/s, depth 0.35"]
    FTG --> APPLY

    APPLY --> CTX["Reward_Context(in)<br/>readonly struct, 16 fields"]
    CTX --> SUMO["Reward_SumoObjective.Evaluate (Mode.Sumo)"]
    CTX --> WALK["Reward_WalkObjective.Evaluate (Mode.Walk)"]
    SUMO --> ADD["AddReward(float)"]
    WALK --> ADD
    ADD --> TERM["terminal checks<br/>(stay in Agent_Biped)<br/>walk: fall -1, xLocal > -0.3 → +3"]
```

---

## 2. Tensor Blueprint (Observation → Network → Action)

`obs_0 [45]` is fed through a 3-hidden-layer MLP (512 units each) with the same
topology as ML-Agents' PPO default `network_settings: { hidden_units: 512,
num_layers: 3, normalize: true }`. Two heads share the trunk: a Gaussian policy
over the 13 actions and a scalar value head.

### 2a. Simple

```mermaid
flowchart LR
    O[45-obs vector] --> N[512x3 MLP trunk] --> A[13 continuous actions]
    O --> N --> V[value estimate]
```

### 2b. Detailed (layer-by-layer)

```mermaid
flowchart LR
    subgraph IN["Inputs"]
        OBS["obs_0 [batch, 45]"]
    end
    subgraph TRUNK["Trunk (shared)"]
        L0["Dense 45 → 512<br/>+ bias 512<br/>≈ 23 552 params"]
        ACT0["ELU"]
        L1["Dense 512 → 512<br/>+ bias 512<br/>≈ 262 656 params"]
        ACT1["ELU"]
        L2["Dense 512 → 512<br/>+ bias 512<br/>≈ 262 656 params"]
        ACT2["ELU"]
    end
    subgraph HEADS["Heads"]
        AH["Dense 512 → 13<br/>(policy mean, σ learned separately)<br/>+ bias 13"]
        VH["Dense 512 → 1<br/>(value head, critic)<br/>+ bias 1"]
    end
    subgraph OUT["Outputs"]
        CA["continuous_actions [batch, 13]"]
        DCA["deterministic_continuous_actions [batch, 13]"]
        V["value (critic) [batch, 1]"]
        VS["version_number [1]"]
        MS["memory_size [1]<br/>(=0 — no LSTM)"]
    end

    OBS --> L0 --> ACT0 --> L1 --> ACT1 --> L2 --> ACT2
    ACT2 --> AH --> CA
    ACT2 --> AH --> DCA
    ACT2 --> VH --> V
    OBS -.->|"TensorProxy 0.1 (normalize)"| L0

    OBS -- "total ~556 K parameters across 14 initializers, 30 graph nodes" --- TOT["ONNX ir_version=4, opset ai.onnx v9<br/>producer pytorch 2.5.1"]
```

> All four fighters use the **identical topology** (same initializers count,
> same total ~556 K params, identical file size 2 228 786 bytes). They differ
> only in trained weights.

---

## 3. Reward Tree

Per-step shaping is split between two providers (`Reward_SumoObjective`,
`Reward_WalkObjective`) plus the shared `Reward_StepCadence`. Episode **terminals**
(`SetReward(-1)` on fall, `AddReward(3)` on walk graduation, and the ±1 from the
referee) stay in `Agent_Biped` and cannot move — providers are
structurally incapable of ending an episode.

### 3a. Simple

```mermaid
graph TD
    S[Sumo per-step shaping] --> U[Upright reward]
    S --> C[Closing toward opponent]
    S --> L[Lunge bonus over threshold]
    S --> I[Impact momentum × cap]
    S --> ST[Stance factor]
    S --> DR[Sustained drive when both feet loaded]
    S --> E[Energy + Jerk + Effort penalties]
    W[Walk per-step shaping] --> F[Forward speed × walkGate]
    W --> B[Knee bend]
    W --> U2[Upright chest]
    W --> K[Step cadence]
    W --> E2[Energy penalty + stall penalty]
    SH[Shared: Reward_StepCadence] --> K
    SH --> S
    T[Terminals stay in Agent_Biped] --> TW[Walk: fall -1 OR graduate +3]
    T --> TS[Sumo: ±1 from referee]
```

### 3b. Detailed (terms + coefficients + which fighter differs)

```mermaid
graph TD
    subgraph PER_STEP["Per-step shaping — Agent_Biped.OnActionReceived @ 50 Hz"]
        UPR["uprightReward · ctx.Upright<br/>default 0.0005"]
        CLO["closingReward · closing·bendGate<br/>default 0.0006"]
        LUN["lungeBonus · max(0, closing-thresh)·bendGate<br/>default 0.001 @ 1.5 m/s"]
        IMP["impactReward · min(impact, cap) · 1<br/>default 0.010 cap 8"]
        RIS["riseReward · (torsoY - lastTorsoY)<br/>fixed 0.02 — used only while IsDown"]
        KNE["kneeBendReward · ctx.KneeBend · 1<br/>default 0.0004"]
        HPS["hipsLowReward · HipsLowFactor<br/>default 0.0003"]
        CAD["cadenceReward · cadence.Evaluate<br/>default 0.0015<br/>(shared Reward_StepCadence)"]
        STA["stanceReward · StanceFactor<br/>default 0.0009"]
        DRV["driveReward · drive·grip<br/>default 0 — only paid when both feet down"]
        PEN1["energyPenalty · |a|<br/>default 0.0004 (gated by 1-useful)"]
        PEN2["jerkPenalty · |Δa|<br/>default 0.0003"]
        PEN3["effortPenalty · a² (UNGATED, quadratic)<br/>default 0.0015"]

        SUMO["Reward_SumoObjective.Evaluate<br/>returns float"]
        UPR --> SUMO
        CLO --> SUMO
        LUN --> SUMO
        IMP --> SUMO
        RIS --> SUMO
        KNE --> SUMO
        HPS --> SUMO
        CAD --> SUMO
        STA --> SUMO
        DRV --> SUMO
        PEN1 --> SUMO
        PEN2 --> SUMO
        PEN3 --> SUMO
    end

    subgraph PER_STEP_WALK["Walk school"]
        WFR["walkForwardReward · vx·Fs·walkGate<br/>default 0.004"]
        WST["walkStanceFloor · 0.15..1"]
        WBN["walkBendReward · KneeBend<br/>default 0.0006"]
        WUP["walkUprightReward · Upright<br/>default 0.001"]
        WCD["walkCadenceReward · cadence.Evaluate<br/>default 0.002"]
        WEN["walkEnergyPenalty · |a|<br/>default 0.0003"]
        WSP["walkStallPenalty · 0.0008<br/>when |vx| < 0.15 m/s"]

        WALK["Reward_WalkObjective.Evaluate<br/>returns float"]
        WFR --> WALK
        WST --> WALK
        WBN --> WALK
        WUP --> WALK
        WCD --> WALK
        WEN --> WALK
        WSP --> WALK
    end

    subgraph SHARED["Shared cadence"]
        CADX["Reward_StepCadence.Evaluate<br/>MIN_STEP_INTERVAL 0.25 s<br/>PLANTED_HEIGHT 0.12 m<br/>PLANTED_SPEED 0.5 m/s"]
    end

    CAD --> CADX
    WCD --> CADX

    subgraph TERMINALS["Terminals (Agent_Biped, NOT providers)"]
        TW1["Walk: IsDown → SetReward(-1), EndEpisode()"]
        TW2["Walk: xLocal > -0.3 → AddReward(3), EndEpisode()"]
        TS1["Sumo: ±1 from Systems_SumoMatchManager<br/>foot below footOffMatY OR torso below fallY<br/>timeout → EpisodeInterrupted (draw)"]
    end

    subgraph PERSONALITIES["Per-character deviations from defaults"]
        M["Matt — closing 0.0009, lunge 0.0016 @ 1.2, impact 0.015 cap 8, cadence 0.0015"]
        N["Nick — cadence 0.0032 (highest), lunge 0.0024 @ 1.0,<br/>closing 0.0011, impact 0.011 cap 8, straightLegEarnFraction 0.75<br/>(reward-hack risk — see Nick MANIFEST)"]
        K["Kim — kneeBend 0.0008, hipsLow 0.0008, impact 0.014 cap 10,<br/>closing 0.0004, lunge 0.0008 @ 1.8,<br/>cadence 0.0006, straightLegEarnFraction 0.15"]
        S["Standard — code defaults (asset's eight fields are all null)"]
    end

    SUMO --> ADD[AddReward]
    WALK --> ADD
    CADX --> SUMO
    CADX --> WALK
    TW1 --> END[EndEpisode]
    TW2 --> END
    TS1 --> END

    M -.->|per-coefficient override| SUMO
    N -.->|per-coefficient override| SUMO
    K -.->|per-coefficient override| SUMO
```

---

## 4. Actuator Map (Action → Joint Drive)

13 continuous actions (clamped to `[-1, 1]`) map one-to-one onto 13 powered
`HingeJoint2D`s. Motor torque is **scaled by muscle fatigue**: every joint's
applied torque is multiplied by `(1 - FATIGUE_DEPTH * fatigue)` where
`fatigue ∈ [0, 1]` is integrated at 50 Hz from the joint's measured
`GetMotorTorque(dt)`. Two unpowered toe hinges (MTP joints) carry limits and
passive resistance but no motor; they are deliberately appended after the 13
powered ones.

### 4a. Simple

```mermaid
flowchart LR
    A[13 continuous actions<br/>clamped [-1,1]] --> J[13 hinge motors]
    J --> B[2D ragdoll]
```

### 4b. Detailed (per-joint breakdown)

```mermaid
flowchart LR
    subgraph ACT["ActionBuffers.ContinuousActions [13]"]
        A0["a[0]"] --- A1["a[1]"] --- A2["a[2]"] --- A3["a[3]"] --- A4["a[4]"]
        A5["a[5]"] --- A6["a[6]"] --- A7["a[7]"] --- A8["a[8]"] --- A9["a[9]"]
        A10["a[10]"] --- A11["a[11]"] --- A12["a[12]"]
    end

    subgraph MOTORS["Powered HingeJoint2D motors (after Hill + fatigue scaling)"]
        M0["HIP-NEAR<br/>τ_max 300 N·m · v_max 400°/s<br/>range −120° .. +30°<br/>(asymmetric — flexion positive)"]
        M1["KNEE-NEAR<br/>τ_max 250 N·m · v_max 500°/s<br/>range 0° .. +150°<br/>(asymmetric — flexion positive)"]
        M2["ANKLE-NEAR<br/>τ_max 160 N·m · v_max 400°/s<br/>range ±35° (symmetric)"]
        M3["HIP-FAR (mirror of M0)"]
        M4["KNEE-FAR (mirror of M1)"]
        M5["ANKLE-FAR (mirror of M2)"]
        M6["SPINE-1 (pelvis→lower back)<br/>τ_max 180 · v_max 250<br/>range ±20°"]
        M7["SPINE-2 (lower→upper back)<br/>τ_max 180 · v_max 250<br/>range ±20°"]
        M8["SPINE-3 (upper back→chest)<br/>τ_max 180 · v_max 250<br/>range ±20°"]
        M9["SHOULDER-NEAR<br/>τ_max 80 · v_max 500<br/>range ±120°"]
        M10["ELBOW-NEAR<br/>τ_max 60 · v_max 500<br/>range −150° .. 0°<br/>(asymmetric — flexion negative)"]
        M11["SHOULDER-FAR (mirror of M9)"]
        M12["ELBOW-FAR (mirror of M10)"]
    end

    subgraph UNPOWERED["Unpowered (no action index, appended at index 13..14)"]
        U0["TOE-NEAR MTP<br/>limits ±35°, passive spring/damper only"]
        U1["TOE-FAR MTP"]
    end

    A0 --> M0
    A1 --> M1
    A2 --> M2
    A3 --> M3
    A4 --> M4
    A5 --> M5
    A6 --> M6
    A7 --> M7
    A8 --> M8
    A9 --> M9
    A10 --> M10
    A11 --> M11
    A12 --> M12

    subgraph SCALE["Per-step torque scaling (Agent_BipedBody.ApplyMotor)"]
        HILL["Hill a/F0 = 0.25<br/>Eccentric gain = 1.5×<br/>Activation lag: rise 0.05 s, fall 0.07 s"]
        FATG["Fatigue multiplier (1 - 0.35 · fatigue)<br/>fatigue integrates at FATIGUE_RATE 0.06/s,<br/>recovers at RECOVERY_RATE 0.10/s"]
        PASSIVE["Passive restoring torque<br/>6% of motor budget at 90° off neutral<br/>+ 10% per 400°/s of damping"]
    end

    M0 --> HILL --> FATG --> PASSIVE --> BODY["HingeJoint2D.useMotor<br/>motorSpeed = a[i] * actionScale * v_max<br/>maxMotorTorque = τ_max * (1 - 0.35*fatigue)"]
    M1 --> BODY
    M2 --> BODY
    M3 --> BODY
    M4 --> BODY
    M5 --> BODY
    M6 --> BODY
    M7 --> BODY
    M8 --> BODY
    M9 --> BODY
    M10 --> BODY
    M11 --> BODY
    M12 --> BODY
```

---

## 5. Hyperparameter Matrix (PPO Config)

All four fighters share an **identical PPO trunk** — only one fighter's
hyperparameters have been deliberately tuned away from the baseline.

### 5a. Simple

```mermaid
flowchart LR
    P[PPO trainer] --> LR[3.0e-4 linear-decay]
    P --> B[batch 2048]
    P --> N[3 hidden × 512 + value head]
    P --> SP[self-play window 25, swap 50K, save 50K, team 250K]
```

### 5b. Detailed (per-fighter deviations)

```mermaid
flowchart LR
    subgraph SHARED["Shared across all four fighters"]
        T["trainer_type = ppo"]
        LR["learning_rate 3.0e-4 (linear schedule)"]
        BS["batch_size 2048 · buffer_size 20480"]
        BE["beta 5.0e-3 · epsilon 0.2"]
        LA["lambd 0.95 · num_epoch 3"]
        GA["gamma 0.997 (extrinsic strength 1.0)"]
        TH["time_horizon 1000"]
        NET["normalize true · hidden_units 512 · num_layers 3"]
        SP["self-play: window 25, swap_steps 50 000, save_steps 50 000,<br/>team_change 250 000, play_against_latest_model_ratio 0.25"]
        CK["keep_checkpoints 5 · checkpoint_interval 250 000"]
        MS["max_steps 15 000 000 · summary_freq 30 000 · threaded true"]
    end

    subgraph MATT["MattUnified02.yaml + MattUnified03.yaml"]
        MC["byte-identical recipes;<br/>v03 is a cold restart only because<br/>Training/results/ is gitignored and<br/>no checkpoint is on disk to resume from"]
    end

    subgraph NICK["NickUnified01.yaml"]
        NC["identical to baseline — Nick's<br/>PERSONALITY is in the character asset,<br/>not in hyperparameters"]
    end

    subgraph STANDARD["StandardUnified01.yaml"]
        SC["identical to baseline — Standard is the<br/>reference, all values match the code defaults"]
    end

    subgraph KIM["KimUnified01.yaml (per manifest: shorter gamma, narrower window)"]
        KC["YAML ships gamma 0.997 (NOT 0.99 as the<br/>manifest claims). CLAUDE.md's<br/>'constant over prose' rule applies.<br/>Note this as a manifest drift to fix."]
    end

    subgraph CURRICULUM["Ring-width curriculum (every fighter)"]
        WIDE["lesson 0 'wide': platform_difficulty 0.0<br/>threshold 0.3, min_lesson_length 100"]
        MIX["lesson 1 'mixed': platform_difficulty 0.5<br/>threshold 0.6, min_lesson_length 100"]
        FULL["lesson 2 'full': platform_difficulty 1.0"]
    end

    SHARED --> MATT
    SHARED --> NICK
    SHARED --> STANDARD
    SHARED --> KIM
    MATT --> CURRICULUM
    NICK --> CURRICULUM
    STANDARD --> CURRICULUM
    KIM --> CURRICULUM
```

---

## 6. Episode Lifecycle

Every agent's episode goes through `Agent_Biped.OnEpisodeBegin` →
`OnActionReceived` (every physics step) → either a walk terminal (in agent),
a sumo terminal (in `Systems_SumoMatchManager`), or the round clock.

### 6a. Simple

```mermaid
stateDiagram-v2
    [*] --> Init: scene loaded
    Init --> Step: ResetPose + sensor clear
    Step --> Step: FixedUpdate 50 Hz
    Step --> Step: DecisionRequester @ period 3
    Step --> Reward: AddReward(float)
    Reward --> Terminal
    Terminal --> Reset: EndEpisode
    Reset --> Init
    Terminal --> [*]
```

### 6b. Detailed

```mermaid
stateDiagram-v2
    [*] --> Spawn: scene Awake / OnEnable
    Spawn --> ResetPose: Agent_BipedBody.ResetPose<br/>+ Reset fatigue (0..1 → 0)<br/>+ RestoreMotors at full budget<br/>(BEFORE the next step's fatigue tax)
    ResetPose --> BeginEpisode: Agent_Biped.OnEpisodeBegin<br/>clears contact sensors, _pendingImpact,<br/>_prevActions, _cadence

    state Round {
        [*] --> Step: FixedUpdate n = 0
        Step --> Decide: n % DecisionPeriod == 0
        Step --> Carry: otherwise — reuse LastActions
        Decide --> Sense: CollectObservations<br/>45 floats, sanitized
        Sense --> Infer: ModelRunner → ONNX Burst<br/>output 13 floats clamped [-1,1]
        Infer --> Actuate: ApplyMotor ×13<br/>+ fatigue tax (1 - 0.35*fatigue)
        Actuate --> PhysX: PhysX 2D step<br/>gravity -9.81
        PhysX --> Report: Sensor_Impact → ReportOpponentImpact(relativeSpeed)
        PhysX --> SenseNext: ++n

        SenseNext --> Decide
    }

    BeginEpisode --> Round

    state Terminals {
        direction LR
        WalkFall: walk · IsDown<br/>SetReward(-1), EndEpisode
        WalkGrad: walk · xLocal > -0.3<br/>AddReward(+3), EndEpisode
        SumoWin: sumo · Systems_SumoMatchManager<br/>footOffMatY/fallY → +1, EndEpisode
        SumoTimeout: sumo · roundTimeoutSeconds<br/>EpisodeInterrupted (draw)
        Knockout: 3 head KOs (Systems_BodyDamage)<br/>→ match loss (GAME-only, not trained against)
        DownOut: downOutSeconds (GAME-only)<br/>→ round forfeit
    }

    Round --> Terminals: per-step check
    Terminals --> ResetPose: EndEpisode
    Round --> BeginEpisode: MaxStep reached (walk only)
    BeginEpisode --> [*]: MaxStep OR external stop
```

---

## Notes for future maintainers

- **Observation count authority**: `Agent_Biped.ObservationCount = 42` (base)
  with the `extendedObservations` bool adding **+3** for a 45-slot vector. The
  xml-doc comments in `Agent_Biped.cs` and the tooltips on
  `Agent_CharacterDefinition.cs` still reference the legacy "41 obs / 44 obs"
  contract — they are stale and should be re-worded to match the +1 task-flag
  addition.
- **Action count authority**: 13 powered joints, always. The two unpowered
  toe hinges are appended after `ActionCount` and never sent through the
  network, so adding another driven joint requires both an input and output
  layer change and invalidates every shipped `.onnx`.
- **Inference engine authority**: `Unity.InferenceEngine.ModelAsset`
  (`com.unity.ai.inference` 2.6.1). The local `com.unity.ml-agents` 4.1.0
  package still calls its inference classes `SentisModel` internally — the
  runtime is the Inference Engine, not legacy Sentis/Barracuda.
- **Reward providers are stateless w.r.t. the agent**: they return a `float`
  and cannot end an episode. The walk fall / walk graduation terminals stay
  hardcoded in `Agent_Biped` to keep reward scales comparable across
  characters on TensorBoard.