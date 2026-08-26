# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

PoSumo: a Unity 6000.5.4f1 (2D URP) game where physics-ragdoll bipeds learn sumo
wrestling via ML-Agents, then fight each other in a playable, presentation-dressed
match/tournament layer. Portrait orientation, Android target
(`com.punkoutersoftware.posumo` — this file said `com.punkouter26.posumo` until
2026-08-01; the constant in `BuildAndroidAAB.APP_ID` is the one that actually ships
and it matches `companyName`, so the doc was the stale side. An application id is
permanent once published, so check the constant, never this sentence). `DESIGN.md` is the original approved spec and is now
partly historical — where it disagrees with this file or the code, the code wins
(notably: the ring is a raised dohyo, not `|x| > 7`; its half-width is **3.5 m** and is read
from `GameTuning.asset` because the arena scenes serialize their own copy).

The ring went 2.75 → 5.5 in the realism pass and back to **4.0** on 2026-07-28, because 5.5
made a decisive finish impossible: with the fighters opening 0.9 m apart there was 4.6 m of
mat to drive an opponent across, and at 0.9 friction the force to start sliding a 69.6 kg
body is 614 N against a measured sustained push of 71-500 N. Measured play had **every**
round expire on the clock and be settled on position — no ring-out at all. The 4.0 ring, a
2.5 m opening stand-off and 0.55 friction cut the drive to 1.5 m against a 376 N wall.
`Systems_SumoMatchManager` does **not** read `GameTuning` — it carries its own copy of the
ring, spawn gap and timeout in every training scene, so a change here must be written into
those scenes too or the brains train on an arena the game does not have. Its **code
defaults were finally corrected on 2026-08-15** and now read 3.5 / `startHalfRange
(1.7, 3.5)` / `spawnGapHalf 2.5` / `roundTimeoutSeconds 20`, matching both
`GameTuning.asset` and what the four training scenes serialize. They had read
5.5 / `(1.7, 5.5)` / 1.2 / 30 — the code and the scenes disagreed by 2 m of mat for
months, and only a new scene would ever have been bitten by it.
Grep the `.unity` files, not the `.cs`, to learn what an existing env trains against;
the `.cs` is what a NEW scene inherits, so both have to be right.

> **That warning fired for real on 2026-08-07, and the fix is the template for next time.**
> `GameTuning.asset` held `ringHalfWidth: 3.5` (a **7 m** mat) while all four training
> scenes serialized **4** (an **8 m** mat). Every brain up to and including the
> `*_fatigue01` runs therefore learned a ring-out boundary **0.5 m further out than the one
> it fights on** — it believed it had half a metre of mat that does not exist, on both
> sides. Nothing errored; the arena simply ended sooner than the policy expected.
>
> Resolved by moving TRAINING to the shipped value: the two `Systems_SumoMatchManager`
> referees in each of the four `SCN_TRAIN_*` scenes are now `ringHalfWidth: 3.5` with
> `startHalfRange (1.7, 3.5)`, and the envs were rebuilt. The game is the ground truth;
> a brain should be fitted to the arena players actually see.
>
> **`Agent_Biped` carries its OWN `ringHalfWidth` too** (it feeds the two edge-distance
> observations), and the training scenes still serialize `4` on the sumo agents. That is
> inert — both referees overwrite it at runtime (`Systems_SumoMatchManager` ~L67,
> `Systems_GameMatchManager` ~L354) — but it is exactly the kind of stale serialized value
> this file warns about, so do not "fix" it and do not trust it when grepping.
>
> The `*Ring01.yaml` configs adapt each trunk to the corrected arena, warm-starting from
> `<name>_fatigue01` at a reduced learning rate. Note this does NOT by itself fix the
> ring-out rate: measured play at 3.5 gave 29% ring-outs against 57% `downOutSeconds`.

There are four **trained** fighters — **Matt**, **Standard**, **Nick**, **Kim** — each
with an `.onnx`, a `*_Character.asset` and a `MANIFEST.md`. `Assets/Agents/ROSTER.md` is
the roster overview; there is no code mirror of it.

**A fifth entry, `Bot_v01`, is in the roster ON PURPOSE and is NOT a defect — do not
"fix" it by deleting it or dropping it from the seeding** (confirmed 2026-08-07). It holds
only `Bot_Character.asset` with `inferenceModel: {fileID: 0}`: no `.onnx` and no manifest.

**It does NOT collapse as a ragdoll, and this file said it did until 2026-08-25.** The
asset carries **`useBot: 1`**, so `Agent_Biped.Awake` sets `BehaviorType.HeuristicOnly` and
the fighter is driven by `Agent_Bot` — 822 lines of hand-written rules, a real opponent
with no neural policy at all. Measured in a played bracket on 2026-08-25 it **won its
quarterfinal 2-0 by ring-out** (`[ROUND] 1 RingOut winner=BOT t=9.0s`, `t=7.0s`). Treat it
as the project's rules-based baseline, which is what makes it useful: toggling `useBot` on
any character compares the bot and a trained brain on identical physique and reward setup.

The `Systems_MatchRoster` error it used to log every match — "will have no brain and will
not fight", about a fighter that had just won — is now suppressed for `useBot` characters.
An Error-level line that is routinely false is worse than no line: it teaches whoever is
reading the console to skip real ones.

Consequence: the 8-slot bracket no longer seeds four fighters twice each. With five
entries it draws Standard ×2, Matt ×2, Nick ×2, Kim ×1, Bot ×1.

`ROSTER.md` and the four per-fighter `MANIFEST.md` files were rewritten on 2026-08-02 and
now describe 45 obs, one unified brain each, and the `*_unifiedNN` runs that actually back
the shipped `.onnx` files. They had predated the walk+fight merge. The character assets and
`Agent_Biped` remain authoritative over all prose — fix a manifest when you touch its
fighter rather than trusting it.

`Training/results/` is gitignored **and is not present in a fresh clone** — the deployed
`.onnx` files under `Assets/Agents/` are the only brains that ship. Anything in this file
about resuming, `--initialize-from` or `DeployLatestCheckpoint` assumes you have first
re-run training locally to recreate that directory.

## Toolchain versions (validated in production — treat as the required set)

| Layer | Tool | Version | Notes |
|---|---|---|---|
| Engine | Unity Editor | **DISPUTED — see below** | `ProjectVersion.txt` says 6000.5.8f1 (changeset 5cb7df797b7d); the only editor INSTALLED and the one actually running is **6000.5.6f1** (measured live 2026-08-25). Drifted 6000.5.4f1 → 6000.5.6f1 → 6000.5.8f1 → back to 6000.5.6f1, each time WITHOUT this table moving. Re-read `ProjectSettings/ProjectVersion.txt` **and** `ls "C:/Program Files/Unity/Hub/Editor/"` — this row is not authoritative and neither source alone is sufficient |
| Engine | Unity Hub | 3.x | headless CLI broken — install modules via UI |
| Package | com.unity.ml-agents | **4.1.0** | LOCAL `file:` package with patches. Upgraded from 4.0.0 (release_23) on 2026-08-06 — re-fetching is now a documented procedure, not a prohibition, but it still LOSES the patches below |
| Package | com.unity.ai.inference | 2.6.1 | auto-dependency of ML-Agents (`Unity.InferenceEngine.ModelAsset`). Was 2.2.1 |
| Package | URP | 17.5.0 | project template |
| MCP | unity-mcp-cli (npm) | **not installed** | `npm ls -g` is empty and there is no global bin. Every `unity-mcp-cli` invocation in the `.claude/skills/*/SKILL.md` files therefore fails — use `Tools/unity.py` instead |
| MCP | com.ivanmurzak.unity.mcp | **0.90.0** | measured live 2026-08-25; was 0.88.0. Ships NuGet `McpPlugin` / `McpPlugin.Common` **8.3.0** under `Assets/Plugins/NuGet/` — those move WITH the package, so `.nuget-installed.json` and the two DLLs are part of the same version decision. Upgrading it reverts the plugin `.meta` platform flags — re-run *PoSumo → Fix Plugin Platforms* |
| MCP | com.ivanmurzak.unity.mcp.animation | 1.2.28 | add-on, resolved against core 0.90.0 |
| MCP | com.ivanmurzak.unity.mcp.particlesystem | **1.2.30 — BACK, and compiling** | removed 2026-08-16 because 1.2.30 did not match core **0.88.0** (it referenced a namespace `AIGD` that version did not ship) and **the whole project failed to compile**, which blocks Play mode entirely. Under core **0.90.0** the same 1.2.30 resolves as a matched dependency and the console is clean — verified live 2026-08-25. The add-on was never the defect; the *pairing* was |
| MCP | com.ivanmurzak.unity.mcp.cinemachine | 1.0.14 | likewise back and compiling under 0.90.0, having been dropped for the same mismatch |
| MCP | com.coplaydev.unity-mcp | 10.1.0 | |
| MCP | com.besty.unity-skills | 2.2.1 | HTTP server port 8090 |
| Python | Python | **3.10.11** | hard range: >=3.10.1, <=3.10.12 |
| Python | mlagents / ml-agents-envs | **1.2.0.dev0** | **editable** (`pip install -e`) against `Training/ml-agents/`, so the source patches ARE the installed copy. Built from 4.1.0 source; envs is patched |
| Python | gymnasium / pettingzoo | 1.3.0 / 1.26.1 | **new requirements in 4.1.0** — 4.0.0 did not need gymnasium at all and shipped against pettingzoo 1.15 |
| — | ML-Agents comms API | **1.5.0** | `Academy.k_ApiVersion` (C#) and `UnityEnvironment.API_VERSION` (py) must be EQUAL or the handshake is refused. Both moved 1.4.0 → 1.5.0 in 4.1.0, so C# and Python must be upgraded together |
| Python | torch | **2.5.1** (+cpu) | PIN — 2.6+ breaks ONNX export |
| Python | setuptools | **69.5.1** | PIN — 70+ removes pkg_resources |
| Python | numpy | 1.23.5 | pinned by mlagents |
| Python | onnx | 1.15.0 | |
| Python | tensorboard | 2.20.0 | always run during training |
| Android | Build Support module | **6000.5.6f1** | must match the editor version — so it moves every time the row above does. The 2026-08-15 note here ("only `6000.5.8f1` is installed") is **stale and was measured false on 2026-08-25**: only `6000.5.6f1` is present under `C:/Program Files/Unity/Hub/Editor/`. The installed module therefore matches the RUNNING editor but not the version `ProjectVersion.txt` names — resolve that before trusting an Android build |
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

**Three of these moved without the table moving with them, and were re-measured on
2026-08-01 rather than trusted:** the editor went 6000.5.4f1 → **6000.5.6f1**
(`ProjectSettings/ProjectVersion.txt` is authoritative), `com.unity.ai.inference` went
2.2.1 → **2.6.1**, and URP reads **17.5.0** where this table claimed 17.6.0
(`Packages/packages-lock.json` is authoritative for both — `manifest.json` records what was
asked for, the lock records what resolved). Nothing in the shipped game was observed to
break: the project compiles clean and the console is error-free.

The one to watch is **`com.unity.ai.inference`**, because it is not a passive dependency
here. It is what runs every `.onnx` at inference time, and it is the package whose
editor-only `Google.Protobuf_Packed.dll` collided with ML-Agents' copy — the collision
that local patch 2 below exists to work around. A minor-version move is exactly when that
patch could stop holding. If inference ever goes silently wrong (a fighter that stands
still or twitches rather than erroring), suspect this before suspecting the brain.

> **An MCP add-on package can break the entire project, and the symptom does not point at
> it.** On 2026-08-16 `com.ivanmurzak.unity.mcp.particlesystem` 1.2.30 was resolved against
> core 0.88.0, referenced a namespace (`AIGD`) that version does not ship, and left
> `EditorUtility.scriptCompilationFailed` **true**. Nothing in `Assets/` was wrong — zero
> errors in project code — but **Play mode is blocked while that flag is set**, so the game
> could not be run at all. These add-ons version independently of the core package and
> nothing verifies the pair.
>
> Fixed at the time by deleting the line from `Packages/manifest.json`. Two follow-ons
> worth knowing: the removal left a stale `CS2001: Source file ... UnityTokenRefresher.cs
> could not be found` that `CompilationPipeline.RequestScriptCompilation(CleanBuildCache)`
> did NOT clear — only an **Editor restart** did; and `EditorApplication.OpenProject(cwd)`
> is a clean way to restart it from the bridge.
>
> **Superseded 2026-08-25, and the correction is the lesson.** Core moved to **0.90.0** and
> `particlesystem` **1.2.30 — the very same version — now resolves clean**, alongside
> `cinemachine` 1.0.14 and `animation` 1.2.28, with `scriptCompilationFailed` false and an
> empty console. The add-on was never the defect; the **pairing** was. So the rule is not
> "never install the add-ons" — it is **never assume an add-on version and a core version
> go together, in either direction**. Re-check the pair after any move on either side.
>
> First diagnostic when anything refuses to run: `scriptCompilationFailed`, then group the
> console errors by package. If they all come from `Library/PackageCache/`, it is not your
> code.

### 24 packages were removed on 2026-08-25 — what went, and how it was decided

`manifest.json` went 58 → 34 → **39**: five had to go back, and why is the important part.

A removal must clear **four** tests. The first three are obvious and were applied:
**no namespace reference** in `Assets/**/*.cs`, **no asset of the type it imports**, and
**nothing in `packages-lock.json` depending on it**.

> **The fourth test is the one that bites: grep the OTHER PACKAGES' OWN SOURCE.**
> `Library/PackageCache/*/**.cs`, not just `Assets/`.
>
> Measured 2026-08-25: removing `com.unity.ugui`, `com.unity.modules.ai`,
> `com.unity.modules.video` and `com.unity.modules.xr` left
> `EditorUtility.scriptCompilationFailed` **true** and blocked Play mode entirely, because
> `com.besty.unity-skills`, `com.coplaydev.unity-mcp` and `com.ivanmurzak.unity.mcp` all
> reference `UnityEngine.UI`, `EventSystems`, `AI`, `Video` and `XR` **in their own Editor
> scripts without declaring any of it in their package manifests**. Neither an `Assets/`
> grep nor the lock file's dependency graph can see that — the lock records what a package
> DECLARES, and these packages declare nothing.
>
> The failure looks like the 2026-08-16 MCP add-on incident and has the same shape: zero
> errors in project code, everything broken anyway, `Play` unavailable. First diagnostic is
> the same — check `scriptCompilationFailed`, then group console errors by path. If they
> are all under `Library/PackageCache/`, it is not your code.
>
> `com.unity.collab-proxy` went back for the same class of reason: removing it left
> `Unity.PlasticSCM.Editor` throwing `TypeLoadException` on a missing `unityplastic`.

**Restored and must stay:** `com.unity.ugui`, `com.unity.modules.ai`,
`com.unity.modules.video`, `com.unity.modules.xr`, `com.unity.collab-proxy`.

Gone: `visualscripting` (+`ugui`, whose only dependent it was), `2d.animation`,
`2d.aseprite`, `2d.psdimporter`, `2d.spriteshape`, `2d.tilemap.extras` (+`2d.tilemap`),
`2d.tooling`, `multiplayer.center`, `pipeline` (0.4.0-**exp**), `collab-proxy`, and twelve
`com.unity.modules.*` with no code reference at all — terrain, terrainphysics, vehicles,
cloth, wind, xr, video, umbra, ai, unityanalytics, adaptiveperformance, vectorgraphics.

The asset census is what makes this safe and is worth re-running before adding anything
back: the project contains **no `.anim`, `.controller`, `.playable`, `.psd`, `.aseprite`,
`.prefab`, tilemap or spriteshape assets at all**, and no UGUI Canvas. Everything is
built in code at runtime, so the importers had nothing to import.

**Two that look removable and are NOT:**
- **`com.unity.timeline`** — nothing here uses it, but `com.besty.unity-skills` depends on
  it. Removing it breaks that package, not the game.
- **`com.unity.inputsystem`** — `Systems_GameMatchManager` reads `Keyboard.current.escapeKey`
  to pause and `Pointer.current.press` / `spaceKey` to continue. `.claude/rules/architecture.md`
  claimed no runtime script read it; that was stale, and it is corrected there now.

Re-measure rather than trusting this table when something behaves oddly; a version drifting
under a "required set" heading is how a project ends up debugging the wrong layer.

## Critical version pins (do not "upgrade")

- **ML-Agents**: local package at `Training/ml-agents/com.unity.ml-agents` (**4.1.0**),
  referenced via `file:` in `Packages/manifest.json`. It contains required local patches
  — see "Local patches" below. Re-fetching loses them; the upgrade procedure is recorded
  there. The whole `Training/ml-agents` tree is **tracked in this repo** (~2300 files), so
  a botched upgrade is recoverable with `git checkout` rather than a re-clone.
- **Python venv** `Training/venv`: `mlagents 1.2.0.dev0` installed **editable**
  (`pip install -e`) against that same tree — so patch 3 in the source IS the installed
  code, and there is no second copy in `site-packages` to keep in sync. **torch 2.5.1**
  (newer torch breaks ONNX checkpoint export; 4.1.0 permits `<=2.8.0`, but the export
  problem is ours, not theirs — stay at 2.5.1), **setuptools 69.5.1** (newer removes
  `pkg_resources` and breaks `mlagents-learn`). Never `pip install --upgrade` in this
  venv; when a new dep is genuinely required, install it with `-c` against a constraints
  file pinning torch/numpy/setuptools/protobuf/onnx, which is how gymnasium and pettingzoo
  were added for 4.1.0 without disturbing anything else.

## Local patches (re-apply if ml-agents source is re-fetched)

**Patch 1 is retired — do not re-apply it.** 4.1.0 fixed it upstream and better:
`Match3ActuatorComponent` now guards with `#if UNITY_6000_3_OR_NEWER` and calls
`gameObject.GetEntityId().GetHashCode()`. The old local patch used a plain
`gameObject.GetHashCode()` unconditionally. Re-applying it would overwrite a correct
upstream fix with a worse one. It is kept here only so the numbering below still matches
older commits and notes.

2. `Plugins/Google.Protobuf_MLAgents.dll` — renamed from `Google.Protobuf_Packed.dll`
   (file, meta, **and internal assembly name**, rewritten with Mono.Cecil) because
   `com.unity.ai.inference` ships an editor-only DLL with the identical original name and
   player builds resolve the reference to the wrong one. All 7 asmdefs reference the new
   name. **Still required at 4.1.0 / ai.inference 2.6.1** — both sides still ship the
   colliding name, verified 2026-08-06.
3. `Runtime/Grpc/Unity.ML-Agents.CommunicatorObjects.asmdef` — `defineConstraints` must be
   `["UNITY_EDITOR || UNITY_STANDALONE"]`. Upstream ships it EMPTY, so the assembly compiles
   for Android and demands `Google.Protobuf_MLAgents.dll`, whose `.meta` carries
   `Exclude Android: 1` — the Android player build then dies with dozens of
   `CS0400: The type or namespace name 'Google' could not be found` across
   `Runtime/Grpc/CommunicatorObjects/*.cs`. The trainer connection is meaningless on a
   phone, so the constraint is the correct fix and enabling Android on the DLL is not.
   **This patch was undocumented and was silently lost in the 4.1.0 upgrade** (`6ca7ee6`),
   which is how it was found: the first Android build attempted afterwards failed, on
   2026-08-06. Nothing warns you — the Editor and every training env compile clean,
   because both are `UNITY_EDITOR || UNITY_STANDALONE`. **Only a player build for a mobile
   target ever exercises this**, so verify an Android build after any ml-agents re-fetch
   rather than trusting a green console.

4. `mlagents_envs/environment.py::_check_communication_compatibility` — `StrictVersion`
   replaced with a manual tuple parse; the original crashes worker auto-restarts. **Still
   required at 4.1.0**, which still does `from distutils.version import StrictVersion`.
   Now only ONE copy to patch (the source tree), because the venv install is editable.

**How the 4.1.0 upgrade was actually done**, since the next one will look the same:
clone upstream to a scratch dir, apply patches 2, 3 and 4 to that *staging* copy, and only
then swap it over `Training/ml-agents` — so the Editor never watches a half-patched
package. (It was done with patch 3 MISSING, because patch 3 was undocumented at the time;
that is the whole reason it is written down now. Diff the staged tree's `.asmdef` files
against the outgoing one before swapping — an asmdef carries no version number and a lost
`defineConstraints` entry is invisible until a platform you rarely build for fails.) The Cecil rename cannot be compiled against directly in the MCP `execute_code`
sandbox (`Mono.Cecil` resolves at runtime but is not a compile-time reference), so drive
it by **reflection** off `System.Reflection.Assembly.Load("Mono.Cecil")`. Carry the
existing `.dll.meta` forward rather than taking upstream's, to keep the plugin GUID
stable. Then `pip install -e` both python packages with `--no-deps`.

Of 163 differing `Runtime/*.cs` files between 4.0.0 and 4.1.0, only **25** are real
changes — the rest are CRLF-only. Diff with `--strip-trailing-cr` or you will badly
over-estimate the blast radius.

## Commands at a glance

Almost nothing here is a shell command — the editor menu is the build system. Details for
each live in *Editor menu tools* and *Training workflow* below.

| Goal | How |
|---|---|
| Play the game | **Always** open `SCN_TOURNAMENT` and enter Play mode from there (it loads `SCN_SUMO` per bout) |
| Compile / import after editing `.cs` outside the editor | MCP `assets-refresh` (ForceUpdate) |
| Behavioural test | Play mode in `SCN_SUMO`, then `MatchTestHarness.Run(n)` via MCP `script-execute` → `HARNESS RESULT:` |
| Ship an Android build | *PoSumo → Build Android APK* / *Build Android AAB (Play release)* |
| Build a training env | *PoSumo → Build \<Name\> Training Env* → `Builds/<Name>Env/<Name>Env.exe` |
| Train | `Training\Start-Training.ps1` (wraps `mlagents-learn.exe` + TensorBoard + `--base-port`) |
| Stop training / kill orphans | `Training\Stop-Training.ps1` (`-Prune` also clears event-less runs) |
| Watch a live env | `curl http://127.0.0.1:8787/metrics` — see *Telemetry*; needs an env built after 2026-08-07 |
| Check portrait layout on real aspects | `python Tools/portrait_check.py` — see *Portrait layout checking* |
| Drive the Editor from a shell | `python Tools/unity.py <ping\|scene\|play\|stop\|errors\|shot\|exec\|raw>` |
| Ship a brain | *PoSumo → Deploy \<Name\> Brain* |
| Unit test | `python Tools/unity.py raw run_tests '{"mode":"EditMode","assemblyNames":["PoSumo.Tests.EditMode"]}'` then poll `get_test_job` |

There is no lint step; the hooks in `.claude/hooks/` are the static checks, and they run
on edit rather than on demand.

There **is** a small EditMode unit suite (`Assets/Tests/EditMode`, 23 tests, added
2026-08-07) — but read what it covers before assuming a green run means anything. It
tests `Systems_CareerLadder` and `Reward_Context.San`, which are the only pure functions
in the project: no MonoBehaviour, no physics, no disk. Everything this game is actually
about — the ragdoll, the referees, the brains — is behavioural and is still verified by
`MatchTestHarness.Run(n)` and the screenshot flow. Do not report a passing unit run as
evidence that a body, reward or scene change works.

**Pass `assemblyNames`, not `testFilter`.** The bridge's `run_tests` silently ignores an
unrecognised filter key and runs everything, which here means the ~200 tests that ship
inside `com.besty.unity-skills` as well. One of those
(`PerceptionSkillsTests.SceneSummarize_CountsObjectsCorrectly`) fails against this
project's scenes and is not ours to fix — an unfiltered run therefore always reports a
failure.

Two things the suite must never do, both of which would be silently destructive:
`Systems_CareerStats` writes `career.json` in `Application.persistentDataPath` on every
mutation, so a test that calls `Get`/`RecordMatch`/`ResetAll` would wipe the player's real
career; and the `.asmdef` is Editor-only with `defineConstraints: ["UNITY_INCLUDE_TESTS"]`
so none of it can reach an Android build.

## Architecture

### Everything about the biped is built at runtime
Scenes contain only manager objects. `Agent_BipedBody.Awake()` constructs the 14-part
ragdoll from code-defined tables (`PART_DEFS` / `JOINT_DEFS`): 4-segment articulated
spine (pelvis→lowerback→upperback→chest), legs and arms — **13 hinge motors**, mirrored
via `facingSign` (one policy works both directions because all observations are
multiplied into a facing-local frame). Intra-biped collisions are disabled pairwise
(limbs pass through their own body by design). `massScale` / `widthScale` /
`torqueScale` come from the character asset, so physique is data, not code.

**The drawn edge is the colliding edge, everywhere, and that rule was tightened
2026-08-02.** Limbs are ellipse sprites over unit `CapsuleCollider2D`s (the same shape
under the part's non-uniform scale); trunk and feet are exact rectangles over
`BoxCollider2D`s. Two things had been drawing somewhere their physics was not:
- the **head art** was aspect-fitted to 0.39 m on HEIGHT against a 0.5 m `headDiameter`
  hitbox, so every head was hit through a ring of empty space (and a wide photo — Nick's
  is 1.33:1 — overflowed it the other way). It now fits `headDiameter` on the sprite's
  LONGEST side, and the plain fallback circle went 0.312 → `headDiameter` too. Art only:
  the collider is untouched, because changing it would invalidate all four brains and
  `Training/results/` holds no checkpoint to warm-start a correction from.
- **`Systems_SoftBodyJiggle` is off.** It slid the torso Art child up to
  `0.055 * widthScale * sqrt(massScale)` m off its own rigidbody. `enableJiggle` was a
  public serialized field, so 42 stale `enableJiggle: 1` values across five scenes ignored
  any code default — it is now `private const bool ENABLE_JIGGLE = false`, which makes
  those serialized values inert (same trick as `Systems_TournamentBracket.ARENA_SCENE`).

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

**Muscle torque now FATIGUES (2026-08-05), and this invalidated all four brains.**
Each joint carries a 0..1 fatigue state integrated at 50 Hz in `IntegrateFatigue` — the
two-state reduction of Xia's three-compartment muscle model — and `ApplyMotor` multiplies
the joint's torque budget by `1 - 0.35 * fatigue`. Constants: `FATIGUE_RATE` 0.06/s,
`RECOVERY_RATE` 0.10/s, `FATIGUE_DEPTH` 0.35. A fighter that holds maximum effort through a
whole 20 s round arrives at the bell ~0.70 fatigued and ~25% weaker; a fully spent joint
still delivers 65%.

Three things about it are load-bearing:
- **Load is read from `joint.GetMotorTorque(dt)`, not from the action vector.** Bracing
  against a shove is a near-zero action holding a near-maximum torque, and any measure
  taken from the actions would score the most expensive thing in sumo as resting.
  Isometric work is most of what a bout is.
- **It stacks with the Hill force-velocity term on purpose.** Hill is how weak you are
  right now because of how fast you are moving; fatigue is how weak you are because of
  what you have already spent. Eccentric bracing keeps its 1.5× gain and still pays the
  fatigue tax, so a braced fighter can be worn down — which is how a real bout is won.
- **`ResetPose` clears it, and clears it BEFORE `RestoreMotors`** (which now scales the
  torque it writes back). Carrying fatigue across an episode boundary would make an
  episode's difficulty depend on how hard the *previous* one was fought — a hidden
  non-stationary term the agent cannot observe at t=0.

`Stamina` (1 fresh → 0 spent, averaged over the 13 **powered** joints; the 2 unpowered toe
hinges are excluded or it would peg near 2/15 forever) is what the telemetry endpoint and
the optional observation read.

The head is still not a separate body — a compound collider on Chest with its 6 kg folded
into Chest's 13, so there is **no neck joint** and the head cannot bob or whip. Adding one
means giving it its own rigidbody and an *unpowered* hinge: a driven neck would be a 14th
action, and `Agent_Biped.CollectObservations` loops over `ActionCount`, so it would change
the action space and the 44-obs vector together and invalidate every brain's input *and*
output layer.

### The brain contract (`Agent_Biped`)
- **13 continuous actions** (`ActionCount`), always.
- **`Agent_Biped.ObservationCount = 42`**, or **45** when `extendedObservations` is on
  (+ opponent uprightness / down flag / edge distance, decision period 3 — the standard
  for all four shipped fighters). Layout: 5 body + 26 joint (13 × angle/speed) + 4 feet
  + **1 task flag** + 4 opponent-or-target + 2 edge distances. The pre-merge counts were
  41/44; the task flag is what took them to 42/45 and invalidated every earlier brain.
  Prose elsewhere in the repo (`MANIFEST.md`, `ROSTER.md`, the tooltip on
  `Agent_CharacterDefinition.extendedObservations`) still says 44 — **the constant in
  `Agent_Biped` is the truth**. Obs count and decision period MUST match what the assigned
  `.onnx` was trained with, or inference is silently garbage.
- Two **opt-in** observation blocks lengthen the vector further and are OFF for every
  shipped brain, because switching either on is only legal together with a retrain:
  `contactObservations` (+4: per-foot contact and load) and `staminaObservation`
  (+1: whole-body stamina, added 2026-08-05). Append order is fixed at
  base → contact → stamina → extended; that order **is** the input layer's layout and
  must never change again once a brain has trained on it.
- **Turn `staminaObservation` on for the next run.** Fatigue applies to the body whether
  or not it is observed, so leaving it off means the policy is fighting a body that is
  silently getting weaker with no way to perceive it — it cannot learn to pace itself or
  to time a push against a tiring opponent. The corrective run the fatigue model already
  requires is the free moment to add it.
- Two `Mode`s: `Walk` (falling ends the episode) and `Sumo` (refereed externally;
  shaping only, ±1 comes from the referee). A third, `Recover` (get up, then walk),
  was **deleted 2026-08-02** along with `recoverShoveChance` and the `shove_chance`
  curriculum read — nothing had referenced it since the walk-in switched to `Mode.Walk`.
  Adding get-up training back means a new mode plus its own reward branch, not a
  revert. Note the enum values: `Walk = 0`, `Sumo = 1` are unchanged by the removal
  because `Recover` was last, so no scene's serialized `mode` shifted.
- Configures its own `BehaviorParameters` / `DecisionRequester` in `Awake` — nothing to
  wire in the Inspector.
- All observations pass through `San()` NaN/Inf sanitization.
- `BeginWalkIn` / `EndWalkIn` **switch `mode` between `Sumo` and `Walk`** for the
  ceremonial round-opening walk-in — flipping the task flag and pointing the four
  "opponent" slots at a virtual target — plus `suppressEpisodeControl` so the presentation
  layer can borrow the body safely. There is **no model swap**: `walkModel` and
  `DeployWalk` no longer exist, and the leftover `<Name>Walk.onnx` files were deleted
  2026-08-02 (8.8 MB of pre-merge artifact that nothing loaded but every build shipped).

### Characters are ScriptableObjects
`Agent_CharacterDefinition` (menu: *PoSumo/Character Definition*) is one asset per
fighter holding identity (behavior name = YAML key, colour, face sprite names), body
build scales, brain generation (`extendedObservations`, `decisionPeriod`,
`inferenceModel` — one model, no `walkModel`), and **every reward-shaping coefficient for both the sumo
and walk schools**. Fighter personality (Nick = light and mobile, Kim = heavy planted
anchor) lives in the asset and the YAML header comments, never in `Agent_Biped`.

When adding a shaping coefficient, default it to the constant the code used before, so an
untuned character keeps training exactly what it always did — that is what makes it safe
to add these mid-project. Episode **terminals** stay hardcoded (walk: fall −1, graduation
+3) so different characters' runs stay comparable on one reward scale. `Systems_MatchRoster`
(`[DefaultExecutionOrder(-500)]`, must run before the agents' `Awake`) assigns the two
characters for a scene, or defers to `Tournament_State` when a bracket is active.

### Reward providers (`Assets/Scripts/Reward/`)
Shaping was extracted out of `Agent_Biped.OnActionReceived` on 2026-08-05 into
`Reward_SumoObjective` and `Reward_WalkObjective` — plain C# classes that hold the
per-character coefficients, are handed the body and a `Reward_Context`, and **return a
float**. They have no reference to the `Agent`, so a provider is structurally incapable of
calling `AddReward`, `SetReward` or `EndEpisode`. The arithmetic is unchanged; term order
was preserved deliberately, because these are small floats accumulated at 50 Hz.

- **Terminals stayed in `Agent_Biped`** and are not going to move. `SetReward(-1)` on a
  fall *discards* that step's shaping outright, so the order of the terminal checks
  against the `Evaluate` call above them is load-bearing.
- **`Reward_StepCadence` is shared by both schools**, owned by the agent, not duplicated
  into each provider. `BeginWalkIn` switches a fighter between Walk and Sumo mid-round; two
  independent alternation histories would pay a fighter twice for one step across that
  switch.
- **`Reward_Context` is a `readonly struct` passed by `in`.** One per agent per physics
  step: as a class, 10 bipeds at 50 Hz would be 500 heap allocations a second in the
  hottest path in the project.
- `_pendingImpact` is cleared by the agent **only in the Sumo branch**, exactly as before —
  a walk-in that brushes the other fighter banks that momentum for the first sumo step
  after `EndWalkIn`.

#### The fighters crawl during the walk-in, and FIVE retrains failed to fix it (2026-08-17)

Do not spend a fifth run on this without reading all of it. The measured gait height (torso
above the mat, standing pose is 1.06 m) went:

| brain | gait height | travels | note |
|---|---|---|---|
| shipped `*_fatigue01` | 0.55-0.76 | yes | the crawl being complained about |
| `tall01` cold 6M, additive 0.003 | **0.33-0.35** | **NO** | collapsed, walk-in hit its stall timeout |
| `tall02` warm 2M, additive 0.0015 | 0.56-0.74 | yes | no change |
| `tall03` warm 2.5M, height GATE, ramp 0.65-0.95 | 0.54-0.71 | yes | no change |
| `tall04` warm 2.5M, height gate, ramp 0.45-1.00 | 0.59-0.74 | yes | no change |
| `gait01` warm **16M**, fall-penalty curriculum 0.05→1.0 | **0.16-0.20** | yes | **WORSE — dragging flat** |

> **`gait01` (2026-08-17) attacked the TERMINAL rather than the shaping, which is the one
> lever this section used to recommend, and it made the gait dramatically worse.** The
> fall penalty was made a curriculum parameter (`walk_fall_penalty`, read by
> `Agent_Biped.OnEpisodeBegin`) and ramped 0.05 → 0.25 → 0.6 → 1.0 across 16M steps, warm
> started from the 15M `*_stamina01` trunks. All four fighters completed the full ramp.
>
> Measured result: torso height **0.16-0.20 m** against the 0.55-0.76 m it was trying to
> raise, and a 1.06 m standing pose. They stopped crawling and started **dragging flat**.
>
> **Why, and this is the part worth keeping:** cheap falls do not buy exploration, because
> **a body already on the ground cannot fall**. Once the policy found the floor there was
> no gradient left pointing up — the fall terminal simply stopped firing. Ramping the
> penalty back to 1.0 over the final 4M steps did not recover it; by then dragging was a
> deep local optimum and the terminal it would have been punished by was unreachable.
>
> So the recommendation this section used to make is now itself disproven. **Do not spend a
> sixth run inside the reward function — shaping and terminal have both been tried.** What
> is left is genuinely different in kind: a stability aid the policy cannot opt out of (a
> torso-height constraint enforced in physics rather than paid for in reward), a separate
> walk-only trunk that does not share capacity with self-play sumo, or accepting the gait
> and solving the ceremony in presentation. The last of those is nearly free and was
> already partly done — see `walkInTouchGap`.
>
> **The fight was unharmed**, exactly as in all four earlier runs: 6 rounds, 83% ring-outs,
> no regression. Every walk experiment on this shared trunk improves or preserves the
> FIGHT and leaves the WALK alone, which is itself evidence that the trunk spends its
> capacity where the reward is.

The **diagnosis of the cause was right**: `walkGate` multiplied the dominant forward-speed
term by `KneeBend`, so at a 0.15 floor straight legs earned 15% of it and a deep crouch
100% — crawling paid ~4x. That gate is now on `TallFactor` instead, which is the correct
mechanism and is kept.

**Three separate things were learned the hard way, all of them still live traps:**

1. **A shaping term that saturates outside the range the policy occupies is not weak, it
   is ABSENT — and looks identical to weak from the outside.** `WALK_TALL_Y`/`WALK_CROUCH_Y`
   were first copied from the sumo school as 0.95/0.65 on the reasoning that both schools
   should share one ruler. The walking gait lives at 0.46-0.80, so `TallFactor` was clamped
   at zero across nearly all of it: the reward could not tell 0.60 from 0.55. Two runs were
   spent tuning the *strength* of something that was not connected. Always check a new
   term's ramp against a measured distribution of the quantity it reads.
2. **You cannot out-shape a terminal.** In `Mode.Walk` a fall is `SetReward(-1)` (which
   also discards that step's shaping) plus the forgone `+3` graduation — about **-4**. The
   whole tall-vs-crawl per-step advantage is **0.0063**. Break-even is an extra fall
   probability of **0.16% per step**, i.e. one extra fall per ~13 s of walking. A tall
   bipedal gait on this ragdoll is certainly more fall-prone than that, so **crouching is
   correct play** and no reasonable coefficient changes it. Raising the shaping further
   just re-runs `tall01`.
3. **Never zero a stabilising term to make room for a new one.** `tall01` set
   `walkBendReward` to 0 and the gait collapsed outright: the crouch was load-bearing for
   balance, and nothing had replaced it yet.

If a genuinely upright gait is wanted, the lever is NOT reward coefficients. It is either
far more training (a stable tall gait is a much harder control problem, and the shared
trunk spends its capacity on self-play sumo — note every one of these runs improved the
FIGHT, ELO +500 to +638, while the walk stood still), or reducing the fall risk/penalty
during a walk curriculum so the policy can afford to experiment with standing up.

`walkHeightReward` (additive, small) and the height gate both remain in place: they cost
nothing and are the right shape. They are simply not sufficient.

### Observation 0 was world-absolute for the whole life of the unified brain

**Fixed 2026-08-25.** `Agent_Biped.CollectObservations` fed `Torso.position.y / 2f`
— a WORLD coordinate — as observation 0, while the walk lane in every `SCN_TRAIN_*`
scene sits at **y = -60**. Measured live in `SCN_TRAIN_MATT` (10 agents: 6 walk,
4 sumo):

| population | world Y | obs 0 before | obs 0 after |
|---|---|---|---|
| walk (6) | -59.0 | **-29.50** | **+0.514** |
| sumo (4) | 0.2 | +0.095 | +0.095 |

One policy, one input slot, two disjoint ranges. It had been that way since the
walk+fight merge.

**This is the identical bug `Reward_StepCadence` documents and was fixed for** —
"against absolute world Y this test was meaningless for the walk lane". The REWARD
side was corrected then and the OBSERVATION side was missed, which is the likeliest
reason five successive gait retrains (`tall01`-`tall04`, `gait01`) all failed: every
one of them tuned what the policy was PAID while what it could PERCEIVE stayed
broken. Do not read those five runs as evidence about reward shaping until a run
exists on the corrected vector.

The fix is `San((tp.y - arenaGroundY) / 2f)`. `arenaGroundY` is written by both
referees, so it is live in game and training alike. Every other height-ish
observation was already relative (the foot slots are measured against the torso), so
this was the only absolute one in the vector.

`Reward_SumoObjective.HipsLowFactor` had the same shape — raw world `TorsoPosition.y`
while `StanceFactor` ten lines below already subtracted `ArenaGroundY`. That one was
**not** an active bug (sumo referees sit at y = 0, and the walk population uses the
other provider) but it is corrected too, because the no-op holds only while nobody
offsets a sumo arena, and the walk lane proves this project does exactly that.

**`Agent_CharacterDefinition.driveReward` is 0 on every fighter**, so the drive term
in `Reward_SumoObjective` has never contributed to any brain. Left at 0 deliberately:
enabling it belongs in its own experiment, not bundled into a run that changes the
observation vector.

#### The corrective run is STAGED, and the staging is the point

`<Name>Obs01.yaml` (created 2026-08-25) warm-starts from `<name>_tall04` at
`learning_rate 0.0001` for 3M steps and changes **only** the obs-0 fix. The vector
stays at **45**.

`contactObservations` (+4) and `staminaObservation` (+1) are both wanted and both
deliberately NOT in that run, because either one takes the vector to **50** and
`--initialize-from` requires a matching observation space — adding them makes it a
COLD retrain against trunks worth 12-45M steps AND confounds three changes, so a
moved ELO would say nothing about which one moved it. Turning those two on is
`Sense01`, cold, and only if `Obs01` shows the fix helps.

> **Warm-start caveat worth knowing:** `normalize: true`, so the checkpoint carries
> running mean/std fitted to the OLD obs-0 distribution — a mixture of -29.5 and
> +0.53, i.e. enormous variance on that one input. After the fix the input collapses
> to a narrow band and the normalizer must re-converge. That is why `Obs01` is 3M
> steps at a reduced learning rate rather than the usual 1M corrective.

**`Training/results/` currently holds no `*_stamina01` or `*_gait01`** — those runs
were pruned. The newest trunk present for all four fighters is `*_tall04`, which
already includes the fatigue model but predates the shrinking mat and
`Systems_StrikeImpulse`, so `Obs01` adapts to those as well.

### Two referees, deliberately kept in sync
- `Systems_SumoMatchManager` — **training** referee. Loss = a foot below `footOffMatY`
  (−0.06) or torso below `fallY`; timeout ⇒ `EpisodeInterrupted` (draw). Per-round domain
  randomization of platform width and surface friction, plus curriculum dials read from
  `Academy.Instance.EnvironmentParameters` (`spawn_gap_half`, `shove_impulse`,
  `platform_difficulty`, `shove_chance`).
- `Systems_GameMatchManager` — **game** referee. Round state machine
  Fighting → RoundEnded → Grace → Fighting, scored to `pointsToWin` (exhibition) or
  `tournamentPointsToWin` (bracket), countdown freeze, timeout tiebreak on position, UI
  Toolkit HUD built in code. Spawns every runtime companion and exposes
  `RoundEnded` / `RoundStarted` / `MatchEnded` / `MatchReset` — the events every
  presentation system subscribes to. It is the largest file in the project and the entry
  point for anything match-shaped.

**The MATCH opens Intro → WalkIn → Fighting; later rounds open Grace → Fighting**
(2026-08-07). The ceremony is a match thing, not a round thing — `_walkInPlayed` gates it,
so only round 1 gets it and rounds 2+ still open from the stand-off with the plain
`countdownSeconds` countdown. The order used to be the reverse of this: the walk-in ran
first and had no countdown at all, while the countdown only ever appeared on later rounds.

- **`Phase.Intro`** — both fighters frozen by `PoseNeutral` on the WALK-IN marks
  (±`walkInStartGapHalf`), `introCountdownSeconds` (4) counting down, one camera beat per
  digit: face A, wide, face B, wide. Frozen and not held by the walk policy, because the
  walk task strides straight through its target and would leave during the count.
- Zero shows **"HAKKEYOI!"** and hands over to `Phase.WalkIn`. `"FIGHT!"` still belongs to
  the moment of contact, which is seconds later — do not merge the two banners.
- A stalled or timed-out approach now **starts the fight on the spot** (park at the
  stand-off, `BeginSimulation`, `FlashFight`) instead of re-running a countdown the
  ceremony already played. A policy that cannot close 3 m of empty mat is a training
  problem; the referee is not the place to hide it.

Two traps this cost, both worth knowing before touching any camera shot:

- **A shot's deadline is REALTIME, a countdown digit is game time**, and they diverge hard
  across the arena scene load. The opening face zoom's 1.05 s window expired inside the
  load hitch before the camera had rendered enough frames to move — measured, ortho drifted
  4.20 → 6.34 toward ordinary follow framing instead of reaching 1.20, while every later
  beat landed exactly. Nothing errored; the first beat simply did not happen.
  `TickIntroCountdown` therefore RE-ISSUES the current beat every physics step rather than
  only on the digit change.
- **`Systems_CameraFollow.smoothing` (4) is tuned for FOLLOWING**, so it is slow on purpose
  and cannot carry a deliberate one-second move from wide (ortho 14) to a head. `PunchIn` /
  `PullBackWide` take an optional `blendSpeed` (ceremony uses 18) and per-axis `centering`
  for this. Vertical centring is deliberately **below** 1 — the countdown digit is drawn
  dead centre of the stage band, and a head centred on both axes gets the numeral painted
  across the face.

Falling is **not** a loss in either referee (`knockdownLoses` is off). If you change a
losing condition, change it in **both** — they have silently diverged before, and
policies then never learn that a stray foot over the edge is fatal.

Three rules used to be game-only. **Two of them were ported into the training referee on
2026-08-15 and one cannot be**, so the asymmetry is now down to a single rule:

> **2026-08-16 — the two referees were brought fully back into step, and two new rules
> exist. Read this before touching either.**
>
> - **The SHRINKING MAT is now in BOTH.** The mat closes from `ringHalfWidth` to
>   `shrinkToHalfWidth` (3.5 → 1.8) between `shrinkStartSeconds` (8) and the bell. It had
>   been game-only, so every brain trained on a mat that never closed. `Systems_SumoMatchManager`
>   scales the target **proportionally** to that round's randomized start width — a fixed
>   1.8 would be a no-op on any round that already started narrow. It detects nothing: it
>   withdraws the floor and the existing ring-out path does the rest.
> - **`Systems_StrikeImpulse` is now in BOTH.** Punches and kicks deliver real momentum, so
>   a clean body shot launches a man. Spawned by `Systems_GameMatchManager` behind
>   `enableStrikeImpulse`, and by `Systems_SumoMatchManager` directly — one instance per
>   scene, because `Sensor_Impact.AnyImpact` is static and serves every body.
> - `downOutSeconds` went to **0 in both** — see below.
>
> **Every brain before `*_stamina01` at 15M trained without all three.** Two traps this
> cost, both recorded in `Systems_StrikeImpulse`: the impulse curve must be calibrated
> against MEASURED strike speeds (3.9-5.3 m/s — a curve peaking at 11.9 m/s was inert, the
> same "term outside the observed range" failure as the walk-tall runs), and its per-strike
> log must stay OFF in training (it wrote 37 MB per env worker in 36 minutes and killed one
> run's env logging outright with `Curl error 23`).

- **`downOutSeconds` is now `0` in BOTH referees — the rule is RETIRED (2026-08-16).** It
  read on screen as the game giving up, and the **shrinking mat below now does its job
  better**: a fighter who stays down is squeezed off the edge and loses an honest ring-out.
  Measured immediately after: 5 rounds, 5 ring-outs, no stalls and no draws, against the
  29% ring-out baseline the rule was introduced to fix.
  **Do not restore it without also removing the shrink, and do not remove the shrink
  without restoring it** — either alone brings back the stall it was written for, two
  motionless bodies on a mat that never closes. The history is worth keeping: it existed
  because `IsDown` can latch permanently once a leg is under the body, and it once decided
  **57%** of rounds.
- **The low-friction `tawara` band** at the rim (`tawaraBandWidth` / `tawaraFriction`) that
  turns "almost out" into "out". **Now in both.** `Systems_SumoMatchManager` writes both
  values onto `Systems_SumoArena` in `Start`, *before* the first `ResetRound`, because the
  band is rebuilt inside `SetPlatformHalfWidth` → `EnsureTawaraBands`. The training scenes
  serialize 0.7 on the arena against the game's 1.2; the referee now overwrites it at
  runtime, exactly as it already did for `ringHalfWidth`.
- **`knockoutsToLoseMatch`** (head KOs lose the match outright, via
  `Systems_BodyDamage.Knockout` — **2** in `GameTuning.asset`, which is what runs; this
  line said 3, which is only the code fallback). **Still game-only, and deliberately so.**
  `Systems_BodyDamage` is a presentation companion that only `Systems_GameMatchManager`
  spawns, so a training env has no damage model and no event to subscribe to. Adding one
  is not a referee change — it puts dismemberment and its mass changes into the training
  physics, invalidating every brain, for a rule that fires once or twice a match.

None of this is retroactive: **every currently shipped brain was trained before the port**
and has never seen either rule. They take effect on the next training run.

Shared numbers live in `Assets/Settings/GameTuning.asset` (`Systems_GameTuning`); scene
components copy from it in `Start`, so tune the asset, not serialized scene values — the
scenes still hold stale copies (SCN_SUMO serializes `ringHalfWidth: 1.68`,
`neutralGapHalf: 1.2`) that are overwritten at runtime and will mislead anyone grepping
the `.unity` file. The **fields on the components are the fallback for when no tuning
asset is assigned**, so a code default and the asset can disagree indefinitely and only
the asset takes effect (`roundTimeoutSeconds` is 30 in code, **20** in the asset).

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

Bracket layout (reworked 2026-08-02): chips are **elastic** (`flexGrow 1` / `flexBasis 0`,
`SLOT_SIZE` 150 as a floor that never binds) so a row divides whatever width it is given,
each round's rows sit in a `Systems_UiKit.Surface` card under a centred header, and the
roster palette is a 2-up grid of 50%-wide cells. Three measured traps, all of which cost a
build here:
- **the usable content column is ~578pt, not the ~696 a 720pt reference panel implies.**
  Size against a live capture, not against the reference; a fixed chip width of 190
  immediately pushed the winner chip off its card.
- **`flexBasis` applies to the MAIN axis.** A chip built for a row and dropped into a
  default (column) cell collapsed to zero HEIGHT and the whole palette vanished with no
  error. Palette cells are `Systems_UiKit.Row()`s for that reason.
- **the gutter goes on the cell, not the chip.** A margin on two 50% chips totals >100%
  and wraps them one per line.

### The look: one shader, and switches that must move together
`Assets/Resources/Shaders/PoSumo_BodyLit.shader` is the **only authored shader in the
project** — everything else is a runtime `new Material` on a stock URP sprite shader, and
there are no `.mat` assets and no Shader Graph files at all. It is a 3-pass copy of
`Sprite-Lit-Default` (all three passes must declare an IDENTICAL `UnityPerMaterial` CBUFFER
or the SRP Batcher silently drops it) plus four terms: rim, subsurface wrap, sweat and clay.

**`Systems_ArenaLighting.LightingEffects` is `false` (2026-08-02) and is the master
switch for all of it.** With it off the rig builds exactly **one** `Light2D.LightType.Global`
at `flatGlobalIntensity` (1.5) and nothing else: no key, no rims, no volumetrics, no
shadows, no post-processing volume, no cylinder normal map, and the BodyLit rim/wrap/sweat
terms zeroed. `Systems_SumoArena` also hides its painted-on `Spotlight` cones and
`LanternHalo` glows, and `Systems_ArenaAtmosphere.showShafts` is off.

Two things about that switch are load-bearing:

- **The materials stay LIT.** Handing everything a `Sprites/Default` unlit material was
  tried first and is the trap: it renders, but at 1.0× where the old rig delivered ~1.7×
  across the middle of the mat, and the arena art is authored dark on that assumption. A
  live capture showed the dohyo, crowd and backdrop collapse into an unreadable brown void.
  A Global light has no position and no falloff, so it shades nothing — it is an exposure,
  not an effect, and it is the only thing between a lit sprite and solid black.
- **`GameTuning.enableLighting` must stay ON** and is deliberately NOT ANDed with the
  switch. It decides whether there is a rig; `LightingEffects` decides what the rig builds.
  Turn the first off and the scene goes black.

The two shadow switches below still gate the (still-disabled) cast shadows, and
`FlatBodyShading` still applies when `LightingEffects` is back on:

| Switch | File | Meaning |
|---|---|---|
| `LightingEffects` | `Systems_ArenaLighting` | **master.** `false` = one flat global light, nothing else |
| `FlatBodyShading` | `Systems_ArenaLighting` | `true` zeroes rim/wrap/sweat and skips the normal map — flat tinted primitives. Implied while `LightingEffects` is off |
| `keyCastsShadows` | `Systems_ArenaLighting` | the key light is the only shadow caster; the rims and global are explicitly cleared because `Light2D.m_ShadowsEnabled` defaults TRUE for code-created lights |
| `castShadows` | `Agent_BipedBody` | adds the `ShadowCaster2D`s |

Casters with no casting light do nothing; a casting light with no casters **still allocates a
shadow render texture every frame**. `Systems_BodySurface` disables itself when
`FlatBodyShading` is on rather than half-enabling the look behind its back.

Hard-won specifics, so nobody re-derives them:
- **Exposure is balanced for shaded bodies at `globalIntensity 0.42` / `keyIntensity 1.25`.**
  A `Light2D.LightType.Global` has no position and no falloff, so at the old 1.35 with the
  key and rims at zero there was literally no light shaping anywhere. Raising the global back
  toward 1.35 while shading is on drives the lit centreline past white and the bodies read as
  chrome — the shader's `headroom` term shapes the additive highlights but cannot rescue an
  over-bright base.
- **The global:key RATIO is what makes shadows visible, and it is easy to get wrong.**
  A Light2D shadow occludes only the light that casts it. The key is the sole caster — the
  global *cannot* be shadowed (Global type, plus `DisableShadows`) and the rims are explicitly
  unshadowed — so the darkest a shadowed patch can get is `(global + rims) / (global + rims +
  key)`. A first pass shipped 0.78 / 0.7, which floors that at ~53% brightness: the shadow
  pass ran every frame and produced **nothing visible on the clay**. The key must dominate the
  unshadowed fill, not merely match it. If shadows ever stop reading, check this ratio before
  touching `shadowIntensity` — and remember the rims fill shadows harder per unit than the
  global, because they sit at fighter height pointing inward.
- **The `ShadowCaster2D` goes on the `Art` child, not the physics GameObject.** Its `Awake`
  fills an empty shape path from the `Renderer` on the same object, so no shape has to be
  authored — but it must therefore be added *after* the `SpriteRenderer`.
- **Every wrestler needs exactly one `CompositeShadowCaster2D` on its root.**
  `ShadowCasterGroup2DManager` walks up to the nearest group ancestor; without it the 14
  heavily-overlapping parts shadow each other and the body interior fills in as a dark blob.
  Same intent as the pairwise collision ignores: a biped is one object.
- **Cast shadows are OFF, and the reason is geometric — do not "fix" it by re-enabling.**
  The full implementation is present and correct, and the light ratio above was rebalanced
  specifically to make shadows readable. Three 1080×1920 live captures across different match
  states then showed no body-shaped shadow at all. At gameplay framing the only clay in frame
  is the dohyo's **front face seen edge-on**; the fighters stand on its top edge, so there is
  no horizontal surface for a shadow to fall across. That is almost certainly why shadows were
  removed the first time, too. Enabling `keyCastsShadows` + `castShadows` costs a shadow render
  texture per frame plus a second volumetric pass — on an Android target — and renders nothing.
  Turn them on only if the camera ever frames the mat from above or a floor plane becomes
  visible. `Systems_BlobShadow` does the contact grounding this geometry can actually show.
- The key's volumetrics and `Systems_ArenaAtmosphere.showShafts` are **independent** and
  stack on purpose — volumetrics give the cone its falloff and its shadow wedges, the shafts
  give it discrete visible beams. Both read against the particle haze `Systems_DustPuff`
  emits, so turning the haze off makes both nearly invisible.

### The HUD: one document, three bands, and a proportional floor
Every screen is UI Toolkit built from C# at runtime — there is no UGUI Canvas in
the project, no `.uxml` and no `.uss`. `Systems_UiKit` holds the tokens and the
builders; `Systems_HudRoot` is the single `UIDocument` the match screen draws
through (three components used to add their own at equal sorting order, which has
no defined draw or pick order, and taps aimed at REMATCH were being swallowed).
`Assets/UI Toolkit/README.md` is the working reference — read it before touching
UI, it carries the pending font-asset work and three gotchas that each cost a bug.

The three that matter most here:

- **Inline styles resolve above every USS rule**, including the runtime theme's
  `:hover` and `:active`. `StyleButton` writes `backgroundColor` inline, so every
  button in the game was visually dead on press until `AddPressFeedback` put the
  feedback back by hand. Build controls through the kit or they will have none.
- **An absolutely positioned child resolves its offsets against its parent's
  PADDING box.** The safe-area inset therefore goes on the content and modal
  layers but deliberately NOT on the scrim between them — under the inset it
  stopped at the notch and left undimmed strips behind every dialog.
- **The panel scales on WIDTH** (`GamePanelSettings` match `0`, 720x1280 ref), so
  band heights authored in points take a different share of the screen on every
  aspect ratio. `Stage` carries a 45% minimum and `Dock` a 28% maximum for that
  reason. Do not "fix" it by switching to a balanced match: that narrows the panel
  below 720pt on tall phones and overflows the bracket's chip row, which is a bug
  already fixed once.

`Systems_FightHud` is split by whether the fight is happening: an always-on
**live strip** in the dock (two damage mannequins flanking one DOMINANCE
tug-of-war bar, ~115pt) and a **detail card** in the stage band, shown on
`RoundEnded`/`MatchEnded` and hidden on `RoundStarted`. It was previously one
~484pt table pinned to the dock with no way to hide it — 39% of a 9:16 panel and
~52% of a 4:3 tablet in portrait, permanently over the bottom of the dohyo.
Nobody parses a work-rate percentage while a bout is being decided; put new
aggregate metrics on the detail card.

The card carries **three** rows — TERRITORY, KNOCKDOWNS DEALT, PUSH IN CONTACT.
It carried six until 2026-08-02, and the test that removed the other three is
worth reusing: SHOVES · BEST and BALANCE are already weighted into the DOMINANCE
bar that is on screen the whole match (`RawDominance` blends territory 0.35, KD
0.30, shoves 0.20, balance 0.15), so printing them again was the card restating
its own inputs, and WORK RATE was a number nobody acted on that cost a
13-iteration loop over `LastActions` per fighter per frame. Deleting the rows also
retired `sumWork` and `bestPush`, and three accumulators — `sumSpd`, `sumLean`,
`sumEdge` — that had been sampled every frame and **read by nothing at all**.
`shoves` and `sumBal` stay: DOMINANCE still needs them.

### Presentation companions: spawned, never wired
Nothing below is placed in a scene. `Systems_GameMatchManager` `new GameObject(...)`s each
one in `Start`, gated by an `enable*` bool on `GameTuning`, and they talk back only through
its four events. That is why the arena scenes stay small and why "turn the feature off" is
a tick on one asset rather than an edit in three scenes.

| Companion | What it adds |
|---|---|
| `Systems_MatchPresentation` | slow-mo finish, camera punch-in, salt throw |
| `Systems_MatchAudio` / `Systems_FighterVoice` / `Systems_VoiceGains` | impact + crowd + ceremony audio; per-fighter spoken lines (silent when a fighter has no clips) |
| `Systems_MusicDirector` | adaptive layered score |
| `Systems_ArenaLighting` / `Systems_ArenaAtmosphere` | 2D light rig + post volume; backdrop parallax, haze, crowd sway, light shafts |
| `Systems_BodySurface` | writes `_Sweat` and `_Dirt` into one fighter's `PoSumo/BodyLit` material — sheen from exertion, clay from arena contact. Rides `enableLighting`; disables itself unless `Systems_ArenaLighting.BodyShadingActive`, so it is **currently inert** |
| `Systems_ImpactFx` / `Systems_DustPuff` / `Systems_SoftBodyJiggle` / `Systems_BlobShadow` | hit bursts, dust, flesh wobble, contact shadows |
| `Systems_StrikeImpulse` | **punches and kicks deliver real momentum** — a clean body shot launches a man. The one companion that is ALSO spawned by `Systems_SumoMatchManager`, so it is in training too. Not presentation: it changes who wins rounds |
| `Systems_BodyDamage` / `Systems_RingBlood` | bruise decals, **limb loss and decapitation**, the bloody head KO, and blood left on the mat. **Owns the `Knockout` static event the referee's 3-KO rule reads**, so it is the one "presentation" system with a rules consequence |
| `Systems_FaceMood` | expression driven by dominance |
| `Systems_CareerRecorder` | the only writer into career stats |
| `Systems_KimariteCaller` | announces the winning technique after every round. Read-only w.r.t. the fight — it names the finish, it does not decide it. Measures; `Systems_Kimarite` (pure static, unit-tested) decides |
| `Systems_CrowdMomentum` | the crowd backs whoever is losing; sustained support is a small torque boost. **Changes who wins rounds** — see below |

**Fighters can be dismembered and decapitated, and this is not cosmetic.** Measured play
produces `[DAMAGE] Damage_Nick lost LegNear at 20.0 damage — bleeding from stump 'Pelvis'`
and `Damage_Matt DECAPITATED at 2.8 damage`.

The two gates are deliberately far apart. **Read the tooltips on the fields, not this
paragraph, for the current numbers** — both have moved repeatedly and this text has been
wrong before. As of 2026-08-07 they are `detachAtRedMultiple` **10** (`regionRedAt` 2.5, so
a gate of **25** summed damage) and `headDetachAtRedMultiple` **1.2** (a gate of **3.0**);
this file previously claimed 16 and 0.8. The tooltip on `detachAtRedMultiple` is the real
changelog — 1.6 → 8 → 16 → 3 → 6 → 10, each move with its measurement.

**Measured at the 10 the tooltip asked someone to measure (2026-08-07):** a 3-match bracket
lost three limbs, **every one of them `LegNear`, at 25.2 / 25.4 / 25.9**. That is the same
*censored-at-the-gate* signature the tooltip records at gate 3 (7.5-8.0) and gate 6
(15.0-15.6): limb damage in a clinch runs past wherever the bar is put, so moving the bar
relocates the pop instead of preventing it. Three gates, three identical outcomes — **the
constant is not the lever.** Do not spend a fourth bracket on a fifth value; the fix has to
change the damage *distribution* (rate-limit per contact, or cap per-region accumulation
per round), not the threshold it is being compared against.

**That advice was taken on 2026-08-08 via a PROBABILITY dial rather than a distribution
change: `Systems_BodyDamage.limbDetachChance`, default `0.5`** — arms and legs are now half
as likely to come off. It is rolled **once per limb**, not once per blow, and the verdict is
remembered in `DetachRollStore` for as long as the damage that caused it (i.e. the whole
tournament). Both halves of that are load-bearing:

- A per-blow re-roll would be nearly useless. Damage keeps arriving after the gate, so the
  limb would simply come off on the second or third roll and the observed rate would barely
  move — the same "censored at the gate" trap in a new costume.
- `ReapplyStandingDismemberment` **must skip spared regions**, and now does. Damage is
  tournament-persistent, so a limb spared in the quarter-final is still past its gate when
  the next scene loads; without the check that pass would tear it off with no roll and no
  log line, turning "half of limbs survive" into "half survive until the next scene load".

Verified live: two limbs crossed the gate at ~25 damage in one bracket, one came off
(`lost LegNear at 25.1`) and one did not (`SPARED LegNear at 25.3 — detach roll failed`).
The head is exempt (decapitation keeps `headDetachAtRedMultiple`), and the **gib path
bypasses the roll entirely** — it calls `DetachRegion` directly, which is deliberate: the
gib is a rare showpiece with its own `gibChance`, and `allowGib` is already independent of
`allowDetach` for the same reason.

Note `Systems_BodyDamage` does **not** read `GameTuning` and is spawned fresh per match, so
unlike most tuning in this project its **code defaults are what actually run**.

Decapitation stays the common showpiece finish. Both wounds of a break bleed —
stump and severed end, neck stump and the head's cut face — through `OpenBleed`, and the
jets carry a `severJetSpeed` / `decapJetSpeed` multiplier into
`Systems_DustPuff.BloodSpray` that raises droplet speed and narrows the cone, so a cut end
throws an arc rather than dribbling on itself. The blood particle budget (2600) is sized
against `decapPeakDroplets` at two wounds at once — raise that and the pour silently
starves rather than erroring. A fighter that loses a leg cannot satisfy the
get-up condition again, so it is `downOutSeconds` — not the ring-out — that actually ends
that round. That is the interaction to keep in mind when tuning either: shorten
`downOutSeconds` and dismembered fighters are retired faster; disable it and they lie
there until the clock expires, which is the exact stall the rule was added to kill.
The training referee has no equivalent, so no brain has ever trained against it.

### Three features added 2026-08-25 (kimarite, banzuke ceremony, crowd momentum)

**`Systems_Kimarite` is pure logic on purpose.** It takes a struct of measurements
and returns a name — no MonoBehaviour, no physics, no disk — which makes it the third
thing in this project that is genuinely unit-testable (with `Systems_CareerLadder` and
`Reward_Context.San`). `Assets/Tests/EditMode/KimariteTests.cs` covers it; the suite is
now **38 tests**, not the 23 this file used to claim. `Systems_KimariteCaller` does the
measuring off a live ragdoll and is verified only by a harness run — a green unit run
says nothing about whether the call on screen is right.

One test there is load-bearing: `EveryRoundOutcome_ProducesANamedResult` walks the
`RoundOutcome` enum. That enum has grown twice already (`Gibbed`, `DownOut`), and a new
member would otherwise fall through to a **blank banner with no error anywhere**.

`Systems_GameMatchManager.LastOutcome` was added rather than a fifth match event, and
is set in `EndRound` **before** `RoundEnded` fires. Four events are the entire coupling
surface between the referee and ~15 companions; a companion reading the manager it was
spawned by is the sanctioned alternative.

> **`Systems_CrowdMomentum` changes who wins rounds, and the training referee has no
> equivalent — so no brain has ever trained against it.** Same class as
> `knockoutsToLoseMatch`: deliberately game-only. Porting it into
> `Systems_SumoMatchManager` would teach the policy to farm the comeback bonus by
> giving up ground early, which is the shaping-exploit failure this file warns about
> throughout. `MAX_BOOST` is **0.12**, inside the band a fighter already loses to
> fatigue (`FATIGUE_DEPTH` 0.35) — enough to decide a close round, not enough to
> rescue a bad one. Raise it and that stops being true.

It writes `Agent_BipedBody.adrenaline`, a NonSerialized whole-body torque multiplier
applied in `ApplyMotor` beside the Hill and fatigue terms. **Not** via `actionScale`:
that path clamps the command to [-1,1] before it reaches `motorSpeed`, so a boost
applied there is silently discarded while a cut is not — and `actionScale` is already
owned by the manager's opening ramp. Going through the torque budget also means the
boost inherits activation dynamics, so it ramps over ~50 ms instead of stepping.
`ResetPose` does NOT clear it; the crowd system restores 1 in its own `OnDisable`.

Measured live: support builds to 1.00 → `adrenaline` 1.120, decays when the backing
lapses, and returns to 1.000 the moment the round is not live.

The **banzuke ceremony** (`Systems_PromotionCeremony`) is a plain C# class built into
the bracket's existing UIDocument root, exactly like `Systems_CareerScreen` — not a
second `UIDocument`. `RankChange` gained a `FromRank`, carried rather than recomputed
because `Systems_CareerStats.Get` hands back the LIVE record and the "before" state no
longer exists by the time the bracket shows it. The old `_rankNews` label **stays**: it
is the persistent record on the page, the ceremony is the one-shot moment over it, and
`TryTakeRankChange` is consume-once so a dismissed ceremony is otherwise unrecoverable.

### Telemetry (`Systems_Telemetry`)
Spawns itself via `[RuntimeInitializeOnLoadMethod]` in **Editor and development builds
only** — a shipped Android build gets no listening socket. It publishes the same numbers
twice: as JSON on `http://127.0.0.1:<port>/metrics`, and into ML-Agents' `StatsRecorder`
so per-fighter stamina lands on TensorBoard beside reward and ELO. TensorBoard answers
"how did this run go"; the HTTP endpoint answers "what is this env doing right now",
which is the question you actually have when a headless run has gone quiet.

**It only started working in a training env on 2026-08-07.** `Spawn` bails unless
`Debug.isDebugBuild || Application.isEditor`, and `BuildTrainingEnv` built with
`BuildOptions.None` — so every env player was a non-development build and the endpoint
never opened there. The port walk below, whose entire reason for existing is 4-8
concurrent envs, had therefore never once run. `BuildTrainingEnv` now passes
`BuildOptions.Development`. **Envs built before that date must be rebuilt**, or the
`curl` in *Commands at a glance* connects to nothing. This is the failure shape to expect
from that gate generally: no error, no log line, just a port that refuses the connection.

- **Raw `TcpListener`, not `HttpListener`.** HttpListener needs a `netsh http add urlacl`
  reservation to bind as a non-admin user on Windows; a telemetry endpoint that throws
  access-denied on a fresh machine is worse than none.
- **Port walks upward from 8787** (12 attempts) so each of the 4-8 concurrent training
  envs gets its own. The bound port is logged as `TELEMETRY RESULT:`.
- **The socket thread never touches a Unity API.** The main thread rebuilds the JSON on a
  2 Hz timer into a reused `StringBuilder` and swaps it under a lock; the socket thread
  only reads that string. A background `FindObjectsByType` would be an immediate crash.
- `OnDestroy` calls `Stop()` **before** joining the thread — `AcceptTcpClient` is
  otherwise parked forever and holds the port past teardown.

### Career stats persist across sessions
`Systems_CareerStats` is static like `Systems_TournamentState` — but where the bracket
*clears* itself on `SubsystemRegistration`, this one **reloads from disk** there, because it
must survive the whole session and domain reload is off. It writes
`career.json` in `Application.persistentDataPath`: per-fighter W/L, round record, titles,
head-to-head and an Elo (start 1000, K 24). Records are keyed by **behavior name**, which is
the only fighter identity stable across folder and asset renames — do not key anything on
folder or asset name. Head-to-head is three parallel `List<>`s because `JsonUtility` cannot
serialize a `Dictionary`.

This Elo is the *game's* ladder and has nothing to do with the self-play ELO in
TensorBoard; do not compare the two numbers.

### The banzuke (`Systems_CareerLadder` + `Systems_CareerScreen`)
`Systems_CareerLadder` maps a career record onto a 10-rung sumo rank — JONOKUCHI
up to YOKOZUNA. Pure logic over `Systems_CareerStats.Record`: no state, nothing
serialized, nothing to reset. The record is the truth and a rank is a view of it.

**The thresholds are tuned for a ZERO-SUM pool and that is the thing to know before
touching them.** Elo here is closed — four fighters, every match moves points from
loser to winner, so the pool mean is pinned at 1000 forever. There is no absolute
scale, only distance from 1000. A dominant fighter realistically reaches ~+160 at
K = 24, which is where YOKOZUNA sits; the bands fan out from 1000, not from zero.
Widen them and the top ranks become unreachable, narrow them and everyone is a
Yokozuna. A fresh fighter at 1000 is MAKUSHITA, rung 3 of 10 — room to fall three
and climb six. Measured: against equal opposition a fighter promotes at matches
1, 4, 7, 11, 17 and 26, so the climb decelerates but never stalls.

- **The top two rungs are gated on TITLES as well as rating** (OZEKI 1, YOKOZUNA 2).
  Without that the bracket would be a way to farm Elo and nothing more; winning a
  tournament is the only route to Ozeki, as in the sport. `IndexFor` walks the
  rungs DOWNWARD so a title gate cannot strand a fighter — 1200 with no titles is
  SEKIWAKE, not stopped at the first rung it fails.
- **`UNRANKED` is −1, not rung 0**, for a fighter with no decided match — their Elo
  is the 1000 default, which would otherwise plant them mid-ladder having done
  nothing. Both the promotion and demotion branches in `Systems_CareerRecorder`
  guard against it, or every fighter's first win would announce a "promotion" that
  is really an arrival.
- `Systems_CareerRecorder` samples each fighter's rank INDEX before writing and
  again after `RecordTitle` — after, because the final of a bracket is exactly when
  a title gate can be cleared. It samples the index and not the `Record`, because
  `Systems_CareerStats.Get` hands back the live object out of its list and a
  "before" reference would be mutated by the write and compare equal to itself.
  The result is held in a consume-once static (cleared on `SubsystemRegistration`,
  like every static here) so the bracket announces it exactly once on return.

`Systems_CareerScreen` is a full-screen overlay **inside the bracket's UIDocument**,
not a second one — see `Assets/UI Toolkit/README.md`. It replaced a collapsed
four-column table that used to be built inline in the bracket's scroll column;
there is now one career UI, not two.

### Scenes
Build settings are exactly two scenes: `SCN_TOURNAMENT` (index 0) and `SCN_SUMO`. The game
therefore always boots into the bracket, which loads `SCN_SUMO` for every bout and gets the
winner back via `Systems_TournamentReporter`.

A third scene, **`Assets/Scenes/SCN_BOT.unity`, is tracked in git but is NOT in build
settings** and is loaded by nothing (found 2026-08-07). It belongs with the deliberately
brainless `Bot_v01` roster entry above, so **do not delete it as orphaned** — it looks
exactly like a stray scene and is not one. It ships in no build, because only
build-settings scenes are included.

**Always start a play session from `SCN_TOURNAMENT`** — never from `SCN_SUMO`, a
`SCN_TRAIN_*` scene, or whatever the Editor was last left on (frequently `SCN_SUMO`).
Entering Play mode inside `SCN_SUMO` runs a standalone exhibition: no bracket, no
`Systems_TournamentState`, no `Systems_TournamentReporter` spawned and no title awarded, so
nothing bracket-, career- or banzuke-shaped can be observed or tested from there. The
arena scene is deliberately usable standalone, which is exactly why the wrong entry point
looks like it works. `SCN_SUMO_ICE` and `SCN_SUMO_STICKY` were
deleted (2026-07-28) and the bracket no longer rotates arenas — `Systems_TournamentBracket`
holds a `const string ARENA_SCENE = "SCN_SUMO"` rather than a serialized array, precisely so
SCN_TOURNAMENT's stale serialized copy of the old three-scene list cannot resurrect them.
`Systems_SumoArena` has **no `style` field at all** any more — an earlier version of this
line said it retained unused ice/sticky values, and that was already false when checked on
2026-08-02. `SCN_WALKVIEW` and its only component, `Systems_FollowX`, were deleted the same
day: the scene was not in build settings and viewed a standalone locomotion brain that
stopped existing at the walk+fight merge. Arena scenes are **baked**: an editor pass ran
`Systems_SumoArena.Build()` and saved the children, so `Awake` only rebinds references.

Training scenes, one per surviving purpose — every one either produced a deployed brain or
is the newest template for a training mode. Keep that rule when adding or retiring scenes:
a scene that produced a shipped brain is the only way to reproduce it.

| Scene | Purpose |
|---|---|
| `SCN_TRAIN_MATT` / `SCN_TRAIN_STANDARD` / `SCN_TRAIN_NICK` / `SCN_TRAIN_KIM` | **unified** self-play sumo + walk, one per fighter |

Matt's scene was `SCN_TRAIN_MATT_AGGR` (and its env `MattAggrEnv`) until 2026-08-02 — the
only fighter whose scene carried a suffix, breaking the `SCN_TRAIN_<NAME>` schema this file
mandates. Renamed to `SCN_TRAIN_MATT` / `Builds/MattEnv`; the `.meta` moved with the scene
so its GUID and every reference survived. Older docs and commands still say `MattAggrEnv`.

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
their configs are gone, and `Mode.Recover` went with them on 2026-08-02.

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
  `Agent_`, `Sensor_`, `Reward_`, `Systems_` (four folders under `Assets/Scripts/`,
  no others — `Reward/` was added 2026-08-05, and this line said three until then);
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

> **All four shipped brains are stale as of 2026-08-05** and need a corrective pass. The
> fatigue model changed the dynamics they were fitted against. The recovery is the cheap
> one described below — rebuild each env, warm-start from the shipped checkpoint, run
> 1-3M steps at a reduced learning rate — but note that `Training/results/` is gitignored
> and absent from a fresh clone, so there may be no checkpoint to warm-start from and the
> pass becomes a cold retrain. Turn on `staminaObservation` in the same run.

> ## **DO NOT USE THE UNITY EDITOR WHILE A RUN IS TRAINING.** Measured twice on
> 2026-08-16 and it is the single most expensive mistake available here.
>
> Entering Play mode alongside 8 env players took one run from **4.2M steps/hour to 69
> steps in 80 minutes** — it looked alive the whole time, with trainers and env players
> in the process list. A second run's env players died 2 minutes after launch, during a
> brain deploy plus Play mode. Neither errored. **Both look identical to a healthy run
> until you diff the step count**, which is why the check below is worth running before
> believing any status.
>
> Deploying a brain, refreshing assets and building an env are all Editor work. Stop
> training first, or accept the run.
>
>     powershell -Command "@(Get-Process mlagents-learn -EA SilentlyContinue).Count"
>     # then compare the newest numbered .pt against itself 5 minutes later

**Three wrappers, and which to use.** All three enforce an explicit `--base-port` (the
trainer takes `--num-envs` consecutive ports, so two runs on the default 5005 collide and
the second hangs on a handshake the first already answered) and start TensorBoard exactly
once (a second bind on 6006 fails quietly enough that you notice an hour later with no
graphs).

| Script | Use it for |
|---|---|
| `Start-Training.ps1` | ONE run, foreground. The original; still correct for a single fighter |
| `Start-StaminaExtension.ps1` | 2+ concurrent runs. `-Phase` picks the config generation, `-InitializeFromPhase` warm-starts a NEW run id from an existing trunk, `-Minutes` arms a graceful auto-stop |
| `Run-GaitCampaign.ps1` | Unattended multi-hour work. Runs the roster in sequential batches, because the limit is MEMORY not cores — 4 fighters x 4 envs is ~17 GB against ~8-10 GB typically free, so 2 at a time is what fits |

`Start-StaminaExtension.ps1` carries two guards that each cost a wasted launch to learn:
a `--resume` into a config whose `max_steps` equals the checkpoint's step count **exits
immediately having trained nothing and looks exactly like a successful short run**; and a
warm start into an existing run id silently resumes that run instead of loading the trunk
you asked for, so it refuses.

`Training\Start-Training.ps1` is the wrapper: it starts TensorBoard *before* the trainer,
always passes an explicit `--base-port`, bounds `--num-envs` to 4-8 and warns if that
leaves under 4 cores for the trainer's torch threads, and records the session (run id,
PIDs, ports) to the gitignored `Training/.session.json`.

`Training\Stop-Training.ps1` tears one down in the order that actually works: trainer
first (killing env workers first accomplishes nothing — it respawns them), then orphaned
players matched by path under `Builds/`, then TensorBoard, then optionally `-Prune` to
delete event-less run directories. It closes the trainer's window rather than killing it
and waits 60 s, because the final-checkpoint write on a large trunk is not instant and
killing through it truncates the `.pt`.

Doing it by hand instead — TensorBoard must be running alongside training. This is a
**hard rule with a blocking hook behind it**: `.claude/hooks/require-tensorboard.sh`
refuses a direct `mlagents-learn` invocation while nothing is listening on 6006. The
wrappers all start it themselves and are exempt. Full reasoning in
`.claude/rules/training.md` — the short version is that ELO is the only accept/reject
signal for a fight run, ELO is a TensorBoard scalar, and mean reward is explicitly the
wrong criterion, so a run without TensorBoard produces nothing you are allowed to judge
it by:
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
  --results-dir=Training/results --env=Builds/<Env>/<Env>.exe --num-envs=6 --no-graphics `
  --base-port=5005
```
`--num-envs` is a **CPU budget, and it is not free to change**: each env is a headless
player process, so keep it to 4-8 on this 12-core box and leave cores for the trainer's
torch threads and TensorBoard. It is also not a pure throughput dial — ML-Agents' own docs
are explicit that changing `--num-envs` with every hyperparameter held fixed still changes
the resulting model, because it changes how experience is batched. The shipped four brains
were trained at **3**; a later run at 6 is a different run, not a faster one, so record the
value in the config header alongside the hyperparameters.

**Always pass an explicit `--base-port`.** The trainer takes `--num-envs` consecutive ports
starting there, so two concurrent runs on the default 5005 collide on the worker sockets and
the second one hangs waiting for a handshake that the first already answered. Space
concurrent runs by at least `--num-envs` (5005, 5015, 5025 …).
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

To stop training: run `Training\Stop-Training.ps1`, or kill `mlagents-learn.exe` itself.
Killing only the env worker EXEs does nothing — the trainer auto-respawns them. On any
disconnect the trainer saves a final checkpoint before exiting, so close it rather than
hard-killing it and give the write time to land.

Deployed models are always **overwritten in place** at `Assets/Agents/<Name>_v01/<Name>.onnx`
so the `.meta` GUID (and every reference to it) survives; `DeployBrain` does this and also
sets the character asset's `inferenceModel`. Copying a checkpoint does not require stopping
a headless run.

`Training/results` **is** the TensorBoard logdir, so treat it as a curated list, not a
dumping ground. Everything else goes elsewhere —

> **As of the 2026-08-25 purge it holds exactly four runs — `matt_tall04`,
> `standard_tall04`, `nick_tall04`, `kim_tall04` — totalling 65 MB.** These are the
> warm-start trunks `<Name>Obs01.yaml` initializes from; each keeps its
> `<Behavior>.onnx`, `<Behavior>/checkpoint.pt`, `configuration.yaml`, `run_logs/` and one
> tfevents file, which is precisely the prune shape specified below.
>
> It had drifted to **16 runs and 4.14 GB**, of which **3.81 GB was numbered per-step
> checkpoints** — the exact thing the first bullet says to delete.
>
> **This line used to claim the directory "holds only runs that back a deployed brain
> (currently eight — a sumo and a walk run for each of the four fighters)". That was false
> in both halves.** There were sixteen runs, not eight; there has been ONE run per fighter
> since the walk+fight merge, not two; and — measured by hashing all four shipped `.onnx`
> against every `.onnx` in every run — **not one of them backed a deployed brain**. The
> runs that produced the shipped brains were pruned long ago.
>
> **Consequence worth stating plainly: the four shipped `.onnx` files are NOT reproducible
> from anything on disk.** They exist only as the committed copies under `Assets/Agents/`.
> Treat those four files as irreplaceable artefacts, not as build output — and never
> `git checkout`/overwrite one without knowing where the replacement comes from.

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
| `BuildTrainingEnv` | Headless Win64 player containing one training scene (`--env` target). One menu entry per surviving training scene. Builds **`BuildOptions.Development`** — required for telemetry and `Systems_Log`, see *Telemetry* |
| `BuildAndroid` | *Build Android APK* → `Builds/Android/PoSumo.apk` from the enabled build-settings scenes |
| `BuildAndroidAAB` | *Build Android AAB (Play release)* → `Builds/Android/PoSumo.aab`, signed. Logs `AAB BUILD RESULT:` |
| `DeployBrain` | Copy a run's ONNX → agent folder + wire the character asset. One entry per fighter, each pinned to the run that currently backs its shipped brain |
| `MatchTestHarness` | `MatchTestHarness.Run(n)` in Play mode: chains N matches unattended, logs a `HARNESS RESULT:` win/loss tally |
| `GenerateAudio` | *Generate Audio* — synthesizes the match SFX bank in-editor; the clips are generated assets, not recordings |
| `NormalizeVoice` | *Normalize Voice Levels* — evens out the per-fighter voice clips |

**Both of these write assets that then go stale silently, and both had done so.** Fixing the
generator does nothing until the menu item is re-run, and nothing in the game warns you.

> **Both were re-measured on 2026-08-25 and both are now CORRECT on disk.** The four stems
> are 661544 bytes each = **7.5000 s exactly**, and `VoiceGains.asset` holds 60 clip names
> against 60 gains with a **maximum of exactly 1.0**. The two paragraphs below describe
> faults that have been repaired; they are kept because the FAILURE MODE is what matters —
> both assets are generated, both can drift again the moment a generator changes and its
> menu item is not re-run, and neither the game nor the console will tell you. Re-measure
> rather than assuming either way.

- The four `MUS_*.wav` stems must be **identical length**. `Systems_MusicDirector` starts all
  four with a single `PlayScheduled` and `loop = true` and never re-syncs, so unequal lengths
  drift permanently. The shipped stems were 7.500 / 7.600 / 7.850 / 7.880 s — drums lapped the
  bed by 380 ms *per loop* and the arrangement was incoherent within a minute. `GenerateAudio`
  has since shared one `MUSIC_LOOP_FADE` across all four and emits 7.5 s for every stem; the
  WAVs simply predated it. **Check `MUSIC_SECONDS` against the files on disk before believing
  the score is in sync** — the byte length is `(size - 44) / 88200` seconds.
- `VoiceGains.asset` must be **attenuate-only**. `AudioSource.volume` clamps to 1 and
  `Systems_FighterVoice` multiplies by 0.9, so any gain above ~1.11 silently pins at maximum.
  The shipped table ran to 6.23 and 24 of its 30 clips were clamped, i.e. normalization was a
  no-op. `NormalizeVoice` rebases so max gain is exactly 1.0; the asset had never been
  regenerated after that fix.

**All four fighters now have all three voice sets** — Happy / Sad / Insult, 5 levels each,
60 clips, verified on disk 2026-08-25. `GeneratePlaceholderVoices` filled the gaps. This
paragraph said "Matt and Nick have all three, Kim has Happy only, Standard has none" until
then, which was true on 2026-08-15 and stopped being true without the text moving.

Face art is still uneven and that IS still the case: only **Kim, Matt and Nick have it**
(7 PNGs each — neutral, happy 1-3, sad 1-3). Standard has no face art.
`Systems_FighterVoice` and `Systems_FaceMood` both disable themselves rather than warn, so
a silent, faceless fighter looks intentional. The bracket seeds all four twice.

**A missing set and an incomplete set behave differently, and only one of them is quiet.**
`LoadSet` returns null when it finds *zero* clips — no log line, which is why Standard and
Kim's Sad/Insult are silent rather than noisy. Find 1-4 of 5 and it `Debug.LogWarning`s on
every match. So a partially-delivered set is worse than none: fill all five levels or
leave the set empty, never in between. Naming is exact —
`Resources/Audio/Voice/<Behavior>_<Happy|Sad|Insult>_<1-5>.wav`, where the behavior name
is the one on the character asset. There is no second accepted spelling any more.

Level 1 is the mildest read and **level 5 fires on the match win**, so order a set by
intensity, not by whatever the source files were called. Clip LENGTH matters at level 5
specifically: `_nextAllowedTime` is `clip.length * 0.6`, so a long line mutes that fighter
for a proportional stretch afterwards — free on a match win, costly anywhere else. Kim's
level 5 is 7.96 s for exactly this reason.

`SCN_TOURNAMENT` has **no AudioListener in the scene** — the game boots into it (build index
0), so `Systems_TournamentBracket.EnsureAudioListener()` adds one in `Start`. It is
deliberately not persistent: `LoadScene(ARENA_SCENE)` is Single mode, so it dies with the
scene rather than fighting `SCN_SUMO`'s own listener.

**Android signing keeps the keystore and its password outside the repo**, at
`C:/Users/punko/Downloads/PoSumo-Release/` (`keystore.pass`, one line), because Unity
deliberately does not serialize keystore passwords into `ProjectSettings`. Both Android
builds read `POSUMO_KEYSTORE_PASS` first and fall back to that file, then clear the
password out of `PlayerSettings` afterwards. Without either, the build **aborts** rather
than producing an unsigned artifact.

`Builds/` is gitignored and disposable — every env build is reproducible from its menu
entry, so retire a build by deleting the folder rather than keeping it around.

Judge a character by the harness tally, not by one eyeballed round. The build/deploy tools
print a `BUILD RESULT:` / `AAB BUILD RESULT:` / `DEPLOY RESULT:` line — that is how their
outcome is read back. `MatchTestHarness` and the Game-view screenshot flow below are the
verification tools for anything behavioural; the EditMode suite in `Assets/Tests/EditMode`
covers only the two pure-logic corners described under *Commands at a glance*.

## Unity Editor automation — `Tools/unity.py` FIRST

**`Tools/unity.py` is the route that works headlessly, and it should be the first
thing tried.** Standard library only, no venv, no login:

```
python Tools/unity.py ping                      # is the Editor reachable
python Tools/unity.py scene SCN_TOURNAMENT      # load a scene
python Tools/unity.py play | stop | pause
python Tools/unity.py errors [--warnings]       # read the console
python Tools/unity.py shot Temp/x.png           # Game view PNG, UI Toolkit included
python Tools/unity.py exec 'return UnityEngine.Application.unityVersion;'
python Tools/unity.py raw manage_scene '{"action":"get_hierarchy"}'
python Tools/unity.py tools                     # the 35 available tools
```

It speaks the **CoplayDev `com.coplaydev.unity-mcp` `StdioBridgeHost`** protocol
directly — a plain TCP socket that is already running whenever the Editor is. An
earlier version of this file said that package was "installed but not connected";
it is the most reliable automation surface in the project.

**The bridge port is not fixed, and this file said 6401 until 2026-08-06.** The host
picks a free port at startup, so it moves on every Editor restart and on any domain
reload that restarts it — an MCP package upgrade moved it to **6400** while the old
6401 listener was *still bound by the same Unity process*. That stale listener
accepts the TCP connect and then sends nothing, so every call hung for its full
300 s timeout rather than failing: **connect succeeds, handshake never arrives** is
the signature. `Tools/unity.py` now discovers the port by probing 6400-6410 for the
handshake itself (an open socket is exactly what the stale listener fakes), and
`POSUMO_UNITY_PORT` forces one — but is still probed, so a stale forced port errors
in ~1.5 s instead of hanging. Read the real port out of the Editor log line
`StdioBridgeHost started on port`.

Why not the other two, both measured on 2026-08-05:
- **`ai-game-developer` (`.mcp.json`) is a REMOTE relay** at
  `https://ai-game.dev/mcp/p/fc108679`, not localhost, and needs an OAuth flow that
  no headless or non-interactive session can complete. This file used to call it
  "no login" — that is wrong.
- **Besty UnitySkills (8090) is easy to lose.** Closing the Editor while a process
  it spawned is still alive — VS Code, as the external script editor — leaks the
  listening socket to that child, which keeps it open. The socket then shows as
  LISTENING under a **dead pid**, the next Editor silently fails to bind, and the
  port answers TCP but never HTTP. Freeing it means killing the inheriting process.

Wire protocol, in case the helper ever needs rewriting (it is in no documentation —
this was read out of `StdioBridgeHost.cs`): connect, read a **raw, unframed**
handshake line `WELCOME UNITY-MCP 1 FRAMING=1\n` up to the newline *and no further*
or you eat the head of the first frame; thereafter every message is an **8-byte
big-endian length** followed by a UTF-8 JSON `{"type": "<tool>", "params": {...}}`.
Tool names are auto-derived from `[McpForUnityTool]` attributes in snake_case.

Two traps the helper already handles, both of which cost a wrong conclusion here:
- **`execute_code` requires `action: "execute"`.** Omitting it fails with a bare
  `'action' parameter is required.` that does not name the offending tool.
- **`shot` must let animations settle.** `ScreenCapture.CaptureScreenshot` is
  asynchronous (end of frame) *and* capturing in the same frame a panel starts its
  140 ms `FadeIn` photographs it at opacity ~0 — which reads as a rendering bug
  rather than a timing one. `--settle` and the size-stability wait exist for that.
  It is `CaptureScreenshot` and not a Camera+RenderTexture render because every
  screen here is UI Toolkit in a screen-overlay panel, which a camera render does
  not see at all.

`manage_editor` has no `get_state`; read editor state with `exec` instead.

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

## Portrait layout checking (`Tools/portrait_check.py`)

`python Tools/portrait_check.py` sets a real device Game-view size, opens
`SCN_TOURNAMENT`, plays a bout, captures both screens and runs an **overflow audit**
over the live visual tree at 1080x1920, 1080x2400 and 1200x1600. `--no-play` audits
the bracket only; `--sizes 1080x2400` picks one.

**It exists because the Game view was on 960x2658 — not a device aspect — and the
Editor was parked on `SCN_TRAIN_MATT`, so a whole class of layout fault was
invisible.** The audit reports any element whose children lay out past its own
resolved height, which is the signature of the bug below; ScrollViews are exempt,
because holding more than they show is their job.

> **A ScrollView's content children inherit `flex-shrink: 1`, so a column taller than
> the viewport is silently COMPRESSED to fit instead of scrolling.** Nothing errors,
> no scroller appears, and the only visible symptom is two unrelated things
> overlapping somewhere further down the page.
>
> MEASURED 2026-08-25 on the bracket at 1080x1920: the roster palette reported
> height **139** while laying its three wrapped lines out to **y=210**, so it drew
> over the QUARTERFINALS header; the banzuke block printed its three rows on top of
> each other; and the whole column measured exactly the 1033pt viewport, i.e. the
> page never scrolled at all. `flexShrink = 0` on the content children took it to
> 1345pt and it scrolls. The palette was worst hit because it WRAPS — a wrapping
> row's height is not the sum of its children, so the shrink has no natural floor.
>
> `Systems_TournamentBracket` now applies this in ONE place after the content is
> built, with the slack spacer as the deliberate exception. Add a block to that
> column and it is covered; build another scrolling screen and it is not.

## Three things measured on 2026-08-25 that contradict what the code assumed

- **The perf HUD shipped to players.** `enablePerfHud` defaults `true` in code and is
  ABSENT from `GameTuning.asset`, so the code default is what runs, and
  `Systems_PerfHud` carries no build guard. It is now ANDed with
  `Debug.isDebugBuild || Application.isEditor` at the spawn site, the same gate
  `Systems_Telemetry` uses.
- **The perf HUD's GC readout was wrong by ~15x.** It printed one 0.25 s window,
  divided by the CONSTANT rather than the measured elapsed time, and only when the
  delta was positive — so it sampled the peaks of a bursty allocator and discarded
  every window that straddled a collection. It read "+280 MB/s" during a live match
  while real heap growth was ~19 MB/s, of which ~13 MB/s was the Editor itself, with
  only 2 gen-0 collections in 8 seconds. **There is no allocation emergency in this
  project** — do not go hunting one on the strength of the old readout. It now
  averages over 3 s and keeps negative deltas.
- **A fighter can meet ITSELF, and it is not just cosmetic.**
  `SeparateFirstRoundMirrors` fixes the opening round and documents that later
  mirrors are structural; a **Nick-v-Nick FINAL** was measured. Both sides render the
  same colour and face, the scorebug reads "NICK 1 : 1 NICK", and
  `Systems_CareerStats.RecordMatch` guards `winner == loser` — so the most important
  match of the bracket banked no W/L, no Elo and no match count while `RecordTitle`
  still awarded the title. `Systems_MatchRoster` now hue-shifts side B's body and
  names it `<Name> II`, read back through `Agent_BipedBody.teamColorOverride` and
  `Agent_Biped.displayNameOverride` (both `[NonSerialized]`, both presentation only —
  `behaviorName` must keep matching the YAML key or that fighter has no brain).

### The walk-in stalls on EVERY match, and the referee now glides out of it

`Phase.WalkInPark` was added 2026-08-25. The stall path was written as a rare
backstop; measured, it fires **every single bout** (7 stalls in a 7-match bracket,
3.3-4.7 s each) because the shipped gait cannot cross the opening gap at all. That
made a hard snap from wherever they gave up straight onto the stand-off marks the
most visible glitch in the game.

The fighters now glide onto the marks over `WALKIN_PARK_SECONDS` (0.45 s,
smoothstepped). Physics is already frozen, the destination is identical and the fight
starts from the same marks — it is presentation only, and it should be **deleted the
day a policy can actually walk in**. It is the "solve the ceremony in presentation"
option this file recommends after five failed gait retrains, not a fix for the gait.

Note `BeginWalkInPark()` must be called **before** `HoldUpright()`, which is itself a
teleport onto the marks — capturing after it glides from the mark to the mark.

### The portrait dead space has a lever nobody had tried: `Camera.rect`

`Systems_CameraFollow`'s `feetDrop` note is right that no camera VALUE fixes the
~30% of every frame that is black below the dohyo, and concludes the space must be
filled. That conclusion assumed the camera owns the whole screen.

`GameTuning.enableArenaBand` (**OFF by default**) confines it to a band instead,
which raises the aspect the ortho maths divides by — the one lever that changes the
trade rather than shuffling it. MEASURED at 1080x1920: a 0.20-0.82 band took aspect
**0.563 -> 0.907** and the fighters rendered about **2.5x larger** with both still in
frame. Verified with no HUD overflow at three aspects.

It is off because it is a RENDERING change, not just a framing one: the region
outside a camera's rect is not drawn by that camera, so it needs the `ArenaBandClear`
camera the follow spawns (`cullingMask = 0`, it exists purely for its clear — without
it the outside keeps the previous frame and smears). Turn it on, look at it at more
than two moments, and give the arena dressing something to reach the band edges.

## Verification expectations

After scene or body changes, verify in Game view (via the screenshot flow above): both
fighters clearly visible on the dohyo, realistic gravity/contacts, no console errors, and
the HUD/score readable in portrait. For behavioural changes, run `MatchTestHarness.Run(n)`
and report the tally rather than an impression.
