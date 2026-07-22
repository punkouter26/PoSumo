# Kim_v01 — Agent Manifest (PLACEHOLDER)

Kim: roster placeholder for the planned 8-fighter tournament. No trained
policy yet — matches involving Kim are skipped/forfeited until one exists.

| Field | Value |
|---|---|
| Status | **PLACEHOLDER — no model trained** |
| Behavior Name | Kim (must match YAML key exactly once configs exist) |
| Team color | teal (0.20, 0.60, 0.60) |
| Observations | 41 vector (same contract as Matt — do not change without retraining all) |
| Actions | 13 continuous |
| Roster entry | Systems_FighterRoster.cs (hasModel = false) |

## To bring Kim to life

1. Copy `Assets/Agents/Matt_v01/MattWalk.yaml` / `MattSumo.yaml` here as
   `KimWalk.yaml` / `KimSumo.yaml`; rename the behavior key to `Kim`.
   Vary reward shaping / hyperparameters to give Kim a distinct style.
2. Train phase 1 (walk), then phase 2 (self-play sumo) per CLAUDE.md.
3. Copy the checkpoint to `Assets/Agents/Kim_v01/Kim.onnx`
   (overwrite in place afterwards to preserve the .meta GUID).
4. Set `hasModel = true` for Kim in `Systems_FighterRoster.cs`.
