# Standard_v01 — Agent Manifest

Standard: the neutral reference fighter. Default physique and default reward
shaping — every other character is defined by how it deviates from this one, and
it is the sparring partner in most training scenes.

| Field | Value |
|---|---|
| Behavior name | `Standard` (must match the YAML key exactly) |
| Character asset | `Standard_Character.asset` — source of truth for build + shaping |
| Observations / actions | 45 / 13 (`extendedObservations = true`, decision period 3) |
| Build | massScale 1.0, widthScale 1.0, torqueScale 1.0 (69.6 kg, ~1.76 m) |
| Brain | `Standard.onnx` ← `standard_unified01` final export (15.0M, cold) |
| Training scene / env | `SCN_TRAIN_STANDARD` → `Builds/StandardEnv` |
| Config | `Training/configs/StandardUnified01.yaml` |
| Faces | none — renders as a flat team-colour block |
| Voice | none — `Systems_FighterVoice` disables itself, so he is silent |
| Colour | green, `teamColor` (0.2, 0.5, 0.3) |
| Inference | `InferenceDevice = Burst` |

**One brain, both tasks.** Walking and fighting are a single policy told apart by
a task flag in the observation vector. Standard no longer "owns the shared walk
brain" — that arrangement is gone along with `walkModel` and `DeployWalk`. Every
fighter's walk lane lives in its own unified scene, because a gait has to be
learned on the physique that runs it.

He is the highest-ELO fighter on the roster (5941 at 15M), which is worth reading
carefully: it is a self-play number, not a claim that he beats the others in the
game. He also trained the longest against an unbroken opponent pool.

## Known trap: this scene has no character asset assigned

`SCN_TRAIN_STANDARD` contains **zero** references to `Standard_Character.asset` —
eight fields sit at `character: {fileID: 0}`, while the other three training
scenes reference theirs correctly. Verified by GUID-grepping the saved `.unity`
file, which is the check CLAUDE.md mandates.

It is harmless *by coincidence*: Standard's sheet is byte-identical to
`Agent_Biped`'s code defaults (build scales 1/1/1, uprightReward 0.0005,
closingReward 0.0006, energyPenalty 0.0004, straightLegEarnFraction 0.3 …), so an
agent with no character trains exactly what his sheet would ask for.

It becomes a real bug the moment anyone tunes this sheet — the training scene will
silently ignore it, which is precisely how this project once trained the wrong
policy for 1.5M steps. **Assign the character in the scene and rebuild
StandardEnv before any Standard-specific tuning.**

Style (from the character asset): the defaults — closing 0.0006, lunge 0.001 @
1.5 m/s, impact 0.010 cap 8, cadence 0.0015, straightLegEarnFraction 0.30.

## Retrain

```powershell
Training\venv\Scripts\mlagents-learn.exe Training/configs/StandardUnified01.yaml `
  --run-id=standard_unified01 --results-dir=Training/results `
  --env=Builds/StandardEnv/StandardEnv.exe --num-envs=3 --no-graphics
```

Cold, not warm — a 44-obs trunk cannot warm-start a 45-obs policy. Then
*PoSumo → Deploy Standard Brain*. `network_settings` stays 512 × 3.
