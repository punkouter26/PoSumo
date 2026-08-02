# Matt_v01 — Agent Manifest

Matt: the original PoSumo wrestler, retuned into the aggressive lightweight.
Baseline physique (all build scales 1.0) with the highest impact reward on the
roster — he wins by driving forward and hitting hard.

| Field | Value |
|---|---|
| Behavior name | `Matt` (must match the YAML key exactly) |
| Character asset | `Matt_Character.asset` — source of truth for build + shaping |
| Observations / actions | 45 / 13 (`extendedObservations = true`, decision period 3) |
| Build | massScale 1.0, widthScale 1.0, torqueScale 1.0 (69.6 kg, ~1.76 m) |
| Brain | `Matt.onnx` ← `matt_unified02` final export (15.0M, cold) |
| Training scene / env | `SCN_TRAIN_MATT` → `Builds/MattEnv` |
| Config | `Training/configs/MattUnified02.yaml` |
| Faces | `Assets/Resources/Faces/Matt_*.png`, resolved by the fallback names in `Systems_FaceMood` (his asset leaves the face fields empty) |
| Voice | `Assets/Resources/Audio/Voice/Matt_*.wav` (15 clips) |
| Inference | `InferenceDevice = Burst` |

**One brain, both tasks.** Walking and fighting are a single policy told apart by
a task flag in the observation vector — there is no separate walk brain, walk
config, walk scene or walk `.onnx`. The flag is what took the vector from 44 to
45 slots and invalidated every pre-merge checkpoint; a 44-obs trunk cannot
warm-start this policy, because the first layer shape no longer matches.

**Matt is on `unified02`, not `01`, and the reason matters.** In
`matt_unified01` all six walk-lane agents were built facing AWAY from their
target (`facingSign = -1` with the target 5 m to the right). Walk progress is
measured in the facing-local frame, so each walker read its start line as 5 m
PAST the finish, banked the hardcoded +3 graduation on its first decision, ended
the episode and respawned — forever. Because walk and fight share ONE network,
that free reward contaminated the whole policy, so the run was restarted rather
than resumed.

Fight style (character asset): closing 0.0009, lunge 0.0016 @ 1.2 m/s, impact
0.015 cap 8, cadence 0.0015, straightLegEarnFraction 0.30.

Walk style (character asset): the same aggression carried into locomotion —
forward 0.0075, stance floor 0.30, cadence 0.0012, stall penalty 0.0018, energy
0.00018, bend 0.0004. Longer-strided and more committed than the 0.004 / 0.15 /
0.002 / 0.0008 defaults Standard walks on.

## Retrain

```powershell
Training\venv\Scripts\mlagents-learn.exe Training/configs/MattUnified02.yaml `
  --run-id=matt_unified02 --results-dir=Training/results `
  --env=Builds/MattEnv/MattEnv.exe --num-envs=3 --no-graphics
```

Cold, not warm — see the 44 → 45 note above.

Then *PoSumo → Deploy Matt Brain* (overwrites `Matt.onnx` in place, preserving
the .meta GUID, and repoints the character asset).
