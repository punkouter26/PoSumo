# PoSumo — ONNX & Component Grid

Generated 2026-07-27 from the live project. Every value is measured
(`os.stat`, `onnx.load`, trainer logs) — nothing here is inferred.

> **Substitution note.** The request asked for a "Prefab" column. PoSumo has **no
> creature prefabs**: `Agent_BipedBody.Awake()` constructs all 14 rigidbodies and 13
> joints at runtime from `PART_DEFS` / `JOINT_DEFS`. Scenes contain manager objects only.
> The Prefab column is therefore replaced by **Character Asset**, the ScriptableObject
> that actually carries a fighter's identity, physique and brain references.

---

## Matrix A — Model Metadata

Every brain shares one architecture: **44 → 512×3 → 13**, 555,125 parameters, 2.12 MB.
`memory_size = 0` (feed-forward, no recurrence).

### Fight brains

| Character Asset | ONNX | Size | mtime | Age | I/O Tensors | Params | Run ID | Final Reward | Promotion |
|---|---|---|---|---|---|---|---|---|---|
| `Matt_Character.asset` | `Matt_v01/Matt.onnx` | 2.12 MB | 2026-07-27 20:03 | 2.9 h | `obs_0[B,44]` → `continuous_actions[B,13]` | 555,125 | **matt_sumo07** @ 250k | 24.44 (live) | ⚠️ **Interim** — hand-deployed 250k checkpoint for A/B test |
| `Standard_Character.asset` | `Standard_v01/Standard.onnx` | 2.12 MB | 2026-07-27 02:56 | 20.0 h | same | 555,125 | standard_sumo02 | 13.89 | 🔴 **Stale** — pre-realism body |
| `Nick_Character.asset` | `Nick_v01/Nick.onnx` | 2.12 MB | 2026-07-27 06:41 | 16.3 h | same | 555,125 | nick_sumo04 | 34.57 | 🔴 **Stale** — pre-realism body |
| `Kim_Character.asset` | `Kim_v01/Kim.onnx` | 2.12 MB | 2026-07-27 05:39 | 17.3 h | same | 555,125 | kim_sumo02 | 18.58 | 🔴 **Stale** — pre-realism body |

### Walk brains

| Character Asset | ONNX | Size | mtime | Age | Params | Run ID | Final Reward | Promotion |
|---|---|---|---|---|---|---|---|---|
| `Matt_Character.asset` | `Matt_v01/MattWalk.onnx` | 2.12 MB | 2026-07-26 20:04 | 26.9 h | 555,125 | matt_walk02 | 4.15 | 🔴 Stale — superseded by `matt_walk03` |
| `Standard_Character.asset` | `Standard_v01/StandardWalk.onnx` | 2.12 MB | — | — | 555,125 | standard_walk01 | 3.56 | 🔴 Stale — superseded by `standard_walk02` |
| `Nick_Character.asset` | `Nick_v01/NickWalk.onnx` | 2.12 MB | 2026-07-27 00:21 | 22.6 h | 555,125 | nick_walk02 | 3.56 | 🔴 Stale — superseded by `nick_walk03` |
| `Kim_Character.asset` | `Kim_v01/KimWalk.onnx` | 2.12 MB | 2026-07-27 00:21 | 22.6 h | 555,125 | kim_walk02 | 3.59 | 🔴 Stale — superseded by `kim_walk03` |

### Why every deployed brain is stale

Not drift — a deliberate, documented invalidation. Five things changed under the
policies in one pass:

1. Ring half-width **2.75 → 5.5 m** (edge distance is a normalised observation)
2. Segment lengths re-derived to Winter — limbs were 8–18% short
3. Joint ROM clamped to human total range
4. Upper-body torque cut to human peak
5. Passive joint resistance + real body damping added

Plus two new shaping terms (`effortPenalty`, `stanceReward`). The corrective runs below
are the replacements.

### Replacement runs (in flight)

| Run | Trunk | Target | Progress | Reward | ELO Δ |
|---|---|---|---|---|---|
| `matt_sumo07` | matt_sumo06 | 6.0M | 1.08M (18%) | 24.44 | +3.23 |
| `standard_sumo03` | standard_sumo02 | 6.0M | 1.08M (18%) | 18.42 | +3.49 |
| `nick_sumo05` | nick_sumo04 | 6.0M | 1.08M (18%) | 31.45 | +2.97 |
| `kim_sumo03` | kim_sumo02 | 6.0M | 1.08M (18%) | 25.15 | +1.50 |
| `matt_walk03` | matt_walk02 | 2.5M | **2.50M ✅** | 4.06 | n/a |
| `standard_walk02` | standard_walk01 | 2.5M | **2.50M ✅** | 3.53 | n/a |
| `nick_walk03` | nick_walk02 | 2.5M | 2.46M (98%) | 3.49 | n/a |
| `kim_walk03` | kim_walk02 | 2.5M | **2.50M ✅** | 3.52 | n/a |

**Judge the sumo runs on ELO, not reward.** The two new shaping terms changed the reward
scale, so `matt_sumo07`'s 24.44 is not comparable to `matt_sumo06`'s 28.02. All four ELO
deltas are positive against a pool that is itself retraining, which is the healthy signal.

**Walk rewards are directly comparable** — the walk terminals (fall −1, graduation +3) are
hardcoded precisely so runs stay on one scale. Matt at 4.06 vs his trunk's 4.15 means the
gait re-timed onto legs 8 cm longer at ~98% of the old quality.

---

## Matrix B — Component Grid

The rig is identical for all four fighters; only `massScale` / `widthScale` /
`torqueScale` differ (all 1.0 today — Kim and Nick are differentiated by reward shaping,
not physique).

### Rigidbody segments — 14 parts, 69.6 kg

| Component | Mass | Dimensions (m) | Functional Description | Behavioural Purpose |
|---|---|---|---|---|
| **Pelvis** | 11.0 kg | 0.32 × 0.18 | Root of the kinematic chain; parent of both hips and spine-1 | Carries the body's balance reference; `HipsLowFactor` reads its height for the crouch reward |
| **ThighNear / Far** | 7.0 kg ea | 0.14 × 0.431 | Hip→knee, Winter 0.245H | Primary drive segment for closing distance and resisting a shove |
| **ShinNear / Far** | 3.5 kg ea | 0.11 × 0.433 | Knee→ankle, Winter 0.246H | Stride length and ground clearance; lengthened 0.38→0.433 in the realism pass |
| **FootNear / Far** | 1.0 kg ea | 0.268 × 0.08 | Sole, μ 0.9 physics material | Only contact with the clay; a foot below `footOffMatY` (−0.06) **loses the round** |
| **LowerBack** | 7.0 kg | 0.30 × 0.14 | Spine segment 1→2 | Lets the torso pitch into a shove without the whole body rotating |
| **UpperBack** | 7.0 kg | 0.31 × 0.14 | Spine segment 2→3 | Second bending stage; contributes to the 4-segment articulated spine |
| **Chest** | 13.0 kg | 0.34 × 0.18 | Heaviest segment; **head is a compound collider on this body**, 6 kg folded in | Uprightness reference (`Chest.transform.up`); carries the face art and the KO hitbox |
| **UArmNear / Far** | 2.5 kg ea | 0.10 × 0.327 | Shoulder→elbow, Winter 0.186H | Reach for the initial engagement; ~28% heavy vs Winter on purpose |
| **FArmNear / Far** | 1.8 kg ea | 0.09 × 0.28 | Elbow→hand | Grapple contact surface |

### Joints — 13 motorised hinges

| Joint | Range | Torque | Speed | Functional Description | Behavioural Purpose |
|---|---|---|---|---|---|
| **Hip** ×2 | −30…120° | 300 N·m | 400°/s | Anatomically correct incl. extension stop | Main power for driving forward; gated by `bendGate` so straight-legged charges earn less |
| **Knee** ×2 | −150…0° | 250 N·m | 500°/s | Correct flexion, **no hyperextension** | Depth of the sumo crouch; `KneeBendFactor` feeds three separate rewards |
| **Ankle** ×2 | ±25° | 120 N·m | 400°/s | Clamped from ±45 in the realism pass | Fine balance; the last thing keeping a foot on the mat |
| **Spine** ×3 | ±20° ea | 180 N·m | 250°/s | Clamped from ±25; torque cut from 400 | Torso pitch. Was 2–4× human and let the upper body catapult the whole fighter |
| **Shoulder** ×2 | ±120° | 80 N·m | 500°/s | Clamped from ±160; torque from 150 | Arm placement for the initial clash |
| **Elbow** ×2 | 0…150° | 60 N·m | 500°/s | Correct, no hyperextension | Grapple leverage |

Every joint additionally carries **passive resistance** — a restoring torque of 6% of its
own budget per 90° plus 10% per 400°/s, applied in `FixedUpdate`. `HingeJoint2D` has no
spring (3D-only feature), hence the explicit torque.

### Non-rig components

| Component | Functional Description | Behavioural Purpose |
|---|---|---|
| `Agent_Biped` | ML-Agents `Agent`: 44 obs, 13 continuous actions, 3 modes (Walk / Recover / Sumo) | The brain contract. Observations loop over `ActionCount`, so action count and obs size are coupled |
| `Agent_BipedBody` | Builds the ragdoll; owns `ApplyMotor`, `GoLimp`, `ResetPose`, passive torque | All physique is data (`PART_DEFS`), not code |
| `Agent_CharacterDefinition` | Per-fighter identity, physique scales, brain refs, **every shaping coefficient** | Personality lives here, never in `Agent_Biped` |
| `Sensor_Impact` | Per-part collision reporter | Feeds impact reward and `Systems_BodyDamage` |
| `Systems_BodyDamage` | Bruise/blood decals, head-KO detection, knockback | KO at ≥7.5 m/s head impact; 4.5 m/s mass-scaled knockback |
| `Systems_MatchRoster` | `[DefaultExecutionOrder(-500)]` — assigns characters before `Awake` | Runs before the body builds, or the ragdoll is the wrong size |

---

## Pruning assessment

**Nothing was pruned.** All 8 deployed `.onnx` files are stale, but they are the *only*
working brains until the corrective runs land — deleting them would leave the game
unplayable. The correct sequence is deploy-then-replace, not prune-then-deploy.

Housekeeping already done: the 8 superseded trunk runs were moved out of the TensorBoard
logdir into `Training/trunks/` (16 runs → 8), per CLAUDE.md's curated-logdir rule. They
were **moved, not deleted** — they remain valid `--initialize-from` sources and still back
the currently-deployed brains.

The 8 live runs retain their numbered per-step checkpoints (~140 MB/run of prunable
weight). Pruning those is safe only once a run has exited, since it otherwise races the
trainer's writer.
