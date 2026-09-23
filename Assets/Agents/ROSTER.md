# PoSumo Fighter Roster (4)

Four trained fighters. The tournament is built and playable: `SCN_TOURNAMENT`
auto-seeds an 8-slot single-elimination bracket from this roster, so each fighter
appears twice and can meet itself.

There is no code mirror of this table — each fighter's `<Name>_Character.asset`
is the single source of truth, and `Systems_TournamentBracket._roster` is where
the bracket's entrants are assigned in the scene.

| Fighter | Behavior | Folder | Colour | Physique | Identity | Brain (run) |
|---|---|---|---|---|---|---|
| MATT | `Matt` | Matt_v01 | red | 1.00 / 1.00 / 1.00 | aggressive baseline — highest impact reward | `matt_unified02` (15.0M) |
| GRANDMA | `Grandma` | Grandma_v01 | green | 1.00 / 1.00 / 1.00 | neutral reference — renamed from `Standard` 2026-09-22; face art added same day | `standard_unified01` (15.0M, run id under the old name) |
| NICK | `Nick` | Nick_v01 | blue | 0.72 / 0.82 / 0.85 | mobile lightweight — highest cadence, no deep-stance requirement | `nick_unified01` (15.0M) |
| KIM | `Kim` | Kim_v01 | purple | 1.45 / 1.30 / 1.50 | heavyweight anchor — deep stance, does not chase | `kim_unified01` (15.0M) |

**One brain per fighter, covering both walking and fighting.** The walk and fight
policies were merged: a task flag in the observation vector tells the two jobs
apart. There is no separate walk brain, walk config, walk scene or walk `.onnx`
any more, and `Grandma` no longer lends its gait to anyone.

Audio and art coverage is uneven, and that is the current state rather than a bug
— `Systems_FighterVoice` and `Systems_FaceMood` each disable themselves rather
than warn, so a silent, faceless fighter looks intentional:

| Fighter | Face art | Voice clips |
|---|---|---|
| Matt | yes (fallback constants — his asset leaves the name fields empty) | yes (15) |
| Nick | yes | yes (15) |
| Kim | yes | none |
| Grandma | yes (added 2026-09-22 — her 7 PNGs landed in `Assets/Art/Faces/`) | none |

## Clothing (visual only)

Each `<Name>_Character.asset` carries a `clothing` block — garment plus colour per
body region. The fabric pattern itself is generated in code by `Agent_Fabric`
(denim twill, fleece, wool, canvas…) so no fighter ships a texture asset, and
`Agent_BipedBody.PartColor` decides which part each garment covers. Clothing
touches no mass, no collider and no observation, so it cannot invalidate a brain.

| Fighter | Legs | Torso | Arms | Feet |
|---|---|---|---|---|
| Matt | jeans (indigo) | white tee | — | white sneakers |
| Grandma (formerly Standard) | grey sweatpants | beige knit sweater | long sleeves | cream socks |
| Nick | navy shorts | light tank | — | light sneakers |
| Kim | dark mawashi | — (bare chest) | — | — (barefoot) |
| Bot | charcoal shorts | red singlet | — | dark boots |

Physique is mass / width / torque scale. Each folder's `MANIFEST.md` holds the
full spec and the exact retrain command.

Colour names above are descriptions of `teamColor` on the character asset, which is
the authority — this table said "teal" for Kim while her asset has been purple
(0.62, 0.32, 0.62). Read the asset, not this column, if the two ever disagree again.

## Adding a fighter

1. Duplicate a `*_Character.asset`, rename it `<Name>_Character.asset` in a new
   `Assets/Agents/<Name>_v01/` folder, and set `behaviorName` to `<Name>`.
2. Tune the build scales and reward shaping on the asset — style lives in data,
   never in `Agent_Biped`.
3. Copy a config to `Training/configs/<Name>Unified01.yaml`, change the
   `behaviors:` key to `<Name>`, and record *why* its hyperparameters differ in
   the header comment (that comment is the project's training log).
4. Duplicate a training scene as `SCN_TRAIN_<NAME>`, point its
   `Systems_MatchRoster` at the new character, add a `PoSumo/Build <Name>
   Training Env` entry building to `Builds/<Name>Env/<Name>Env.exe`, build, train.
   **Verify the assignment by reading the saved `.unity` file**, not the wiring
   script's log — a pass once reported success while the scene on disk still held
   `character: {fileID: 0}`, and the env trained the wrong policy for 1.5M steps.
5. Deploy with a `PoSumo/Deploy <Name> Brain` entry, then add the character to
   `Systems_TournamentBracket._roster` in `SCN_TOURNAMENT`.

The **45**-observation / 13-action contract must stay identical across all
fighters so any pair can share an arena. That is 42 base plus the 3 added by
`extendedObservations`, which every shipped fighter has on;
`Agent_Biped.ObservationCount` is the authority, not this line.
