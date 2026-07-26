# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

PoSumo: a Unity 6000.5.4f1 (2D URP) game where physics-ragdoll bipeds learn sumo
wrestling via ML-Agents, then fight each other in a playable, presentation-dressed
match/tournament layer. Portrait orientation, Android target
(`com.punkouter26.posumo`). `DESIGN.md` is the original approved spec and is now
partly historical — where it disagrees with this file or the code, the code wins
(notably: the ring is a raised dohyo of half-width ~2.75 m, not `|x| > 7`).

The roster is exactly four trained fighters — **Matt**, **Standard**, **Nick**,
**Kim** — each with an `.onnx`, a `*_Character.asset` and a `MANIFEST.md`. The
8-slot bracket seeds each of them twice. `Assets/Agents/ROSTER.md` is the roster
overview; there is no code mirror of it.

## Toolchain versions (validated in production — treat as the required set)

| Layer | Tool | Version | Notes |
|---|---|---|---|
| Engine | Unity Editor | **6000.5.4f1** (Unity 6.2) | changeset d550df8bd089 |
| Engine | Unity Hub | 3.x | headless CLI broken — install modules via UI |
| Package | com.unity.ml-agents | **4.0.0** (release_23) | LOCAL `file:` package with patches — never re-fetch |
| Package | com.unity.ai.inference | 2.2.1 | auto-dependency of ML-Agents (`Unity.InferenceEngine.ModelAsset`) |
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

## Architecture

### Everything about the biped is built at runtime
Scenes contain only manager objects. `Agent_BipedBody.Awake()` constructs the 14-part
ragdoll from code-defined tables (`PART_DEFS` / `JOINT_DEFS`): 4-segment articulated
spine (pelvis→lowerback→upperback→chest), legs and arms — **13 hinge motors**, mirrored
via `facingSign` (one policy works both directions because all observations are
multiplied into a facing-local frame). Intra-biped collisions are disabled pairwise
(limbs pass through their own body by design). `massScale` / `widthScale` /
`torqueScale` come from the character asset, so physique is data, not code.

### The brain contract (`Agent_Biped`)
- **13 continuous actions** (`ActionCount`), always.
- **41 observations** (legacy, decision period 5) or **44** when `extendedObservations`
  is on (+ opponent uprightness / down flag / edge distance, decision period 3 — the
  standard for new characters). Obs count and decision period MUST match what the
  assigned `.onnx` was trained with, or inference is silently garbage.
- Three `Mode`s: `Walk` (falling ends the episode), `Recover` (get up, then walk —
  falling never ends it, but lying down bleeds reward), `Sumo` (refereed externally;
  shaping only, ±1 comes from the referee).
- Configures its own `BehaviorParameters` / `DecisionRequester` in `Awake` — nothing to
  wire in the Inspector.
- All observations pass through `San()` NaN/Inf sanitization.
- `BeginWalkIn` / `EndWalkIn` temporarily swap in the character's `walkModel` (and the
  Recover observation layout) for the ceremonial round-opening walk-in, with
  `suppressEpisodeControl` so the presentation layer can borrow the body safely.

### Characters are ScriptableObjects
`Agent_CharacterDefinition` (menu: *PoSumo/Character Definition*) is one asset per
fighter holding identity (behavior name = YAML key, colour, face sprite names), body
build scales, brain generation (`extendedObservations`, `decisionPeriod`,
`inferenceModel`, `walkModel`), and **every sumo reward-shaping coefficient**. Fighter
personality (Nick = light and mobile, Kim = heavy planted anchor) lives in the asset and
the YAML header comments, never in `Agent_Biped`. `Systems_MatchRoster`
(`[DefaultExecutionOrder(-500)]`, must run before the agents' `Awake`) assigns the two
characters for a scene, or defers to `Tournament_State` when a bracket is active.

### Two referees, deliberately kept in sync
- `Systems_SumoMatchManager` — **training** referee. Loss = a foot below `footOffMatY`
  (−0.06) or torso below `fallY`; timeout ⇒ `EpisodeInterrupted` (draw). Per-round domain
  randomization of platform width and surface friction, plus curriculum dials read from
  `Academy.Instance.EnvironmentParameters` (`spawn_gap_half`, `shove_impulse`,
  `platform_difficulty`, `shove_chance`).
- `Systems_GameMatchManager` — **game** referee. Round state machine
  Fighting → RoundEnded → Grace → Fighting, scored to `pointsToWin` (exhibition) or
  `tournamentPointsToWin` (bracket), countdown freeze, timeout tiebreak on position, UI
  Toolkit HUD built in code. Spawns the presentation companions
  (`Systems_MatchPresentation` slow-mo/punch-in, `Systems_MatchAudio`, `Systems_FaceMood`)
  and exposes `RoundEnded` / `RoundStarted` / `MatchEnded` / `MatchReset` events.

Falling is **not** a loss in either referee (`knockdownLoses` is off). If you change a
losing condition, change it in **both** — they have silently diverged before, and
policies then never learn that a stray foot over the edge is fatal.

Shared numbers live in `Assets/Settings/GameTuning.asset` (`Systems_GameTuning`); scene
components copy from it at startup, so tune the asset, not serialized scene values.

### Tournament
`Systems_TournamentState` is a **static** 8-slot single-elimination bracket (it must
outlive the scene loads that play each match). Enter Play Mode domain reload is
**disabled** in this project, so it clears itself via
`[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` — any new static game state needs
the same treatment. `Systems_TournamentBracket` (SCN_TOURNAMENT) draws/seeds the bracket
and loads the next arena; `Systems_TournamentReporter` is spawned into the arena only
during a bracket match and reports the winner back, keeping SCN_SUMO usable as a
standalone exhibition scene.

Bracket UI gotcha: several rows display the *same* match (match 0 is both the QF-0 winner
and the semifinal's left entrant), so `_winnerSlots` holds one entry **per chip**, not per
match, and `Refresh()` reads each chip's match from its `userData`. Keying that list by
match index silently orphans the duplicate chips and they never repaint.

### Scenes
Build settings: `SCN_TOURNAMENT` (index 0), `SCN_SUMO`, `SCN_SUMO_ICE`, `SCN_SUMO_STICKY` —
the three arenas differ only by `Systems_SumoArena.style`/friction and are cycled through
by the bracket. `SCN_WALKVIEW` views a locomotion brain. Arena scenes are **baked**: an
editor pass ran `Systems_SumoArena.Build()` and saved the children, so `Awake` only
rebinds references.

Six training scenes remain, one per surviving purpose — every one either produced a
deployed brain or is the newest template for a training mode:
`SCN_TRAIN_MATT_AGGR`, `SCN_TRAIN_STD`, `SCN_TRAIN_NICK`, `SCN_TRAIN_KIM` (sumo),
`SCN_TRAIN_WALK` (walk school), `SCN_TRAIN_RECOVER4` (recover school). Keep that rule when
adding or retiring scenes — a scene that produced a shipped brain is the only way to
reproduce it.

### Conventions
- Scene hierarchy rule: every environment root has exactly 7 groups
  (Agents/Obstacles/Goals/SpawnPoints/Cameras/UI/Systems).
- Naming schema, enforced across the whole tree: script prefixes are exactly
  `Agent_`, `Sensor_`, `Systems_` (three folders under `Assets/Scripts/`, no others);
  scenes `SCN_*` with training scenes as `SCN_TRAIN_<NAME>`; env builds as
  `Builds/<Name>Env/` matching their scene; configs as `<Name><Phase><NN>.yaml` paired
  1:1 with run-id `<name>_<phase><nn>`; agent assets in `Assets/Agents/<Name>_v<NN>/`
  with a `MANIFEST.md`; face art as `Assets/Resources/<Name>_{neutral,happy1-3,sad1-3}.png`.
- `Systems_AcademyLifecycle` (static init) sets `runInBackground = true` — **critical**;
  without it Unity stops simulating on focus loss and the trainer times out — plus
  gravity, solver iterations, and Academy disposal on quit.
- Face sprites are resolved by the names on the character asset. Matt is the one exception:
  his asset leaves those fields empty and `Systems_FaceMood` falls back to its `Matt_*`
  constants — rename his PNGs and those constants together.

## Training workflow

Always run TensorBoard alongside training (user rule):
```powershell
Training\venv\Scripts\python.exe -m tensorboard.main --logdir Training/results --port 6006 --reload_interval 15
```

Typical loop for a new or updated fighter:
1. Character asset + a `Training/configs/<Name><Phase><NN>.yaml` whose `behaviors:` key
   **exactly** matches `behaviorName`. Each config's header comment records why its
   hyperparameters and shaping differ — keep that habit; it is the project's training log.
2. Build a headless env from the fighter's training scene
   (`PoSumo/Build <Name> Training Env` → `Builds/<Name>Env/<Name>Env.exe`).
3. Train:
```powershell
Training\venv\Scripts\mlagents-learn.exe Training/configs/<cfg>.yaml --run-id=<id> `
  --results-dir=Training/results --env=Builds/<Env>/<Env>.exe --num-envs=3 --no-graphics
```
4. Deploy the ONNX (`PoSumo/Deploy <Name> Brain`, or `DeployBrain.DeployLatestCheckpoint(...)`
   to try a brain from a still-running run — the trainer only writes the unnumbered
   `<Behavior>.onnx` on shutdown).

`--initialize-from=<run>` resolves **by behavior name**, so a cross-character fine-tune
needs the source weights staged under the new name (see the `kim_init` / `nick_init` runs).
`network_settings` must match the trunk being initialized from exactly (512 × 3 here).

Restart rules: physics/observation/action changes ⇒ new run-id or `--force` (cold);
parameter-only tweaks ⇒ `--resume`. `--force` deletes the run dir — **restart TensorBoard
afterward** (it holds a stale handle on Windows and shows an empty run).

To stop training: kill `mlagents-learn.exe` itself. Killing only the env worker EXEs does
nothing — the trainer auto-respawns them. On any disconnect the trainer saves a final
checkpoint before exiting.

Deployed models are always **overwritten in place** at `Assets/Agents/<Name>_v01/<Name>.onnx`
so the `.meta` GUID (and every reference to it) survives; `DeployBrain` does this and also
sets the character asset's `inferenceModel`. Copying a checkpoint does not require stopping
a headless run.

`Training/results` hygiene: a finished run keeps only its final `<Behavior>.onnx`, a
resumable `checkpoint.pt`, `configuration.yaml`, `run_logs/` and its tfevents. The numbered
per-step checkpoints are ~140 MB per run and nothing deploys from them — prune them once a
run is deployed. Staging directories for cross-character `--initialize-from` hold no
history and appear in TensorBoard as empty runs, so delete them after the real run starts;
they are one `Copy-Item` away from being recreated. `Training/README.md` maps every kept
config to the run and deployed brain it produced.

## Editor menu tools (`Assets/Editor/`, all under the **PoSumo** menu)

| Tool | Purpose |
|---|---|
| `BuildTrainingEnv` | Headless Win64 player containing one training scene (`--env` target). One menu entry per surviving training scene |
| `BuildAndroid` | APK to `Builds/Android/PoSumo.apk` from the enabled build-settings scenes |
| `DeployBrain` | Copy a run's ONNX → agent folder + wire the character asset. One entry per fighter, each pinned to the run that currently backs its shipped brain |
| `MatchTestHarness` | `MatchTestHarness.Run(n)` in Play mode: chains N matches unattended, logs a `HARNESS RESULT:` win/loss tally |

`Builds/` is gitignored and disposable — every env build is reproducible from its menu
entry, so retire a build by deleting the folder rather than keeping it around.

Judge a character by the harness tally, not by one eyeballed round. The build/deploy tools
print a `BUILD RESULT:` / `DEPLOY RESULT:` line — that is how their outcome is read back.

## Unity Editor automation (MCP)

The `ai-game-developer` MCP server (IvanMurzak Unity-MCP, HTTP via `.mcp.json`, no login)
is the way to drive the editor: `scene-*`, `gameobject-*`, `script-execute`,
`console-get-logs`, `assets-refresh`, `screenshot-*`. Hard-won specifics:

- To force import/recompile after writing files, call `assets-refresh` (ForceUpdate) —
  window-focus tricks are unreliable.
- `script-execute` calls that block the main thread >~30 s (e.g. `BuildPipeline.BuildPlayer`)
  return an MCP retry error **while still executing** — poll `Logs/Editor.log` for the
  `BUILD RESULT:` line.
- The plugin drops briefly on every domain reload (play-mode change, recompile); retry.
- Game-view verification: `screenshot-game-view`, or `script-execute` a `Camera.main`
  RenderTexture capture to a PNG under `Temp/` and read the image.
- Scene edits require exiting Play mode first (`EditorApplication.ExitPlaymode()`).

Two other Unity MCP packages are installed but not connected as Claude clients
(CoplayDev `com.coplaydev.unity-mcp`, Besty `com.besty.unity-skills`); enabling them
requires in-editor menu steps plus a Claude Code restart.

## Working in this repo (hooks)

`.claude/hooks/` enforces the rules in `.claude/rules/` and blocks several things by
design — these are not bugs:
- `.unity` / `.prefab` / `.meta` files cannot be text-edited. Use MCP tools.
- `UnityEditor` usage in non-`Editor/` C# without an `#if UNITY_EDITOR` guard is blocked.
- The **first** `Edit`/`Write` on a given `.cs` file is denied on purpose (fact-gathering
  gate); read the message, then retry the same edit.
- `git add ProjectSettings/…` and destructive Bash (`rm -rf Library/`, `git clean -fdx`, …)
  are denied on first attempt.

## Verification expectations

After scene or body changes, verify in Game view (via the screenshot flow above): both
fighters clearly visible on the dohyo, realistic gravity/contacts, no console errors, and
the HUD/score readable in portrait. For behavioural changes, run `MatchTestHarness.Run(n)`
and report the tally rather than an impression.
