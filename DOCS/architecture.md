# PoSumo — Architecture Diagram Suite

> **Scope note.** The originating request named `Assets/Scenes/CreatureTrainingRace.unity`,
> a `config/` directory and `results/<run_id>/run_summary.json`. **None of those exist in
> this project** — they belong to a different ML-Agents codebase. This suite documents
> what PoSumo actually contains: `SCN_*` scenes, `Training/configs/*.yaml`, and
> `Training/results/<run_id>/<Behavior>/`, which stores `checkpoint.pt` + tfevents and has
> no `run_summary.json`. Every number below is measured from the live project, not assumed.

All figures verified 2026-07-27 against the running corrective training pass.
Trainer is **PPO for every brain** — there is no SAC config in this repo.

---

## 1. Runtime Inference Loop

### 1-simple

```mermaid
flowchart TD
    A[FixedUpdate 50 Hz] --> B{DecisionPeriod 3?}
    B -- no --> H[Hold last motor targets]
    B -- yes --> C[CollectObservations]
    C --> D[Sentis / Inference Engine]
    D --> E[OnActionReceived]
    E --> F[HingeJoint2D motors]
    F --> G[PhysX 2D solver]
    G --> A
    H --> G
```

### 1-detailed

```mermaid
flowchart TD
    subgraph UNITY["Unity main loop — fixedDeltaTime 0.02 s"]
        A["FixedUpdate<br/>Agent_BipedBody.FixedUpdate<br/>passive joint torque, all 13 joints"]
        DR["DecisionRequester<br/>DecisionPeriod = 3<br/>→ 1 decision / 60 ms"]
    end

    subgraph OBS["CollectObservations → VectorSensor"]
        O1["torso y, vel x, vel y (3)"]
        O2["chest lean, angular vel (2)"]
        O3["13 joints x angle+speed (26)"]
        O4["foot near x,y + far x,y (4)"]
        O5["opponent rel pos + rel vel (4)"]
        O6["edge ahead, edge behind (2)"]
        O7["opp upright, opp down, opp edge (3)"]
        SAN["San() NaN/Inf sanitize<br/>every element"]
    end

    subgraph NN["Unity.InferenceEngine — 555,125 params"]
        I["obs_0 : float32[batch, 44]"]
        N1["normalize: true (running mean/var)"]
        L1["Dense 512 + Swish"]
        L2["Dense 512 + Swish"]
        L3["Dense 512 + Swish"]
        MU["continuous_actions : float32[batch, 13]"]
    end

    subgraph ACT["OnActionReceived"]
        CL["Mathf.Clamp(a, -1, 1)"]
        SC["x actionScale<br/>0.3 → 1.0 over minSettleSeconds"]
        MOT["ApplyMotor(j, a)<br/>motorSpeed = a x maxSpeed[j] x facingSign"]
        CAV["ClampAngularVelocities()"]
    end

    subgraph PHYS["PhysX 2D"]
        SOLV["velocityIterations 14<br/>positionIterations 16"]
        BOD["14 rigidbodies, 69.6 kg<br/>linearDamping 0.25 / angularDamping 0.8"]
    end

    A --> DR
    DR -- "step % 3 == 0" --> O1 & O2 & O3 & O4 & O5 & O6 & O7
    O1 & O2 & O3 & O4 & O5 & O6 & O7 --> SAN --> I
    I --> N1 --> L1 --> L2 --> L3 --> MU --> CL --> SC --> MOT --> CAV
    DR -- "otherwise" --> HOLD["motors hold previous target"]
    CAV --> SOLV
    HOLD --> SOLV
    SOLV --> BOD --> A

    style NN fill:#1a2a3a,color:#fff
    style PHYS fill:#2a1a1a,color:#fff
```

**Timing consequence.** The policy acts every 60 ms while physics runs at 20 ms, so
motors hold their target for 3 ticks. Passive joint resistance deliberately runs in
`FixedUpdate` (every tick) rather than on the decision boundary — tissue does not take
turns with the brain.

---

## 2. Tensor Blueprint

### 2-simple

```mermaid
flowchart LR
    OBS[44 observations] --> H1[512] --> H2[512] --> H3[512] --> ACT[13 continuous actions]
```

### 2-detailed

```mermaid
flowchart LR
    subgraph IN["Input — obs_0 [B, 44]"]
        direction TB
        A1["proprioception [B,31]<br/>torso 3 · chest 2 · joints 26"]
        A2["contact [B,4]<br/>foot near/far x,y"]
        A3["opponent [B,8]<br/>rel pos 2 · rel vel 2<br/>upright 1 · down 1 · edge 1<br/>+ own edge 2"]
    end

    subgraph BODY["Trunk — normalize: true"]
        direction TB
        D1["Dense 44→512<br/>22,528 w + 512 b"]
        S1["Swish"]
        D2["Dense 512→512<br/>262,144 w + 512 b"]
        S2["Swish"]
        D3["Dense 512→512<br/>262,144 w + 512 b"]
        S3["Swish"]
    end

    subgraph HEADS["Heads"]
        direction TB
        MU["mu: Dense 512→13"]
        SIG["log_sigma: [13] (train only)"]
        VAL["value: Dense 512→1 (train only)"]
    end

    subgraph OUT["ONNX graph outputs"]
        direction TB
        C1["continuous_actions [B,13]"]
        C2["deterministic_continuous_actions [B,13]"]
        C3["version_number, memory_size,<br/>continuous_action_output_shape"]
    end

    A1 & A2 & A3 --> D1 --> S1 --> D2 --> S2 --> D3 --> S3
    S3 --> MU --> C1 & C2
    S3 --> SIG
    S3 --> VAL
    HEADS --> C3

    style BODY fill:#1a2a3a,color:#fff
```

| Fact | Value | Source |
|---|---|---|
| Input tensor | `obs_0 : [batch, 44]` | read from every deployed `.onnx` |
| Action tensor | `continuous_actions : [batch, 13]` | ditto |
| Total parameters | **555,125** | summed ONNX initializers |
| File size | 2.12 MB per brain | `os.stat` |

`memory_size = 0` — these are feed-forward policies, no recurrence.

---

## 3. Reward Tree

### 3-simple

```mermaid
graph TD
    R[Total reward] --> T[Terminal ±1 from referee]
    R --> S[Shaping, per step]
    S --> POS[Posture]
    S --> AGG[Aggression]
    S --> COST[Effort costs]
```

### 3-detailed

```mermaid
graph TD
    ROOT["Episode return"]

    ROOT --> TERM["TERMINAL — assigned externally"]
    TERM --> T1["Sumo: ±1 from Systems_SumoMatchManager<br/>loss = foot below footOffMatY (−0.06)<br/>or torso below fallY"]
    TERM --> T2["Walk: fall = −1, graduation = +3<br/>HARDCODED so runs stay comparable"]

    ROOT --> SHAPE["SHAPING — per step, per character sheet"]

    SHAPE --> P["Posture"]
    P --> P1["upright x uprightReward 0.0005"]
    P --> P2["kneeBend x kneeBendReward 0.0004"]
    P --> P3["hipsLow x hipsLowReward 0.0003"]
    P --> P4["cadence x cadenceReward 0.0015"]
    P --> P5["StanceFactor x stanceReward 0.0009<br/>NEW: both feet planted AND apart AND bent"]

    SHAPE --> A["Aggression"]
    A --> A1["closing x closingReward 0.0006 x bendGate"]
    A --> A2["lunge above 1.5 m/s x lungeBonus"]
    A --> A3["impact x impactReward 0.01, capped 8"]

    SHAPE --> C["Effort cost"]
    C --> C1["−energy x energyPenalty 0.0004<br/>x (1 − useful) ← GATED: free when moving fast"]
    C --> C2["−jerk x jerkPenalty 0.0003"]
    C --> C3["−effort x effortPenalty 0.0015<br/>NEW: UNGATED and QUADRATIC"]

    ROOT --> REC["Recovery (falls are not losses)"]
    REC --> R1["rise x riseReward 0.02 while down"]

    style C3 fill:#2d4a2d,color:#fff
    style P5 fill:#2d4a2d,color:#fff
    style C1 fill:#4a2d2d,color:#fff
```

**The two green nodes are new in the realism pass and are why the brains needed
retraining.** The red node is the defect they address: `energyPenalty` is multiplied by
`(1 − useful)`, so motor effort became *free* whenever the fighter moved fast. Measured
consequence before the fix: **7–12 of 13 motors railed above |0.9|**, mean |action|
0.75–0.91. After 250k corrective steps: **3/13 railed**, mean(a²) 0.45 vs 0.83.

---

## 4. Actuator Map

### 4-simple

```mermaid
flowchart LR
    A[13 actions] --> M[HingeJoint2D motors] --> J[Joint rotation] --> B[Body]
```

### 4-detailed

```mermaid
flowchart LR
    subgraph VEC["Action vector a[0..12], clamped ±1"]
        direction TB
        V0["a0 hip near"]
        V1["a1 knee near"]
        V2["a2 ankle near"]
        V3["a3 hip far"]
        V4["a4 knee far"]
        V5["a5 ankle far"]
        V6["a6-a8 spine 1-3"]
        V9["a9,a11 shoulders"]
        V10["a10,a12 elbows"]
    end

    subgraph DRIVE["ApplyMotor — velocity drive, not PD"]
        direction TB
        F1["motorSpeed = a x maxSpeed[j] x facingSign"]
        F2["maxMotorTorque = torque[j] x torqueScale"]
        F3["+ passive: −(angle x k + speed x c)<br/>k = 6% budget / 90°, c = 10% / 400°/s"]
    end

    subgraph LIMITS["Per-joint limits (deg) / torque (N·m) / speed (deg·s⁻¹)"]
        direction TB
        L0["hip −30..120 · 300 · 400"]
        L1["knee −150..0 · 250 · 500"]
        L2["ankle −25..25 · 120 · 400"]
        L6["spine ±20 each · 180 · 250"]
        L9["shoulder ±120 · 80 · 500"]
        L10["elbow 0..150 · 60 · 500"]
    end

    V0 & V3 --> L0
    V1 & V4 --> L1
    V2 & V5 --> L2
    V6 --> L6
    V9 --> L9
    V10 --> L10
    VEC --> F1 --> F2
    F3 --> F2
    F2 --> PHYS["PhysX 2D · foot μ 0.9 on clay μ 0.9<br/>body μ 0.4 · gravity −9.81"]

    style L2 fill:#3a3a1a,color:#fff
    style L6 fill:#3a3a1a,color:#fff
    style L9 fill:#3a3a1a,color:#fff
```

**Not a PD controller.** `HingeJoint2D` exposes a *velocity* motor with a torque ceiling,
so there is no $K_p$/$K_d$ pair — the analogue is `motorSpeed` (target rate) bounded by
`maxMotorTorque`. Unity 2D hinges have **no spring**, which is why passive elasticity is
applied as an explicit restoring torque in `FixedUpdate` rather than via joint settings.

Yellow nodes were clamped in the realism pass (ankle ±45→±25, spine ±25→±20 each,
shoulder ±160→±120). They remain symmetric: flexion sign differs per joint in this rig
and assigning asymmetry blind risks inverting a usable range.

---

## 5. Hyperparameter Matrix

### 5-simple

```mermaid
flowchart LR
    PPO[PPO] --> NET[512 x 3] --> SUMO[Sumo: self-play + curriculum]
    PPO --> WALK[Walk: single agent]
```

### 5-detailed

```mermaid
flowchart LR
    subgraph SHARED["Shared — identical across all 8 runs"]
        direction TB
        S1["trainer_type: ppo"]
        S2["batch 2048 / buffer 20480"]
        S3["lr 1.5e-4, linear schedule"]
        S4["beta 5e-3 · epsilon 0.2 · lambd 0.95"]
        S5["num_epoch 3"]
        S6["network 512 x 3, normalize: true"]
    end

    subgraph SUMO["Sumo — 4 runs"]
        direction TB
        F1["gamma 0.997"]
        F2["time_horizon 1000"]
        F3["max_steps 6,000,000"]
        F4["self_play: window 25<br/>save/swap 50k · team_change 250k<br/>latest-model ratio 0.25"]
        F5["curriculum platform_difficulty<br/>wide 0.0 → mixed 0.5 → full 1.0"]
    end

    subgraph WALK["Walk — 4 runs"]
        direction TB
        W1["gamma 0.995"]
        W2["time_horizon 500"]
        W3["max_steps 2,500,000"]
        W4["no self-play, no curriculum"]
    end

    SHARED --> SUMO
    SHARED --> WALK
    SUMO --> WARM["--initialize-from trunk<br/>512x3 MUST match"]
    WALK --> WARM

    style SUMO fill:#1a2a3a,color:#fff
    style WALK fill:#1a3a2a,color:#fff
```

`network_settings` must match the trunk exactly or `--initialize-from` cannot load the
weights. `lr 1.5e-4` sits between the 1.0e-4 used for a contact-shape tweak and the
3.0e-4 of a fresh run — the body changed, so this is more than a nudge and less than a
rebuild.

---

## 6. Episode Lifecycle

### 6-simple

```mermaid
stateDiagram-v2
    [*] --> Reset
    Reset --> Stepping
    Stepping --> Stepping: collect / act / reward
    Stepping --> Terminal: out of ring or timeout
    Terminal --> Reset
```

### 6-detailed

```mermaid
stateDiagram-v2
    [*] --> OnEpisodeBegin

    OnEpisodeBegin --> WalkIn: enableWalkIn && walk brains ready<br/>&& first round of match
    OnEpisodeBegin --> Countdown: otherwise

    state WalkIn {
        [*] --> Widen: mat → walkInHalfWidth 8 m
        Widen --> Approach: spawn ±6 m,<br/>swap in walkModel,<br/>suppressEpisodeControl
        Approach --> Park: each fighter parks<br/>on arrival OR overshoot
        Park --> Contract: mat → ringHalfWidth 5.5 m
        Contract --> [*]
    }
    WalkIn --> Countdown

    state Countdown {
        [*] --> Digit3: camera punch-in on head A
        Digit3 --> Digit2: punch-in on head B
        Digit2 --> Digit1: release wide
        Digit1 --> Fight
        Fight --> [*]: BeginSimulation()<br/>actionScale 0.3 → 1.0
    }
    Countdown --> Fighting

    state Fighting {
        [*] --> Step
        Step --> Step: obs → policy → motors → reward<br/>ScoringLive gates all averaged stats
        Step --> KO: head impact ≥ koSpeed 7.5 m/s
        KO --> Step: limp koLimpSeconds,<br/>knockback 4.5 m/s, +1 KO count
    }

    Fighting --> RoundEnded: foot < footOffMatY (−0.06)<br/>or torso < fallY
    Fighting --> Timeout: elapsed ≥ 30 s
    Fighting --> MatchOver: KO count reaches 3<br/>(three-knockdown rule)

    Timeout --> RoundEnded: decided on distance from centre<br/>dead heat < 0.15 m = draw
    RoundEnded --> Grace: score, banner, dust
    Grace --> Countdown: rounds remain
    RoundEnded --> MatchOver: score reaches pointsToWin

    MatchOver --> [*]: freeze both fighters,<br/>result card, 2 s hold then wide shot

    note right of Fighting
        Falling is NOT a loss.
        knockdownLoses = false in BOTH referees.
    end note
```

---

## Cross-references

| Concern | File |
|---|---|
| Body tables (`PART_DEFS`, `JOINT_DEFS`) | `Assets/Scripts/Agent/Agent_BipedBody.cs` |
| Observations, actions, reward shaping | `Assets/Scripts/Agent/Agent_Biped.cs` |
| Per-fighter coefficients | `Assets/Scripts/Agent/Agent_CharacterDefinition.cs` |
| Game referee | `Assets/Scripts/Systems/Systems_GameMatchManager.cs` |
| Training referee + domain randomisation | `Assets/Scripts/Systems/Systems_SumoMatchManager.cs` |
| Shared tuning (ring, walk-in, camera) | `Assets/Settings/GameTuning.asset` |
