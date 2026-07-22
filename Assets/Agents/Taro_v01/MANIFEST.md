# Taro_v01 — Agent Manifest (PLACEHOLDER)

Taro: roster placeholder for the planned 8-fighter tournament. No trained
policy yet — matches involving Taro are skipped/forfeited until one exists.

| Field | Value |
|---|---|
| Status | **PLACEHOLDER — no model trained** |
| Behavior Name | Taro (must match YAML key exactly once configs exist) |
| Team color | indigo (0.30, 0.32, 0.60) |
| Observations | 41 vector (same contract as Matt — do not change without retraining all) |
| Actions | 13 continuous |
| Roster entry | Systems_FighterRoster.cs (hasModel = false) |

## To bring Taro to life

1. Copy `Assets/Agents/Matt_v01/MattWalk.yaml` / `MattSumo.yaml` here as
   `TaroWalk.yaml` / `TaroSumo.yaml`; rename the behavior key to `Taro`.
   Vary reward shaping / hyperparameters to give Taro a distinct style.
2. Train phase 1 (walk), then phase 2 (self-play sumo) per CLAUDE.md.
3. Copy the checkpoint to `Assets/Agents/Taro_v01/Taro.onnx`
   (overwrite in place afterwards to preserve the .meta GUID).
4. Set `hasModel = true` for Taro in `Systems_FighterRoster.cs`.
