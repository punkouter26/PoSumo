# Matt_v01 — Agent Manifest

Matt: the original PoSumo wrestler, retuned into the aggressive lightweight.
Baseline physique (all build scales 1.0) with the highest impact reward on the
roster — he wins by driving forward and hitting hard.

| Field | Value |
|---|---|
| Behavior name | `Matt` (must match the YAML key exactly) |
| Character asset | `Matt_Character.asset` — source of truth for build + shaping |
| Observations / actions | 44 / 13 (`extendedObservations = true`, decision period 3) |
| Build | massScale 1.0, widthScale 1.0, torqueScale 1.0 (~79 kg, ~1.76 m) |
| Fight brain | `Matt.onnx` ← `matt_sumo05` final export (8.0M steps) |
| Walk brain | `Standard_v01/StandardWalk.onnx` (shared walk-in brain) |
| Training scene / env | `SCN_TRAIN_MATT_AGGR` → `Builds/MattAggrEnv` |
| Config | `Training/configs/MattSumo05.yaml` |
| Faces | `Assets/Resources/Matt_*.png`, resolved by the fallback names in `Systems_FaceMood` (his asset leaves the face fields empty) |
| Inference | `InferenceDevice = Burst` |

Style (from the character asset): closing 0.0009, lunge 0.0016 @ 1.2 m/s,
impact 0.015 cap 8, cadence 0.0015, straightLegEarnFraction 0.30.

## Retrain

```powershell
Training\venv\Scripts\mlagents-learn.exe Training/configs/MattSumo05.yaml --run-id=matt_sumo06 `
  --initialize-from=matt_sumo05 --results-dir=Training/results `
  --env=Builds/MattAggrEnv/MattAggrEnv.exe --num-envs=3 --no-graphics
```
Then *PoSumo → Deploy Matt Brain* (overwrites `Matt.onnx` in place, preserving the
.meta GUID, and repoints the character asset).
