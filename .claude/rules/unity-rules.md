# UNITY_RULES — ML-Agents Physics Simulation

Principal Unity ML-Agents Architect: modular, production-ready C# for active-ragdoll agents.

## 1. Naming

- `Assets/Agents/<Name>_v<NN>/` — `.onnx`, `*_Character.asset`, `MANIFEST.md`.
- Four script prefixes, each matching its folder under `Assets/Scripts/`: `Agent_`,
  `Sensor_`, `Reward_`, `Systems_` (referees, presentation, persistence, UI — the largest).
- Scenes `SCN_`; training `SCN_TRAIN_<NAME>`, no suffixes. Envs `Builds/<Name>Env/`;
  configs `<Name><Phase><NN>.yaml` paired 1:1 with run-id `<name>_<phase><nn>`.

## 2. Physics & Biomechanics

- Earth gravity (−9.81), SI units, realistic friction, deterministic execution.
- Actions apply only in `FixedUpdate` — physics integrates once per tick, so a torque
  written on the render clock becomes a frame-rate-dependent force, and training runs
  uncapped while the phone runs at 60. Lock Δt = 0.02 s and solver iterations: every torque
  ramp integrates against `fixedDeltaTime`, and a quality-level override changes the
  dynamics every brain was fitted against with no error raised. `timeScale` is safe.
- Normalize actions to [−1, 1], scaled to real joint limits and DoF.
- Fatigue reads load from **applied torque, not the action vector** — isometric bracing is a
  near-zero action at near-maximum torque. Clear it on reset *before* restoring motors.
- Anchors derive from segment lengths — move a segment, everything above it moves.
- Verify joint ranges **parent-local**: gravity off, the body counter-rotates and a
  world-space test reads drift. Measure the `jointAngle` sign.

## 3. Display

- UI Toolkit only — no UGUI, no IMGUI, no `.uxml`/`.uss`. Screens built from C# at runtime.
- Safe area mandatory: absolute children resolve against the parent's *padding* box, so the
  inset belongs on that layer.
- Portrait 9:16, 60 FPS (`vSyncCount = 0` or `targetFrameRate` is ignored).
- `Application.version` top-left of the opening scene: inset layer, outside any ScrollView,
  non-pickable.
- The panel scales on width — size against a live capture.

## 4. MLOps

- C# and Python `mlagents` versions must match; comms API version **equal** or the
  handshake is refused.
- Overwrite `.onnx` in place to preserve `.meta` GUIDs.
- Headless: `--env --no-graphics` + explicit `--base-port` (envs take consecutive ports;
  collisions hang). 4–8 envs, leaving cores for torch. `--num-envs` changes how experience
  is batched — record it.
- Telemetry over HTTP *and* `StatsRecorder`. Kill TensorBoard before `--force`: it holds
  Windows handles and the wipe silently no-ops. Clean up trainer → envs → TensorBoard.
- **Judge self-play on ELO, not mean reward.**

## 5. Architecture — read before planning

**No dependency-injection or messaging library is installed** — no VContainer, MessagePipe,
UniTask or R3. `[Inject]`, `IPublisher<T>` and `await UniTask` do not compile. If a plan
asks "which messages does this publish?" or "who owns the Model?", it is assuming a stack
this project does not have. Ignore the question and follow the recipe below.

**Adding a feature — the whole recipe:**

1. Write a `Systems_*` MonoBehaviour in `Assets/Scripts/Systems/`. It lands in
   `PoSumo.Runtime`; `PoSumo.Editor` is for menu tools only. Those are the only two
   assemblies.
2. Subscribe to what it needs, and unsubscribe in `OnDisable`:
   `RoundStarted`, `RoundEnded`, `MatchEnded`, `MatchReset` on `Systems_GameMatchManager`,
   or the `Knockout` / `Dismembered` statics on `Systems_BodyDamage`.
3. Add an `enable<Feature>` flag for it on `GameTuning.asset`.
4. Add one line to `Systems_GameMatchManager.Start` that spawns it when the flag is on.

No scene editing, no Inspector wiring. Scenes hold only managers; everything else is built
in code during `Awake`.

**Four more rules:**

- A `static` holding game state needs `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]`
  to clear or reload itself. Domain reload is off, so state otherwise leaks into your next
  Play session — and the bug only shows up on the second run.
- Reward providers never touch the `Agent`. They take the body and return a float. Only
  `Agent_Biped` ends an episode.
- Changing a losing condition means changing it in **both** referees —
  `Systems_GameMatchManager` (game) and `Systems_SumoMatchManager` (training) — or the
  brains never learn the new rule is fatal.
- No coroutines. Use a countdown field in `Update`.

Detail: `.claude/rules/architecture.md`. *Verified 2026-08-07.*
