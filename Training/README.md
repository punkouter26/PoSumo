# PoSumo — ML-Agents Training

2D ragdoll-biped reinforcement learning for the PoSumo Unity project
(Unity 6000.5.4f1, ML-Agents Release 23). Version pins and local patches are
documented in the repo-root `CLAUDE.md` — do not upgrade anything in here.

## Versions (matched pair)
- Unity package: `com.unity.ml-agents` **4.0.0**, installed from the local
  `release_23` source at `Training/ml-agents/com.unity.ml-agents` (referenced via
  `file:` in `Packages/manifest.json` — it carries local patches, never re-clone)
- Python package: `mlagents` **1.2.0.dev0**, installed from that same source into
  `Training/venv`
- Python 3.10.11 · torch 2.5.1+cpu · setuptools 69.5.1 (all pinned)

## Layout
```
Training/
  venv/            Python virtual-env with mlagents installed (gitignored)
  ml-agents/       release_23 source clone; the Unity package points here
  configs/         PPO configs — one per training run, header comments explain the tuning
  results/         mlagents-learn output: final .onnx, checkpoint.pt, tfevents (gitignored)
  logs/            supervision logs (gitignored)
```

`results/` keeps only what is needed to reproduce or continue a run: the final
`<Behavior>.onnx`, a resumable `checkpoint.pt`, `configuration.yaml`, `run_logs/`
and the TensorBoard event files. Intermediate numbered checkpoints are pruned —
they are ~140 MB per run and nothing ever deploys from them.

## Configs and the runs they produced

**One brain per fighter, covering both walking and fighting** (2026-07-28). The walk
and fight policies were merged: a task flag in the observation vector tells the two
jobs apart, which took the vector 44 → 45 and invalidated every brain trained before
it. There is no separate walk run, walk config, walk env or walk `.onnx` any more.

| Config | Behavior | Run | Env | Backs |
|---|---|---|---|---|
| `MattUnified01.yaml` | Matt | `matt_unified02` (15.0M, cold) | MattAggrEnv | `Matt.onnx` |
| `StandardUnified01.yaml` | Standard | `standard_unified01` (15.0M, cold) | StandardEnv | `Standard.onnx` |
| `KimUnified01.yaml` | Kim | `kim_unified01` (15.0M, cold) | KimEnv | `Kim.onnx` |
| `NickUnified01.yaml` | Nick | `nick_unified01` (15.0M, cold + resume) | NickEnv | `Nick.onnx` |

All four reached the configured `max_steps: 15000000`. Final ELO: Standard 5941,
Matt 4832, Kim 4776, Nick 4581 — but see the Nick note below before comparing his
number with the other three.

### Nick was interrupted at 3.75M and resumed (2026-07-30)

`nick_unified01` died at step 3,749,814 on 2026-07-29 11:06 with no final
`<Behavior>.onnx` and no `configuration.yaml`, while the other three ran to 15M.
It was **resumed, not restarted** — the run was healthy, not broken: ELO had risen
monotonically 1199 → 1786 and reward carried real variance, so this was not the
inverted-walk-`facingSign` failure that killed `matt_unified01` (that one pins mean
reward just under the +3 graduation bonus with near-zero variance).

The resume took it 3.75M → 15.0M at ~744 steps/s, about 3.7 h on 3 envs.

**The interruption cost the ELO scale, and this is the part worth remembering.**
Self-play ELO and the opponent pool live in `run_logs/training_status.json`, which
the crash never wrote. On `--resume` mlagents restored the *weights* from
`checkpoint.pt` but reset ELO to `initial_elo: 1200` and started a fresh pool. So
Nick's 4581 is measured against a pool that began from scratch at 3.75M, while the
other three accumulated theirs across a full 15M — the numbers are not strictly
comparable, and a crashed-then-resumed run can never be compared on ELO *level*
again. Judge it on shape, which is clean: monotonic non-decreasing across all ten
deciles (1411 → 4369 by decile mean), max drawdown 586 from running peak.

Verified in-game rather than on the curve alone —
`MatchTestHarness.Run(10)` in SCN_SUMO (roster is Matt vs Nick):

```
HARNESS RESULT: MATT 3 — 7 NICK over 10 matches / 38 rounds | longest round 14.4s
```

Nick beats a full-budget Matt 7-3, and no round reached the timeout, which is the
behaviour the 4.0 m ring was shrunk to produce.

**That 7-3 no longer reproduces — re-measured 2026-08-01 it is 0-3 the other way:**

```
HARNESS RESULT: MATT 3 — 0 NICK over 3 matches / 10 rounds | longest FIGHT 12.4s of 20s limit
```

Same brains, same scene. Nothing in the observation, action or reward contract changed
between the two measurements, so this is the game layer, not the policies: the ring,
friction and HUD passes all landed in between, and the game-only rules (down-out, the
3-knockout rule, the tawara band) are exactly the sort of thing that moves a matchup
without touching a brain. Three matches is also a small sample against ten — re-run at
`Run(10)` before treating the reversal as settled. The part that DID hold is the
finishing behaviour: longest fight 12.4s against a 20s limit, no round near the clock.
| `MattSumo06/07/08`, `StandardSumo02/03/04`, `KimSumo02/03/04`, `NickSumo04/05/06` | — | — | — | pre-merge history, parked in `trunks/pre_merge/` |

**These are COLD runs and had to be.** A 44-obs checkpoint cannot warm-start a 45-obs
policy — the first layer shape no longer matches — so no pre-merge weights carry over.

Retired on 2026-07-28: every `*Walk*` config (walking is a lane inside each unified
scene now), `MattRecover05` (its scene is gone; the walk-in sets `Mode.Walk`, not
`Mode.Recover`), plus the earlier round — `KimSumo01`, `MattSumo05`, `NickSumo01/02/03`,
`StandardSumo01`.

A gait still has to be learned on the physique it runs on, so the walk lane lives in
each fighter's own scene rather than one shared walk scene.

## The actuator rebuild (2026-08-01) — every brain needs a corrective pass

The motor model changed, so every shipped brain is now driving a body that answers
differently. This is the same class of change as the capsule-limb re-tune below, and
the same recovery applies: rebuild each env, `--initialize-from` the shipped trunk,
run a short pass at reduced learning rate. Do NOT retrain from scratch.

What changed under the policies:

1. **Force-velocity (Hill) scaling on motor torque.** A `HingeJoint2D` motor is an
   ideal servo — full torque at any speed — so a hip could deliver 300 N·m while
   already swinging at 400°/s. Concentric torque now falls off with shortening
   velocity (a/F0 = 0.25) and eccentric keeps 1.5×, as in vivo. This is the physical
   version of what `effortPenalty` was buying with a reward term, so **expect the
   effort term to be worth re-tuning downward** once the policies adapt.
2. **Activation dynamics.** Commanded torque is now first-order lagged, 50 ms rise /
   70 ms fall. At decision period 3 (60 ms) the policy could previously invert a
   joint's whole torque between decisions.
3. **Nonlinear end-range passive stiffness**, cubic past 70% of each joint's own
   half-range, so limbs stop parking against their stops.
4. **Angular damping 0.8 → 0.35.** The old value was compensating for 1; keeping both
   would leave the bodies sluggish.
5. **Ankle ±25° → ±35°, torque 120 → 160 N·m.** Human total ROM is ~70°, and the
   ankle is the joint that actually drives a sumo forward.

Two things are staged but OFF, and both want the next run rather than a corrective
pass, because each changes the observation vector and so invalidates the input layer:

- `Agent_CharacterDefinition.contactObservations` (+4 obs: per-foot contact and the
  share of body weight through each foot). The policy is currently **blind to the
  floor** — the four "feet" slots are torso-relative positions and nothing else — so
  it cannot tell a planted foot from one in the air. This is the highest-value
  observation available and it takes 45 → 49.
- `Agent_CharacterDefinition.driveReward` (0 by default). Every existing sumo term
  pays for a *collision*; nothing pays for holding a load. Measured push in contact
  was 71–500 N against a human's sustained 350–700 N. Try 0.004–0.008 with
  `contactObservations` on.

### Better training than more PPO

Self-play from scratch is why the gaits read as learned-not-lived. In rough order of
value per unit of work:

1. **Reference-motion imitation (DeepMimic-style).** Pretrain against even a short
   reference clip — a shiko stomp, a tachiai charge — then fine-tune on winning with
   self-play. This is the best-established route to human-looking bipedal motion and
   it composes with everything already here.
2. **Mirror/symmetry augmentation.** The body is symmetric and `facingSign` already
   mirrors observations, so each sample can be reflected into a second training
   example. Roughly halves the cost of learning a symmetric gait.
3. **Early termination on non-postures.** Ending an episode when the chest passes,
   say, 60° off vertical stops the policy spending its budget learning to be good at
   being collapsed, which is where a lot of measured play currently lives.
4. **Curriculum on push, not just on ring width.** `platform_difficulty` already
   exists; a `push_resistance` dial that ramps opponent mass or friction would train
   the sustained drive that `driveReward` pays for.

## The capsule-limb re-tune

Limb colliders changed from `BoxCollider2D` to `CapsuleCollider2D` (matched to the
drawn capsule so a kick connects where it looks like it connects). Feet and torso
were left as boxes, so balance and chest-to-chest shoving are unchanged. Every
brain had been trained against box limbs, so all eight were re-tuned: rebuild the
env, warm-start from the shipped checkpoint, run a short corrective pass at a
reduced learning rate, deploy.

Seven of eight improved. Walk brains gained both mean reward and consistency —
the tighter std matters more, since walk school pays +3 for arriving on your feet,
so a smaller spread means fewer falls:

| Brain | before | after |
|---|---|---|
| Kim walk | 3.46 (std 0.93) | 3.60 (std 0.64) |
| Nick walk | 3.39 (std 1.09) | 3.54 (std 0.78) |
| Standard walk | 3.31 (std 1.26) | 3.58 (std 0.67) |

**`nick_sumo03` is the one that failed, and it is worth understanding.** Over 3M
steps its mean reward rose to ~36 while its self-play ELO fell monotonically
1198 → 1140. Reward up with ELO down means the policy learned to farm shaping
rather than win bouts. Nick is uniquely exposed: he carries the largest and most
farmable shaping on the roster (cadence 0.0032, lunge 0.0024 @ 1.0 m/s,
straightLegEarnFraction 0.75). The same 3M budget re-tuned the other three cleanly.

`NickSumo04.yaml` is the conservative retry — less exploration, half the learning
rate, a third of the steps, and a self-play pool weighted to the strongest recent
opponents so farming that does not win gets punished. **Judge a fight re-tune on
ELO, never on mean reward.**

Run-ids are `<behavior>_<phase><NN>`, so a TensorBoard row always reads
`<run>\<Behavior>` with matching names (`matt_unified01\Matt`, `kim_unified01\Kim`).

## What lives where

`results/` is the TensorBoard logdir, and it holds **only runs that back a
deployed brain** — one per row in the table above. Everything else is either
deleted or moved out:

- `trunks/` — checkpoints kept as weights rather than history. `trunks/pre_merge/`
  holds every run from before the walk+fight merge; those are 44-obs and cannot
  warm-start anything now, but they are what a rollback would restore. Outside the
  logdir so TensorBoard shows only live runs. **A config's documented
  `--initialize-from` needs its trunk copied back into `results/` first** —
  `Copy-Item -Recurse Training/trunks/<run> Training/results/`.
- **Staging dirs** (`kim_init`, `kim_walk_init`, …) must sit *inside* `results/`
  at launch because `--initialize-from` resolves relative to `--results-dir`.
  They contain no history, so they appear in TensorBoard as empty runs — delete
  them once the real run is stepping and re-create them with one `Copy-Item`.
- Superseded runs are deleted outright. `nick_sumo01` went this way once
  `nick_sumo02` shipped.

## Train

Always run TensorBoard alongside training (project rule):
```powershell
Training\venv\Scripts\python.exe -m tensorboard.main --logdir Training/results --port 6006 --reload_interval 15
```

Build the env from the Unity Editor first (*PoSumo → Build \<Name\> Training Env*),
then:
```powershell
Training\venv\Scripts\mlagents-learn.exe Training/configs/<cfg>.yaml --run-id=<id> `
  --results-dir=Training/results --env=Builds/<Env>/<Env>.exe --num-envs=3 --no-graphics
```

- `--resume` for parameter-only tweaks; a new run-id (or `--force`) for any
  physics / observation / action change.
- `--force` deletes the run directory — **restart TensorBoard afterward**, it
  holds a stale handle on Windows and will show an empty run.
- Stop a run by killing `mlagents-learn.exe` itself. Killing only the env worker
  EXEs does nothing; the trainer respawns them. Any disconnect still writes a
  final checkpoint.

### Cross-character fine-tunes need a staged trunk

`--initialize-from=<run>` resolves weights by the **new** behavior name, so
starting `Kim` from a `Matt` run needs the checkpoint copied under a `Kim/`
folder first:
```powershell
New-Item -ItemType Directory -Force Training/results/kim_init/Kim
Copy-Item Training/results/matt_sumo04/Matt/checkpoint.pt Training/results/kim_init/Kim/checkpoint.pt
# ... then --initialize-from=kim_init
```
`network_settings` must match the trunk exactly (512 × 3 across this roster).
Staging directories are disposable — they hold no training history, and they show
up in TensorBoard as empty runs, so delete them once the real run has started.

## After training

*PoSumo → Deploy \<Name\> Brain* copies the run's final ONNX into the fighter's
agent folder and repoints its character asset. The ONNX is always **overwritten in
place** so the .meta GUID (and every scene reference to it) survives.

To audition a brain from a run that is still going, call
`DeployBrain.DeployLatestCheckpoint("<run-id>", "<Behavior>", "Assets/Agents/<Folder>")`
via MCP `script-execute` — the trainer only writes the unnumbered export on
shutdown. Copying a checkpoint does not require stopping the run.

## Realism pass — corrective runs (pending)

The body was rebuilt and the ring doubled, which invalidates all four shipped
fight brains. These four configs are the corrective pass. They are **warm starts,
not resumes**: `--initialize-from` the current trunk, 6M steps at lr 1.5e-4.

| Config | Run id | Trunk | Env | Port |
|---|---|---|---|---|
| `MattSumo07.yaml` | `matt_sumo07` | `matt_sumo06` | `MattAggrEnv` | 5600 |
| `StandardSumo03.yaml` | `standard_sumo03` | `standard_sumo02` | `StandardEnv` | 5610 |
| `NickSumo05.yaml` | `nick_sumo05` | `nick_sumo04` | `NickEnv` | 5620 |
| `KimSumo03.yaml` | `kim_sumo03` | `kim_sumo02` | `KimEnv` | 5630 |

What changed under the policies:

1. Ring half-width 2.75 → 5.5 (edge distance is a normalised observation).
2. Segment lengths re-derived to Winter — limbs were 8–18% short.
3. Joint ROM clamped to human total (ankle ±25, spine ±20 each, shoulder ±120).
4. Upper-body torque cut to human peak (spine 180 each, shoulder 80, elbow 60).
5. Passive joint resistance + real body damping.
6. Two new shaping terms: `effortPenalty` (ungated, quadratic) and `stanceReward`.

**Judge these on the ELO curve, not mean reward.** Two new shaping terms mean the
reward scale is not comparable with the trunk runs at all.

Each config carries a `platform_difficulty` curriculum (wide → mixed → full) so
the policy learns edge distance across ring widths rather than memorising one mat.
`Systems_SumoMatchManager.startHalfRange` was widened to 1.7–5.5 to match.

Rebuild each env first — the body changed, so an old env binary trains the old
skeleton:

```powershell
# PoSumo > Build <Name> Training Env, for each of the four
Training\venv\Scripts\mlagents-learn.exe Training/configs/MattSumo07.yaml `
  --run-id=matt_sumo07 --initialize-from=matt_sumo06 --results-dir=Training/results `
  --env=Builds/MattAggrEnv/MattAggrEnv.exe --num-envs=3 --no-graphics --base-port 5600
```

### Restarted 2026-07-28 — the leg and arm bent the wrong way

All eight runs above (four fight, four walk) were **discarded and relaunched** from
the same trunks. The fight runs lost ~1.44M steps each; the walk runs had already
completed 2.5M and those brains were thrown away too.

The three asymmetric joints — hip, knee, elbow — had inverted ranges. `jointAngle`
on these `HingeJoint2D`s is the *negative* of the child segment's geometric
rotation (ratio −1.00, measured on every joint), so ranges authored as if they
were geometric bent the limb backwards. The knee swung the shin **forward** and the
elbow swung the forearm **backward** — a bird leg — and the hip offered 120° of
extension against 30° of flexion. No fighter could crouch and drive off a loaded
leg, which is the single most basic thing a sumo does. Corrected to
hip (−120…30), knee (0…150), elbow (−150…0); the symmetric joints were unaffected.

Nothing already trained transfers cleanly, because the *sign of what every leg and
arm action does* has flipped. These are still warm starts — the trunk carries
balance, opponent tracking and edge awareness, all built on unchanged observations
— but `learning_rate` was raised **1.5e-4 → 3.0e-4** in all eight configs for this
restart only, so the wrong motor priors get overwritten at fresh-run speed rather
than nudged. Walk runs use ports 5700/5710/5720/5730 to sit clear of the fight runs.

Expect the early ELO/reward curve to look worse than the discarded runs did at the
same step count; that is the policy unlearning the bird leg, not a regression.

**Walk school finished 02:20 on the corrected body** and all four brains are deployed
(`DeployWalk`, run ids updated in `DeployBrain.cs`). Every fighter now owns a walk
brain for the first time — Standard previously borrowed `matt_walk01`'s export, and
Kim and Nick had no `walkModel` at all, which is why their walk-in ceremony silently
skipped.

| Run | Final | Reward | Old-body reward |
|---|---|---|---|
| `kim_walk03` | 2.49M | **3.53** | 3.52 |
| `matt_walk03` | 2.49M | **3.47** | 4.06 |
| `standard_walk02` | 2.49M | **3.14** | 3.53 |
| `nick_walk03` | 2.49M | **3.06** | 3.49 |

Do **not** read the last column as a 10–15% regression. `KneeBendFactor()` changed
meaning with the sign fix — it used to score the bird-leg bend and now scores true
flexion — and it gates a term in the walk reward, so the two scales measure
different things. What is comparable is the shape: all four climbed from about −0.9
at 60k to positive by 700k and converged by 2.5M, i.e. the gait was re-learned from a
trunk whose leg priors were wrong, which is the outcome the restart was betting on.

### Trap: `--force` did not clear the run directories

The restart left **106 files from the discarded runs** in `results/` — numbered
checkpoints and, for the two walk runs still going at the time, a stale top-level
`<Behavior>.onnx`. TensorBoard was still holding those directories open when the runs
launched; it was killed a few minutes *after*, not before.

This is a live deployment hazard, because the stale checkpoints outrank the new ones
numerically — `matt_walk03` kept a discarded `Matt-2500071.onnx` while that night's run
had only reached `Matt-2500037.onnx`, so `DeployLatestCheckpoint` would have shipped a
bird-leg brain. `Deploy`/`DeployWalk` were safe only because they read the top-level
`<Behavior>.onnx`, which each run rewrites when it finishes.

**Kill TensorBoard before launching with `--force`, not after.** The note further up
saying to restart it afterward is necessary but not sufficient.

### Known trap: SCN_TRAIN_STANDARD has no character asset assigned

`SCN_TRAIN_STANDARD` contains **zero** references to `Standard_Character.asset` (8 fields
sit at `character: {fileID: 0}`), while the other seven training scenes reference
theirs correctly. Verified by GUID-grepping the saved `.unity` files, which is the
check CLAUDE.md mandates.

It is currently harmless *by coincidence*: Standard's sheet is byte-identical to
`Agent_Biped`'s code defaults (massScale/widthScale/torqueScale 1, uprightReward
0.0005, closingReward 0.0006, energyPenalty 0.0004, straightLegEarnFraction 0.3 …),
so an agent with no character trains exactly what Standard's sheet would ask for.
`standard_sumo03` was therefore left running rather than restarted.

It becomes a real bug the moment anyone tunes Standard's sheet — the training scene
will silently ignore it, which is precisely how this project once trained the wrong
policy for 1.5M steps. Assign the character in the scene and rebuild StandardEnv
before any Standard-specific tuning.

### Sumo corrective runs: full 6M budget (an earlier 3M cut was reverted)

These runs go the configured `max_steps: 6000000`. The trainers stop themselves at
that point and write the unnumbered `<Behavior>.onnx`.

An earlier note here said they would be stopped at 3M. That was a wall-clock
decision, not a training one — at the measured ~100 steps/s with four self-play
runs plus four walk runs sharing the machine, 6M was ~15 h. 3M would still have
been defensible, since CLAUDE.md's documented corrective window is "1-3M steps
against trunks of 12-45M". With more time available the cut was reverted, and the
early-stop watcher removed, so nothing truncates them now.

The walk runs finish first (2.5M), which frees four env processes and should let
the sumo runs speed up for the remainder.

If a run is interrupted, resume rather than restart:

```powershell
Trainingenv\Scripts\mlagents-learn.exe Training/configs/MattSumo07.yaml `
  --run-id=matt_sumo07 --results-dir=Training/results `
  --env=Builds/MattAggrEnv/MattAggrEnv.exe --num-envs=2 --no-graphics --base-port 5600 --resume
```

`--resume`, not `--force`: force deletes the run directory, and would also need
TensorBoard restarted because it holds a stale handle on Windows.
