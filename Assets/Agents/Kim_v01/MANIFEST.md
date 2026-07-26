# Kim_v01 — Agent Manifest

Kim: the heavyweight anchor — the opposite pole from Nick. Heavy, wide, and
high-torque, rewarded for a deep planted stance and raw impact rather than for
chasing. She does not come to you.

| Field | Value |
|---|---|
| Behavior name | `Kim` (must match the YAML key exactly) |
| Character asset | `Kim_Character.asset` — source of truth for build + shaping |
| Observations / actions | 44 / 13 (`extendedObservations = true`, decision period 3) |
| Build | massScale 1.45, widthScale 1.30, torqueScale 1.50 (~115 kg, sumo belly) |
| Fight brain | `Kim.onnx` ← `kim_sumo01` final export (12.0M steps) |
| Walk brain | `Standard_v01/StandardWalk.onnx` (shared walk-in brain) |
| Training scene / env | `SCN_TRAIN_KIM` → `Builds/KimEnv` (spars against Standard) |
| Config | `Training/configs/KimSumo01.yaml` |
| Faces | `Assets/Resources/Kim_*.png`, named on the character asset |
| Inference | `InferenceDevice = Burst` |

Style (from the character asset): kneeBend + hipsLow both 0.0008 (deep planted
stance), impact 0.014 cap 10 (wins with force), closing 0.0004 (does not chase),
lunge 0.0008 @ 1.8 m/s (rare, big commits), cadence 0.0006 (plants rather than
dances), straightLegEarnFraction 0.15 (must be deep to earn anything).

Her hyperparameters differ from the others deliberately — short credit horizon
(gamma 0.99), low exploration (beta 3e-3), narrow self-play window drilling the
strongest recent opponents. `KimSumo01.yaml`'s header explains each choice.

## Retrain

`kim_sumo01` was initialized from a **staged trunk**: Matt's aggressive
`matt_sumo04` checkpoint copied under a `Kim/` folder, because
`--initialize-from` resolves by the *new* behavior name. Re-stage it with:

```powershell
New-Item -ItemType Directory -Force Training/results/kim_init/Kim
Copy-Item Training/results/matt_sumo04/Matt/checkpoint.pt Training/results/kim_init/Kim/checkpoint.pt
Training\venv\Scripts\mlagents-learn.exe Training/configs/KimSumo01.yaml --run-id=kim_sumo02 `
  --initialize-from=kim_init --results-dir=Training/results `
  --env=Builds/KimEnv/KimEnv.exe --num-envs=3 --no-graphics
```
Then *PoSumo → Deploy Kim Brain*. `network_settings` must stay 512 × 3 to match
the trunk.
