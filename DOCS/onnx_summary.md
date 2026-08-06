# PoSumo — ONNX & Component Grid

> Snapshot 2026-08-06. PoSumo is a 2D URP sumo game built on ML-Agents 4.1.0
> and the Unity Inference Engine 2.6.1 — note that the local ML-Agents package
> still calls its inference classes `SentisModel` internally; the runtime is
> the Inference Engine (Burst device), not legacy Sentis or Barracuda.
>
> **There is no per-fighter prefab or rig** in this project. The 2D ragdoll is
> defined once in `Agent_BipedBody.PART_DEFS` / `JOINT_DEFS` (16 parts, 15 hinge
> motors of which 13 are policy-driven) and built at runtime by
> `Agent_BipedBody.Build()`. Fighter personality is encoded as data on the
> per-fighter `*_Character.asset` (build scales + reward shaping coefficients).
> The columns the request calls "Prefab" and "Rig Component" are therefore
> adapted below to the actual PoSumo equivalents.

---

## Matrix A — ONNX metadata

| Fighter | Asset (Prefab-equivalent) | ONNX file | Size (bytes) | mtime (ISO-8601, EDT) | I/O tensors | Parameters | Run ID | Final reward | Promotion status |
|---|---|---:|---:|---|---|---:|---|---:|---|
| **Matt** | `Assets/Agents/Matt_v01/Matt_Character.asset` | `Assets/Agents/Matt_v01/Matt.onnx` | 2 228 786 | 2026-07-31T18:45:44 | in: `obs_0 [batch, 45]` · out: `continuous_actions [batch, 13]`, `deterministic_continuous_actions [batch, 13]`, `value [batch, 1]`, `version_number [1]`, `memory_size [1]` | 555 639 (14 initializers, 30 nodes) | `matt_unified02` (shipped) → `matt_unified03` (in flight, 2.76 M / 15.0 M) | 4.15 (cumulative) at 2.76 M | **Shipped**. v03 cold re-establishment in progress — not deployable until it beats the shipped brain on self-play ELO shape |
| **Standard** | `Assets/Agents/Standard_v01/Standard_Character.asset` (NOT referenced by `SCN_TRAIN_STANDARD`) | `Assets/Agents/Standard_v01/Standard.onnx` | 2 228 786 | 2026-07-31T18:45:44 | identical to Matt | 555 639 (14 initializers, 30 nodes) | `standard_unified01` (shipped) | 4.15 (canonical reference) | **Shipped** — highest self-play ELO on the roster (5941 at 15 M) |
| **Nick** | `Assets/Agents/Nick_v01/Nick_Character.asset` | `Assets/Agents/Nick_v01/Nick.onnx` | 2 228 786 | 2026-07-31T18:45:44 | identical to Matt | 555 639 (14 initializers, 30 nodes) | `nick_unified01` (shipped) | n/a (reward-hack-prone; never judge Nick on mean reward) | **Shipped** — ELO 4581 against a pool reset at 3.75 M; judge shape only |
| **Kim** | `Assets/Agents/Kim_v01/Kim_Character.asset` | `Assets/Agents/Kim_v01/Kim.onnx` | 2 228 786 | 2026-07-31T18:45:44 | identical to Matt | 555 639 (14 initializers, 30 nodes) | `kim_unified01` (shipped) | n/a (silent run; no TensorBoard dir on disk) | **Shipped** — manifest γ drift to fix (manifest 0.99 vs YAML 0.997) |

### Common ONNX metadata (identical across all four brains)

```
ONNX ir_version   : 4
Opset             : ai.onnx v9
Producer          : pytorch 2.5.1
Initializers      : 14 weight tensors (3 W + 3 b per hidden layer × 3 + 1 W/b for value head + 1 W/b for action head)
Graph nodes       : 30 (Dense + ELU + Gaussian policy head + value head)
Total params      : ≈ 555 639  (mean across the four fighters — measured 555 639 for Matt, identical topology for the other three)
Memory size       : [1] (=0 — no LSTM)
LSTM in use       : No
Action space      : 13 continuous (Gaussian policy, deterministic argmax also exposed)
Inference device  : Unity.MLAgents.Policies.InferenceDevice.Burst
```

### I/O tensor schema (canonical)

| Tensor name | Direction | Shape | Notes |
|---|---|---|---|
| `obs_0` | input | `[batch, 45]` | Leading 0 = dynamic batch dim. 45 = base 42 + extended 3. `contactObservations (+4)` and `staminaObservation (+1)` are OFF for every shipped brain. |
| `continuous_actions` | output | `[batch, 13]` | Sampled from the Gaussian policy mean head. |
| `deterministic_continuous_actions` | output | `[batch, 13]` | Argmax / mean of the policy — used when `Deterministic` is requested. |
| `value` | output | `[batch, 1]` | Critic head scalar. |
| `version_number` | output | `[1]` | ML-Agents internal API version sentinel. |
| `memory_size` | output | `[1]` | LSTM cell state size; 0 because the policy has no recurrent core. |

### Why all four files are bit-identical in size

The trunk and value head share topology, so the only bytes that vary across
fighters are the values of the 14 weight tensors. The roster was deployed
together on 2026-07-31 — any fighter whose shipped `.onnx` has been
superseded will show an older mtime than the others.

---

## Matrix B — Component grid (rig + behavioral purpose)

> The 2D ragdoll is built once from `Agent_BipedBody.PART_DEFS` (16 parts) and
> `JOINT_DEFS` (15 hinge motors — 13 powered, 2 unpowered toe MTP). Personality
> comes from the per-fighter character asset's build scales and reward shaping
> coefficients. The "Rig component" column therefore lists the per-part anatomy
> that all four fighters share; the "Functional description" describes that
> part's mechanical role in the ragdoll; the "Behavioral purpose" describes how
> each fighter's coefficients USE that part differently.

### B.1 Parts (the rig — identical across all four fighters)

| # | Rig part (Rigidbody2D) | Mass fraction | Functional description | Behavioral purpose — MATT | STANDARD | NICK | KIM |
|---|---|---:|---|---|---|---|---|
| 0 | Pelvis | 11 / 69.6 kg | Root mass; ring-out tracking reads its X. `Torso.position.y` for fall height. | Highest torso impact reward (0.015 cap 8). | Reference. | Reward-hacks torso position via cheap lunge. | Planted deep — knees/hips keep it low (0.0008 each). |
| 1 | ThighNear | 7.0 | Hip flexor / extensor; carries most of the driving mass. | `massScale 1.0` — baseline leg. | `massScale 1.0`. | `massScale 0.72` — light, dances. | `massScale 1.45` — heavy, drives forward. |
| 2 | ShinNear | 3.5 | Knee extensor; lunge's torque-limited actuator. | Knee torque 250 N·m, range 0..150°. | Same. | Same. | Same — but cadence reward 0.0006 keeps the knee planted. |
| 3 | FootNear (foot, unpowered MTP child) | 0.8 + 0.2 (toe) | Sole, friction material (μ = 0.9). Tracked by `Sensor_BodyPartContact`. | Both feet load required for the new drive reward (0 on ship). | Default. | Looser (`straightLegEarnFraction 0.75`). | Tight (`straightLegEarnFraction 0.15`) — must crouch to earn. |
| 4 | ThighFar | 7.0 | Mirror of #1. | Mirror. | Mirror. | Mirror (lightweight). | Mirror (heavyweight). |
| 5 | ShinFar | 3.5 | Mirror of #2. | Mirror. | Mirror. | Mirror. | Mirror. |
| 6 | FootFar | 0.8 + 0.2 (toe) | Mirror of #3. | Mirror. | Mirror. | Mirror. | Mirror. |
| 7 | LowerBack | 7.0 | First spine segment; hinge to pelvis. | Torso torque 180 N·m; bending = lunge leverage. | Reference. | `straightLegEarnFraction 0.75` rewards staying flat. | `hipsLowReward 0.0008` rewards keeping it low. |
| 8 | UpperBack | 7.0 | Second spine segment. | Mirror. | Mirror. | Mirror. | Mirror. |
| 9 | Chest | 13 | Top segment; face art + lean sensor + head collider. | Chest angular velocity `×F_s / 500` is an obs slot. | Same. | Same. | Same — bigger chest on Kim (widthScale 1.30). |
| 10 | UArmNear | 2.5 | Shoulder; strike arm. | Shoulders still symmetric ±120° (τ_max 80). | Same. | Same — but high cadence reward (0.0032) flails these. | Same — lunge 0.0008 @ 1.8 m/s for big slow commits. |
| 11 | FArmNear | 1.8 | Elbow; forearm lever. | Asymmetric −150..0° (τ_max 60) after the sign-convention fix. | Same. | Same. | Same. |
| 12 | UArmFar | 2.5 | Mirror of #10. | Mirror. | Mirror. | Mirror. | Mirror. |
| 13 | FArmFar | 1.8 | Mirror of #11. | Mirror. | Mirror. | Mirror. | Mirror. |
| 14 | ToeNear (unpowered MTP) | 0.2 | Spring hinge; push-off. | Passive (motor disabled, range ±35°). | Passive. | Passive. | Passive. |
| 15 | ToeFar (unpowered MTP) | 0.2 | Mirror. | Mirror. | Mirror. | Mirror. | Mirror. |

> **Visual dressing** (`head` collider on chest, `Art` children on every
> part, one `CompositeShadowCaster2D` per wrestler) is identical across the
> roster and toggled by the same switches — see `Systems_ArenaLighting`.
> `castShadows = false` and `ENABLE_JIGGLE = false` are universal (the
> latter is a `private const` that makes any scene-serialized
> `enableJiggle: 1` values inert — same trick as
> `Systems_TournamentBracket.ARENA_SCENE`).

### B.2 Rig-level components (MonoBehaviour-level summary)

| Rig component | Type | Functional description | Behavioral purpose — how each fighter uses it |
|---|---|---|---|
| `Agent_Biped` | `Unity.MLAgents.Agent` (MonoBehaviour) | ML-Agents brain wrapper. Configures `BehaviorParameters`, `DecisionRequester`, observation and action spec in `Awake`. | Same skeleton, same observation count, same 13-action head — only the assigned `.onnx` changes. |
| `Agent_BipedBody` | `MonoBehaviour` (`[DefaultExecutionOrder(-400)]`) | Builds the 16-part ragdoll at runtime; applies motor commands; integrates fatigue; updates passives. | `massScale` × `widthScale` × `torqueScale` come from the character asset; everything else is identical. |
| `Agent_CharacterDefinition` | `ScriptableObject` (per-fighter asset) | Identity, build scales, brain generation flags, every sumo + walk reward shaping coefficient. | **This is the fighter.** Personality = data, never code. Adding a coefficient defaults it to the constant the code used before, so an untouched character trains exactly what it always did. |
| `Reward_SumoObjective` | Plain C# class | Per-step shaping inside a refereed bout. | Configured from the character asset at `Awake`; returns a `float`; cannot end an episode. |
| `Reward_WalkObjective` | Plain C# class | Per-step shaping for the walk lane. | Same — `walkForwardReward`, `walkStanceFloor`, `walkBendReward`, etc. |
| `Reward_StepCadence` | Plain C# class | Shared alternation bonus. | Owned by the agent (not duplicated into each provider) so the walk-in mode switch does not double-pay. |
| `Reward_Context` | `readonly struct`, passed `in` | 16-field snapshot handed to the providers. | Constructed once per agent per physics step — 500 allocations/s saved at 10 bipeds × 50 Hz. |

### B.3 Persona at a glance

| Fighter | Behavior name | Mass / Width / Torque | Tagline | Dominant reward coefficient | Known risk |
|---|---|---|---|---|---|
| Matt | `Matt` | 1.00 / 1.00 / 1.00 | Aggressive baseline | `impactReward 0.015 cap 8` | None — clean trainer, mean reward tracks ELO. |
| Standard | `Standard` | 1.00 / 1.00 / 1.00 | Neutral reference | Defaults (asset fields are null) | Scene's eight `character: {fileID: 0}` fields — currently inert because Standard's sheet is byte-identical to the code defaults. |
| Nick | `Nick` | 0.72 / 0.82 / 0.85 | Mobile lightweight, dances | `cadenceReward 0.0032`, `lungeBonus 0.0024 @ 1.0 m/s` | **Reward-hack prone.** Cut shaping before retraining; judge ELO on shape, never on mean reward. ELO reset on resume — judge level against a pool baseline of 1200, not against other fighters' absolute values. |
| Kim | `Kim` | 1.45 / 1.30 / 1.50 | Heavyweight anchor, does not chase | `kneeBendReward 0.0008`, `hipsLowReward 0.0008`, `impactReward 0.014 cap 10` | Manifest γ drift (0.99) vs YAML (0.997) — pick one and document the choice. |

---

## Cross-reference

- See [architecture.md](architecture.md) for the six mermaid diagrams
  (inference loop, tensor blueprint, reward tree, actuator map, hyperparameter
  matrix, episode lifecycle).
- See [creatures.html](creatures.html) for the full technical report (joint
  breakdown, vector sizes, per-character coefficients, training metrics).
- See [creatures_simple.html](creatures_simple.html) for the executive dashboard
  (status, progress bars, behavior evolution, health indicators).