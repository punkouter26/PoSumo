# Bot_v01 — Agent Manifest

The BOT is the project's **one** heuristic fighter: a sumo wrestler driven by
hand-written C# rules instead of a trained policy. There is deliberately one of
these for the whole game, not one per fighter — it is a control, a sparring
partner and a fallback, and four copies of it would be four copies of the same
controller wearing different colours.

| Field | Value |
|---|---|
| Behavior name | `Bot` |
| Character asset | `Bot_Character.asset` — source of truth for build + shaping |
| Brain | **none.** `inferenceModel: {fileID: 0}`, and there is no `.onnx` here |
| Controller | [`Assets/Scripts/Agent/Agent_Bot.cs`](../../Scripts/Agent/Agent_Bot.cs), 822 lines |
| Observations / actions | 13 actions. It writes actions directly and reads none of the observation vector |
| Config | none — nothing trains it |
| Training scene / env | none. `SCN_BOT.unity` is the manual test bed (not in build settings) |
| Faces / voice | none — `Systems_FaceMood` and `Systems_FighterVoice` disable themselves |

## Why there is no `.onnx`

`Bot_Character.asset` sets `useBot: 1`. `Agent_Biped.Awake` reads that flag and
sets `BehaviorParameters.BehaviorType = HeuristicOnly`, which routes every
decision to `Agent_Biped.Heuristic` and from there into `Agent_Bot.Decide`. The
inference model is never consulted, so the empty `inferenceModel` reference is
correct rather than missing.

`Systems_MatchRoster` does not know that, and logs

```
Systems_MatchRoster: character 'Bot' has no inferenceModel
```

at Error level on every match the BOT appears in. **That line is expected.** Do
not "fix" it by deleting this entry or dropping it from the bracket seeding.

## How it works, in one paragraph

An action in this project is a target motor *speed*, not a torque —
`Agent_BipedBody.ApplyMotor` does `motorSpeed = clamp(action,-1,1) * maxSpeed *
facingSign`. A proportional controller on joint angle therefore lands directly
on the action space, with no torque model to reproduce. Both `JointAngleNorm`
and `ApplyMotor` are already facing-local, so one set of joint targets drives a
fighter facing either way — exactly as one policy does. **Never re-apply
`facingSign` inside `Agent_Bot`**; it is applied twice already and a third
un-mirrors the body.

Four behaviours, selected each decision: `Recover` (uprightness below 0.35, or
limp), `Stand` (upright but not yet stable — both feet stay planted, because a
swing leg removes half the support from a toppling body), `Advance` (a
capture-point gait: the swing foot is aimed at `x_com + v·sqrt(h/g)` and
deliberately lands short of it, which is what makes a biped accelerate rather
than brake), and `Drive` (crouch, then extend knees and spine into the shove).
A cornered fighter drives regardless of range.

## Efficiency

The BOT runs in the hottest path in the project and is written for it:

- `Agent_BotContext` is a `readonly struct` passed by `in`, for the same reason
  `Reward_Context` is — one per agent per decision, and a class would be
  hundreds of heap allocations per second.
- Zero allocations in `Decide` and everything it calls. No LINQ, no `foreach`
  over non-`List`, no string building, no `GetComponent`.
- Joint targets are a fixed `float[13]` seeded once (`_targetsSeeded`).
- Foot contact is debounced with two ints and two bools, not a collection.
- `_dt` is wall-clock delta, not a fixed step: `Heuristic` runs once per
  `DecisionRequester` period (3 physics steps for every shipped fighter), so
  assuming a step would run the gait at a third of its cadence.

## Uses

1. **In the game.** The BOT is a roster entry and is seeded into the 8-slot
   bracket like any fighter, so there is always an opponent that needs no brain.
2. **In training.** Set `useBot` on either fighter in a `SCN_TRAIN_*` scene to
   spar a learning policy against a fixed, non-drifting opponent. This is the
   only opponent in the project whose strength does not move under self-play,
   which makes it the only fair ELO *baseline* — self-play ELO is relative to a
   pool that is itself retraining.

As of 2026-08-15 no training scene enables it. That is the open work, not a
design choice.
