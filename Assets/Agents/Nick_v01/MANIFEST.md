# Nick_v01 — Agent Manifest

Nick: the mobile lightweight. Smallest body on the roster and the highest cadence
reward — he dances, steps, and picks his moment instead of grinding forward.

| Field | Value |
|---|---|
| Behavior name | `Nick` (must match the YAML key exactly) |
| Character asset | `Nick_Character.asset` — source of truth for build + shaping |
| Observations / actions | 44 / 13 (`extendedObservations = true`, decision period 3) |
| Build | massScale 0.72, widthScale 0.82, torqueScale 0.85 (~50 kg) |
| Fight brain | `Nick.onnx` ← `nick_sumo04` (1.2M capsule-physics re-tune on top of `nick_sumo02`'s 4.0M, itself on `nick_sumo01`'s 12.0M) |
| Walk brain | `NickWalk.onnx` ← `nick_walk02` (2.0M on capsule physics, on top of nick_walk01's 4.0M) — his own lightweight gait |
| Training scene / env | sumo `SCN_TRAIN_NICK` → `Builds/NickEnv` (spars against Standard); walk `SCN_TRAIN_WALK_NICK` → `Builds/NickWalkEnv` |
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

**Nick is the fighter that reward-hacks.** The capsule-physics re-tune had to be
run twice. `nick_sumo03` (3M, beta 7e-3) climbed to mean reward ~36 while its ELO
fell monotonically 1198 → 1140 — it learned to farm cadence and lunge instead of
winning, and was rejected rather than deployed. `nick_sumo04` (1.2M, beta 4e-3,
half the learning rate, self-play weighted to the strongest recent opponents) held
ELO flat at 1199 → 1198.7 with reward flat too, and is what ships.

The cause is structural, not a bad seed: he carries the roster's largest and most
farmable shaping (cadence 0.0032, lunge 0.0024 @ 1.0 m/s, straightLegEarnFraction
0.75). If he ever needs real improvement rather than adaptation, cut that shaping
on this asset — do not just train him longer, which makes it worse.

## Retrain

```powershell
Training\venv\Scripts\mlagents-learn.exe Training/configs/NickSumo02.yaml --run-id=nick_sumo03 `
  --initialize-from=nick_sumo02 --results-dir=Training/results `
  --env=Builds/NickEnv/NickEnv.exe --num-envs=3 --no-graphics
```
Then *PoSumo → Deploy Nick Brain*. `network_settings` must stay 512 × 3 to match
the trunk being initialized from.
