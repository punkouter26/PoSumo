# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

PoSumo: a Unity 6000.5.4f1 (2D URP) reinforcement-learning game where physics-ragdoll
bipeds learn sumo wrestling via ML-Agents. The current agent is **Matt** (behavior name
`Matt` — this string must match the YAML config key exactly). Approved game design is in
`DESIGN.md`: 1v1 side-view sumo, **ring-out only** rules (falling is not a loss), two-phase
curriculum (walk first, then self-play sumo initialized from the walk policy).

## Toolchain versions (validated in production — treat as the required set)

| Layer | Tool | Version | Notes |
|---|---|---|---|
| Engine | Unity Editor | **6000.5.4f1** (Unity 6.2) | changeset d550df8bd089 |
| Engine | Unity Hub | 3.x | headless CLI broken — install modules via UI |
| Package | com.unity.ml-agents | **4.0.0** (release_23) | LOCAL `file:` package with patches — never re-fetch |
| Package | com.unity.ai.inference | 2.2.1 | auto-dependency of ML-Agents |
| Package | URP | 17.6.0 | project template |
| MCP | unity-mcp-cli (npm) | 0.86.0 | |
| MCP | com.ivanmurzak.unity.mcp | 0.86.0 | + gamedev-mcp-server 9.2.0 |
| MCP | com.coplaydev.unity-mcp | 10.1.0 | |
| MCP | com.besty.unity-skills | 2.2.1 | HTTP server port 8090 |
| Python | Python | **3.10.11** | hard range: >=3.10.1, <=3.10.12 |
| Python | mlagents / ml-agents-envs | **1.2.0.dev0** | built from release_23 source; envs is patched |
| Python | torch | **2.5.1** (+cpu) | PIN — 2.6+ breaks ONNX export |
| Python | setuptools | **69.5.1** | PIN — 70+ removes pkg_resources |
| Python | numpy | 1.23.5 | pinned by mlagents |
| Python | onnx | 1.15.0 | |
| Python | tensorboard | 2.20.0 | always run during training |
| Android | Build Support module | 6000.5.4f1 | matches editor version |
| Android | OpenJDK | 17.0.18+8 | embedded in AndroidPlayer |
| Android | NDK | r27c | |
| Android | SDK build-tools | 36.0.0 | |
| Android | SDK platforms | android-34, android-36 | |
| Android | SDK cmdline-tools | 16.0 | |
| Android | SDK platform-tools (adb) | 36.0.0 | |
| Android | CMake | **3.22.1** | NOT in Hub module set — must sit at `SDK/cmake/3.22.1` |
| Shell | Node.js / npm | 24.x / 11.x | for MCP CLIs |
| Shell | Git | 2.55+ | |
| Shell | uv | 0.11+ | CoplayDev server runner |

## Critical version pins (do not "upgrade")

- **ML-Agents**: local editable package at `Training/ml-agents/com.unity.ml-agents`
  (release_23 / 4.0.0), referenced via `file:` in `Packages/manifest.json`. It contains
  required local patches — see "Local patches" below. Re-cloning loses them.
- **Python venv** `Training/venv`: `mlagents 1.2.0.dev0` (installed from the same
  release_23 source), **torch 2.5.1** (newer torch breaks ONNX checkpoint export),
  **setuptools 69.5.1** (newer removes `pkg_resources` and breaks `mlagents-learn`).
  Never `pip install --upgrade` in this venv.

## Local patches (re-apply if ml-agents source is re-cloned)

1. `Runtime/Integrations/Match3/Match3ActuatorComponent.cs:63` — `GetInstanceID()` and the
   `EntityId->int` cast are obsolete-as-error on Unity 6.2; uses `gameObject.GetHashCode()`.
2. `Plugins/Google.Protobuf_MLAgents.dll` — renamed from `Google.Protobuf_Packed.dll`
   (file, meta, **and internal assembly name**, rewritten with Mono.Cecil) because
   `com.unity.ai.inference` ships an editor-only DLL with the identical original name and
   player builds resolve the reference to the wrong one. All 7 asmdefs reference the new name.
3. `mlagents_envs/environment.py::_check_communication_compatibility` (venv site-packages
   AND source clone) — `StrictVersion` replaced with a manual tuple parse; the original
   crashes worker auto-restarts.

## Training commands

Always run TensorBoard alongside training (user rule):
```powershell
Training\venv\Scripts\python.exe -m tensorboard.main --logdir Training/results --port 6006 --reload_interval 15
```

Current state (post-cleanup): only `SCN_SUMO` (the game) exists; training scenes and env
builds were pruned. To train again: build a training scene via MCP using
`Systems_WalkTraining` (walk school) or two agents + `Systems_SumoMatchManager` (self-play),
build a headless env exe, then:
```powershell
Training\venv\Scripts\mlagents-learn.exe Training/configs/<cfg>.yaml --run-id=<id> `
  --results-dir=Training/results --env=Builds/<Env>/<Env>.exe --num-envs=4 --no-graphics
```
Resume/fine-tune sources kept: `Training/results/matt_sumo02` (Matt) and
`Training/results/dave_sumo01` (Dave) — use `--initialize-from=<run>`.

Rules for restarts: physics/observation/action changes ⇒ new run-id or `--force` (cold);
parameter-only tweaks ⇒ `--resume`. `--force` deletes the run dir — **restart TensorBoard
afterward** (it holds a stale handle on Windows and shows an empty run).

To stop training: kill `mlagents-learn.exe` itself. Killing only the env worker EXEs does
nothing — the trainer auto-respawns them. On any disconnect the trainer saves a final
checkpoint before exiting.

## Architecture

- **Everything about the biped is built at runtime.** Scenes contain only manager objects;
  `Agent_BipedBody.Awake()` constructs the 14-part ragdoll from code-defined tables
  (`PART_DEFS`/`JOINT_DEFS`): 4-segment articulated spine (pelvis→lowerback→upperback→chest),
  legs and arms — 13 hinge motors, mirrored via `facingSign` (one policy works both
  directions because all observations are multiplied into a facing-local frame).
  Intra-biped collisions are disabled pairwise (limbs pass through own body by design).
- `Agent_Biped` (ML-Agents `Agent`): 41 observations / 13 continuous actions; configures
  its own `BehaviorParameters` in `Awake` (nothing to set in the Inspector). `Mode.Walk`
  self-terminates on falls; `Mode.Sumo` is refereed by `Systems_SumoMatchManager`
  (ring-out at |x| > 7 from arena center; timeout ⇒ `EpisodeInterrupted`).
- `Systems_WalkTraining` spawns N self-contained lane environments at runtime; when its
  `inferenceModel` is set it spawns **one** lane (viewing mode) instead of 8 (training).
- `Systems_AcademyLifecycle` (static init): `runInBackground=true` (critical — without it
  Unity stops simulating on focus loss and the trainer times out), gravity, solver
  iterations, Academy disposal on quit.
- Scene hierarchy rule: every environment root has exactly 7 groups
  (Agents/Obstacles/Goals/SpawnPoints/Cameras/UI/Systems). Naming: `Agent_`, `Sensor_`,
  `Systems_` script prefixes; scenes `SCN_*`; agent assets in `Assets/Agents/<Name>_v<NN>/`.
- Trained models: copy checkpoint to `Assets/Agents/Matt_v01/Matt.onnx` — **always
  overwrite in place** (preserves the .meta GUID). Assign to `Systems_WalkTraining.
  inferenceModel` (or `Agent_Biped.inferenceModel`) for Python-free Burst inference;
  copying a checkpoint does not require stopping a headless training run.

## Unity Editor automation (MCP)

The `ai-game-developer` MCP server (IvanMurzak Unity-MCP, stdio via `.mcp.json`, no login)
is the way to drive the editor: `scene-*`, `gameobject-*`, `script-execute`,
`console-get-logs`, `assets-refresh`, `screenshot-isolated`. Hard-won specifics:

- To force import/recompile after writing files, call `assets-refresh` (ForceUpdate) —
  window-focus tricks are unreliable.
- `script-execute` calls that block the main thread >~30 s (e.g. `BuildPipeline.BuildPlayer`)
  return an MCP retry error **while still executing** — poll `Logs/Editor.log` for the
  result (the build scripts log a `BUILD RESULT:` line).
- The plugin drops briefly on every domain reload (play-mode change, recompile); retry.
- Game-view verification: `script-execute` a `Camera.main` RenderTexture capture to a PNG
  under `Temp/`, then read the image.
- Scene edits require exiting Play mode first (`EditorApplication.ExitPlaymode()`).

Two other Unity MCP packages are installed but not connected as Claude clients
(CoplayDev `com.coplaydev.unity-mcp`, Besty `com.besty.unity-skills`); enabling them
requires in-editor menu steps plus a Claude Code restart.

## Verification expectations

After scene or body changes, verify in Game view (via the screenshot flow above): biped(s)
clearly visible against the 1-meter grid (heavy line every 5 m), realistic gravity/contacts,
no console errors. The user judges creature size by grid squares — keep the grid.
