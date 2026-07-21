# PoSumo — Design Spec

1v1 sumo wrestling between physics-driven 2D ragdoll bipeds trained with ML-Agents.
Side view. Two humanlike bipeds built from 2D primitives walk in from the screen
edges, meet at center, and try to shove each other out of the ring.

## Goals
- Two AI sumo bipeds that walk upright to the center and push each other with
  arms/body using realistic physics (earth gravity, human masses, joint torques).
- Watchable training: fixed camera showing the whole arena, 1-meter grid
  background so size/height is readable at a glance.
- Trainable on CPU in hours, not days.

## Non-goals
- No player control, no 3D, no art pass (primitives only), no getting-up skill
  requirement (falling costs time, not the round), no networking.

## Rules
- **Ring-out only:** you lose when your torso crosses the ring edge (x = ±7 m).
- Falling down is not a loss — the round continues until ring-out or the 30 s
  timeout (timeout = draw).

## The biped (per wrestler)
13 parts from 2D primitives, ~1.8 m tall, ~79 kg total:

| Part | Primitive | Mass | Joint to parent | Motor limits |
|---|---|---|---|---|
| Torso | box 0.35×0.60 | 32 kg | — | — |
| Head | circle r 0.12 | 6 kg | fixed to torso | none |
| Upper arm ×2 | box 0.10×0.30 | 2.5 kg | shoulder (hinge) | ±160°, 150 N·m |
| Forearm ×2 | box 0.09×0.28 | 1.8 kg | elbow (hinge) | 0–150°, 100 N·m |
| Thigh ×2 | box 0.14×0.40 | 7 kg | hip (hinge) | −30°…+120°, 300 N·m |
| Shin ×2 | box 0.11×0.38 | 3.5 kg | knee (hinge) | −150°…0°, 250 N·m |
| Foot ×2 | box 0.22×0.08 | 1 kg | ankle (hinge) | ±45°, 120 N·m |

- **10 motorized HingeJoint2D** (2 shoulders, 2 elbows, 2 hips, 2 knees, 2 ankles).
  Action = target motor speed in [−1, 1] × max speed; torque capped per joint.
- **Self-pass-through:** each wrestler's parts live on their own physics layer
  (`WrestlerA`, `WrestlerB`). Collision matrix: A↔A off, B↔B off, A↔B **on**,
  both ↔ Ground on. Limbs swing freely past their own body but hit the opponent.
- Feet/ground use high-friction PhysicsMaterial2D (≈0.8–0.9) so pushing works.
- Gravity: Physics2D global (0, −9.81).

## Arena & camera
- Flat ground at y = 0 spanning the screen; ring edges marked at x = ±7 m
  (posts/flags). Spawns at x = ±5 m, facing each other.
- Background: 1 m × 1 m grid lines (subtle), heavier line every 5 m, so the
  1.8 m bipeds read instantly against it.
- Camera: orthographic, fixed at (0, 4), size 5 → frames 10 m of height and the
  full ring width at 16:9. Both bipeds always fully visible.

## ML-Agents setup
- **One behavior: `Matt`** — identical observation/action spaces in both
  phases so weights transfer.
- **Observations (vector):** torso world-y, torso velocity, torso up-angle; per
  joint: angle + angular velocity (10×2); per body part (subset): relative
  position/velocity; opponent: relative position and velocity of their torso;
  distance to nearest ring edge; facing sign. All x-quantities multiplied by the
  facing sign so **one policy works facing left or right** (mirror-invariant).
- **Actions:** 10 continuous (one per motor).
- **Phase 1 — Walk** (`Training_Walk.unity`): 8 parallel solo bipeds in offset
  lanes. "Opponent" observation slots are filled with a virtual target at ring
  center. Reward: forward velocity toward target + upright bonus − energy
  penalty; episode ends on torso/ground contact or reaching center.
  Run: `mlagents-learn configs/MattWalk.yaml --run-id=walk01`.
- **Phase 2 — Sumo** (`PoSumo.unity`): two bipeds, team ids 0/1, **PPO +
  self-play** (shared policy, ELO tracked). Initialized from phase 1 via
  `--initialize-from=walk01`. Reward: ring-out win +1 / loss −1, small ongoing
  upright + approach shaping (decayed), draw 0.
- Existing venv/config layout in `Training/`; behavior name matches
  `Training/configs/*.yaml`.

## Components (all runtime C#, scene assembled via MCP tools — no editor scripts)
- `BipedAgent.cs` — Agent subclass: observations, actions→motors, rewards.
- `BipedBuilder` prefab structure (assembled once via MCP, saved as prefab).
- `SumoMatchManager.cs` — spawns/resets both wrestlers, detects ring-out,
  assigns win/loss, handles timeout.
- `WalkTrainingArea.cs` — phase-1 lane reset logic.
- `GridBackground` — sprite/shader-less tiled 1 m grid.

## Risks / open items
- Ragdoll walking is genuinely hard: expect hours of wobbling in phase 1 before
  gait emerges; hyperparameters may need 1–2 iterations.
- Since falling isn't a loss, matches can stall with both wrestlers down —
  mitigated by timeout + upright shaping.
- 2D hinge ragdolls can jitter at high torque; may need increased solver
  iterations (Physics2D velocity/position iterations 12/6).

## Approval
- [x] Approved by user: 2026-07-20 ("go")
