# Architecture Rules

This file describes the architecture PoSumo **actually has**. It is not a generic Unity
template. Where it disagrees with `CLAUDE.md`, `CLAUDE.md` wins — and where either
disagrees with the code, the code wins.

**There is no VContainer, no MessagePipe, no UniTask, and no R3/UniRx in this project.**
None of the three appear in `Packages/manifest.json` or `packages-lock.json`. Do not write
`[Inject]`, `LifetimeScope`, `IPublisher<T>`, `ReactiveProperty<T>` or `await UniTask...` —
none of it compiles here. An earlier version of this file mandated all of them, which is
how plans get written against a stack that does not exist.

## The shape of the project

```
Assets/Scripts/
  Agent/    Agent_*     the ragdoll biped, its brain contract, character assets
  Sensor/   Sensor_*    observation helpers
  Reward/   Reward_*    reward-shaping providers (plain C#, no MonoBehaviour)
  Systems/  Systems_*   everything else — referees, presentation, persistence, UI
```

Exactly four folders, four prefixes. The prefix and the folder must agree. Adding a fifth
family means adding a folder and updating `CLAUDE.md`'s Conventions section.

Three assembly definitions, and that is the whole graph:

| Assembly | Contains |
|---|---|
| `PoSumo.Runtime` | `Assets/Scripts/` — everything shipped |
| `PoSumo.Editor` | `Assets/Editor/` — menu tools, builders, the match harness |
| `PoSumo.Tests.EditMode` | `Assets/Tests/EditMode/` — the pure-logic unit suite |

Editor code may reference runtime code. Never the reverse. The test assembly references
runtime only, is `includePlatforms: ["Editor"]` and carries
`defineConstraints: ["UNITY_INCLUDE_TESTS"]`, so it cannot reach a player build. Nothing
references it. Runtime code that needs a
`UnityEditor` type must guard it with `#if UNITY_EDITOR` — a PreToolUse hook
(`guard-editor-runtime.sh`) blocks the unguarded case.

## Scenes hold managers; everything else is built at runtime

Arena and training scenes contain manager objects and (for arenas) baked arena children.
The 14-part ragdoll is constructed in `Agent_BipedBody.Awake()` from the code tables
`PART_DEFS` / `JOINT_DEFS`. `Agent_Biped` configures its own `BehaviorParameters` and
`DecisionRequester` in `Awake`. There is nothing to wire in the Inspector, and a feature
that requires Inspector wiring is fighting the project.

Consequence worth stating plainly: **prefer code-built structure over serialized scene
structure.** A serialized value in a `.unity` file is the thing that goes stale silently
here — see the tuning-asset rule below.

## Presentation companions: spawned, never placed

`Systems_GameMatchManager.Start` does `new GameObject(...)` for every presentation system —
lighting, audio, music, damage, dust, blob shadows, face mood, career recording — each
gated by an `enable*` bool on `Assets/Settings/GameTuning.asset`.

**This is the extension point.** A new presentation feature is:

1. a `Systems_*` MonoBehaviour that subscribes to the match events below,
2. an `enable*` flag on `Systems_GameTuning`,
3. one spawn line in `Systems_GameMatchManager.Start`.

It is **not** a scene object, and it is not a new manager that other systems reference
directly. That is why the arena scenes stay small and why turning a feature off is a tick
on one asset rather than an edit in three scenes.

## Communication: C# events on the match manager

There is no message bus. Cross-system communication goes through four instance events on
`Systems_GameMatchManager` and two statics on `Systems_BodyDamage`:

| Event | Signature | Meaning |
|---|---|---|
| `RoundStarted` | `Action` | a round began — hide result UI |
| `RoundEnded` | `Action<Agent_Biped, Agent_Biped>` | winner, loser |
| `MatchEnded` | `Action<Agent_Biped>` | match winner |
| `MatchReset` | `Action` | rematch |
| `Systems_BodyDamage.Knockout` | `static Action<Agent_BipedBody, Vector3>` | head KO — **the referee's 3-KO rule reads this** |
| `Systems_BodyDamage.Dismembered` | `static Action<Agent_BipedBody, Region, Vector3>` | limb loss |

Rules:

- **Subscribe in `OnEnable`, unsubscribe in `OnDisable`.** Statics especially — a static
  event holding a destroyed MonoBehaviour survives the scene load into the next bout.
- **Do not add a fifth match event casually.** Four events are the entire coupling surface
  between the referee and ~15 companions; each new one is a new thing every companion may
  come to depend on.
- A companion may read the manager it was spawned by. It must not reach across to another
  companion.

## Static state is legitimate here, with one mandatory rule

`Systems_TournamentState`, `Systems_CareerStats`, `Systems_AcademyLifecycle` and
`Systems_Telemetry` are static or self-spawning by design — they must outlive the
`LoadScene` that plays each bout, and a bracket that died with the arena scene would not be
a bracket.

**Enter Play Mode domain reload is DISABLED in this project.** Static state therefore
persists across Play sessions in the Editor unless you handle it. Every static holding game
state must carry a `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]`
that either clears it (`Systems_TournamentState`, the consume-once promotion flag in
`Systems_CareerRecorder`) or reloads it from disk (`Systems_CareerStats`). Adding new static
game state without this is a bug that only shows up on the *second* Play session.

Records are keyed by **behavior name** — the only fighter identity stable across folder and
asset renames. Never key persisted data on folder or asset name.

## Configuration lives in ScriptableObjects

| Asset | Type | Holds |
|---|---|---|
| `Assets/Settings/GameTuning.asset` | `Systems_GameTuning` | shared match numbers + every `enable*` feature flag |
| `Assets/Agents/<Name>_v01/<Name>_Character.asset` | `Agent_CharacterDefinition` | identity, body scales, brain generation, **all reward-shaping coefficients** |

Two rules that have each cost a bug:

- **Tune the asset, not the serialized scene value.** Scene components copy from
  `GameTuning` in `Start`, so the scenes still hold stale copies that are overwritten at
  runtime and will mislead anyone grepping the `.unity` file.
- **When adding a shaping coefficient, default it to the constant the code used before,**
  so an untuned character keeps training exactly what it always did.

Fighter personality belongs in the character asset and the training YAML header, never in
`Agent_Biped`.

When a code default and the asset disagree, the asset wins at runtime and the field is only
a fallback for when no tuning asset is assigned. That is a deliberate design, not drift.

## Reward providers: structurally unable to end an episode

`Reward_SumoObjective` and `Reward_WalkObjective` are plain C# classes. They hold the
per-character coefficients, are handed the body and a `Reward_Context`, and **return a
float**. They have no reference to the `Agent`, so a provider is structurally incapable of
calling `AddReward`, `SetReward` or `EndEpisode`.

Keep it that way when adding a provider:

- **Terminals stay in `Agent_Biped`.** `SetReward(-1)` discards that step's shaping, so the
  order of terminal checks against the `Evaluate` call is load-bearing.
- **`Reward_Context` is a `readonly struct` passed by `in`.** One per agent per physics
  step; as a class, 10 bipeds at 50 Hz would be 500 heap allocations a second in the
  hottest path in the project.
- **Cross-school state is owned by the agent, not duplicated per provider** —
  `Reward_StepCadence` is shared because `BeginWalkIn` switches a fighter between Walk and
  Sumo mid-round.

## Async: no coroutines, no UniTask

`Assets/Scripts/` currently contains **zero** `StartCoroutine` and zero `IEnumerator`.
Deferred and timed work is done with accumulator fields advanced in `Update` /
`FixedUpdate`, or with a small state machine like the referee's
Fighting → RoundEnded → Grace → Fighting loop.

Keep it that way. Do not introduce coroutines (they stop silently on `SetActive(false)` and
allocate), and do not reach for UniTask — it is not installed.

## Input

`com.unity.inputsystem` 1.20.0 is installed and **is read at runtime** —
`Systems_GameMatchManager` uses `Keyboard.current.escapeKey` to pause and
`Pointer.current.press` / `Keyboard.current.spaceKey` to continue. (This section claimed
"no runtime script reads either" until 2026-08-25; it was true once and the code moved
past it. The package is therefore NOT a removal candidate.)

`Assets/Settings/InputSystem_Actions.inputactions` is a different matter: nothing reads the
asset, only the static `Keyboard`/`Pointer` devices. Everything else is UI Toolkit buttons
handling their own events. Legacy `Input.GetKey` / `GetAxis` / `GetButton` must not be
introduced. If real gameplay input is ever added, add a single `Systems_Input*` reader that
enables its map in `OnEnable` and disables it in `OnDisable`, and have it call into the
referee rather than mutating fighters directly.

## UI

Every screen is UI Toolkit built from C# at runtime. There is **no UGUI Canvas, no `.uxml`
and no `.uss`** in the project. `Systems_UiKit` holds the tokens and builders;
`Systems_HudRoot` is the single `UIDocument` the match screen draws through — three
components used to add their own at equal sorting order, which has no defined draw or pick
order, and taps were being swallowed.

Build controls through `Systems_UiKit` or they will have no press feedback. Read
`Assets/UI Toolkit/README.md` before touching UI.

## Composition over inheritance

MonoBehaviour is a component, not a base class. Max inheritance depth 2. Intra-biped
collisions are disabled pairwise and every wrestler carries one
`CompositeShadowCaster2D` — the same intent both times: **a biped is one object assembled
from parts**, not a hierarchy.

## On `Systems_GameMatchManager` being large

It is ~2170 lines and it is the largest file in the project: round state machine, scoring,
countdown, timeout tiebreak, ceremony camera beats, and HUD. A generic rule would call this
a god object. (This line said ~1455 until 2026-08-25 — it had grown 50% while the doc
stood still, which is the usual way a file like this gets away from you. Re-count it rather
than trusting this number.)

The companion spawning came out on 2026-08-25 into
`Systems_GameMatchManager.Companions.cs`, a **partial of the same class** — the fourteen
`enable*` flags are private fields resolved from `GameTuning` in `Start`, so a separate type
would mean exposing all fourteen or threading a fourteen-field struct, which adds more
surface than the split removes. It is a file split, not yet the decoupling; the bodies did
not change, so it cannot alter a match. Making those flags a record this can take by
reference is what turns it into a real class with no further edits.

It is accepted, and the containment is the event surface: companions do not call back into
it, they subscribe. Do not add unrelated responsibility to it — a new feature is a new
`Systems_*` companion plus its `enable*` flag. Extracting the referee state machine from
the presentation spawning would be a welcome refactor; growing the file further is not.

## Verification

There is no lint step. The verification tools are:

- `MatchTestHarness.Run(n)` in Play mode → a `HARNESS RESULT:` win/loss tally. It stays
  inside ONE arena scene, so it proves nothing about the loop,
- `BracketTestHarness.Run()` in Play mode **on SCN_TOURNAMENT** → a
  `BRACKET HARNESS RESULT:` PASS/FAIL over a full 7-match bracket. This is the only check
  that crosses a `LoadScene`, which is where the static bracket, the reporter handover, the
  roster re-seed and `Time.timeScale` all have to survive,
- `python Tools/ref_audit.py` → every asset reference resolves, or exit 1. It indexes all
  THREE meta roots (`Assets/`, `Library/PackageCache/`, and the `file:` package at
  `Training/ml-agents/`); a scan missing any one of them invents broken references,
- the Game-view screenshot flow (`python Tools/unity.py shot ...`),
- `Tools/unity.py errors` / `console-get-logs` for the console,
- the EditMode unit suite in `Assets/Tests/EditMode` — run it with
  `run_tests` + `assemblyNames: ["PoSumo.Tests.EditMode"]`, never `testFilter`, which the
  bridge ignores while running every test in the project including a third-party one that
  always fails here.

**The unit suite deliberately covers almost nothing.** Only `Systems_CareerLadder` and
`Reward_Context.San` are testable without a scene, a physics step or the disk; everything
this project is about is behavioural. A green unit run is not evidence that a body,
reward, scene or brain change works — report those as a harness tally, not as an
impression. Judge a training run on ELO, not mean reward.

When adding a test, keep it off the disk: `Systems_CareerStats` saves `career.json` to
`Application.persistentDataPath` on every mutation, so exercising `Get`, `RecordMatch`,
`RecordRound`, `RecordTitle` or `ResetAll` destroys the player's real career. Construct a
`Systems_CareerStats.Record` directly instead.
