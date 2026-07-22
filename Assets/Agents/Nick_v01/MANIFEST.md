# Nick_v01 — Agent Manifest (PLACEHOLDER)

Nick: roster placeholder for the planned 8-fighter tournament. No trained
policy yet — matches involving Nick are skipped/forfeited until one exists.

| Field | Value |
|---|---|
| Status | **PLACEHOLDER — no model trained** |
| Behavior Name | Nick (must match YAML key exactly once configs exist) |
| Team color | blue (0.25, 0.40, 0.75) |
| Observations | 41 vector (same contract as Matt — do not change without retraining all) |
| Actions | 13 continuous |
| Roster entry | Systems_FighterRoster.cs (hasModel = false) |

## To bring Nick to life

1. Copy `Assets/Agents/Matt_v01/MattWalk.yaml` / `MattSumo.yaml` here as
   `NickWalk.yaml` / `NickSumo.yaml`; rename the behavior key to `Nick`.
   Vary reward shaping / hyperparameters to give Nick a distinct style.
2. Train phase 1 (walk), then phase 2 (self-play sumo) per CLAUDE.md.
3. Copy the checkpoint to `Assets/Agents/Nick_v01/Nick.onnx`
   (overwrite in place afterwards to preserve the .meta GUID).
4. Set `hasModel = true` for Nick in `Systems_FighterRoster.cs`.
