# Matt_v01 — Agent Manifest

Matt: the original PoSumo wrestler, retuned into the aggressive lightweight.
Baseline physique (all build scales 1.0) with the highest impact reward on the
roster — he wins by driving forward and hitting hard.

| Field | Value |
|---|---|
| Behavior name | `Matt` (must match the YAML key exactly) |
| Character asset | `Matt_Character.asset` — source of truth for build + shaping |
| Observations / actions | 44 / 13 (`extendedObservations = true`, decision period 3) |
| Build | massScale 1.0, widthScale 1.0, torqueScale 1.0 (69.6 kg, ~1.76 m) |
| Fight brain | `Matt.onnx` ← `matt_sumo06` final export (3.0M re-tune on the capsule-limb physics, on top of matt_sumo05's 8.0M) |
| Walk brain | `MattWalk.onnx` ← `matt_walk02` (2.0M, his own aggressive shaping on capsule physics) — no longer shares Standard's |
| Training scene / env | sumo `SCN_TRAIN_MATT_AGGR` → `Builds/MattAggrEnv`; walk `SCN_TRAIN_WALK_MATT` → `Builds/MattWalkEnv` |
| Configs | `Training/configs/MattSumo06.yaml`, `Training/configs/MattWalk02.yaml` |
| Faces | `Assets/Resources/Matt_*.png`, resolved by the fallback names in `Systems_FaceMood` (his asset leaves the face fields empty) |
| Inference | `InferenceDevice = Burst` |

Fight style (character asset): closing 0.0009, lunge 0.0016 @ 1.2 m/s, impact
0.015 cap 8, cadence 0.0015, straightLegEarnFraction 0.30.

Walk style (character asset): the same aggression carried into locomotion —
forward 0.0075, stance floor 0.30, cadence 0.0012, stall penalty 0.0018, energy
0.00018, bend 0.0004. Longer-strided and more committed than the 0.004 / 0.15 /
0.002 / 0.0008 defaults that Standard still walks on.

## Retrain

```powershell
Training\venv\Scripts\mlagents-learn.exe Training/configs/MattSumo05.yaml --run-id=matt_sumo06 `
  --initialize-from=matt_sumo05 --results-dir=Training/results `
  --env=Builds/MattAggrEnv/MattAggrEnv.exe --num-envs=3 --no-graphics
```
Then *PoSumo → Deploy Matt Brain* (overwrites `Matt.onnx` in place, preserving the
.meta GUID, and repoints the character asset).
