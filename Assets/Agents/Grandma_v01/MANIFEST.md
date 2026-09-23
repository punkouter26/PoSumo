# Grandma_v01 — Agent Manifest

Grandma: the neutral reference fighter (renamed from `Standard` on 2026-09-22).
Default physique and default reward shaping — every other character is defined by
how it deviates from this one, and it is the sparring partner in most training
scenes.

| Field | Value |
|---|---|
| Behavior name | `Grandma` (must match the YAML key exactly) |
| Character asset | `Grandma_Character.asset` — source of truth for build + shaping |
| Observations / actions | 45 / 13 (`extendedObservations = true`, decision period 3) |
| Build | massScale 1.0, widthScale 1.0, torqueScale 1.0 (69.6 kg, ~1.76 m) |
| Brain | `Grandma.onnx` ← `standard_unified01` final export (15.0M, cold; run id under the old name) |
| Training scene / env | `SCN_TRAIN_GRANDMA` (legacy name) → `Builds/GrandmaEnv` |
| Config | `Training/configs/GrandmaRebuild01.yaml` (live); `StandardUnified01.yaml` is superseded |
| Faces | `Grandma_Neutral` / `_Happy_1-3` / `_Sad_1-3` (added 2026-09-22) |
| Voice | none — `Systems_FighterVoice` finds zero clips and stays silent |
| Colour | green, `teamColor` (0.2, 0.5, 0.3) |
| Inference | `InferenceDevice = Burst` |

**One brain, both tasks.** Walking and fighting are a single policy told apart by
a task flag in the observation vector. Grandma no longer "owns the shared walk
brain" — that arrangement is gone along with `walkModel` and `DeployWalk`. Every
fighter's walk lane lives in its own unified scene, because a gait has to be
learned on the physique that runs it.

She is the highest-ELO fighter on the roster (5941 at 15M), which is worth reading
carefully: it is a self-play number, not a claim that she beats the others in the
game. She also trained the longest against an unbroken opponent pool.

## Known trap: this scene has no character asset assigned

`SCN_TRAIN_GRANDMA` contains **zero** references to `Grandma_Character.asset` —
eight fields sit at `character: {fileID: 0}`, while the other three training
scenes reference theirs correctly. Verified by GUID-grepping the saved `.unity`
file, which is the check CLAUDE.md mandates.

It is harmless *by coincidence*: Grandma's sheet is byte-identical to
`Agent_Biped`'s code defaults (build scales 1/1/1, uprightReward 0.0005,
closingReward 0.0006, energyPenalty 0.0004, straightLegEarnFraction 0.3 …), so an
agent with no character trains exactly what her sheet would ask for.

It becomes a real bug the moment anyone tunes this sheet — the training scene will
silently ignore it, which is precisely how this project once trained the wrong
policy for 1.5M steps. **Assign the character in the scene and rebuild
GrandmaEnv before any Grandma-specific tuning.**

Style (from the character asset): the defaults — closing 0.0006, lunge 0.001 @
1.5 m/s, impact 0.010 cap 8, cadence 0.0015, straightLegEarnFraction 0.30.

## Retrain

```powershell
Training\venv\Scripts\mlagents-learn.exe Training/configs/GrandmaRebuild01.yaml `
  --run-id=grandma_rebuild01 --results-dir=Training/results `
  --env=Builds/GrandmaEnv/GrandmaEnv.exe --num-envs=4 --no-graphics --base-port=5015
```

Cold by design — `GrandmaRebuild01.yaml`'s header records why. Build the env
first (*PoSumo → Build Grandma Training Env*), then deploy with
*PoSumo → Deploy Grandma Brain*. `network_settings` stays 512 × 3.

> **Historical runs export `Standard.onnx` under `standard_*` run ids.** Any run
> recreated under the old name must be copied to `Grandma.onnx` by hand —
> `Deploy Grandma Brain` resolves `grandma_*` runs and writes `Grandma.onnx`.
