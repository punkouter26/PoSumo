# PoSumo - ONNX & Rig Model Inventory

> Last revised: **2026-08-16**
> Engine: Unity **6000.5.8f1** | ML-Agents **4.1.0** | InferenceEngine **2.6.1**
> Backed by `Assets/Agents/<Name>_v01/<Name>.onnx` and the per-fighter `MANIFEST.md`.

This document is the canonical inventory of every shipped brain and the ragdoll it drives. Two matrices:

* **Matrix A** - the brains themselves: file path, size, I/O tensor shapes, parameter counts, source run-id, observed reward / ELO, and promotion status.
* **Matrix B** - the rig each brain controls: rig components (`HingeJoint2D` / compound child collider), mass / torque layout, degrees of freedom, and behavioural purpose.

---

## Matrix A - Models

| Fighter | ONNX file | File size | Input tensor | Output tensor | Initializers | Params (approx) | Source run-id | Final mean reward (fight) | Self-play ELO (last decile) | Promotion status |
|---|---|---:|---|---|---:|---:|---|---:|---:|---|
| **Matt** | `Assets/Agents/Matt_v01/Matt.onnx` | 2.13 MB | `obs_0` shape `[batch, 45]` | `continuous_actions` shape `[batch, 13]` (+ `version_number[1]`, `memory_size[1]`, `continuous_action_output_shape[1]`, `deterministic_continuous_actions[b,13]`) | 14 | ~445k | `matt_unified02` cold (15.0M) | ~36 | ~1140 | **Production** |
| **Standard** | `Assets/Agents/Standard_v01/Standard.onnx` | 2.13 MB | `obs_0` shape `[batch, 45]` | same shape set | 14 | ~445k | `standard_unified01` cold (15.0M) | ~31 | ~1080 | **Production** |
| **Nick** | `Assets/Agents/Nick_v01/Nick.onnx` | 2.13 MB | `obs_0` shape `[batch, 45]` | same shape set | 14 | ~445k | `nick_unified01` cold 3.75M + resumed to 15.0M | ~33 | ~1115 | **Production** |
| **Kim** | `Assets/Agents/Kim_v01/Kim.onnx` | 2.13 MB | `obs_0` shape `[batch, 45]` | same shape set | 14 | ~445k | `kim_unified01` cold (15.0M) | ~30 | ~1050 | **Production** |
| **Bot** | _none_ (deliberately brainless) | 0 | n/a | n/a | 0 | 0 | n/a | n/a | n/a | **Bot / no brain** |

**Shared trunk** (every shipped `.onnx`):

```
Input  obs_0  [batch, 45]
   -->  Dense(512) + ReLU + L2 normalize
   -->  Dense(512) + ReLU
   -->  Dense(512) + ReLU
   -->  Linear(13)
Output  continuous_actions  [batch, 13]   (sampled; deterministic mode returns the mean)
```

* All four use `InferenceDevice = Burst` and run via the Unity InferenceEngine (Sentis) runtime.
* Initializers include running-mean/var for the input normalizer, 4 weight matrices, 4 bias vectors, and the corresponding optimizer state - hence the 14 initializer count versus the 6 weight+bias pairs of the visible trunk.
* ELO numbers are read from TensorBoard **last decile** (last 10% of training steps), not the absolute peak. Mean reward is the fight-run reward only; the unified policy mixes two schools and mean reward is not comparable across runs.
* **Nick's ELO is not directly comparable to the others** because his run was interrupted at 3.75M and resumed with a fresh opponent pool; judge him on curve shape (clean monotonic non-decreasing, max drawdown 586 from running peak), not the absolute level.

---

## Matrix B - Physics & Actuators (rig inventory)

The rig is the same 14-part biped for every fighter - what differs is **scale and shaping**. Every cell in the "Component / Drive mode / DOF" column is identical across fighters; the "Build scales" and "Behavioural purpose" columns carry the divergence.

| Fighter | Behavioural purpose | Rig component | Drive mode | DOF (powered) | Mass (kg) | Build scales (mass / width / torque) | Standing height | Notes |
|---|---|---|---|---:|---:|---|---|---|
| **Matt** | Aggressive lightweight baseline. Drives forward, hits hard. | `Agent_BipedBody` (custom runtime build) - 14 `Rigidbody2D` + 16 `HingeJoint2D` + 12 `CapsuleCollider2D` + 4 `BoxCollider2D` + 1 `CircleCollider2D` (head compound on Chest) | PD per `HingeJoint2D` (motor with slew-limited target velocity) | 13 (all symmetric pairs except spine 3-of-3) | 69.6 | 1.00 / 1.00 / 1.00 | 1.76 m | Highest impact reward (0.015) and shortest lunge threshold (1.2 m/s). |
| **Standard** | Reference fighter; default shaping and default body. | identical to Matt | identical to Matt | 13 | 69.6 | 1.00 / 1.00 / 1.00 | 1.76 m | Code defaults match character sheet defaults byte-for-byte; "no character assigned" is harmless. |
| **Nick** | Light mobile perimeter fighter. Smallest body, highest cadence. | identical to Matt | identical to Matt | 13 | ~50 | 0.72 / 0.82 / 0.85 | ~1.51 m | Cadence 0.0032 (highest), `straightLegEarnFraction = 0.75` (not required to crouch). |
| **Kim** | Heavy planted anchor. Wins by lean and impact, not by chase. | identical to Matt | identical to Matt | 13 | ~101 | 1.45 / 1.30 / 1.50 | ~1.76 m (same joint heights, wider trunk) | `straightLegEarnFraction = 0.15` (must be deep). Short-horizon PPO (gamma 0.99). |
| **Bot** | Roster padding, deliberately brainless. | identical rig | `Agent_Bot` heuristic; motors cut on no-brain fallback | 13 | 69.6 | 1.00 / 1.00 / 1.00 | 1.76 m | Logs `character 'Bot' has no inferenceModel` at Error level on every match. |

### Joint inventory (all fighters share)

| # | Joint | Range (deg) | Torque (N m) | Speed (deg/s) | Reversal slew | Powered | Mirror |
|---:|---|---:|---:|---:|---|:---:|:---:|
| 0 / 3 | Hip near / far | -120 .. 30 | 300 | 260 | 0.12 s | yes | yes |
| 1 / 4 | Knee near / far | 0 .. 150 | 250 | 320 | 0.12 s | yes | yes |
| 2 / 5 | Ankle near / far | -35 .. 35 | 160 | 220 | 0.12 s | yes | yes |
| 6 / 7 / 8 | Spine 1 / 2 / 3 | -20 .. 20 | 180 | 160 | 0.12 s | yes | no |
| 9 / 11 | Shoulder near / far | -120 .. 120 | 80 | 320 | 0.12 s | yes | yes |
| 10 / 12 | Elbow near / far | -150 .. 0 | 60 | 320 | 0.12 s | yes | yes |
| 14 / 15 | Toe MTP near / far | -35 .. 35 | 40 | 400 | n/a | no | yes |

### Muscle / fatigue model (all fighters share)

| Parameter | Value | Meaning |
|---|---:|---|
| `HILL_A_F0` | 0.25 | Hill force-velocity factor - concentric torque falls off with shortening speed. |
| `ECCENTRIC_GAIN` | 1.5 | Eccentric bracing multiplier. |
| `ACT_RISE_TAU` | 0.05 s | Activation rise time constant. |
| `ACT_FALL_TAU` | 0.07 s | Activation fall time constant (asymmetric). |
| `MOTOR_REVERSAL_SECONDS` | 0.12 s | Slew limit on motor target velocity. |
| `FATIGUE_RATE` | 0.06 / s | Per-joint fatigue accumulation rate. |
| `RECOVERY_RATE` | 0.10 / s | Per-joint fatigue recovery rate (loaded recovery). |
| `FATIGUE_DEPTH` | 0.35 | Max share of torque budget that fatigue can take. |
| `PASSIVE_STIFFNESS_FRAC` | 0.06 | Ligament / antagonist torque at 90 deg off neutral. |
| `PASSIVE_DAMPING_FRAC` | 0.10 | Per 400 deg/s of joint damping. |
| `END_RANGE_KNEE` | 0.7 | Fraction of half-range where stiffening kicks in. |
| `END_RANGE_GAIN` | 6 | Multiple of linear stiffness at the very stop. |

### Sensors (all fighters share)

| Sensor | What it reads | Output slot |
|---|---|---|
| Body-state sensors | `Rigidbody2D` velocity, `transform.up` uprightness, world Y, root mass | obs[0..4] |
| Joint sensors | `HingeJoint2D.jointAngle` + slew-limited speed | obs[5..30] |
| `Sensor_BodyPartContact` x 4 | Foot ground contact + normalized load | obs[31..34] |
| Task flag | 1 = sumo, 0 = walk | obs[35] |
| Opponent / walk target | relative position + velocity | obs[36..39] |
| Edge distance | nearX / farX vs `ringHalfWidth` | obs[40..41] |
| Extended (opt-in) | opponent uprightness, down flag, edge distance | obs[42..44] |

### Reward shaping (per fighter)

The reward tree and shared penalty terms live in `Reward_SumoObjective` and `Reward_WalkObjective`. Per-fighter coefficients are read from the character asset. Default code values are the constants the project used before shaping became per-character, so an unassigned character trains exactly what the un-tuned code would have.

| Coefficient | Matt | Standard (default) | Nick | Kim | Code default | Notes |
|---|---:|---:|---:|---:|---:|---|
| `uprightReward` | 0.0005 | 0.0005 | 0.0005 | 0.0005 | 0.0005 | shared |
| `closingReward` | **0.0009** | 0.0006 | **0.0011** | 0.0004 | 0.0006 | sumo; higher = chases more |
| `lungeBonus` | 0.0016 | 0.001 | 0.0024 | 0.0008 | 0.001 | sumo |
| `lungeThreshold` | 1.2 m/s | 1.5 m/s | 1.0 m/s | 1.8 m/s | 1.5 | sumo |
| `impactReward` | **0.015** | 0.010 | 0.011 | **0.014** | 0.010 | sumo; the high-leverage farm |
| `impactCap` | 8 | 8 | 8 | **10** | 8 | sumo |
| `kneeBendReward` | 0.0004 | 0.0004 | 0.0002 | **0.0008** | 0.0004 | sumo |
| `hipsLowReward` | 0.0003 | 0.0003 | 0.0001 | **0.0008** | 0.0003 | sumo |
| `stanceReward` | 0.0009 | 0.0009 | 0.0009 | 0.0009 | 0.0009 | sumo |
| `cadenceReward` | 0.0015 | 0.0015 | **0.0032** | 0.0006 | 0.0015 | sumo; the most farmable |
| `effortPenalty` | 0.0015 | 0.0015 | 0.0015 | 0.0015 | 0.0015 | shared |
| `energyPenalty` | 0.0004 | 0.0004 | 0.0004 | 0.0004 | 0.0004 | shared |
| `jerkPenalty` | 0.0003 | 0.0003 | 0.0003 | 0.0003 | 0.0003 | shared |
| `straightLegEarnFraction` | 0.30 | 0.30 | **0.75** | **0.15** | 0.30 | sumo gate on closing terms |
| `forwardReward` (walk) | 0.0075 | 0.004 | 0.005 | 0.004 | 0.004 | walk |
| `stallPenalty` (walk) | 0.0018 | 0.0008 | 0.0012 | 0.001 | 0.0008 | walk |

(Bold values are the per-character deltas from the default.)

---

## Promotion rules

A brain moves from **Staging** to **Production** when:

1. The TensorBoard ELO curve is **monotonic non-decreasing** across the last 4 deciles, OR oscillates within +/- 100 ELO of its peak.
2. `MatchTestHarness.Run(10)` reports a `HARNESS RESULT:` line that does not regress by more than 2 wins against the same opponent pool.
3. The build (`<Name>Env.exe`) was produced **after** the `.onnx` was finalised (BuildOptions.Development).
4. The corresponding `<Name>_Character.asset` has `inferenceModel` repointed at the deployed `.onnx`.

A brain is demoted to **Staging** when:

* A new round of physics / observation / action changes invalidates its input or output shape (e.g. the 44 -> 45 task-flag change).
* An opt-in observation slot is turned on (`contactObservations`, `staminaObservation`) without retraining.
* A hyperparameter of `network_settings` changes (e.g. `hidden_units`, `num_layers`) and the policy is not retrained on the new shape.

---

## Telemetry & verification

| Channel | Address | Content |
|---|---|---|
| TensorBoard | `http://127.0.0.1:6006` | ELO, mean reward, episode length, value/policy/entropy loss, per-fighter stamina. |
| HTTP telemetry | `http://127.0.0.1:8787+/metrics` (envs built **after** 2026-08-07) | Live JSON snapshot of every fighter: position, velocity, edge distance, stamina, round state. |
| Console | `[MATCH]`, `[ROUND]`, `HARNESS RESULT:`, `BUILD RESULT:`, `DEPLOY RESULT:` | Markers the harness, build tools, and deployment tools print and that the verification flow reads back. |

### Verification commands

```powershell
# Confirm the four shipped brains round-trip in the InferenceEngine.
python Tools/unity.py ping

# Drive a full bracket (3 minutes per match, 4 matches = 12 minutes)
python Tools/unity.py scene SCN_TOURNAMENT
python Tools/unity.py play

# Behavioural test (chains N matches unattended)
python Tools/unity.py exec 'return PoSumo.MatchTestHarness.Run(10);'

# Re-deploy a freshly-trained brain (overwrites the .onnx in place, preserves .meta GUID)
# *PoSumo -> Deploy <Name> Brain*

# Live env metrics
curl http://127.0.0.1:8787/metrics
```

---

## Source files

* `Assets/Agents/<Name>_v01/<Name>.onnx` - the shipped brain
* `Assets/Agents/<Name>_v01/<Name>_Character.asset` - the source-of-truth per-character sheet
* `Assets/Agents/<Name>_v01/MANIFEST.md` - the per-fighter changelog (this file draws heavily on it)
* `Assets/Scripts/Agent/Agent_Biped.cs` - the brain contract: `ActionCount = 13`, `ObservationCount = 42` (45 with `extendedObservations`)
* `Assets/Scripts/Agent/Agent_BipedBody.cs` - the rig: `PART_DEFS` (16 parts), `JOINT_DEFS` (16 joints, 13 powered)
* `Assets/Scripts/Reward/Reward_SumoObjective.cs` - per-character shaping coefficients for the sumo school
* `Assets/Scripts/Reward/Reward_WalkObjective.cs` - per-character shaping coefficients for the walk school
* `Training/configs/<Name>UnifiedNN.yaml` - the PPO config that produced the shipped brain
* `Training/results/<run-id>/` - gitignored, present only after a local retrain; contains checkpoints, TFEvents, and final `<Behavior>.onnx`