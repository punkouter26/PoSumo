# Hana_v01 — Agent Manifest (PLACEHOLDER)

Hana: roster placeholder for the planned 8-fighter tournament. No trained
policy yet — matches involving Hana are skipped/forfeited until one exists.

| Field | Value |
|---|---|
| Status | **PLACEHOLDER — no model trained** |
| Behavior Name | Hana (must match YAML key exactly once configs exist) |
| Team color | pink (0.85, 0.45, 0.55) |
| Observations | 41 vector (same contract as Matt — do not change without retraining all) |
| Actions | 13 continuous |
| Roster entry | Systems_FighterRoster.cs (hasModel = false) |

## To bring Hana to life

1. Copy `Assets/Agents/Matt_v01/MattWalk.yaml` / `MattSumo.yaml` here as
   `HanaWalk.yaml` / `HanaSumo.yaml`; rename the behavior key to `Hana`.
   Vary reward shaping / hyperparameters to give Hana a distinct style.
2. Train phase 1 (walk), then phase 2 (self-play sumo) per CLAUDE.md.
3. Copy the checkpoint to `Assets/Agents/Hana_v01/Hana.onnx`
   (overwrite in place afterwards to preserve the .meta GUID).
4. Set `hasModel = true` for Hana in `Systems_FighterRoster.cs`.
