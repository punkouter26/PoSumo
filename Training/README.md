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

**Everything below was re-tuned onto the current capsule-limb physics** (see
"The capsule-limb re-tune"). The lineage column is the trunk each run continued.

| Config | Behavior | Run | Env | Deployed as |
|---|---|---|---|---|
| `MattSumo06.yaml` | Matt | `matt_sumo06` (3.0M, ← `matt_sumo05`) | MattAggrEnv | `Matt_v01/Matt.onnx` |
| `StandardSumo02.yaml` | Standard | `standard_sumo02` (3.0M, ← `standard_sumo01`) | StandardEnv | `Standard_v01/Standard.onnx` |
| `KimSumo02.yaml` | Kim | `kim_sumo02` (3.0M, ← `kim_sumo01`) | KimEnv | `Kim_v01/Kim.onnx` |
| `NickSumo04.yaml` | Nick | `nick_sumo04` (1.2M, ← `nick_sumo02`) | NickEnv | `Nick_v01/Nick.onnx` |
| `MattSumo05.yaml` | Matt | `matt_sumo05` (8.0M) | MattAggrEnv | superseded — trunk for `matt_sumo06` |
| `StandardSumo01.yaml` | Standard | `standard_sumo01` (45.0M) | StandardEnv | superseded — trunk for `standard_sumo02` |
| `KimSumo01.yaml` | Kim | `kim_sumo01` (12.0M) | KimEnv | superseded — trunk for `kim_sumo02` |
| `NickSumo01/02.yaml` | Nick | `nick_sumo01` (12.0M) → `nick_sumo02` (4.0M) | NickEnv | superseded — trunk for `nick_sumo04` |
| `NickSumo03.yaml` | Nick | `nick_sumo03` (3.0M) | NickEnv | **REJECTED — regressed, see below** |
| `MattWalk01.yaml` | Matt | `matt_walk01` (12.0M) | MattWalkEnv | `Standard_v01/StandardWalk.onnx` (baseline gait, kept by Standard) |
| `MattWalk02.yaml` | Matt | `matt_walk02` (2.0M) | MattWalkEnv | `Matt_v01/MattWalk.onnx` (restyled, aggressive) |
| `KimWalk01.yaml` | Kim | `kim_walk01` (4.0M) | KimWalkEnv | `Kim_v01/KimWalk.onnx` |
| `NickWalk01.yaml` | Nick | `nick_walk01` (4.0M) | NickWalkEnv | `Nick_v01/NickWalk.onnx` |
| `StandardWalk01.yaml` | Standard | *(not run)* | StandardWalkEnv | — Standard still uses `matt_walk01`'s export; this exists so that gait stays reproducible |

Every fighter owns a walk brain, and all of them descend from `matt_walk01` — the
baseline gait, trained on the shared 1.0 body under behavior name `Matt`.

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
`<run>\<Behavior>` with matching names (`matt_walk01\Matt`, `kim_walk01\Kim`).
The baseline walk was originally `standard_walk01`/`StandardWalk01.yaml`, which
broke that rule — it is renamed, and only the deployed file `StandardWalk.onnx`
still carries the old name, because Standard is the fighter that uses it.
| `MattRecover05.yaml` | Matt | *(archived)* | RecoverEnv | — recover-school recipe, nothing deployed |

## What lives where

`results/` is the TensorBoard logdir, and it holds **only runs that back a
deployed brain** — one per row in the table above. Everything else is either
deleted or moved out:

- `trunks/` — checkpoints kept purely as `--initialize-from` sources, not as
  history worth plotting: every pre-capsule run each current brain descends from
  (`matt_sumo04/05`, `standard_sumo01`, `kim_sumo01`, `nick_sumo02`, `matt_walk01`,
  `kim_walk01`, `nick_walk01`). Outside the logdir so TensorBoard shows exactly one
  run per deployed brain. **A config's documented `--initialize-from` therefore
  needs its trunk copied back into `results/` first** —
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
