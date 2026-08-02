# Nick_v01 — Agent Manifest

Nick: the mobile lightweight. Smallest body on the roster and the highest cadence
reward — he dances, steps, and picks his moment instead of grinding forward.

| Field | Value |
|---|---|
| Behavior name | `Nick` (must match the YAML key exactly) |
| Character asset | `Nick_Character.asset` — source of truth for build + shaping |
| Observations / actions | 45 / 13 (`extendedObservations = true`, decision period 3) |
| Build | massScale 0.72, widthScale 0.82, torqueScale 0.85 (~50 kg) |
| Brain | `Nick.onnx` ← `nick_unified01` final export (15.0M: cold to 3.75M, then resumed) |
| Training scene / env | `SCN_TRAIN_NICK` → `Builds/NickEnv` (spars against Standard) |
| Config | `Training/configs/NickUnified01.yaml` |
| Faces | `Assets/Resources/Faces/Nick_*.png`, named on the character asset |
| Voice | `Assets/Resources/Audio/Voice/Nick_*.wav` (15 clips) |
| Colour | blue, `teamColor` (0.25, 0.42, 0.72) |
| Inference | `InferenceDevice = Burst` |

**One brain, both tasks.** Walking and fighting are a single policy told apart by
a task flag in the observation vector — there is no separate walk brain, walk
config, walk scene or walk `.onnx`. The flag took the vector from 44 to 45 slots,
so no pre-merge checkpoint can warm-start this policy.

**His run crashed at 3.75M and was resumed, which cost the ELO scale.** Self-play
ELO and the opponent pool live in `run_logs/training_status.json`, which the crash
never wrote. On `--resume` mlagents restored the *weights* from `checkpoint.pt`
but reset ELO to `initial_elo: 1200` and started a fresh pool. Nick's final 4581
is therefore measured against a pool that began from scratch at 3.75M, while the
other three accumulated theirs across a full 15M — **do not compare his ELO level
with theirs.** Judge it on shape, which is clean: monotonic non-decreasing across
all ten deciles (1411 → 4369 by decile mean), max drawdown 586 from running peak.

Style (from the character asset): cadence 0.0032 (highest), lunge 0.0024 @ 1.0
m/s (frequent, cheap commits), closing 0.0011, impact 0.011 cap 8, kneeBend
0.0002 / hipsLow 0.0001 and straightLegEarnFraction 0.75 — he is *not* required
to sit in a deep stance.

**Nick is the fighter that reward-hacks, and it is structural.** A pre-merge
re-tune (`nick_sumo03`) climbed to mean reward ~36 while its ELO fell
monotonically 1198 → 1140: it learned to farm cadence and lunge instead of
winning, and was rejected rather than deployed. He carries the roster's largest
and most farmable shaping (the three numbers above). If he ever needs real
improvement rather than adaptation, cut that shaping on this asset — do not just
train him longer, which makes it worse. **Judge any Nick re-tune on ELO, never on
mean reward.**

## Retrain

```powershell
Training\venv\Scripts\mlagents-learn.exe Training/configs/NickUnified01.yaml `
  --run-id=nick_unified01 --results-dir=Training/results `
  --env=Builds/NickEnv/NickEnv.exe --num-envs=3 --no-graphics
```

Cold, not warm. If it is interrupted, `--resume` (not `--force`) — but note the
ELO-reset consequence above. Then *PoSumo → Deploy Nick Brain*.
`network_settings` stays 512 × 3 across this roster.
