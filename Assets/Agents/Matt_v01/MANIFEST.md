# Matt_v01 — Agent Manifest

Matt: the first PoSumo sumo wrestler. v01 body = 4-segment articulated spine.

| Field | Value |
|---|---|
| Behavior Name | Matt (must match YAML key exactly) |
| Observations | 41 vector (5 body + 26 joints + 4 feet + 4 opponent + 2 edges; mirrored, NaN-sanitized) |
| Actions | 13 continuous (hips, knees, ankles, spine x3, shoulders, elbows) |
| Body | 14 primitive parts, ~1.76 m, ~79 kg; pelvis/lowerback/upperback/chest spine chain |
| Physics | gravity -9.81, solver 12 pos / 8 vel iter, continuous CCD, 0.03 m spawn clearance |
| Phase 1 | MattWalk.yaml — solo walk to center, run-id matt_walk01 |
| Phase 2 | MattSumo.yaml — self-play sumo, --initialize-from=matt_walk01 |
| Model | Matt.onnx — overwrite IN-PLACE to preserve the .meta GUID |
| Inference | InferenceDevice = Burst |
