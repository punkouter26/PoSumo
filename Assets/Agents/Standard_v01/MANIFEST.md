# Standard_v01 — Agent Manifest

Standard: the neutral reference fighter. Default physique and default reward
shaping — every other character is defined by how it deviates from this one, and
it is the sparring partner in most training scenes.

It also owns the roster's shared **walk brain**: `StandardWalk.onnx` drives the
ceremonial round-opening walk-in for every fighter.

| Field | Value |
|---|---|
| Behavior name | `Standard` (must match the YAML key exactly) |
| Character asset | `Standard_Character.asset` — source of truth for build + shaping |
| Observations / actions | 44 / 13 (`extendedObservations = true`, decision period 3) |
| Build | massScale 1.0, widthScale 1.0, torqueScale 1.0 (69.6 kg, ~1.76 m) |
| Fight brain | `Standard.onnx` ← `standard_sumo02` final export (3.0M re-tune on the capsule-limb physics, on top of standard_sumo01's 45.0M) |
| Walk brain | `StandardWalk.onnx` ← `standard_walk01` (2.0M on capsule physics, warm-started from the baseline `matt_walk01`) — now its own run under behavior name `Standard` |
| Training scene / env | sumo `SCN_TRAIN_STD` → `Builds/StandardEnv`; walk `SCN_TRAIN_WALK_STD` → `Builds/StandardWalkEnv` |
| Configs | `Training/configs/StandardSumo02.yaml`, `Training/configs/StandardWalk01.yaml` |
| Faces | none — renders as a flat team-colour block |
| Inference | `InferenceDevice = Burst` |

Style (from the character asset): the defaults — closing 0.0006, lunge 0.001 @
1.5 m/s, impact 0.010 cap 8, cadence 0.0015, straightLegEarnFraction 0.30.

## Retrain

```powershell
Training\venv\Scripts\mlagents-learn.exe Training/configs/StandardSumo01.yaml --run-id=standard_sumo02 `
  --initialize-from=standard_sumo01 --results-dir=Training/results `
  --env=Builds/StandardEnv/StandardEnv.exe --num-envs=3 --no-graphics
```
Then *PoSumo → Deploy Standard Brain*. The walk brain retrains separately with
`StandardWalk01.yaml` against `Builds/StandardWalkEnv` — its own scene and its own
behavior key now, so Standard and Matt can each be retuned without disturbing the
other. `--initialize-from` still needs the `matt_walk01` trunk staged under a
`Standard/` folder, since it resolves by behavior name.
