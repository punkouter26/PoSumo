# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

PoSumo: a Unity 6000.5.4f1 (2D URP) game where physics-ragdoll bipeds learn sumo
wrestling via ML-Agents, then fight each other in a playable, presentation-dressed
match/tournament layer. Portrait orientation, Android target
(`com.punkouter26.posumo`). `DESIGN.md` is the original approved spec and is now
partly historical — where it disagrees with this file or the code, the code wins
(notably: the ring is a raised dohyo, not `|x| > 7`; its half-width is **4.0 m** and is read
from `GameTuning.asset` because the arena scenes serialize their own copy).

The ring went 2.75 → 5.5 in the realism pass and back to **4.0** on 2026-07-28, because 5.5
made a decisive finish impossible: with the fighters opening 0.9 m apart there was 4.6 m of
mat to drive an opponent across, and at 0.9 friction the force to start sliding a 69.6 kg
body is 614 N against a measured sustained push of 71-500 N. Measured play had **every**
round expire on the clock and be settled on position — no ring-out at all. The 4.0 ring, a
2.5 m opening stand-off and 0.55 friction cut the drive to 1.5 m against a 376 N wall.
`Systems_SumoMatchManager` does **not** read `GameTuning` — it carries its own copy of the
ring, spawn gap and timeout in every training scene, so a change here must be written into
those scenes too or the brains train on an arena the game does not have.

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

Physical fidelity, so nobody has to re-derive it: gravity is Earth's −9.81 (project
setting *and* re-asserted at runtime), and the baseline body is **69.6 kg** —
trust `Agent_BipedBody.TotalMass`, not prose. Segment masses track Winter's anthropometric
fractions closely (thigh 10.1% vs 10.0, foot 1.44% vs 1.45) with the arms ~20-28% heavy.
**Segment lengths** now track Winter too, re-derived for a 1.76 m body in the realism pass:
every limb had been 8-18% short (shank 0.38 vs 0.246H = 0.433, foot 0.22 vs 0.152H = 0.268)
while the trunk ran long. The joint anchors are *derived from* those lengths — change a
segment and every anchor above it moves with it, or the chain comes apart. Verified by
measuring anchor separation across all 13 joints: 0.0000 m.

**The sign convention is measured, not guessable, and it bit this project hard.**
`HingeJoint2D.jointAngle` here is the **negative** of the child segment's geometric rotation
relative to its parent — probe it with
`Vector2.SignedAngle(parent.transform.up, child.transform.up) * facingSign` and the ratio is
−1.00 on every joint. Ranges written as if they were geometric therefore bend the limb
*backwards*. For the whole life of the project the three asymmetric joints were inverted:
the knee swung the shin **forward** and the elbow swung the forearm **backward** (a bird
leg), and the hip had 120° of extension against only 30° of flexion, so no fighter could
crouch and drive off a loaded leg. Corrected to hip (−120…30°), knee (0…150°), elbow
(−150…0°) — flexion is *positive* jointAngle at the knee, *negative* at hip and elbow.
`Agent_Biped.KneeBendFactor()` reads the knee as positive and must move with these.

Ankle (±25°), the three spine joints (±20° each) and shoulder (±120°) are clamped to roughly
human TOTAL range and are genuinely **symmetric**, so the sign error never touched them.
Leg torques are realistic (hip 300, knee 250, ankle 120 N·m); the upper body was 2-4× human
and was brought back to spine 180 each / shoulder 80 / elbow 60.

When you change a joint range, verify it **parent-local**, never in world space: with gravity
off the whole body counter-rotates to conserve angular momentum, so a world-space "is the
foot behind the knee" test reads the body's drift, not the joint. Measure the child's centre
via `parent.transform.InverseTransformPoint(...)` at rest and at full flexion and compare the
delta — that is rotation-invariant and gave the unambiguous answer here.

Joints carry **passive resistance** — a restoring torque at 6% of each joint's motor budget
per 90°, plus 10% per 400°/s of damping, applied every physics step in
`Agent_BipedBody.FixedUpdate`. `HingeJoint2D` has no spring (that is 3D-only), hence the
explicit torque. Bodies also damp at 0.25 linear / 0.8 angular, up from 0.05.

The head is still not a separate body — a compound collider on Chest with its 6 kg folded
into Chest's 13, so there is **no neck joint** and the head cannot bob or whip. Adding one
means giving it its own rigidbody and an *unpowered* hinge: a driven neck would be a 14th
action, and `Agent_Biped.CollectObservations` loops over `ActionCount`, so it would change
the action space and the 44-obs vector together and invalidate every brain's input *and*
output layer.

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
`inferenceModel`, `walkModel`), and **every reward-shaping coefficient for both the sumo
and walk schools**. Fighter personality (Nick = light and mobile, Kim = heavy planted
anchor) lives in the asset and the YAML header comments, never in `Agent_Biped`.

When adding a shaping coefficient, default it to the constant the code used before, so an
untuned character keeps training exactly what it always did — that is what makes it safe
to add these mid-project. Episode **terminals** stay hardcoded (walk: fall −1, graduation
+3) so different characters' runs stay comparable on one reward scale. `Systems_MatchRoster`
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
Build settings are exactly two scenes: `SCN_TOURNAMENT` (index 0) and `SCN_SUMO`. The game
therefore always boots into the bracket, which loads `SCN_SUMO` for every bout and gets the
winner back via `Systems_TournamentReporter`. `SCN_SUMO_ICE` and `SCN_SUMO_STICKY` were
deleted (2026-07-28) and the bracket no longer rotates arenas — `Systems_TournamentBracket`
holds a `const string ARENA_SCENE = "SCN_SUMO"` rather than a serialized array, precisely so
SCN_TOURNAMENT's stale serialized copy of the old three-scene list cannot resurrect them.
`Systems_SumoArena.style` still has the ice/sticky values; they are simply unused.
`SCN_WALKVIEW` views a locomotion brain and is **not** in build settings. Arena scenes are
**baked**: an editor pass ran `Systems_SumoArena.Build()` and saved the children, so `Awake`
only rebinds references.

Training scenes, one per surviving purpose — every one either produced a deployed brain or
is the newest template for a training mode. Keep that rule when adding or retiring scenes:
a scene that produced a shipped brain is the only way to reproduce it.

| Scene | Purpose |
|---|---|
| `SCN_TRAIN_MATT_AGGR` / `SCN_TRAIN_STANDARD` / `SCN_TRAIN_NICK` / `SCN_TRAIN_KIM` | **unified** self-play sumo + walk, one per fighter |

**One brain per fighter, trained on both tasks at once (2026-07-28).** Walk and fight used
to be separate policies with separate scenes, and `BeginWalkIn` hot-swapped the model
mid-match. They are now a single policy told apart by a task flag in the observation
vector — `1` in a real bout, `0` when the four "opponent" slots carry a virtual walk
target instead. That flag took the vector from 44 to 45 slots and invalidated every brain
trained before it.

Each unified scene therefore holds two populations under ONE behavior name:
- 4 sumo agents on two self-play arenas;
- 6 walk agents on a lane 60 m below, `mode = Walk`, no opponent.

Walk agents are over-provisioned 6-vs-4 on purpose: self-play periodically freezes one
team as the ghost and DISCARDS its experience, and walk agents sit on a team like anyone
else, so an even split would quietly halve the walk sample rate.

The retired `SCN_TRAIN_WALK_*` and `SCN_TRAIN_RECOVER4` scenes, their env build entries and
their configs are gone. `Mode.Recover` still exists in `Agent_Biped` but nothing references
it — the walk-in used to set it and now sets `Mode.Walk`. It is kept only because folding
get-up training back in as a third lane is the obvious next use for it.

A gait still has to be learned on the physique it runs on — Kim's 1.45 mass does not accept
a gait learned at 1.0 — which is why the walk lane lives in each fighter's own scene rather
than in one shared walk scene.

**Verify a scene's character assignment by reading the saved `.unity` file, not the script
log.** A wiring pass once reported "4 walkers -> Matt" while the scene on disk still held
`character: {fileID: 0}`, and the resulting env trained the wrong policy for 1.5M steps
before the mismatch was spotted. Grep for `character: {fileID: 11400000` and check the guid.

**A walk agent's `facingSign` must point AT its target, and the only safe test is the sign
of `xLocal` at spawn.** Walk progress and graduation are both measured in the facing-local
frame — `xLocal = (Torso.x - arenaCenterX) * facingSign`, graduating at `xLocal > -0.3` —
so a walker whose target is 5 m to the right but whose `facingSign` is `-1` reads its start
line as *5 m past the finish*. It banks the hardcoded +3, ends the episode on its first
decision, respawns, and repeats forever: never a step of travel, never a step of learning,
and a torrent of free reward. It is invisible from the outside — the ragdoll stands there
looking merely untrained, and the console is silent.

It cost 2.6M steps of `matt_unified01` before it was caught (2026-07-28); Matt, Kim and Nick
were all built with the wrong sign and only Standard was correct. **On TensorBoard the tell
is a mean reward pinned just under the graduation bonus with near-zero variance** — Matt sat
at `2.999 ± 0.015`, which is not a policy converging but one terminal firing every episode.
After any change to the walk lane, assert every walker spawns at `xLocal < -0.3`; if
`StepCount` stays 0 while `CompletedEpisodes` climbs by one per decision period, this is why.

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

**Judge a self-play fight run on ELO, not mean reward.** They can move in opposite
directions: a re-tune once climbed to reward ~36 while its ELO fell 1198 → 1140, which
means the policy learned to farm shaping (closing, cadence, impact) instead of winning
bouts. Mean reward is measured against a moving opponent pool and is not comparable across
runs; ELO is. Accept a fight run on the **shape** of the ELO curve, not a single threshold:
a monotonic slide is regression (reject), oscillation within a point or two of the start is
noise (fine — flat ELO against a pool that is itself retraining means the policy kept pace). A fighter with large,
easily-farmed shaping is the one that will fail this — the fix is its character sheet's
shaping-to-win ratio, not more training.

**Changing collider shape or mass invalidates every brain**, because they were trained
against the old dynamics. The recovery is cheap: rebuild each env, warm-start from the
shipped checkpoint, and run a short corrective pass at a reduced learning rate (1-3M steps
against trunks of 12-45M) rather than retraining from scratch.

Restart rules: physics/observation/action changes ⇒ new run-id or `--force` (cold);
parameter-only tweaks ⇒ `--resume`. **Kill TensorBoard *before* launching with `--force`,
and restart it after.** It holds handles on the run dirs on Windows, and a `--force` fired
while it is live leaves the old contents *in place* — silently, with no error. That is not
cosmetic: the surviving checkpoints outrank the new run's numerically for a long while, so
`DeployBrain.DeployLatestCheckpoint` will ship a brain from the run you thought you deleted.
`Deploy`/`DeployWalk` are safe because they read the top-level `<Behavior>.onnx`, which a run
rewrites only when it finishes.

To stop training: kill `mlagents-learn.exe` itself. Killing only the env worker EXEs does
nothing — the trainer auto-respawns them. On any disconnect the trainer saves a final
checkpoint before exiting.

Deployed models are always **overwritten in place** at `Assets/Agents/<Name>_v01/<Name>.onnx`
so the `.meta` GUID (and every reference to it) survives; `DeployBrain` does this and also
sets the character asset's `inferenceModel`. Copying a checkpoint does not require stopping
a headless run.

`Training/results` **is** the TensorBoard logdir, so treat it as a curated list, not a
dumping ground: it holds only runs that back a deployed brain (currently eight — a sumo
and a walk run for each of the four fighters). Everything
else goes elsewhere —

- prune a deployed run to its final `<Behavior>.onnx`, `checkpoint.pt`,
  `configuration.yaml`, `run_logs/` and tfevents; the numbered per-step checkpoints are
  ~140 MB per run and nothing deploys from them;
- a checkpoint kept only as an `--initialize-from` source is weights, not history — park it
  in `Training/trunks/` (gitignored, outside the logdir);
- staging dirs must sit *inside* `results/` at launch (`--initialize-from` resolves relative
  to `--results-dir`) but hold no history, so they show up as empty TensorBoard runs —
  delete them once the run is stepping and re-create with one `Copy-Item`;
- superseded runs are deleted outright.

`Training/README.md` maps every kept config to the run and deployed brain it produced.

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
  return an MCP retry error **while still executing** — poll for the `BUILD RESULT:` line.
  **`console-get-logs` is the reliable source; prefer it.** Which *file* is the live log
  depends on how the editor was launched, so check both before trusting either: on
  2026-07-28 the repo's `Logs/Editor.log` was live (1.0 M lines, current mtime, all the
  `BUILD RESULT` / `DEPLOY RESULT` / `HARNESS` lines) while
  `%LOCALAPPDATA%\Unity\Editor\Editor.log` held 64 stale lines — the reverse of what this
  file used to claim. Compare line count and mtime rather than assuming a path.
  Note that a log file may not flush during Play mode, so `console-get-logs` is the only
  dependable way to watch something like `MatchTestHarness` progress live.
- Those MCP retries each **re-invoke** the call, so one blocking build actually runs several
  times back-to-back. Harmless for an idempotent build; do not use this pattern for anything
  that appends or increments.
- Never edit a `.cs` file while a player build is running: the recompile aborts the build
  (`BUILD RESULT: Unknown`) and leaves `EditorUtility.scriptCompilationFailed` stuck true.
  `CompilationPipeline.RequestScriptCompilation(CleanBuildCache)` clears the stale flag.
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
