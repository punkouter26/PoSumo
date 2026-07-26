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

| Config | Behavior | Run | Env | Deployed as |
|---|---|---|---|---|
| `MattSumo05.yaml` | Matt | `matt_sumo05` (8.0M) | MattAggrEnv | `Matt_v01/Matt.onnx` |
| `StandardSumo01.yaml` | Standard | `standard_sumo01` (45.0M) | StandardEnv | `Standard_v01/Standard.onnx` |
| `NickSumo01.yaml` → `NickSumo02.yaml` | Nick | `nick_sumo01` (12.0M) → `nick_sumo02` (4.0M) | NickEnv | `Nick_v01/Nick.onnx` |
| `KimSumo01.yaml` | Kim | `kim_sumo01` (12.0M) | KimEnv | `Kim_v01/Kim.onnx` |
| `StandardWalk01.yaml` | Matt | `standard_walk01` (12.0M) | WalkEnv | `Standard_v01/StandardWalk.onnx` (shared walk-in brain) |
| `MattRecover05.yaml` | Matt | *(archived)* | RecoverEnv | — recover-school recipe, nothing deployed |

`matt_sumo04` is also kept: it is the aggressive trunk that Kim was fine-tuned
from, and it is the only reason her retrain is reproducible.

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
