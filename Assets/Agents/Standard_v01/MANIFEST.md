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
| Build | massScale 1.0, widthScale 1.0, torqueScale 1.0 (~79 kg, ~1.76 m) |
| Fight brain | `Standard.onnx` ← `standard_sumo01` final export (45.0M steps) |
| Walk brain | `StandardWalk.onnx` ← `standard_walk01` final export (12.0M steps, trained under behavior name `Matt`) |
| Training scene / env | `SCN_TRAIN_STD` → `Builds/StandardEnv`; walk school `SCN_TRAIN_WALK` → `Builds/WalkEnv` |
| Configs | `Training/configs/StandardSumo01.yaml`, `Training/configs/StandardWalk01.yaml` |
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
Then *PoSumo → Deploy Standard Brain*. The walk brain is retrained separately
with `StandardWalk01.yaml` against `Builds/WalkEnv`; note its behavior key is
`Matt`, so its export lands as `Matt.onnx` and is copied in as `StandardWalk.onnx`.
