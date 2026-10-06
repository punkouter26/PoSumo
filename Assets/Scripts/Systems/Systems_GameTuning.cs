using UnityEngine;

namespace PoSumo
{
    /// Single source of truth for camera and match tuning. Scene components
    /// copy from this asset at startup when it is assigned, so numbers live in
    /// one place instead of being scattered across serialized scene values.
    [CreateAssetMenu(fileName = "GameTuning", menuName = "PoSumo/Game Tuning")]
    public sealed class Systems_GameTuning : ScriptableObject
    {
        [Header("Camera")]
        public float minOrtho = 1.9f;
        public float maxOrtho = 3.5f;
        [Tooltip("Camera centers this far below the wrestlers' average torso — at the feet.")]
        public float feetDrop = 0.95f;
        public float horizontalMargin = 0.5f;
        public float smoothing = 4f;
        [Tooltip("Ortho for the wide establishing shot used by the walk-in and the post-match pull-back. ~14 holds the +/-6 m walk-in start marks at portrait aspect.")]
        public float wideOrtho = 14f;


        [Header("Ring")]
        // The ring lives here rather than only on the arena scene because it is
        // serialized into it, and a public field already written into a scene
        // ignores any later change to its code default — the trap this asset
        // exists to avoid.
        [Tooltip("Half-width of the FIGHTING ring in metres. This is also the physical platform half-width and is fed to the agents as an edge-distance observation, so changing it changes what every brain sees.\n\n4.0, down from 5.5. The 5.5 ring was the documented reason bouts stopped finishing: with the fighters starting 0.9 m apart there was 4.6 m of mat to drive an opponent across, and measured sustained push in contact was only 71-500 N against 614 N of static friction. Shrinking the ring and widening the stand-off cuts that distance to about 1.5 m.")]
        public float ringHalfWidth = 4f;

        [Tooltip("Half the gap the fighters stand at when a round opens. 2.5 (was 0.9): starting them near the tawara rather than nose-to-nose in the middle means a drive of ~1.5 m wins the round instead of ~4.6 m. Costs nothing in retraining — it is a spawn position, not an observation.")]
        public float neutralGapHalf = 2.5f;

        [Tooltip("Friction of the dohyo clay in the GAME. 0.55, down from 0.9.\n\nAt 0.9 the force to start sliding a 69.6 kg opponent is 0.9*69.6*9.81 = 614 N, and measured sustained push in contact was 71-500 N — so no fighter could move another at all. 0.55 drops the wall to ~375 N, which the harder pushes clear.\n\nSafe without retraining: Systems_SumoMatchManager already randomises friction over [0.5, 1.1] during training, so 0.55 is inside the distribution every brain has seen.")]
        public float surfaceFriction = 0.55f;

        [Tooltip("Width in metres of the low-friction tawara band at each rim. A fighter driven onto the bales loses grip and slides out instead of planting — which is what the bales physically do. 0 disables the band.\n\nSystems_SumoMatchManager reads this asset too (since 2026-10-04) and writes the band onto the training arena, so game and training share the one value; its own serialized copy is only the fallback for a scene with no tuning asset. The asset ships 1.2; this code default is the old 0.7 and only applies when no tuning asset is assigned.")]
        public float tawaraBandWidth = 0.7f;

        [Tooltip("Friction inside the tawara band. Deliberately slick so 'almost out' becomes 'out'.")]
        public float tawaraFriction = 0.18f;

        [Tooltip("Seconds into a round before the ring starts closing in. 0 disables the shrinking ring entirely.\n\nMEASURED REASON: a 14-round tournament finished 57% on a timeout decision and 7% on a timeout draw — 64% settled by the clock — against a single ring-out. The [ROUND] logs show why: at the bell the fighters are bunched within ~2 m of the centre, so nobody is anywhere near an edge. They lock up in the middle and grind. A shrinking ring converts a stalemate into the sport's own win condition instead of handing it to a judge.\n\nThe machinery is not new: Systems_SumoArena.SetPlatformHalfWidth already resizes the platform collider and the walk-in has always used it. Ring-out detection needs no change either, because OutOfRing is purely vertical — it fires when a foot drops below the mat surface, so withdrawing the floor is detected for free.")]
        public float shrinkStartSeconds = 8f;

        [Tooltip("Half-width the ring closes to by the time the round clock expires. Reached by a linear contraction from ringHalfWidth starting at shrinkStartSeconds.\n\n1.8 m leaves a mat about two body-widths across at the bell: tight enough that a grapple in the middle runs out of floor, wide enough that it is still wrestling rather than a coin toss. Set equal to ringHalfWidth to disable the contraction while keeping the timer.")]
        public float shrinkToHalfWidth = 1.8f;
        [Tooltip("Seconds the contraction takes from ringHalfWidth to shrinkToHalfWidth. It used to be implied by roundTimeoutSeconds - shrinkStartSeconds (20 - 8 = 12); it has its own number now because the clock can be OFF. The mat does not stop at shrinkToHalfWidth — it keeps closing at the same rate to zero, so a round with no clock still ends: at the defaults the floor is gone ~33 s in.")]
        public float shrinkSeconds = 12f;
        [Tooltip("Play the ceremonial walk-in at the start of each match. Lives here because the arena scenes each serialize their own copy of the flag, and SCN_SUMO had it switched OFF — so the ceremony was silently never running no matter what the code default said.")]
        public bool enableWalkIn = true;
        [Tooltip("Platform half-width during the ceremonial walk-in. The mat contracts to ringHalfWidth before the bell.")]
        public float walkInHalfWidth = 8f;
        [Tooltip("Half the gap the fighters start the walk-in from. Was 3 (6 m apart) and the walk-in then FAILED ON EVERY BOUT — see walkInTouchGap. 1.8 gives 3.6 m, which the measured gait closes comfortably.")]
        // 3 -> 1.8 on 2026-08-16. The previous note here reasoned that 3 was already
        // the cautious value because 6 had run past 9 s; it was still too far. Logged
        // failures read `STALLED after 3.3s, surfaceGap=5.05, bestGap=4.87` and
        // `timed out after 7.0s, surfaceGap=0.80, bestGap=0.69`.
        public float walkInStartGapHalf = 1.8f;
        [Tooltip("Surface gap in metres between the two fighters' colliders at which they count as having met — this is the moment the policy flips from its walk task to its fight task. Measured body-to-body rather than torso-to-torso, because limb pose swings torso-separation-at-contact between about 0.9 m and 1.8 m.")]
        // 0.05 -> 0.6 on 2026-08-16, with the start gap above.
        //
        // 0.05 demanded literal contact and NO BOUT EVER ACHIEVED IT. Every match
        // logged a stall or a timeout, and the referee fell back to parking the pair
        // at the stand-off — so the ceremony this whole code path exists for was
        // never once seen. The gait's measured floor is bestGap 0.69 m; a threshold
        // an order of magnitude below what the walker can actually reach is not a
        // strict setting, it is an unreachable one.
        //
        // 0.6 is a CEREMONY threshold, not a physics one. Nothing downstream needs
        // the colliders to touch — it only decides when the task flag flips — and at
        // 0.6 m of surface separation the two men read as having met in the middle.
        // Fixing the gait itself is a training problem with four failed runs behind
        // it (see CLAUDE.md); this makes the presentation work with the gait we have.
        public float walkInTouchGap = 0.6f;
        [Tooltip("Hard cap on the walk-in, in seconds. 30 since 2026-08-26 (was 9): a pair that is still edging together is left to edge together, and on the cap — or after 4 s without 8 cm of new closing — the fight brains simply take over where they stand. Nothing is parked or re-counted any more.")]
        public float walkInTimeout = 30f;

        [Header("Match")]
        [Tooltip("Round wins needed in a standalone exhibition match.")]
        public int pointsToWin = 3;
        [Tooltip("Round wins needed to take a tournament bracket. 2 = best of three. Single source for both modes so they cannot drift apart.")]
        public int tournamentPointsToWin = 2;
        [Tooltip("Round clock. 0 = NO CLOCK: no timeout decision, no draw, the clock label stays blank, and the shrinking mat (see shrinkSeconds) is what ends a stalemate — every round finishes on a ring-out. Set to 0 on 2026-08-26; a bracket had just gone 3 timeout decisions in 8 rounds.")]
        public float roundTimeoutSeconds = 0f;
        public float betweenRoundsPause = 2.5f;
        public float graceSeconds = 0.4f;
        // downGraceSeconds, downOutSeconds, knockdownLoses and headTouchLoses were
        // DELETED 2026-08-26 from this asset and both referees. The ring-out is
        // the only losing condition; the shrinking mat (shrinkSeconds) is what
        // ends a stalemate, squeezing a fighter who stays down off the edge.
        [Tooltip("A fighter that loses all four limbs to a single blow loses the round instantly.\n\nGAME-ONLY: Systems_SumoMatchManager has no equivalent, so no brain has trained against it. Safe for the same reason the rest are — the victim is a limbless torso and could not have continued.\n\nTurning this OFF does not let a gibbed fighter keep going: the shrinking mat squeezes it off the edge instead. The only difference is the wait.\n\nHow often it fires is set on Systems_BodyDamage (gibSpeed / gibChance), not here.")]
        public bool gibLosesRound = true;
        [Tooltip("THE RING-OUT RULE (2026-08-26). ON: a fighter is out when ANY body part touches the arena floor below the dohyo — a static contact more than 0.3 m under the mat top (Sensor_FloorContact on every part, feet and head included). Feet dipping over the edge no longer end the round by themselves — they cut the motors so the fall plays out (see GoLimp) — and the torso backstop is 2 m below the floor. OFF: the old rule, a foot below footOffMatY.\n\nIt was 'the HEAD only' for one afternoon and a bracket stalled forever on two limp fighters resting on each other with neither head down; any-part was chosen to replace it.\n\nBoth referees carry this; Systems_SumoMatchManager.ringOutOnFloorContact must stay equal.")]
        [UnityEngine.Serialization.FormerlySerializedAs("ringOutOnHeadFloor")]
        public bool ringOutOnFloorContact = true;
        [Tooltip("REAL SUMO LOSS (2026-10-05). ON: a fighter loses the instant any part other than the soles of the feet touches the clay (Agent_Biped.IsDown), or a foot drops off the edge of the dohyo. Read by BOTH referees. Every brain trained before this date has never met the rule and goes down within seconds under it — retrain before judging a bout.")]
        public bool touchDownLoses = true;
        [Tooltip("TACHIAI. ON: every bout opens from the shikiri crouch (Agent_BipedBody.startCrouched) — squatting, fists on the clay — and both fighters are released on the same physics step. Read by both referees. Pair with a small neutralGapHalf and enableWalkIn off.")]
        public bool tachiaiStart = true;
        [Tooltip("Seconds after the release during which touchDownLoses ignores non-foot contact, so the fists that start on the clay are not a loss. Both referees.")]
        public float tachiaiGraceSeconds = 0.6f;
        [Tooltip("BELT GRIPS. ON: a forearm that touches the opponent's pelvis pins itself there (Sensor_BeltGrip) until the pull exceeds its break force. Automatic — no grip action or observation, so the 13-action / 51-slot contract is unchanged. Read by both referees.")]
        public bool beltGrips = true;
        [Tooltip("THE DIVE. Chance per second, while one fighter has the other within 0.9 m of the rim and 1.5 m away, that he launches his whole body at him (Systems_Dive). 0 disables it. A gamble under touchDownLoses: the diver lands on the clay. Read by both referees.")]
        public float diveChancePerSecond = 0.35f;
        [Tooltip("Launch speed of a dive, m/s, applied to every part of the diver. Both referees.")]
        public float diveSpeed = 3.5f;
        [Tooltip("Head knockouts (motors cut, blood spray). OFF for real sumo: a head clash is not a finish and there is no blood. Bruise marks are unaffected.")]
        public bool allowKnockout = false;
        [Tooltip("How far the tawara bale stands proud of the clay, in metres. With tawaraFriction near 1 and a narrow tawaraBandWidth this is a real bale a heel can brace against; at 0.005 with a slick wide band it is the old slide-out strip. Read by both referees.")]
        public float tawaraHeight = 0.005f;
        [Tooltip("Head knockouts one fighter can suffer before losing the whole match on the spot — boxing's three-knockdown rule. 0 disables it. GAME-ONLY: Systems_SumoMatchManager has no equivalent, so the brains never train against it; it is a spectacle rule layered on top of the sumo rules, not one of them.")]
        public int knockoutsToLoseMatch = 3;
        [Tooltip("Realtime seconds between the deciding knockout and the result card. Must outlast Systems_MatchPresentation.koSlowMoRealSeconds or the card cuts off the slow-motion replay of the hit that ended it.")]
        public float knockoutAnnounceSeconds = 2.2f;
        [Tooltip("Fraction of each joint's END-OF-ROUND fatigue a fighter carries into the next round of the SAME match. 0 = today's behaviour: every round opens on fresh legs. 1 = no rest at all between rounds.\n\nGAME-ONLY, and it must stay that way. Systems_SumoMatchManager never sets it, so a training episode still opens at zero fatigue — carrying it across an ML-Agents episode boundary would make an episode's difficulty depend on how hard the previous one was fought, which is the hidden non-stationary term Agent_BipedBody.ResetPose exists to prevent. It never crosses a MATCH either: a rematch and every bracket bout start fresh.\n\nNo brain has trained against a round that opens tired, but every shipped brain observes stamina (the +1 slot), so the state is at least visible to the policy. Above 0 this changes who wins later rounds — measure with MatchTestHarness before shipping a value.")]
        [Range(0f, 1f)] public float roundFatigueCarry = 0f;

        // Systems_BodyDamage's designer-facing dials. They lived only on the
        // component until 2026-10-04, and that component is spawned fresh per
        // match by Systems_GameMatchManager — so its CODE DEFAULTS were what ran
        // and there was no asset to tune. The defaults below are those same
        // numbers, so moving them here changed nothing about a bout. The long
        // measurement history behind each one stays in the tooltips on
        // Systems_BodyDamage, which is still the fallback when no tuning asset
        // is assigned; read those before moving a value.
        [Header("Body damage / dismemberment (GAME-ONLY)")]
        [Tooltip("Summed mark strength at which a region reads fully RED on the HUD mannequin. Every detach gate below is a multiple of this.")]
        public float regionRedAt = 2.5f;
        [Tooltip("Multiple of regionRedAt past which an ARM or LEG can tear off (10 x 2.5 = a gate of 25). Measured three times: moving this relocates the pop, it does not change the rate — limbDetachChance and regionDamageRefractory are the levers.")]
        public float detachAtRedMultiple = 10f;
        [Tooltip("Same, for the HEAD (1.2 x 2.5 = a gate of 3.0). Far lower than the limb figure so decapitation stays the showpiece finish.")]
        public float headDetachAtRedMultiple = 1.2f;
        [Tooltip("Minimum seconds between damage applications to the SAME region. Turns limb damage from a contact-count process into a time process; 0 restores unlimited accumulation.")]
        public float regionDamageRefractory = 0.15f;
        [Tooltip("Master switch for limb loss and decapitation. OFF leaves bruising, the mannequin colouring and the head KO intact. The player's REALISTIC MODE setting forces this off for a bout regardless of the value here.")]
        public bool allowDetach = true;
        [Tooltip("Probability that an arm or leg actually comes off once it has reached the detach gate. Rolled ONCE per limb and remembered for the tournament. The head is exempt.")]
        [Range(0f, 1f)] public float limbDetachChance = 0.5f;
        [Tooltip("Master switch for the gib (all four limbs and the head on one blow). Independent of allowDetach. REALISTIC MODE forces it off too.")]
        public bool allowGib = true;
        [Tooltip("Impact speed at or above which a hit is ELIGIBLE to gib. Above Systems_BodyDamage.koSpeed (7.5) on purpose.")]
        public float gibSpeed = 11f;
        [Tooltip("Probability that a hit at or above gibSpeed gibs. 1% of QUALIFYING hits, not of all contacts.")]
        [Range(0f, 1f)] public float gibChance = 0.01f;

        // Which runtime companions the match manager spawns.
        //
        // These were per-scene booleans on Systems_GameMatchManager, which meant
        // every arena scene carried its own serialized copy — and a public field
        // already written into a scene IGNORES any later change to its code
        // default, so "I flipped the default and nothing happened" was a recurring
        // trap. Here there is exactly one copy for every arena. (There were three
        // arena scenes when this was written; SCN_SUMO_ICE and SCN_SUMO_STICKY
        // were deleted 2026-07-28 and SCN_SUMO is now the only one — which makes
        // the asset less necessary and no less correct.)
        [Header("Arena band")]
        [Tooltip("Confine the arena camera to a horizontal BAND of the screen instead of letting it own the whole frame.\n\nPortrait leaves roughly 30% of every frame as black nothing below the dohyo, and no camera VALUE fixes it - minOrtho, feetDrop and the rest only shuffle the dead space around, because they all work inside a fixed 9:16 viewport. The band changes the aspect the orthographic maths divides by, which is the one lever that changes the trade instead of moving it.\n\nThis is a RENDERING change, not just a framing one: the region outside a camera rect is not drawn by that camera, so Systems_CameraFollow also spawns an ArenaBandClear camera whose only job is to clear it. Without that the outside keeps the previous frame and smears.")]
        public bool enableArenaBand = true;
        // OFF after being measured on 2026-09-05, and the measurement is the point.
        // The band DOES do what it promises to the maths: at 1440x3088 it took the
        // aspect 0.466 -> 0.752 and the fighters rendered far larger. It still looked
        // WORSE, because the arena has nothing to put in the space it opens up. The
        // band is filled by backdrop grid and the empty crowd wall, and the pull-back
        // shots (wideOrtho 14, the establishing shot at 9.5) do not shrink with the
        // aspect, so a wide beat shows MORE emptiness than it did before, not less.
        // Turning this on is therefore blocked on arena dressing that reaches the
        // band edges - not on anything in this file.
        [Tooltip("Bottom edge of the band as a fraction of screen height.")]
        [Range(0f, 0.45f)] public float arenaBandBottom = 0.20f;
        [Tooltip("Top edge of the band as a fraction of screen height. The gap above the band is where the score and the round banner live, so this is deliberately short of 1.")]
        [Range(0.55f, 1f)] public float arenaBandTop = 0.88f;

        [Header("Presentation companions")]
        [Tooltip("Slow-mo finishes, camera punch-in, salt throw.")]
        public bool enablePresentation = true;
        [Tooltip("Impact audio, crowd, ceremony.")]
        public bool enableAudio = true;
        [Tooltip("Per-fighter expression driven by dominance.")]
        public bool enableFaceMood = true;
        [Tooltip("Per-fighter spoken lines. Fighters with no recorded clips stay silent.")]
        public bool enableVoice = true;
        [Tooltip("2D light rig plus the post-processing volume.\n\nMUST STAY ON. The full rig (key, rims, global, volumetrics) has been on since 2026-08-25 and the old LightingEffects / FlatBodyShading switches on Systems_ArenaLighting were deleted — but every sprite in the arena uses a LIT material, and with no Light2D at all they render solid black. This switch decides whether there is a rig at all.")]
        public bool enableLighting = true;
        [Tooltip("Event post-processing on the arena's existing volume: a chromatic-aberration + vignette punch on KOs and dismemberments, and a desaturated look while a slow-motion finish runs. Drives the profile Systems_ArenaLighting already builds — no renderer feature, no extra blit. Rides enableLighting too: no volume, nothing to drive.")]
        public bool enablePostFx = true;
        [Tooltip("Warm corner lanterns that flicker like a live venue and flare with crowd support and round ends. Small on purpose: they punctuate the rig, they do not light the fight. Cast shadows stay off — the edge-on dohyo can show no projected shadow regardless of light count.")]
        public bool enableLanterns = true;
        [Tooltip("Dust and sweat bursts scaled by hit strength.")]
        public bool enableImpactFx = true;
        [Tooltip("A one-quad radial STREAK burst at solid-strike contact points, aimed along the blow — the directed middle tier between ImpactFx's particle flash and ShockwaveFx's slam ring. Presentation only; subscribes to the impact static like the other two.")]
        public bool enableHitSmear = true;
        [Tooltip("Backdrop parallax, haze tinting, crowd sway, light shafts.")]
        public bool enableAtmosphere = true;
        [Tooltip("Adaptive layered score.")]
        public bool enableMusic = false;
        [Tooltip("Bruise decals where a fighter is hit, plus the bloody head KO. Presentation only — no referee reads it.")]
        public bool enableBodyDamage = true;
        [Tooltip("Punches and kicks drive the man they land on backwards, and a good one launches him. GAME-ONLY: the training referee has no equivalent, so no brain has fought against it. Turning this on changes who wins rounds — a launched fighter can be knocked clean off the mat.")]
        public bool enableStrikeImpulse = true;

        [Tooltip("Announce the winning technique (kimarite) after every round. Read-only with respect to the fight — it names the finish, it does not decide it.")]
        public bool enableKimarite = true;

        [Tooltip("The crowd backs whoever is losing, and sustained support grants a small torque boost. THIS CHANGES WHO WINS ROUNDS and the training referee has no equivalent, so no brain has trained against it — game-only, like knockoutsToLoseMatch.")]
        public bool enableCrowdMomentum = true;

        [Tooltip("Real-time diagnostic overlay: frame time, FPS, GC delta, physics step and per-fighter stamina. Development aid — turn OFF for a release build.")]
        public bool enablePerfHud = true;

        [Tooltip("Expanding shock rings on the heaviest moments - a head KO, a limb coming off, and body-on-body contact hard enough to count as a slam. Presentation only: it reads outcomes and changes nothing a referee sees, so it is safe in a bracket and needs no retraining.\n\nEvery other VFX in the game is particles (Systems_DustPuff builds six systems), so the biggest moments all read as more of the same small stuff. One ring is the cheapest way to say that one was different.")]
        public bool enableShockwave = true;

        [Tooltip("Device haptics on the big moments, plus camera shake on the discrete events that produced none (head KO, dismemberment, gib, round end). Presentation only — it reads outcomes and changes nothing a referee sees, so it is safe in a bracket and needs no retraining.\n\nHaptics are Android-only and additionally respect the player's own switch (Systems_FeelFx.HapticsEnabled, persisted in PlayerPrefs). This flag decides whether the system exists at all.")]
        public bool enableFeelFx = true;

        [Tooltip("A looping clay-scrape voice per fighter whose gain follows planted-foot slip speed x foot load, so being driven back across the mat is audible. Presentation only: it reads the foot load the body already samples and writes nothing.\n\nPaused outright below its floor, so a fighter standing still makes no sound - this project has removed two always-on noise layers already and this must not become a third.")]
        public bool enableFootScrape = true;

        [Tooltip("Danger bands standing on the LIVE edge of the mat, brightening from amber to red as it closes. Presentation only: it reads the ring width and changes nothing a referee sees, so it is safe in a bracket and needs no retraining.\n\nMeasured over 17 rounds of live bracket play, 16 ran past shrinkStartSeconds and every one ended in a ring-out — the contraction, not a push, is what decides almost every round, and nothing on screen said so. Pairs with the MAT meter in the dock: this is the thing, that is the number.")]
        public bool enableRingSqueezeCue = true;

        [Tooltip("The tale of the tape in the empty band between the dohyo and the dock. Per fighter: elo and banzuke rank, match record and win streak, weight and build, a live stamina bar and six damage pips; between them a push tug-of-war in newtons, and above them the head-to-head and what is at stake. Read-only with respect to the fight: it decides nothing, is not mirrored into Systems_SumoMatchManager, and touches no observation, mass or collider, so no brain is affected. Numbers refresh on round boundaries only; just the bars and pips move during a bout, because the six-row aggregate table that used to sit in the dock was cut for being unreadable mid-bout.")]
        public bool enableFighterPanel = true;

        [Tooltip("The five fixed screen corners: game title (top-left), frame rate (top-centre), menu (top-right), the DBG button (bottom-left) and the build version (bottom-right). Drawn absolutely on the HUD's Overlay layer, so it competes with no band and pushes nothing around. Presentation only — it reads outcomes and changes nothing a referee sees.\n\nThe menu button is the pause affordance; with chrome off, pause is the hardware back key and the Escape binding only.")]
        public bool enableScreenChrome = true;

        [Tooltip("The agent telemetry panel behind the DBG button: which brain is driving each fighter and the shape of the vector it was trained against, one plain-language verdict per fighter, and 30-second graphs of stamina, mat remaining and effort.\n\nUnlike enablePerfHud this is NOT gated on a development build, and that is deliberate: the perf overlay answers a question about the BUILD and is a development aid, while this answers a question about the FIGHTERS and is worth having on the phone. It is read-only with respect to the fight and touches no observation, mass or collider, so no brain is affected.")]
        public bool enableAgentDebug = true;

        [Header("Spectator layer (all read-only w.r.t. the fight)")]
        [Tooltip("Broadcast win-probability meter in the dock: a smoothed logistic blend over dominance, stamina, mat-behind and the career Elo prior. Systems_DirectorAI and Systems_Caster read its numbers. Read-only with respect to the fight — it decides nothing and is not mirrored into Systems_SumoMatchManager.")]
        public bool enableTensionEngine = true;
        [Tooltip("The muscle cam: limb tint driven by per-joint fatigue and instantaneous motor load, drawn through SpriteRenderer.vertex colour so no material is cloned and the SRP batcher keeps the shared body material. Read-only with respect to the fight.")]
        public bool enableJointHeatmap = true;
        [Tooltip("In-fight storytelling camera: comeback close-ups, blowout wides, separation and clinch shots, chosen from the tension reading. Deliberately SILENT during finishes and the ceremony — Systems_MatchPresentation and the referee own those moments. Read-only with respect to the fight.")]
        public bool enableDirectorAI = true;
        [Tooltip("Play-by-play caster: short text lines assembled from round events, fatigue and odds thresholds, body-damage statics and the mutator telegraphs. Read-only with respect to the fight.")]
        public bool enableCaster = true;
        [Tooltip("Emergent storylines (head-to-head series, streaks, upset watch, titles) computed pure from Systems_CareerStats and shown by the fighter panel and the caster. No lifecycle of its own — this switch only gates whether its lines appear.")]
        public bool enableStorylines = true;
        [Tooltip("GAME-ONLY, like enableStrikeImpulse: once a round, with ~3 s of telegraph, either a slick salt patch appears on the clay or the shrinking mat briefly contracts several times faster. Systems_SumoMatchManager has no equivalent, so no brain has trained against a mutator — do not port this into the training referee without making it observable, which it cannot be.")]
        public bool enableArenaMutators = true;
        [Tooltip("Biometrics card in the dock: a 27-second stamina history per fighter plus peak impact delivered and the crowd-adrenaline peak, drawn PerfHud-style as fixed ring bars written only when a value moves. Read-only with respect to the fight.")]
        public bool enableBiometrics = true;
        [Tooltip("Record every fighter's actions and state to CSV (Systems_ActionLog): Logs/ActionLogs in the Editor, persistentDataPath/ActionLogs on a device. Read-only w.r.t. the fight. About 25 MB per hour of live fighting; the newest 20 sessions are kept.")]
        public bool enableActionLog = true;
    }
}
