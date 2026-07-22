# Maya_v01 — Agent Manifest (PLACEHOLDER)

Maya: roster placeholder for the planned 8-fighter tournament. No trained
policy yet — matches involving Maya are skipped/forfeited until one exists.

| Field | Value |
|---|---|
| Status | **PLACEHOLDER — no model trained** |
| Behavior Name | Maya (must match YAML key exactly once configs exist) |
| Team color | purple (0.55, 0.30, 0.65) |
| Observations | 41 vector (same contract as Matt — do not change without retraining all) |
| Actions | 13 continuous |
| Roster entry | Systems_FighterRoster.cs (hasModel = false) |

## To bring Maya to life

1. Copy `Assets/Agents/Matt_v01/MattWalk.yaml` / `MattSumo.yaml` here as
   `MayaWalk.yaml` / `MayaSumo.yaml`; rename the behavior key to `Maya`.
   Vary reward shaping / hyperparameters to give Maya a distinct style.
2. Train phase 1 (walk), then phase 2 (self-play sumo) per CLAUDE.md.
3. Copy the checkpoint to `Assets/Agents/Maya_v01/Maya.onnx`
   (overwrite in place afterwards to preserve the .meta GUID).
4. Set `hasModel = true` for Maya in `Systems_FighterRoster.cs`.
