# Dave_v01 — Agent Manifest

Dave: heavyweight experimental sumo wrestler. Same 14-part spine skeleton as Matt,
scaled to sumo mass. Trains the full curriculum from scratch (no Matt inheritance).

| Field | Value |
|---|---|
| Behavior Name | Dave (must match YAML key exactly) |
| Observations / Actions | 41 / 13 (identical layout to Matt — cross-sparring possible) |
| Build | massScale 2.0 (~139 kg), widthScale 1.3 (wide torso), torqueScale 2.0 |
| Height | 1.76 m (same skeleton heights as Matt) |
| Phase 1 | DaveWalk.yaml — walk school, run-id dave_walk01 |
| Phase 2 | DaveSumo.yaml — self-play on the raised dohyo, run-id dave_sumo01 |
| Model | Dave.onnx — overwrite IN-PLACE to preserve the .meta GUID |
| Team color | dark green |

Purpose: experiment. Peer: Matt_v01 (69.6 kg lightweight, peak sumo ELO ~1351).
