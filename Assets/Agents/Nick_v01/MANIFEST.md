# Nick_v01 — Agent Manifest

Nick: the mobile lightweight. Smallest body on the roster and the highest cadence
reward — he dances, steps, and picks his moment instead of grinding forward.

| Field | Value |
|---|---|
| Behavior name | `Nick` (must match the YAML key exactly) |
| Character asset | `Nick_Character.asset` — source of truth for build + shaping |
| Observations / actions | 44 / 13 (`extendedObservations = true`, decision period 3) |
| Build | massScale 0.72, widthScale 0.82, torqueScale 0.85 (~57 kg) |
| Fight brain | `Nick.onnx` ← `nick_sumo02` final export (4.0M corrective steps on top of `nick_sumo01`'s 12.0M) |
| Walk brain | `Standard_v01/StandardWalk.onnx` (shared walk-in brain) |
| Training scene / env | `SCN_TRAIN_NICK` → `Builds/NickEnv` (spars against Standard) |
| Configs | `Training/configs/NickSumo01.yaml` (cold, from `standard_sumo01`), `NickSumo02.yaml` (corrective fine-tune) |
| Faces | `Assets/Resources/Nick_*.png`, named on the character asset |
| Inference | `InferenceDevice = Burst` |

Style (from the character asset): cadence 0.0032 (highest), lunge 0.0024 @ 1.0
m/s (frequent, cheap commits), closing 0.0011, impact 0.011 cap 8, kneeBend
0.0002 / hipsLow 0.0001 and straightLegEarnFraction 0.75 — he is *not* required
to sit in a deep stance.

`NickSumo02.yaml`'s header records why v1 failed (paid almost nothing for
advancing, so it could not contest ground despite a healthy self-play ELO) — read
it before retuning him.

## Retrain

```powershell
Training\venv\Scripts\mlagents-learn.exe Training/configs/NickSumo02.yaml --run-id=nick_sumo03 `
  --initialize-from=nick_sumo02 --results-dir=Training/results `
  --env=Builds/NickEnv/NickEnv.exe --num-envs=3 --no-graphics
```
Then *PoSumo → Deploy Nick Brain*. `network_settings` must stay 512 × 3 to match
the trunk being initialized from.
