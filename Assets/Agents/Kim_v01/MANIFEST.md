# Kim_v01 — Agent Manifest

Kim: the heavyweight anchor — the opposite pole from Nick. Heavy, wide, and
high-torque, rewarded for a deep planted stance and raw impact rather than for
chasing. She does not come to you.

| Field | Value |
|---|---|
| Behavior name | `Kim` (must match the YAML key exactly) |
| Character asset | `Kim_Character.asset` — source of truth for build + shaping |
| Observations / actions | 45 / 13 (`extendedObservations = true`, decision period 3) |
| Build | massScale 1.45, widthScale 1.30, torqueScale 1.50 (~101 kg, sumo belly) |
| Brain | `Kim.onnx` ← `kim_unified01` final export (15.0M, cold) |
| Training scene / env | `SCN_TRAIN_KIM` → `Builds/KimEnv` (spars against Standard) |
| Config | `Training/configs/KimUnified01.yaml` |
| Faces | `Assets/Resources/Faces/Kim_*.png`, named on the character asset |
| Voice | **Happy only** (2026-08-15): `Resources/Audio/Voice/Kim_Happy_1..5.wav`. No Sad or Insult set, so she is silent when losing or taunting — that is a complete absence (`found == 0`), which the loader returns as null without warning, not a partial set |
| Colour | purple, `teamColor` (0.62, 0.32, 0.62) |
| Inference | `InferenceDevice = Burst` |

**One brain, both tasks.** Walking and fighting are a single policy told apart by
a task flag in the observation vector — there is no separate walk brain, walk
config, walk scene or walk `.onnx`. The flag took the vector from 44 to 45 slots,
so no pre-merge checkpoint can warm-start this policy: the first layer shape no
longer matches. `kim_unified01` was cold for that reason, not by preference.

A gait has to be learned on the physique that runs it — Kim's 1.45 mass will not
accept a gait learned at 1.0 — which is why her walk lane lives in her own scene
rather than in a shared walk scene.

Style (from the character asset): kneeBend + hipsLow both 0.0008 (deep planted
stance), impact 0.014 cap 10 (wins with force), closing 0.0004 (does not chase),
lunge 0.0008 @ 1.8 m/s (rare, big commits), cadence 0.0006 (plants rather than
dances), straightLegEarnFraction 0.15 (must be deep to earn anything).

**The happy set is ordered by LENGTH, and that ordering is load-bearing.** Slot 1 is
the mildest read and slot 5 fires on the match win, so the clips were mapped
0.95 / 1.55 / 2.62 / 3.11 / **7.96** s onto slots 1-5 rather than by their delivered
filenames (`KimSing_1..5`, which were not in intensity order). The 7.96 s clip is far
longer than anything else in the project's voice bank — `Systems_FighterVoice` sets
`_nextAllowedTime = clip.length * 0.6`, so playing it mutes Kim for ~4.8 s afterwards.
On the match win that lockout costs nothing, because the bout is already over. Anywhere
else it would swallow the next three lines. If these are ever re-cut, keep the longest
clip at slot 5.

Her hyperparameters differ from the others deliberately — short credit horizon
(gamma 0.99), low exploration (beta 3e-3), narrow self-play window drilling the
strongest recent opponents. `KimUnified01.yaml`'s header explains each choice.

## Retrain

```powershell
Training\venv\Scripts\mlagents-learn.exe Training/configs/KimUnified01.yaml `
  --run-id=kim_unified01 --results-dir=Training/results `
  --env=Builds/KimEnv/KimEnv.exe --num-envs=3 --no-graphics
```

Cold, not warm — see the 44 → 45 note above. Then *PoSumo → Deploy Kim Brain*
(overwrites `Kim.onnx` in place, preserving the .meta GUID). `network_settings`
stays 512 × 3 across this roster.
