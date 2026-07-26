# PoSumo Fighter Roster (4)

Four trained fighters. The tournament is built and playable: `SCN_TOURNAMENT`
auto-seeds an 8-slot single-elimination bracket from this roster, so each fighter
appears twice and can meet itself.

There is no code mirror of this table — each fighter's `<Name>_Character.asset`
is the single source of truth, and `Systems_TournamentBracket._roster` is where
the bracket's entrants are assigned in the scene.

| Fighter | Behavior | Folder | Colour | Physique | Identity | Brain (run) |
|---|---|---|---|---|---|---|
| MATT | `Matt` | Matt_v01 | red | 1.00 / 1.00 / 1.00 | aggressive baseline — highest impact reward | `matt_sumo05` (8.0M) |
| STANDARD | `Standard` | Standard_v01 | green | 1.00 / 1.00 / 1.00 | neutral reference + owns the shared walk brain | `standard_sumo01` (45.0M) |
| NICK | `Nick` | Nick_v01 | blue | 0.72 / 0.82 / 0.85 | mobile lightweight — highest cadence, no deep-stance requirement | `nick_sumo02` (4.0M on 12.0M) |
| KIM | `Kim` | Kim_v01 | teal | 1.45 / 1.30 / 1.50 | heavyweight anchor — deep stance, does not chase | `kim_sumo01` (12.0M) |

Physique is mass / width / torque scale. Each folder's `MANIFEST.md` holds the
full spec and the exact retrain command.

## Adding a fighter

1. Duplicate a `*_Character.asset`, rename it `<Name>_Character.asset` in a new
   `Assets/Agents/<Name>_v01/` folder, and set `behaviorName` to `<Name>`.
2. Tune the build scales and reward shaping on the asset — style lives in data,
   never in `Agent_Biped`.
3. Copy a config to `Training/configs/<Name>Sumo01.yaml`, change the `behaviors:`
   key to `<Name>`, and record *why* its hyperparameters differ in the header
   comment (that comment is the project's training log).
4. Duplicate a training scene, point its `Systems_MatchRoster` at the new
   character, add a `PoSumo/Build <Name> Training Env` entry, build, train.
5. Deploy with a `PoSumo/Deploy <Name> Brain` entry, then add the character to
   `Systems_TournamentBracket._roster` in `SCN_TOURNAMENT`.

The 44-observation / 13-action contract must stay identical across all fighters
so any pair can share an arena.
