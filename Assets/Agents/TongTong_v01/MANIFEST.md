# TongTong_v01 — Agent Manifest (PLACEHOLDER)

TongTong: roster placeholder for the planned 8-fighter tournament. No trained
policy yet — matches involving TongTong are skipped/forfeited until one exists.

| Field | Value |
|---|---|
| Status | **PLACEHOLDER — no model trained** |
| Behavior Name | TongTong (must match YAML key exactly once configs exist) |
| Team color | gold (0.85, 0.65, 0.20) |
| Observations | 41 vector (same contract as Matt — do not change without retraining all) |
| Actions | 13 continuous |
| Roster entry | Systems_FighterRoster.cs (hasModel = false) |

## To bring TongTong to life

1. Copy `Assets/Agents/Matt_v01/MattWalk.yaml` / `MattSumo.yaml` here as
   `TongTongWalk.yaml` / `TongTongSumo.yaml`; rename the behavior key to `TongTong`.
   Vary reward shaping / hyperparameters to give TongTong a distinct style.
2. Train phase 1 (walk), then phase 2 (self-play sumo) per CLAUDE.md.
3. Copy the checkpoint to `Assets/Agents/TongTong_v01/TongTong.onnx`
   (overwrite in place afterwards to preserve the .meta GUID).
4. Set `hasModel = true` for TongTong in `Systems_FighterRoster.cs`.
