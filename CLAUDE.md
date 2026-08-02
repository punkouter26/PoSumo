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
those scenes too or the brains train on an arena the game does not have. Its **code
defaults were never updated** and still read 5.5 / `startHalfRange (1.7, 5.5)` /
`spawnGapHalf 1.2`; the four training scenes serialize the correct 4 / `(1.7, 4)` / 2.5.
Grep the `.unity` files, not the `.cs`, to learn what an env actually trains against.

The roster is exactly four trained fighters — **Matt**, **Standard**, **Nick**,
**Kim** — each with an `.onnx`, a `*_Character.asset` and a `MANIFEST.md`. The
8-slot bracket seeds each of them twice. `Assets/Agents/ROSTER.md` is the roster
overview; there is no code mirror of it.

**`ROSTER.md` and the per-fighter `MANIFEST.md` files predate the walk+fight merge** and
still describe 44 obs, separate walk brains and the old `*_sumoNN` runs. `Training/README.md`
is current (it has the unified-run table); the character assets and `Agent_Biped` are
authoritative over all of them. Fix a manifest when you touch its fighter rather than
trusting it.

`Training/results/` is gitignored **and is not present in a fresh clone** — the deployed
`.onnx` files under `Assets/Agents/` are the only brains that ship. Anything in this file
about resuming, `--initialize-from` or `DeployLatestCheckpoint` assumes you have first
re-run training locally to recreate that directory.

## Toolchain versions (validated in production — treat as the required set)

| Layer | Tool | Version | Notes |
|---|---|---|---|
| Engine | Unity Editor | **6000.5.6f1** (Unity 6.2) | changeset 0e0577a1a2ac. Was 6000.5.4f1 (d550df8bd089) — see the drift note below |
| Engine | Unity Hub | 3.x | headless CLI broken — install modules via UI |
| Package | com.unity.ml-agents | **4.0.0** (release_23) | LOCAL `file:` package with patches — never re-fetch |
| Package | com.unity.ai.inference | 2.6.1 | auto-dependency of ML-Agents (`Unity.InferenceEngine.ModelAsset`). Was 2.2.1 |
| Package | URP | 17.5.0 | project template |
| MCP | unity-mcp-cli (npm) | 0.86.0 | |
| MCP | com.ivanmurzak.unity.mcp | 0.86.3 | + gamedev-mcp-server 9.2.0 |
| MCP | com.coplaydev.unity-mcp | 10.1.0 | |
| MCP | com.besty.unity-skills | 2.2.1 | HTTP server port 8090 |
| Python | Python | **3.10.11** | hard range: >=3.10.1, <=3.10.12 |
| Python | mlagents / ml-agents-envs | **1.2.0.dev0** | built from release_23 source; envs is patched |
| Python | torch | **2.5.1** (+cpu) | PIN — 2.6+ breaks ONNX export |
| Python | setuptools | **69.5.1** | PIN — 70+ removes pkg_resources |
| Python | numpy | 1.23.5 | pinned by mlagents |
| Python | onnx | 1.15.0 | |
| Python | tensorboard | 2.20.0 | always run during training |
| Android | Build Support module | 6000.5.6f1 | must match the editor version |
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

Re-measure rather than trusting this table when something behaves oddly; a version drifting
under a "required set" heading is how a project ends up debugging the wrong layer.

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

## Commands at a glance

Almost nothing here is a shell command — the editor menu is the build system. Details for
each live in *Editor menu tools* and *Training workflow* below.

| Goal | How |
|---|---|
| Play the game | Open `SCN_TOURNAMENT` and enter Play mode (it loads `SCN_SUMO` per bout) |
| Compile / import after editing `.cs` outside the editor | MCP `assets-refresh` (ForceUpdate) |
| Behavioural test | Play mode in `SCN_SUMO`, then `MatchTestHarness.Run(n)` via MCP `script-execute` → `HARNESS RESULT:` |
| Ship an Android build | *PoSumo → Build Android APK* / *Build Android AAB (Play release)* |
| Build a training env | *PoSumo → Build \<Name\> Training Env* → `Builds/<Name>Env/<Name>Env.exe` |
| Train | `Training\venv\Scripts\mlagents-learn.exe` (+ TensorBoard, always) |
| Ship a brain | *PoSumo → Deploy \<Name\> Brain* |

There is no lint step and no unit-test suite; the hooks in `.claude/hooks/` are the
static checks, and they run on edit rather than on demand.

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
- **`Agent_Biped.ObservationCount = 42`**, or **45** when `extendedObservations` is on
  (+ opponent uprightness / down flag / edge distance, decision period 3 — the standard
  for all four shipped fighters). Layout: 5 body + 26 joint (13 × angle/speed) + 4 feet
  + **1 task flag** + 4 opponent-or-target + 2 edge distances. The pre-merge counts were
  41/44; the task flag is what took them to 42/45 and invalidated every earlier brain.
  Prose elsewhere in the repo (`MANIFEST.md`, `ROSTER.md`, the tooltip on
  `Agent_CharacterDefinition.extendedObservations`) still says 44 — **the constant in
  `Agent_Biped` is the truth**. Obs count and decision period MUST match what the assigned
  `.onnx` was trained with, or inference is silently garbage.
- Three `Mode`s: `Walk` (falling ends the episode), `Recover` (get up, then walk —
  falling never ends it, but lying down bleeds reward; nothing references it any more),
  `Sumo` (refereed externally; shaping only, ±1 comes from the referee).
- Configures its own `BehaviorParameters` / `DecisionRequester` in `Awake` — nothing to
  wire in the Inspector.
- All observations pass through `San()` NaN/Inf sanitization.
- `BeginWalkIn` / `EndWalkIn` **switch `mode` between `Sumo` and `Walk`** for the
  ceremonial round-opening walk-in — flipping the task flag and pointing the four
  "opponent" slots at a virtual target — plus `suppressEpisodeControl` so the presentation
  layer can borrow the body safely. There is **no model swap**: `walkModel` and
  `DeployWalk` no longer exist. The leftover `<Name>Walk.onnx` files in each agent folder
  are pre-merge artifacts that nothing loads.

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
  presentation system subscribes to. At 1455 lines it is the largest file in the project
  and the entry point for anything match-shaped.

Falling is **not** a loss in either referee (`knockdownLoses` is off). If you change a
losing condition, change it in **both** — they have silently diverged before, and
policies then never learn that a stray foot over the edge is fatal.

**Three game-only rules exist that the brains never train against**, and that asymmetry is
deliberate — they are spectacle layered on the sumo rules, not sumo rules:
`downOutSeconds` (3 s lying down forfeits the round — `IsDown` can latch permanently once
a leg is under the body, and measured play had half of every round be two motionless
ragdolls waiting out the clock), `knockoutsToLoseMatch` (3 head KOs lose the match
outright, via `Systems_BodyDamage.Knockout`), and the low-friction `tawara` band at the
rim (`tawaraBandWidth` / `tawaraFriction`) that turns "almost out" into "out".
`Systems_SumoMatchManager` has no equivalent of any of them.

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

### The look: one shader, and switches that must move together
`Assets/Resources/Shaders/PoSumo_BodyLit.shader` is the **only authored shader in the
project** — everything else is a runtime `new Material` on a stock URP sprite shader, and
there are no `.mat` assets and no Shader Graph files at all. It is a 3-pass copy of
`Sprite-Lit-Default` (all three passes must declare an IDENTICAL `UnityPerMaterial` CBUFFER
or the SRP Batcher silently drops it) plus four terms: rim, subsurface wrap, sweat and clay.

Three switches in two files gate that look and are only correct **together**:

| Switch | File | Meaning |
|---|---|---|
| `FlatBodyShading` | `Systems_ArenaLighting` | `true` zeroes rim/wrap/sweat and skips the normal map — flat tinted primitives |
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
tug-of-war bar, ~115pt) and a **detail card** in the stage band carrying the
seven aggregate metrics, shown on `RoundEnded`/`MatchEnded` and hidden on
`RoundStarted`. It was previously one ~484pt table pinned to the dock with no way
to hide it — 39% of a 9:16 panel and ~52% of a 4:3 tablet in portrait,
permanently over the bottom of the dohyo. Nobody parses a work-rate percentage
while a bout is being decided; put new aggregate metrics on the detail card.

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
| `Systems_BodySurface` | writes `_Sweat` and `_Dirt` into one fighter's `PoSumo/BodyLit` material — sheen from exertion, clay from arena contact. Rides `enableLighting`; disables itself when `FlatBodyShading` is on |
| `Systems_ImpactFx` / `Systems_DustPuff` / `Systems_SoftBodyJiggle` / `Systems_BlobShadow` | hit bursts, dust, flesh wobble, contact shadows |
| `Systems_BodyDamage` / `Systems_RingBlood` | bruise decals, **limb loss and decapitation**, the bloody head KO, and blood left on the mat. **Owns the `Knockout` static event the referee's 3-KO rule reads**, so it is the one "presentation" system with a rules consequence |
| `Systems_FaceMood` | expression driven by dominance |
| `Systems_CareerRecorder` | the only writer into career stats |

**Fighters can be dismembered and decapitated, and this is not cosmetic.** Measured play
produces `[DAMAGE] Damage_Nick lost LegNear at 20.0 damage — bleeding from stump 'Pelvis'`
and `Damage_Matt DECAPITATED at 2.8 damage`. A fighter that loses a leg cannot satisfy the
get-up condition again, so it is `downOutSeconds` — not the ring-out — that actually ends
that round. That is the interaction to keep in mind when tuning either: shorten
`downOutSeconds` and dismembered fighters are retired faster; disable it and they lie
there until the clock expires, which is the exact stall the rule was added to kill.
The training referee has no equivalent, so no brain has ever trained against it.

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
| `BuildAndroid` | *Build Android APK* → `Builds/Android/PoSumo.apk` from the enabled build-settings scenes |
| `BuildAndroidAAB` | *Build Android AAB (Play release)* → `Builds/Android/PoSumo.aab`, signed. Logs `AAB BUILD RESULT:` |
| `DeployBrain` | Copy a run's ONNX → agent folder + wire the character asset. One entry per fighter, each pinned to the run that currently backs its shipped brain |
| `MatchTestHarness` | `MatchTestHarness.Run(n)` in Play mode: chains N matches unattended, logs a `HARNESS RESULT:` win/loss tally |
| `GenerateAudio` | *Generate Audio* — synthesizes the match SFX bank in-editor; the clips are generated assets, not recordings |
| `NormalizeVoice` | *Normalize Voice Levels* — evens out the per-fighter voice clips |

**Both of these write assets that then go stale silently, and both had done so.** Fixing the
generator does nothing until the menu item is re-run, and nothing in the game warns you:

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

Audio content is uneven and this is not a bug: only **Matt and Nick have voice clips**, and
only **Kim, Matt and Nick have face art** — Standard has neither. `Systems_FighterVoice` and
`Systems_FaceMood` both disable themselves rather than warn, so a silent, faceless fighter
looks intentional. The bracket seeds all four twice.

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
outcome is read back. There is no test suite: `MatchTestHarness` and the Game-view
screenshot flow below are the verification tools this project has.

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
