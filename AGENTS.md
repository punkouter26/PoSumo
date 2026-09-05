# AGENTS.md — Biomechanical Physics Simulation

Agent instructions for this repository. **Domain: biologically plausible terrestrial
motion under 1G.** Active ragdolls, bipedal humanoid locomotion, and quadruped/creature
motor control trained with Unity ML-Agents.

Read `CLAUDE.md` for what this project *is*; read this file for how motion must be
built, rewarded and judged. Where the two disagree, `CLAUDE.md` wins. Where either
disagrees with the code, **the code wins**.

Process and convention rules — master-only branching, reading `DOCS/` for the project
overview, TensorBoard-with-every-run, pruning obsolete behaviours from the logdir before a
launch, the RED-heuristic-bot / GREEN-reference-RL colour convention, the
coded-bot + reference-bot + custom-bots cast, and the TL;DR-on-long-answers rule — live in
**`CLAUDE.md` → *Standing working agreements***. They apply here too.

---

## 0. Read this before writing a joint

**This project is 2D.** The shipped biped is a 14-part `Rigidbody2D` ragdoll driven by
**13 powered `HingeJoint2D` motors** built in code by `Agent_BipedBody.Awake()` from
`PART_DEFS` / `JOINT_DEFS`. There is **no `ConfigurableJoint` anywhere in
`Assets/Scripts/`** — that is a 3D-only type and does not apply to the existing rig.

| Rig | Joint | Drive |
|---|---|---|
| Existing 2D biped (`Agent_BipedBody`) | `HingeJoint2D` + `JointMotor2D` | `motorSpeed` target + `maxMotorTorque` budget |
| Any NEW 3D humanoid or quadruped rig | `ConfigurableJoint` | `targetRotation` / `targetAngularVelocity` + PD springs |

The **principles below apply to both**. Only the API differs. Do not port
`ConfigurableJoint` code into the 2D biped, and do not use raw `AddTorque` on a 3D rig
where a PD-driven joint is the correct tool.

---

## 1. Project Domain & Biomechanical Objectives

**Core objective:** natural, energy-efficient, grounded motion that mimics real biology.

Every change is judged against the elimination of these failure modes:

| Failure mode | What it looks like | Where it is fought |
|---|---|---|
| **Skating** | feet slide while "walking"; translation with no stance phase | `Reward_StepCadence` (`PLANTED_SPEED` 0.5 m/s), foot `PhysicsMaterial2D` friction 0.9 |
| **Micro-jitter / twitching** | high-frequency action chatter, buzzing limbs | `jerkPenalty`, activation lag in `ApplyMotor`, `MIN_STEP_INTERVAL` 0.25 s |
| **Stiff spine** | torso as one rigid block | 4-segment articulated spine (pelvis→lowerback→upperback→chest), ±20° each |
| **Torque exploitation** | superhuman leverage, motors railed at max | per-joint torque budgets, Hill force-velocity term, quadratic `effortPenalty` |
| **Crouch/drag degeneracy** | policy discovers the floor is cheaper than standing | see `CLAUDE.md` "the fighters crawl during the walk-in" — **five retrains have already failed here; read it before proposing a sixth** |

**Tech stack:** Unity 6000.5.8f1 (C#, 2D URP) + ML-Agents 4.1.0 (local patched package)
+ Python `mlagents` 1.2.0.dev0 (editable) + PyTorch 2.5.1 (pinned; 2.6+ breaks ONNX
export).

---

## 2. C# Simulation & Biomechanical Constraints

### 2.1 Joint control — never touch `transform`

**Never modify `transform.position` or `transform.rotation` on an active physics body
during simulation.** Motion comes out of the solver, not out of a transform write.
Teleporting a body invents momentum the integrator never accounted for and silently
corrupts every contact and every velocity-derived observation.

Legal exceptions, and only these: `Agent_BipedBody.ResetPose()` between episodes, and
the ceremonial `PoseNeutral` freeze in `Systems_GameMatchManager`'s intro phase. Both
run while the body is not being stepped by a policy.

- **2D (existing rig):** write `JointMotor2D.motorSpeed` (the velocity target) and
  `maxMotorTorque` (the force budget) through `Agent_BipedBody.ApplyMotor`. An
  un-driven motor is a **brake**, not a free joint — `useMotor = true` with
  `motorSpeed = 0` actively holds a joint still. Turning `useMotor` off is how you get
  a limp body, and `RestoreMotors()` is how you get it back.
- **3D (new creature rigs):** drive `ConfigurableJoint.targetRotation` /
  `targetAngularVelocity` with explicit PD springs
  (`JointDrive.positionSpring` / `positionDamper` / `maximumForce`). Set
  `rotationDriveMode` deliberately; never leave `maximumForce` at `Infinity`.

### 2.2 Muscle & torque limits

Torque budgets are **physiological, per joint, and scale with segment mass** — not a
tuning free-for-all. The shipped 2D values, in N·m before `torqueScale`:

```
hip 300   knee 250   ankle 120   spine 180 (each of 3)   shoulder 80   elbow 60
```

Rules:

- **No infinite-force motors.** `maximumForce = Mathf.Infinity` (3D) or an unbounded
  `maxMotorTorque` (2D) produces superhuman leverage and violent twitching. Every motor
  carries a finite budget.
- **Force-velocity (Hill).** Concentric torque falls off with contraction speed;
  eccentric bracing keeps its 1.5x gain. Implemented in `ApplyMotor`.
- **Fatigue.** Each joint carries a 0..1 fatigue state integrated at 50 Hz
  (`IntegrateFatigue`, the two-state reduction of Xia's three-compartment muscle model:
  `FATIGUE_RATE` 0.06/s, `RECOVERY_RATE` 0.10/s, `FATIGUE_DEPTH` 0.35).
  **Load is read from `joint.GetMotorTorque(dt)`, never from the action vector** —
  isometric bracing is a near-zero action holding a near-maximum torque, and any
  action-derived measure would score the most expensive thing in a bout as resting.
- **Activation lag.** Torque *and* the velocity target are both smoothed. Smoothing only
  the torque while writing `motorSpeed` straight from the action leaves a perfectly
  crisp velocity command, and the jitter comes back through the other door.
- **Passive resistance.** Real joints are not frictionless linkages: a restoring torque
  at 6% of each joint's motor budget per 90°, plus 10% per 400°/s of damping, applied
  every `FixedUpdate`. (`HingeJoint2D` has no spring — that is 3D-only — hence the
  explicit torque.) Bodies damp at 0.25 linear / 0.8 angular.

### 2.3 Anatomical limits & action normalization

- Actions are **13 continuous values in [−1, 1]** (`Agent_Biped.ActionCount`), mapped
  into each joint's own biological range. Never let an action index outrun the joint
  table: `ApplyMotor` indexes `0..ActionCount-1`, so the 2 unpowered toe (MTP) hinges
  **must stay last** in `JOINT_DEFS`.
- **Anchors derive from segment lengths.** Change a segment length and every anchor
  above it moves with it, or the chain comes apart. Verify by measuring anchor
  separation across all joints — it must read 0.0000 m.
- **Verify joint ranges parent-local, never in world space.** With gravity off the whole
  body counter-rotates to conserve angular momentum, so a world-space test reads the
  body's drift, not the joint. Use
  `parent.transform.InverseTransformPoint(child.position)` at rest and at full flexion
  and compare the delta — that is rotation-invariant.
- **The sign convention is measured, not guessed.** `HingeJoint2D.jointAngle` here is the
  **negative** of the child's geometric rotation relative to its parent. Ranges written
  as if geometric bend the limb *backwards* — this shipped a bird leg for the whole
  early life of the project. Current: hip (−120…30°), knee (0…150°), elbow (−150…0°),
  ankle (±25°), spine (±20° each), shoulder (±120°).
  `Agent_Biped.KneeBendFactor()` reads the knee as positive and must move with these.

### 2.4 Energy & effort penalties

Three distinct per-step costs, all exposed on `Agent_CharacterDefinition` and consumed
through `Reward_Context`:

| Field | Reads | Purpose |
|---|---|---|
| `effortPenalty` (0.0015) | `ctx.Effort` — **sum of tau^2, ungated** | bites hardest at the rails; the anti-flail term |
| `energyPenalty` (0.0004) | `ctx.Energy`, gated by `(1 - useful)` | charges effort only when it is not driving toward the objective |
| `jerkPenalty` (0.0003) | `ctx.Jerk` — sum of abs(delta a) | suppresses micro-jitter; yields follow-through and smooth swing/stance |

`walkEnergyPenalty` (0.0003) is the walk-school equivalent.

**The gated/ungated pair is deliberate.** `energyPenalty` alone switches off whenever the
fighter is moving fast, which made full-power flailing free mid-charge and left 7–12 of
13 motors railed. `effortPenalty` is quadratic and always applies. Do not collapse them
into one term.

### 2.5 Ground interaction & anti-skating

- **Real friction materials.** `PhysicsMaterial2D` — feet 0.9, body 0.4, bounciness 0 on
  both (`Agent_BipedBody`, ~L554). Surface friction is domain-randomized per round by
  `Systems_SumoMatchManager`; the rim `tawara` band is deliberately low-friction.
- **Stance tracking is height AND speed, and both are arena-relative.**
  `Reward_StepCadence` counts a foot planted below `PLANTED_HEIGHT` 0.12 m **relative to
  the arena ground**, moving slower than `PLANTED_SPEED` 0.5 m/s, and pays only on an
  alternation of the single planted foot, with `MIN_STEP_INTERVAL` 0.25 s between paid
  steps. Measured against absolute world Y this term was meaningless for the walk lane at
  y = −60 — every foot was trivially "below" threshold, plantedness collapsed to the
  velocity gate alone, and the airborne apex of a swing scored as a step, while the same
  line behaved correctly in the sumo lane at y = 0. **Any new contact or height test must
  be arena-relative.**
- Per-foot contact and normal force come from the feet's own contact lists into a
  preallocated `ContactPoint2D[]` (`_contactBuf`, size 8) — **no allocation in
  `FixedUpdate`**. Non-foot parts report ground contact through `Sensor_BodyPartContact`.
- **Penalize relative linear velocity at a contacting foot**, not merely "foot is
  moving". A planted foot sliding under load is the skate; a swing foot moving fast is
  correct gait.

### 2.6 Upright posture & centre of mass

- **Never freeze `Rigidbody` rotation axes** to keep a body upright, and never add an
  invisible stabilising torque the policy cannot observe. Balance is an outcome, not a
  constraint. Monitor CoM trajectory, pelvis alignment and ground reaction force, and
  *reward* them.
- Uprightness is shaped; falling is terminal (Walk school only). Trunk height is read
  arena-relative; standing pose is ~1.06 m for the 1.76 m / 69.6 kg baseline body.
- **Never zero a stabilising term to make room for a new one.** The `tall01` run set
  `walkBendReward` to 0 and the gait collapsed outright — the crouch was load-bearing for
  balance and nothing had replaced it yet.
- Gravity is Earth's −9.81 (project setting *and* re-asserted at runtime by
  `Systems_AcademyLifecycle`). Segment masses track Winter's anthropometric fractions;
  trust `Agent_BipedBody.TotalMass`, not prose.

### 2.7 Determinism

Actions apply **only in `FixedUpdate`**. Δt is locked at 0.02 s with fixed solver
iterations: a torque written on the render clock is a frame-rate-dependent force, and
training runs uncapped while a phone runs at 60 Hz. `Time.timeScale` is safe; a
quality-level solver override is not — it changes the dynamics every brain was fitted
against and raises no error.

---

## 3. Reward Engineering & Motion Shaping

### 3.1 Where reward lives

`Reward_SumoObjective` and `Reward_WalkObjective` are **plain C# classes** that hold the
per-character coefficients, take the body plus a `Reward_Context`, and **return a
float**. They hold no reference to the `Agent`, so a provider is *structurally incapable*
of calling `AddReward`, `SetReward` or `EndEpisode`. Keep it that way.

- **`Reward_Context` is a `readonly struct` passed by `in`.** One per agent per physics
  step; as a class, 10 bipeds at 50 Hz would be 500 heap allocations per second in the
  hottest path in the project.
- **Cross-school state is owned by the agent**, not duplicated per provider —
  `Reward_StepCadence` is shared because `BeginWalkIn` switches a fighter between Walk
  and Sumo mid-round, and two alternation histories would pay one step twice.
- Term **order is preserved deliberately**: these are small floats accumulated at 50 Hz.

### 3.2 Reward hierarchy

**Dense (`AddReward`, per step, inside a provider):**
forward velocity matching · root orientation alignment · torso upright posture ·
ground-contact gait cadence · energy / effort / jerk penalties · anti-skate penalty.

**Sparse / terminal (`SetReward`, in `Agent_Biped` only):**
catastrophic collapse (head or torso ground impact) · structural failure · target
reached or ring-out.

**Terminals stay in `Agent_Biped` and are not moving.** `SetReward(-1)` on a fall
*discards* that step's shaping outright, so the order of the terminal checks against the
`Evaluate` call above them is load-bearing.

### 3.3 The two shaping traps that have cost the most here

1. **A shaping term that saturates outside the range the policy occupies is not weak, it
   is ABSENT — and looks identical to weak from outside.** `WALK_TALL_Y` /
   `WALK_CROUCH_Y` were copied from the sumo school as 0.95/0.65 while the walking gait
   lives at 0.46–0.80, so the term was clamped to zero across nearly its entire operating
   range: the reward could not tell 0.60 from 0.55. Two full retrains were spent tuning
   the *strength* of something that was not connected. **Always check a new term's ramp
   against a measured distribution of the quantity it reads.** The same failure killed
   the first strike-impulse curve, which peaked at 11.9 m/s against measured strike
   speeds of 3.9–5.3 m/s.
2. **You cannot out-shape a terminal.** In `Mode.Walk` a fall is `SetReward(-1)` plus the
   forgone `+3` graduation, about **−4**; the whole tall-vs-crawl per-step shaping
   advantage was **0.0063**. Break-even is 0.16% extra fall probability per step. If a
   shaped behaviour is more fall-prone than that, the degenerate behaviour is *correct
   play* and no reasonable coefficient changes it.
   Corollary, learned from the `gait01` run: **attacking the terminal instead does not
   work either.** Cheap falls do not buy exploration, because **a body already on the
   ground cannot fall** — once the policy found the floor, the fall terminal simply
   stopped firing and there was no gradient left pointing up.

### 3.4 Reference trajectories (GAIL / BC)

If matching MoCap or reference clips, compute **delta pose errors** — joint angle offsets
and end-effector positions in the character's own root frame — never absolute global
coordinates. Absolute coordinates make the imitation reward a function of where the arena
happens to be, which breaks the moment an env is duplicated or a lane is offset (this
project's walk lane sits 60 m below the sumo lane for exactly that reason).

### 3.5 Episode reset hygiene

`OnEpisodeBegin()` must leave **no state leak**. `Agent_Biped.OnEpisodeBegin` →
`Agent_BipedBody.ResetPose()` must clear:

- linear and angular velocities on every body;
- **fatigue — BEFORE `RestoreMotors()`**, which scales the torque it writes back.
  Carrying fatigue across an episode boundary makes an episode's difficulty a function of
  how hard the *previous* one was fought: a hidden non-stationary term the agent cannot
  observe at t = 0;
- `_motorSpeedCmd[]`, the PD/velocity-target accumulator — `Array.Clear`, not a partial
  reset;
- contact states and any cadence or alternation history (`Reward_StepCadence.Reset()`);
- joint target rotations, back to the neutral pose.

Anything you add to the body that integrates over time gets a line in `ResetPose` in the
same commit. There is no second place to catch it.

### 3.6 Observations

`Agent_Biped.ObservationCount = 42`, or **45** with `extendedObservations` (the standard
for all four shipped fighters, decision period 3). Append order is fixed —
base → contact → stamina → extended — and **that order IS the input layer's layout**. It
must never change again once a brain has trained on it. All observations pass through
`San()` NaN/Inf sanitization. Obs count and decision period must match what the assigned
`.onnx` was trained with, or inference is **silently garbage** — no error, just a fighter
that twitches or stands still.

---

## 4. Build & Training Commands

### 4.1 Environment

```powershell
# venv is at Training/venv — NEVER `pip install --upgrade` in it.
Training\venv\Scripts\Activate.ps1
# New deps go in with -c Training/constraints.txt (pins torch/numpy/setuptools/protobuf/onnx).
```

### 4.2 Build a training env

*PoSumo → Build \<Name\> Training Env* → `Builds/<Name>Env/<Name>Env.exe`.
Built with `BuildOptions.Development`, which is required for telemetry and `Systems_Log`.

### 4.3 Train

Prefer the wrappers; they enforce `--base-port` and start TensorBoard exactly once.

| Script | Use for |
|---|---|
| `Training\Start-Training.ps1` | one run, foreground |
| `Training\Start-StaminaExtension.ps1` | 2+ concurrent runs; `-InitializeFromPhase` warm-starts a new run id |
| `Training\Run-GaitCampaign.ps1` | unattended multi-hour work, sequential batches (memory-bound: 4 fighters x 4 envs is ~17 GB) |

By hand:

```powershell
Training\venv\Scripts\mlagents-learn.exe Training/configs/<Name><Phase><NN>.yaml `
  --run-id=<name>_<phase><nn> --results-dir=Training/results `
  --env=Builds/<Name>Env/<Name>Env.exe --num-envs=4 --no-graphics --base-port=5005
```

**Flags:**

- `--resume` — parameter-only tweaks. A `--resume` into a config whose `max_steps`
  already equals the checkpoint's step count **exits immediately having trained nothing
  and looks exactly like a successful short run**.
- `--force` — physics, observation or action changes (or use a new run-id).
  **Kill TensorBoard first.** It holds Windows handles on the run dirs, and a `--force`
  fired while it is live leaves the old contents *in place*, silently — after which the
  surviving checkpoints outrank the new run numerically and a deploy ships a brain from
  the run you thought you deleted.
- `--base-port` — **always explicit.** The trainer takes `--num-envs` consecutive ports
  from there; two runs on the default 5005 collide and the second hangs on a handshake
  the first already answered. Space concurrent runs by at least `--num-envs`
  (5005, 5015, 5025 …).
- `--num-envs` — a CPU budget, 4–8 on a 12-core box, leaving cores for torch threads.
  **Not a pure throughput dial**: changing it changes how experience is batched and
  therefore changes the resulting model. Record the value in the config header.

Stop with `Training\Stop-Training.ps1` (trainer → orphaned env players → TensorBoard;
`-Prune` clears event-less runs). Killing only env workers does nothing — the trainer
respawns them. Close the trainer rather than hard-killing it: the final checkpoint write
is not instant and killing through it truncates the `.pt`.

### 4.4 TensorBoard — always running alongside training

```powershell
Training\venv\Scripts\python.exe -m tensorboard.main --logdir Training/results --port 6006 --reload_interval 15
```

Watch cumulative reward, **policy entropy** (collapse means premature convergence, often
to a degenerate crouch), value loss, and the per-fighter stamina stats `Systems_Telemetry`
pushes into `StatsRecorder`.

### 4.5 Judging a run

- **Self-play fight runs are judged on ELO, not mean reward.** The two move in opposite
  directions when a policy learns to farm shaping instead of winning bouts: one re-tune
  climbed to reward ~36 while its ELO fell 1198 → 1140. Mean reward is measured against a
  moving opponent pool and is not comparable across runs.
- Accept on the **shape** of the ELO curve: a monotonic slide is regression; oscillation
  within a point or two of the start is noise (flat ELO against a pool that is itself
  retraining means the policy kept pace).
- **Watch for the terminal-firing-every-episode signature:** mean reward pinned just under
  a graduation bonus with near-zero variance (one fighter sat at 2.999 ± 0.015). That is
  not a policy converging, it is one terminal firing every episode. Assert `StepCount`
  climbs, not just `CompletedEpisodes`.
- Behavioural claims are reported as a `MatchTestHarness.Run(n)` tally or a Game-view
  screenshot, never as an impression. The EditMode suite covers only two pure functions,
  and **a green run is not evidence that a body, reward or brain change works**.

### 4.6 Do not use the Unity Editor while a run is training

Entering Play mode alongside 8 env players took one run from 4.2M steps/hour to **69 steps
in 80 minutes**, with every process still alive and nothing logged. Deploying a brain,
refreshing assets and building an env are all Editor work. Stop training first.

```powershell
powershell -Command "@(Get-Process mlagents-learn -EA SilentlyContinue).Count"
# then diff the newest numbered .pt against itself 5 minutes later
```

---

## 5. File Integrity & Exclusions

**Never inspect, edit, or regenerate:**

| Path | Why |
|---|---|
| `*.meta` | hand-editing breaks GUIDs; hooks block text edits on these |
| `*.unity`, `*.prefab` | same — use the MCP tools or `Tools/unity.py` |
| `Library/`, `Temp/`, `Logs/`, `obj/` | Editor-generated caches |
| `Builds/` | gitignored and disposable; reproduce from the menu entry |
| `Training/results/` | gitignored, absent from a fresh clone, and the live TensorBoard logdir |
| `*.onnx` | binary weights. **Overwrite in place** via *PoSumo → Deploy \<Name\> Brain* so the `.meta` GUID and every reference to it survive |
| `ProjectSettings/` | `git add` on these is denied on first attempt by design |

Deployed brains under `Assets/Agents/<Name>_v01/` are the only weights that ship.

### Expose tuning through serialized fields and ScriptableObjects

Every biomechanical constant a human might want to move at runtime — spring stiffness,
damping, torque caps, friction, penalty coefficients — is a `[SerializeField] private`
field or a field on a ScriptableObject, not a `const` buried in a method.

| Asset | Type | Holds |
|---|---|---|
| `Assets/Settings/GameTuning.asset` | `Systems_GameTuning` | shared match numbers plus every `enable*` flag |
| `Assets/Agents/<Name>_v01/<Name>_Character.asset` | `Agent_CharacterDefinition` | body scales (`massScale` / `widthScale` / `torqueScale`), brain generation, **every reward-shaping coefficient for both schools** |

Two rules, each of which has already cost a bug here:

- **When adding a shaping coefficient, default it to the constant the code used before**,
  so an untuned character keeps training exactly what it always did. That is what makes
  it safe to add these mid-project.
- **Tune the asset, not the serialized scene value.** Scene components copy from
  `GameTuning` in `Start`, so the `.unity` files hold stale copies that are overwritten
  at runtime and will mislead anyone grepping them.

The inverse trick is also in use and is deliberate: a value that must **not** be
overridable becomes a `private const` (`Systems_SoftBodyJiggle.ENABLE_JIGGLE`,
`Systems_TournamentBracket.ARENA_SCENE`), which makes dozens of stale serialized values
across five scenes inert in one edit.

### Two referees, kept in sync

Losing conditions exist in **both** `Systems_GameMatchManager` (game) and
`Systems_SumoMatchManager` (training). Change one without the other and the brains never
learn the new rule is fatal. They have silently diverged before — by 2 m of mat, for
months.

---

## Quick reference

| Goal | Command |
|---|---|
| Is the Editor reachable | `python Tools/unity.py ping` |
| Load a scene / play / stop | `python Tools/unity.py scene SCN_TOURNAMENT \| play \| stop` |
| Read the console | `python Tools/unity.py errors [--warnings]` |
| Game-view screenshot | `python Tools/unity.py shot Temp/x.png` |
| Recompile after editing `.cs` outside the Editor | MCP `assets-refresh` (ForceUpdate) |
| Behavioural test | Play mode in `SCN_SUMO`, then `MatchTestHarness.Run(n)` → `HARNESS RESULT:` |
| Watch a live env | `curl http://127.0.0.1:8787/metrics` |
| Unit tests | `python Tools/unity.py raw run_tests '{"mode":"EditMode","assemblyNames":["PoSumo.Tests.EditMode"]}'` |

Always start a play session from **`SCN_TOURNAMENT`**, never `SCN_SUMO` — the arena scene
is deliberately usable standalone, which is exactly why the wrong entry point looks like
it works.
